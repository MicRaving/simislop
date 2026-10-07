# Rendering & Client Framework Audit — FreeSO (`simitone-local`)

Scope: `FreeSO/TSOClient/tso.world/**`, `FreeSO/TSOClient/tso.client/**`,
`FreeSO/TSOClient/FSO.UI/**`, `FreeSO/TSOClient/tso.common/**`, weighted towards
what a single-player TS1 fork actually exercises (2D lot view, MSAA/SSAA,
lot + object + UI thumbnails, city view, EOD/UI panels).

Read-only static audit. No source was modified, no UI automation was performed.
Line numbers are from branch `simitone-local` @ `7e76c19`.

Severity scale: **crash** · **correctness** · **perf** · **consistency** · **cosmetic**.
Confidence is about the *diagnosis*, not the blast radius.

---

## 1. CRASH

### R-01 · `tso.world/Utils/DGRPRenderer.cs:427` · portal path dereferences a null depth mask
`Draw3D` guards the mask in the ordinary path (`if (Mesh.DepthMask != null)` at :365) but
the portal branch below reads it unguarded:
`var geom = Mesh.DepthMask; ... if (geom.Verts != null)` (:425-427).
A DGRP with `DGRP3DMaskType.Portal` and no depth mask throws `NullReferenceException`
mid-draw, on the render thread, with the device left bound to an arbitrary target.
*Why it matters:* the non-portal path proves the null case is reachable-by-data, not
theoretically impossible.
**Fix:** `if (Mesh.DepthMask != null) { var geom = Mesh.DepthMask; if (geom.Verts != null) { … } }`,
or early-return the stencil-clearing step when the mask is absent.
**Severity:** crash · **Confidence:** high

### R-02 · `tso.world/Components/TerrainComponent.cs:212-213, 283-295, 626, 643` · disposed `IndexBuffer` is re-bound after a regen
`RegenTerrain` disposes `GridIndexBuffer` / `TGridIndexBuffer` but does **not** null them
(`?.Dispose()` only), and only reallocates them `if (GridIndices.Length > 0)`. A regen that
produces an empty grid therefore leaves a **disposed, non-null** buffer assigned and leaves
`GridPrimitives` at its stale value. The draw path then does
`device.Indices = GridIndexBuffer; device.DrawIndexedPrimitives(..., GridPrimitives)`.
The two `TGrid` buffers *are* guarded with `!TGridIndexBuffer.IsDisposed` (:612, :632) —
the `Grid` buffers are not, which is the tell that this is an oversight rather than a
deliberate policy.
*Why it matters:* `GridIndices` comes from `GetGridIndicesForFine(bp.FineArea, quads)` /
`GetGridIndicesForArea(bp.BuildableArea, quads)`, both of which can legitimately return
length 0 (no buildable area / fine area covering nothing).
**Fix:** after disposal set `GridIndexBuffer = TGridIndexBuffer = null;
GridPrimitives = TGridPrimitives = 0;` and add `&& !GridIndexBuffer.IsDisposed` to the two
unguarded uses.
**Severity:** crash · **Confidence:** high (code), medium (trigger frequency)

### R-03 · `FSO.UI/Framework/Matrix2D.cs:61-65` (via `UIElement.cs:691`) · `TransformPoint(ref x, ref y)` recomputes `y` from the already-updated `x`
```csharp
public static void TransformPoint(this float[] M, ref float x, ref float y)
{
    x = x * M[0] + y * M[2] + M[4];
    y = x * M[1] + y * M[3] + M[5];   // <-- uses the NEW x
}
```
The `ref` overload is the only one with the bug; the two `Vector2`-returning overloads
right below it (:67-81) are correct. `UIElement.LocalRect(x, y, w, h, mtx)` uses the broken
one, and `LocalRect` is on live draw/hit paths:
`UIRectangle.cs:70` (its mouse region), `UIMotiveDisplay.cs:57-75`,
`UIRelationshipDialog.cs:373-376`, `UITextEdit.cs:1163` (the text frame border),
`UISecureTradeEOD.cs:462-463`.
*Why it matters:* the two other transforms of a typical UI matrix are identity, so the
error is invisible until a container is rotated (`M[1] != 0`) or has non-uniform
`ScaleX`/`ScaleY` — at which point every `LocalRect` inside it lands in the wrong place.
**Fix:** stash the original x (`var ox = x;`) and use it in the `y` line, or delete the
`ref` overload and route `LocalRect` through `M.TransformPoint(x, y)`.
**Severity:** correctness (feeds a hit region) · **Confidence:** high

### R-04 · `FSO.UI/Controls/UITextEdit.cs:495-542` ← `tso.common/Rendering/Framework/IO/InputManager.cs:260-265` ← `GameScreen.cs:36,56` · arrow / Home / End never reach `UITextEdit`
`ApplyKeyboardInput` sets `processChar = false` for **every** non-`Keys.None` key (:82), so
the `if (processChar)` block that produces `UnhandledKeys` is only reached when
`key == Keys.None` — i.e. only for entries past `NewKeys.Count`. Inside that block:
```csharp
if (j >= state.NewKeys.Count) value = state.FrameTextInput[j - state.NewKeys.Count];
else if (state.FrameTextInput != null) continue;      // <-- always taken
else value = TranslateChar(key, …);
```
`GameScreen.Update` assigns `State.FrameTextInput = TextCharacters` where `TextCharacters` is
a `static List<char>` created at `GameScreen.cs:36` and therefore **never null**. The
`continue` is unconditionally taken, `TranslateChar` is never reached, and
`Keys.Left/Right/Up/Down` — whose only consumer is the `switch (key)` over
`inputResult.UnhandledKeys` at `UITextEdit.cs:495` — are dropped.
`UpdateState.Update` still re-adds them every 400 ms as "new" keys (`UpdateState.cs:110-114`),
so the repeat machinery feeds a path that discards them.
*Why it matters:* the whole `TranslateChar` function (135 lines) is dead on desktop, and
cursor/selection navigation inside the chat input is dead with it.
**Fix:** in the `key != Keys.None` branch, add an `else { result.UnhandledKeys.Add(key); }`
for keys that produced no action; make the `continue` conditional on a real text-input
frame (`charCount > 0`) rather than on `FrameTextInput != null`.
**Severity:** correctness · **Confidence:** high

### R-05 · `FSO.UI/Framework/UISpriteBatch.cs:172-186, 212-243` · `GetBuffer()` returns null and the pool is permanently poisoned
`GetBuffer()` returns `null` once the pool is empty (nested `WithBuffer` calls). `UIRenderPlane`'s
constructor then does `GD.SetRenderTarget(null); GD.Clear(Color.Transparent)` — **clearing the
screen backbuffer mid-frame** — and `Texture.SetValue(null)`. On dispose, `FreeBuffer(null)`
pushes `null` into `Buffers`, after which `GetBuffer()`'s `Buffers.Count > 0` check keeps
handing out `null` forever. There is also no `Disposed` flag, so a manual `Dispose` plus a
`using` block puts the same `RenderTarget2D` into the pool twice.
*Why it matters:* the guard comments elsewhere in the codebase show the team already knows
buffer pools get exhausted; the null path has no equivalent guard.
**Fix:** `GetBuffer()` should throw or synchronously grow the pool; `UIRenderPlane` should
early-return when `Target == null` and never `FreeBuffer` a null; add `if (Disposed) return;`
in `Dispose`.
**Severity:** correctness (silent wrong-target render + pool corruption) · **Confidence:** high

### R-06 · `FSO.UI/Framework/UICachedContainer.cs:43-47, 124` · zero-sized render target and divide-by-zero
`PreDraw` builds the target from `Size * Scale` with no clamp:
`new RenderTarget2D(gd, (int)size.X, (int)size.Y, …)`. `UIDialog` only ever sets `Size` inside
`SetSize` (`UIDialog.cs:189`), which hand-built dialogs may never call — `UIDialog`'s own
constructor does not call it. `Size` defaults to `Vector2.Zero` (`UIElement.cs:331`).
`Draw` then computes `new Vector2(1 / Scale.X, 1 / Scale.Y)` (:124), which is `Infinity` if
`Scale` is ever 0.
*Why it matters:* `UIDialog` has 33 subclasses in this tree; the failure is a device
exception on the first PreDraw of any dialog whose script does not set `size`.
**Fix:** clamp with `PPXDepthEngine.ClampToDevice(gd, (int)size.X, (int)size.Y)` (the helper
already exists and is used by `_2DWorldBatch.ResizeBuffer`), and guard the reciprocal.
**Severity:** crash · **Confidence:** medium

### R-07 · `FSO.UI/Framework/UIElement.cs:846-858` · `DPISwitch` dereferences a nullable rect and hard-codes 2×
`DPISwitch` is called from the 6-arg and 8-arg `DrawLocalTexture` overloads (:925, :961) and
from `DrawTiledTexture` (:982). It immediately does `rect.Value.X` with no null check, and
uses `var rscale = 2;` for a DPI ratio that `FSOEnvironment.DPIScaleFactor` already models.
The `texture.Tag is Texture2D` branch is live: `AbstractTextureRef.Get` sets
`_Instance.Tag = hidef` (another `Texture2D`) on the `ReplacePath` branch
(`tso.content/Model/TextureRef.cs:163-164`).
*Why it matters:* any `DrawLocalTexture(..., from: null, ..., blend)` on a texture with a
replace path throws. Also, `texture = tex2` swaps in the hi-dpi texture *and* rescales the
rect by 2 regardless of the actual DPI factor, so the two mechanisms double-apply.
**Fix:** null-check `from` (fall through to no-op), use `FSOEnvironment.DPIScaleFactor`
instead of `2`, and stop reassigning `texture` when the caller already passed the hi-dpi one.
**Severity:** crash · **Confidence:** medium

### R-08 · `FSO.UI/Framework/UIElement.cs:593-599` · `RemoveUpdateHook` locks a null list
```csharp
public void RemoveUpdateHook(UpdateHookDelegate hook) { lock (UpdateHooks) { UpdateHooks.Remove(hook); } }
```
`UpdateHooks` is null until the first `AddUpdateHook`, and `lock (null)` throws
`ArgumentNullException`. Today the only caller (`UIUtils.UIDragHandler.DragMouseEvents`)
always Adds before Removes, so it is latent — but it is a one-line trap.
**Fix:** `if (UpdateHooks == null) return;` before the lock.
**Severity:** crash (latent) · **Confidence:** high

### R-09 · `tso.world/Utils/DGRPRenderer.cs:255` · unchecked cast of a texture `Tag`
`var pt = ((TextureInfo)sprite.Pixel.Tag).Size;` — no null check, no `as`. The DGRP path that
produces these pixels goes through `AbstractTextureRef.Get`, which sets `Tag` to
`this` (an `AbstractTextureRef`, `TextureRef.cs:168`) or to another `Texture2D` (`:164`),
not to a `TextureInfo` (which only the raw `ImageLoader`/`SPR` path sets —
`SPR.cs:370`, `SPR2.cs:744`).
*Why it matters:* a DGRP whose frame is loaded through a `TextureRef` throws
`InvalidCastException` on the first 2D draw of the object.
**Fix:** `var pt = (sprite.Pixel.Tag as TextureInfo)?.Size ?? new Point(sprite.Pixel.Width, sprite.Pixel.Height);`
**Severity:** crash · **Confidence:** medium

### R-10 · `tso.common/Utils/TextureGenerator.cs` + `tso.world/WorldContent.cs:16,75,91,107` + `FSO.UI/UILayer.cs:178-182` · no static GPU resource survives a device reset
`TextureGenerator` holds ~15 `private static Texture2D` fields guarded by `if (X == null)`
(`:9-23, 25-152, 212-259, 433-560`) with **no** reset path. `WorldContent._be`,
`_TextureVerts`, `_TextureVertsInv` are static with the same shape. The one device-reset
handler in the UI layer, `UILayer.GraphicsDevice_DeviceReset`, forwards only to
`UIScreen.DeviceReset`; `UISpriteBatch`'s own handler is commented out
(`UISpriteBatch.cs:36, 125-140`) leaving the `public static bool Invalidated` it keyed off
permanently `false`.
*Why it matters:* after a reset every one of these is disposed-but-non-null, so the
`== null` guard does not fire and the next use is a use-after-dispose. The client's
fullscreen toggle (`UILayer.cs:305-308`) is the trigger.
**Fix:** add `TextureGenerator.ResetAll()` / `WorldContent.ResetGPU()` and call both from
`UILayer.GraphicsDevice_DeviceReset` before forwarding to the screens.
**Severity:** crash · **Confidence:** high (code), medium (whether this fork's SDL path raises the event)

---

## 2. CORRECTNESS

### R-11 · `tso.world/World.cs:689-693` · the 2D sprite texture cache is cleared **every frame** for the first minute
```csharp
FrameCounter++;
if (FrameCounter < LastCacheClear + 60*60) { State._2D.ClearTextureCache(); }
```
`LastCacheClear` is declared at `World.cs:46` and **written nowhere in the tree**. The
condition is therefore true for frames 1..3600 and permanently false afterwards, so the
intended periodic clear never happens and instead the cache is destroyed on *every* frame of
the first minute of play. The comparison is also the wrong way round for the stated intent.
*Why it matters:* `_2DWorldBatch.GetTexture` (`_2DWorldBatch.cs:849-863`) is the per-sprite hot
path. Clearing forces a `Dictionary` miss + `CachableTexture2D` acquire + `TimedReferenceController.KeepAlive`
per sprite per frame. (`SPR2Frame.GetTexture` does own its texture via the reference-counted
`CachableTexture2D`, so this is a churn/refcount cost, not a VRAM leak.)
**Fix:** `if (FrameCounter >= LastCacheClear + 60*60) { LastCacheClear = FrameCounter; State._2D.ClearTextureCache(); }`
— and drop `LastCacheClear` from the public surface.
**Severity:** perf · **Confidence:** high

### R-12 · `tso.world/Utils/_2DWorldBatch.cs:873-877` · the world-texture cache is declared and never used
```csharp
private Dictionary<IWorldTextureProvider, WorldTexture> _WorldTextureCache = new …;
public WorldTexture GetWorldTexture(IWorldTextureProvider item) { return item.GetWorldTexture(this.Device); }
```
`SPR2Frame.GetWorldTexture` allocates a fresh `WorldTexture` on **every** call
(`tso.files/Formats/IFF/Chunks/SPR2.cs:843`), so the 2D DGRP path allocates one wrapper per
sprite per frame. The dead dictionary is the fossil of the intended cache.
**Fix:** route through the dictionary the same way `GetTexture` does, and clear it in
`ClearTextureCache`/`Dispose`.
**Severity:** perf · **Confidence:** high

### R-13 · `tso.world/LMap/LMapBatch.cs:1022-1030` + `156-162, 199` · `Dispose` leaks the light vertex buffer and the blit batch
`Dispose` frees only the six `RenderTarget2D`s. It never touches `LightBuf` (a `VertexBuffer`)
or `ShadowTargBlit` (a `SpriteBatch`). `Init` calls `Dispose()` at :156 and then immediately
re-creates both at :162 and :199. `Init` is reached on every lot load
(`World.cs:152-156`, `GameThread.InUpdate(() => Light?.Init(blueprint))`) and on every
`ChangedWorldConfig`.
*Why it matters:* one abandoned `VertexBuffer` + one abandoned `SpriteBatch` per lot load.
**Fix:** add `LightBuf?.Dispose(); LightBuf = null; ShadowTargBlit?.Dispose(); ShadowTargBlit = null;`
to `Dispose`.
**Severity:** perf · **Confidence:** high

### R-14 · `tso.world/Platform/WorldPlatform3D.cs:75-77` · every lot thumbnail leaks a render target
```csharp
LotThumbTarget = null;
if (LotThumbTarget == null) LotThumbTarget = new RenderTarget2D(gd, size, size, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8);
```
The first line throws away the previous target; the `if` is then trivially true. Because the
field is already null, `Dispose()` (:26-33) can never see or free the old one.
*Why it matters:* `GetLotThumb` is called for every catalog tile, every lot button, every
neighbour page. Each call abandons a `size × size × (4+4)`-byte target.
**Fix:** delete line 75 entirely and replace the target only when the size changed
(`LotThumbTarget == null || LotThumbTarget.Width != size`), disposing the old one.
**Severity:** perf · **Confidence:** high

### R-15 · `tso.world/World.cs:1215-1246` · `World.Dispose` frees a fraction of the blueprint's GPU footprint
Disposed: `AmbientLight`, `OutsidePx`, `Light`, `Platform`, `State.Rooms`, `State._2D`, objects,
particles, `Terrain`, `RoofComp`. **Not** disposed: `Blueprint.WCRC` (a `WallComponentRC`
holding `GVerts`/`GIndices` — `WallComponentRC.cs:473-477`), `Blueprint.FloorGeom` (a
`_3DFloorGeometry` with GPU groups and a `Modelled3DPool` — `3DFloorGeometry.cs:600, 679-695`),
`Blueprint.WallComp`'s cached buffers, and `Blueprint.WallCache2D` — a
`List<_2DDrawBuffer>` of `VertexBuffer`/`IndexBuffer` (`_2DDrawGroup.cs:38-43`).
`WallCache2D` is only freed by `WorldPlatform2D.RecacheWalls` → `ClearDrawBuffer` (:337),
i.e. on a recache, not on teardown.
**Fix:** give `Blueprint` an `IDisposable` that walks `WCRC`, `FloorGeom`, `WallComp` and
`WallCache2D`, and call it from `World.Dispose`.
**Severity:** perf · **Confidence:** high

### R-16 · `tso.world/Model/Blueprint.cs:230-240` · `InterpNormal` computes the wrong terrain normal
```csharp
float xl1 = xLerp * h10 + (1 - xLerp) * h00;   // value at baseY
float xl2 = xLerp * h11 + (1 - xLerp) * h01;   // value at nextY
float xDist1 = (h10 - h00) * TerrainFactor;    // d/dx at baseY
float xDist2 = (h11 - h01) * TerrainFactor;    // d/dx at nextY
float xChange = xLerp * xDist1 + (1 - xLerp) * xDist2;
float yChange = (xl2 - xl1) * TerrainFactor;
```
Two errors. (a) `xDist1`/`xDist2` vary along **y**, so interpolating them must be weighted by
`yLerp`, not `xLerp` — and in the right direction. (b) `yChange` is the raw difference
between the y=0 and y=1 lerps; the height change relative to `xl1` is `yLerp * (xl2 - xl1)`.
Compare the correct sibling in `InterpAltitude` at :206
(`yLerp * xl2 + (1 - yLerp) * xl1`). The comment at :233 ("Reduced to a single line in the Y
direction") describes the intent, not the code.
*Consumer:* `AvatarComponent.cs:113` (`AltitudeNormal`).
**Fix:** `float xChange = (yLerp * xDist2 + (1 - yLerp) * xDist1); float yChange = yLerp * (xl2 - xl1) * TerrainFactor;`
**Severity:** correctness · **Confidence:** high

### R-17 · `tso.world/WorldState.cs:425-460` (via `World.cs:1015-1029`) · `State.Light` is never assigned
```csharp
Light = new LMapBatch(gd, config.ShadowDetail);   // World.cs:1021
if (Blueprint != null) { … }                     // :1022-1027
State.Light = Light;                              // :1028  <-- unreachable
```
`State.Light = Light` sits inside `if (Light == null)` (:1015) two lines after `Light` was
assigned non-null, so the condition is always false. `WorldState.Light` is read by
`PrepareLighting` (:428, :440-446) to pick the light map, the wall offset divisor
(`Light.Blueprint.Width - 2`) and `InvMapLayout` — so after the batch is rebuilt,
`PrepareLighting` reads the **old, disposed** `LMapBatch`.
The surrounding `if (true) //config.AdvancedLighting && config.Shadow3D` block
(`World.cs:1040-1058`) and `if (true || WorldConfig.Current.Shadow3D || FSOEnvironment.Enable3D)`
(`Blueprint.cs:131`) are the same class of edit-me-not.
**Fix:** drop the `if (Light == null)` wrapper; delete the `if (true)` guards and either
restore the settings or delete the branch.
**Severity:** correctness · **Confidence:** high

### R-18 · `tso.world/WorldState.cs:31-51` · `RenderingThumbnail` clobbers the transitions flag
```csharp
public bool DisableSmoothRotation { set { Cameras.DisableTransitions = value; _DisableSmoothRotation = value; } }
public bool RenderingThumbnail    { set { Cameras.DisableTransitions = value; _RenderingThumbnail = value; } }
```
Both setters write `Cameras.DisableTransitions` from their own value only. Setting
`RenderingThumbnail = false` (done by every thumbnail capture —
`WorldPlatform2D.cs:123, 312`; `WorldPlatform3D.cs:152`) therefore **re-enables camera
transitions** even though `DisableSmoothRotation` is still `true`. The `get` on
`DisableSmoothRotation` correctly ORs the two; the setters do not.
**Fix:** route both through a single `_DisableSmoothRotation || _RenderingThumbnail` writer.
**Severity:** correctness · **Confidence:** high

### R-19 · `tso.world/World.cs:714` vs `_2DWorldBatch.cs:100-104, 807-840` · scroll sign flip + SSAA size mismatch
`SetScroll` calls `ResetMatrices(LastWidth, LastHeight, (int)Scroll.X, (int)-Scroll.Y)`;
`ResetMatrices(int, int)` at :809 calls it with `(int)Scroll.X, (int)Scroll.Y` — **un-negated**.
`World.InternalDraw` uses the second form at :714 before `State.PrepareCamera()` at :720
overwrites it, so the call is currently harmless-but-wrong. What is *not* harmless is that
:714 sets `LastWidth/LastHeight` from the **unscaled** `device.Viewport`, and
`WorldState.PrepareCulling` builds the 2D cull rect `WorldRectangle` from those
(`WorldState.cs:121-130`). Related inconsistency: the `_2DWorldBatch` constructor calls
`GenBuffers(device.Viewport.Width, device.Viewport.Height)` (:139, unscaled) while
`World.GameResized` calls `GenBuffers(ssaa.X, ssaa.Y)` (`World.cs:121-122`, scaled). The two
paths size the buffers differently, and `ResetMatrices` never divides by `SSAA`.
**Fix:** have one owner of the target size. Make `ResetMatrices` take an explicit
`transY`, delete the :714 call, and pass the SSAA-divided viewport in both
`GenBuffers` call sites.
**Severity:** correctness · **Confidence:** high (code), medium (visible symptom)

### R-20 · `tso.world/LMap/LMapBatch.cs:461, 473, 525, 654-658, 273, 325` · lighting never restores blend or rasterizer state
`DrawRoom` sets `GD.RasterizerState = Scissor` (:461) and never clears it; `ParseInvalidated`
sets `GD.BlendState = AlphaBlend` on entry (:273) and `DrawRoom`/`MultiplyOutdoors` then
overwrite it with `Opaque` (:473, :487), `MulBlend` (:654), `MinBlend`/`MaxBlend` (:657-658),
`AddBlendRed` (:921), `MaxBlendGreen` (:945) — and `ParseInvalidated` exits at :325 with only
`SetRenderTarget(null)`. The scissor rectangle is left at the last room's rect too.
*Why it matters:* the 2D world batch that runs next sets `BlendState` but **never** a
`RasterizerState` (`_2DWorldBatch.End` :488, `RenderCache` :575, `RenderDrawGroup` :693-709),
so it draws with the lighting pass's scissor test enabled against a stale rectangle.
**Fix:** snapshot `BlendState`/`RasterizerState`/`ScissorRectangle` at the top of
`ParseInvalidated`/`RedrawAll` and restore them in a `finally`.
**Severity:** correctness · **Confidence:** high

### R-21 · `tso.world/Utils/_2DWorldBatch.cs:349` · off-by-one in the depth-buffer index clamp
`if (depthBufferIndex > Buffers.Count) depthBufferIndex = Buffers.Count - 1;`
`==` slips through; `BufferAt` then returns `null` (:331-334), the depth target silently
becomes null, and `PPXDepthEngine.SetPPXTarget(Target, null, true)` re-binds the *backbuffer*
as the depth surface (`PPXDepthEngine.cs:118-120`). The `ResolveBufferIndex` helper three
lines later (which the colour index does use) gets this right.
**Fix:** `>=` , or just delete both lines and let `ResolveBufferIndex` do it for both indices.
**Severity:** correctness · **Confidence:** high

### R-22 · `FSO.UI/UILayer.cs:76-78` · `ViewMatrix` setter writes `m_WorldMatrix`
```csharp
public Matrix ViewMatrix { get { return m_ViewMatrix; } set { m_WorldMatrix = value; } }
```
Classic copy-paste. Currently unreferenced (only the ctor assigns `m_ViewMatrix`), so latent.
**Fix:** `set { m_ViewMatrix = value; }`.
**Severity:** correctness · **Confidence:** high

### R-23 · `FSO.UI/UILayer.cs:261-276` · `RemoveCurrent()` removes the wrong screen and never updates `currentScreen`
```csharp
var currentScreen = mainUI.GetChildren().OfType<UIScreen>().FirstOrDefault();
```
`AddScreen` (:199-218) appends the new screen and then re-appends `dialogContainer` to bring
dialogs to the top, and `UIContainer.Add` dedupes by re-appending — so the child order is
`[oldestScreen … newestScreen, dialogContainer]`. `FirstOrDefault<UIScreen>()` is therefore
the **background-most** screen, while `currentScreen` is the top one. `RemoveCurrent` also
never assigns `currentScreen`, unlike `RemoveScreen` (:220-236) which does.
*Why it matters:* this is the standard transition path —
`GameController.cs:81, 106, 132, 429, 442, 451`, plus `MaxisLogo.cs:37`, `EALogo.cs:39`.
**Fix:** iterate `mainUI.GetChildren().OfType<UIScreen>().LastOrDefault()`, mirror
`RemoveScreen`'s restore logic, and null `currentScreen` before reassigning.
**Severity:** correctness · **Confidence:** high

### R-24 · `FSO.UI/Framework/UISpriteBatch.cs:167-170` + `FSO.UI/Framework/UIElement.cs:833-835, 846-864` + `UILayer.cs:387` · re-`Begin` drops `SpriteSortMode.Immediate` and `CullNone`
`UIBegin` establishes `SpriteSortMode.Immediate` and `GraphicsDevice.RasterizerState = CullNone`
(:153-160). Three places re-`Begin` the batch with only some of that state:
* `UIElement.DrawLocalString` (VFont path) — `batch.Begin(transformMatrix: mat, rasterizerState: RasterizerState.CullNone)`
* `UILayer.DrawTooltip` — a bare `batch.Begin()`
* `UISpriteBatch.Resume()` — `this.Begin(_SortMode, _BlendMode)`, i.e. no matrix at all
*Why it matters:* MonoGame's `SpriteSortMode.Deferred` groups by texture, so for the rest of
the frame overlapping UI quads using different textures are submitted out of tree order —
labels and buttons pop in front of dialogs drawn later. `Resume()` additionally drops the
`BatchMatrixStack` transform, which is what `UICachedContainer` and every render plane rely on.
**Fix:** funnel all three through one `UISpriteBatch.Restart()` that re-applies
`_SortMode`, `_BlendMode`, `BatchMatrixStack.Peek()`, `RasterizerState.CullNone` and the
current effect.
**Severity:** correctness · **Confidence:** high

### R-25 · `FSO.UI/Controls/UIButton.cs:100-102` · `Size` setter silently drops the height
```csharp
[UIAttribute("size")]
public override Vector2 Size { get {…} set { Width = value.X; } }
```
Every `.ts`-style UI script attribute that sets a dialog button's size sets `Y` into
nowhere, and the getter reports `m_Height` (texture-derived), so a round-trip
`Size = GetBounds()` is not idempotent.
**Fix:** route both axes (`Width = value.X; m_Height = (int)value.Y;` and refresh
`ClickHandler.Region` as `ImageStates` does at :119-123).
**Severity:** correctness · **Confidence:** high

### R-26 · `FSO.UI/Controls/UIDialog.cs:150-153` · drag clamp mixes scaled and unscaled units, and is asymmetric
```csharp
if ((position.X - m_DragOffsetX) < (GlobalSettings.Default.GraphicsWidth - m_DragTolerance) && (position.X - m_DragOffsetX) > 0)
```
`this.X` is in the screen's coordinate space, and `UIScreen`'s ctor applies
`ScaleX = ScaleY = FSOEnvironment.DPIScaleFactor` (`UIScreen.cs:12`) — so the comparison is
scaled-vs-unscaled. The high side allows 20 px of overhang, the low side allows none.
**Fix:** compare against `GraphicsWidth * FSOEnvironment.DPIScaleFactor` (or divide the
position by `Scale`) and use `± m_DragTolerance` on both sides.
**Severity:** consistency · **Confidence:** high

### R-27 · `FSO.UI/Framework/UIContainer.cs:113-121` · `Remove` leaves `child.Parent` dangling
`//if (child?.Parent == this) child.Parent = null;` is commented out. Consequences:
`UIElement.WillDraw()` (:555-565) still walks into the removed container and can report the
control as drawable; `FindParent<T>()`/`FindController<T>()` (:1195-1231) resolve through the
dead tree; and `GetMousePosition`/`GlobalPoint` keep using an `_InvertedMtx` computed against
the old parent until `CalculateMatrix` runs, which `Remove` does not trigger.
**Fix:** uncomment the line and follow it with `child.InvalidateMatrix()`.
**Severity:** correctness · **Confidence:** high

### R-28 · `FSO.UI/Framework/UIElement.cs:998-1002, 990` · hit testing ignores `Disabled` and its own cache
```csharp
public bool HitTestArea(MouseState state, Rectangle area, bool cache)
{
    if (!Visible) { return false; }
    return area.Contains(GlobalPoint(state.Position.ToVector2()));
}
```
The `cache` parameter is accepted and ignored, and the `Dictionary<Rectangle, Vector4> _HitTestCache`
declared one line above is allocated per `UIElement` and never read or written — a per-element
allocation that exists only to support a removed cache. There is no `Disabled` check, so every
`ListenForMouse` consumer other than `UIButton` (which guards internally at :325) receives
hover/press on a disabled control: `UIDialog.DragMouseEvents` (registered on the background
image at `UIDialog.cs:71`), `UIDragHandler`, `UIClickableLabel`, custom panel handlers.
*Why it matters:* a dialog can be dragged by a control that is supposed to be inert.
**Fix:** add an `IsEnabled`/`Disabled` virtual to `UIElement` and honour it here; delete
`_HitTestCache` and the `cache` parameter, or implement the cache.
**Severity:** correctness · **Confidence:** high

### R-29 · `tso.world/Components/ObjectComponent.cs:261-267` · `ForceDynamic` setter has two identical branches
```csharp
if (value) blueprint.Changes.RegisterObjectChange(this);
else blueprint.Changes.RegisterObjectChange(this);
```
Either the static→dynamic transition is not the only one that needs invalidating (in which
case the `if` should be something else) or the `else` is dead. As written, every assignment
marks the object dirty, so a static object re-marked dirty in a loop never settles.
**Fix:** decide the intent; likely `if (value) RegisterObjectChange(this);` with the
dynamic→static transition handled by the static-layer manager that the :437 TODO names.
**Severity:** consistency · **Confidence:** high

### R-30 · `tso.world/Components/ObjectComponent.cs:600-622` · `GetParticleBounds` hard-codes a camera and computes a value it discards
`var image = DGRP.GetImage(1, 3, 1);` — direction 1, zoom 3, rotation 1, regardless of the
actual camera; `image.Sprites` is dereferenced at :605 **after** `minY/maxY` init but the
`image == null` case is unhandled. The loop accumulates `objOffset` (:616-618) which is then
**never used**. The magic divisor is `95f` against a comment that says `128`.
*Why it matters:* the estimate feeds `part.Volume`, which decides how many particle vertices
get allocated (`ParticleComponent.cs:171-180`).
**Fix:** pass the current zoom/rotation/direction in, null-check `image`, delete
`objOffset`, and reconcile 95 vs 128.
**Severity:** correctness · **Confidence:** high

### R-31 · `tso.world/Components/EntityComponent.cs:117-125` vs `176-188` · `Position` and `UnmoddedPosition` disagree
The `Position` setter folds terrain altitude into the stored Z:
`_Position.Z += blueprint.InterpAltitude(…) + MTOffset.Z / 16f;` — so assigning `Position`
twice double-counts it. `UnmoddedPosition` does not. The thumbnail path deliberately mixes
them (`WorldPlatform3D.cs:278` sets `UnmoddedPosition`, :298 restores it) while
`ObjectComponent.GetParticleBounds` and `GetScreenPos` read `Position`.
**Fix:** keep the raw tile position in the field and add the altitude in the `get`, so the
two accessors differ by construction rather than by convention.
**Severity:** correctness · **Confidence:** medium

### R-32 · `tso.common/Rendering/Framework/IO/InputManager.cs:530` · `return` inside the per-mouse loop
```csharp
if (mouse.LastMouseDown != null) { return; }   // "we already have mouse down"
```
This aborts the whole method, so any *remaining* entry in `state.MouseStates` never gets its
`MouseUp`. Should be `continue`. With the desktop single-mouse path
(`GameScreen.cs:122-127`) the list holds one entry, so impact is limited to the touch/multi-mouse
path — but the same loop is also where `mouse.LastMouseOver`'s `MouseOut` is delivered.
**Fix:** `continue`.
**Severity:** correctness · **Confidence:** high

### R-33 · `tso.common/Rendering/Framework/IO/InputManager.cs:487-490` · `NewMultiMouse` gates the button-state sync
```csharp
if (mouse.NewMultiMouse) mouse.NewMultiMouse = false;
else mouse.LastMouseDownState = mouseBtnDown;
```
The flag is consumed *before* `LastMouseDownState` is synced, so the first frame of a new
mouse never records its current button state and the following frame computes a spurious
`mouseDif` — a phantom `MouseDown`/`MouseUp` pair.
**Fix:** set `mouse.LastMouseDownState = mouseBtnDown` unconditionally; keep
`NewMultiMouse` only as a "first frame" marker if something needs it.
**Severity:** correctness · **Confidence:** medium

### R-34 · `tso.world/WorldEntities.cs:186-190` vs `tso.world/WorldState.cs:119-135` · duplicated cull-rect pass that has already drifted
`DrawObjBuf` computes `size`, `mainBd`, `diff` (`mainBd` and `diff` are **unused**) and then
assigns `state.WorldRectangle` — which `state.PrepareCulling(pxOffset)` on the very next line
overwrites. The two versions are not equivalent: `PrepareCulling` applies the `PreciseZoom`
inset (:125-130), `DrawObjBuf` does not.
*Why it matters:* an exact duplicate of a layout computation that has already drifted is the
classic setup for a future divergence. `StaticDraw` (:49) and `StaticDraw` in
`WorldArchitecture` (:97) carry the same unused-local pattern.
**Fix:** delete the dead computation; let `PrepareCulling` be the single owner of
`WorldRectangle`.
**Severity:** consistency · **Confidence:** high

### R-35 · `tso.world/Components/ParticleComponent.cs:68-72` · snowflake texture leaked on every regeneration
```csharp
if (Mode == ParticleType.SNOW)
    using (var file = File.OpenRead(…)) Tex = ImageLoader.FromStream(gd, file);
```
The previous `Tex` is overwritten without being disposed. `Draw` (:153) calls
`InitParticleVolume` whenever `Vertices == null` **or** the zoom/camera mode changed and this
is a weather particle — so scrolling through the three zoom levels in snow regenerates three
times, and each leaks a decoded PNG.
**Fix:** `Tex?.Dispose();` before the assignment (or route it through `TextureGenerator`).
**Severity:** perf · **Confidence:** high

### R-36 · `tso.world/Components/ParticleComponent.cs:369-370` · restores a blend state nobody asked for
`device.BlendState = BlendState.NonPremultiplied; device.DepthStencilState = DepthStencilState.Default;`
— the draw used `Additive`/`AlphaBlend` and `DepthRead`/`None` (:356-357). `NonPremultiplied`
is not the MonoGame default, so the "restore" installs a third state rather than the incoming
one.
**Fix:** capture and restore, as `WorldPlatform2D.GetObjectIDAtScreenPos` does at :142/:202.
**Severity:** consistency · **Confidence:** high

### R-37 · `tso.world/World.cs:257` · copy-paste in the scroll-vector table
`cursor = CursorType.ArrowRight; scrollVector = new Vector2(1, -1);` where `ArrowLeft` is
`(-1, 0)`, `ArrowUp` is `(0, -1)`, `ArrowDown` is `(0, 1)`. The value is discarded eight
lines later (`World.cs:288` re-zeroes `scrollVector` before the basis switch), so it is dead
today — but it is a live trap for anyone who "cleans up" line 288.
**Fix:** `(1, 0)`, and delete the whole dead first assignment block (:228-279 keeps only
`cursor`).
**Severity:** cosmetic · **Confidence:** high

### R-38 · `tso.world/Model/Blueprint.cs:508-544` · `GetGrassMask` caches forever and has empty branches
The diagonal-tile branch is two empty `if`/`else` bodies, so `GrassMask[i]` is left `0`
("no grass") for every tile with a diagonal wall regardless of its floor. The `GrassMask`
array is also never invalidated on a wall or floor change, so it is stale after the first
edit. (No callers in the tree today — dead, but it is the kind of dead that gets wired up.)
**Fix:** fill the branch from the two triangles' floor patterns and hook
`GrassMask = null` into `SignalWallChange`/`SignalFloorChange`.
**Severity:** correctness · **Confidence:** high

---

## 3. PERF

### R-39 · `tso.common/Utils/PPXDepthEngine.cs:571-622` · `CreateRenderTarget` ignores its `dformat` argument
```csharp
public static RenderTarget2D CreateRenderTarget(GraphicsDevice device, int numberLevels, int multisample,
    SurfaceFormat surface, int width, int height, DepthFormat dformat)
{ …
    return new RenderTarget2D(device, size.X, size.Y, (numberLevels > 1), surface,
        DepthFormat.Depth24Stencil8, multisample, RenderTargetUsage.PreserveContents);
}
```
`dformat` is never used. The `ReuseOrBuildSnapshotTarget` comment at :523-525 documents this
("which forces Depth24Stencil8 whatever it is asked for") but the function that could honour
it was never fixed.
*Why it matters:* `_2DWorldBatch.GenBuffers` explicitly requests `DepthFormat.None` for
`BUFFER_STATIC_DEPTH` and the object-ID buffer (`_2DWorldBatch.cs:182-186`) and still pays for
a full depth+stencil surface — roughly doubling the cost of those targets. At 1920×1080 plus
the 512 px scroll buffer that is ~31 MB per buffer of pure waste.
**Fix:** pass `dformat` through (falling back to `Depth24Stencil8` when the caller asks for
`None` but the format needs stencil), and audit the `!UseMRT && !SoftwareDepth` expression at
`_2DWorldBatch.cs:185` while you are there.
**Severity:** perf · **Confidence:** high

### R-40 · `FSO.UI/Framework/UIContainer.cs:81-91, 76, 108` · `Add` is O(subtree) per call
`Add`/`AddAt` call `AssignAsInvalidationParent(this)`, which walks `cont.Children` recursively
(`if (child is UIContainer) AssignAsInvalidationParent((UIContainer)child)`) on **every**
insertion. Building N children is therefore O(N²) in subtree size. Worse, for a plain
`UIContainer` the whole method is a no-op: `this.InvalidationParent` is null (only
`UICachedContainer` sets it, :24) so it returns at :83 having done nothing.
*Why it matters:* catalog and EOD panels add hundreds of items to a cached container on open.
**Fix:** delete the call. `UICachedContainer` should walk its own subtree once, from
`Invalidate()` or on child attach, not per `Add`.
**Severity:** perf · **Confidence:** high

### R-41 · `FSO.UI/Controls/UIButton.cs:72-90, 226-229` · two tooltip handlers per `new UIButton()`
`UIButton() : this(StandardButton)` runs the `Texture2D` ctor, which calls
`UIUtils.GiveTooltip(this)` (:89), and then the parameterless ctor calls it **again** (:75).
`m_TooltipHandler` keeps only the second; both hooks live in `UpdateHooks` forever. The same
is true by construction for `UIImage.cs:60` and `UITextEdit.cs:96` relative to their own
base chain. `ActivateTooltip()` (:226) adds a third, fourth, … with no removal path.
*Why it matters:* each `UITooltipHandler.Update` does a matrix invert test, a
`GetBounds().Contains` and four `state.UIState` writes per frame, per button.
**Fix:** register the hook once (constructor only), and give `UITooltipHandler` a `Dispose`
that calls `RemoveUpdateHook`, invoked from `UIElement.Removed()`.
**Severity:** perf · **Confidence:** high

### R-42 · `FSO.UI/Utils/UIUtils.cs:151, 161` + `UIElement.cs:593` · the drag hook can be added twice and only removed once
`MouseDown` → `DragControl.AddUpdateHook(UpdateHook)`; `MouseUp` → `RemoveUpdateHook`.
A second `MouseDown` before a matching `MouseUp` (double-click, a second button, or a
`MouseUp` that is never delivered because the control was hidden or moved out from under the
cursor) adds a **duplicate delegate** to `UpdateHooks`; the single `Remove` takes out one
copy and the survivor runs `Update` for the life of the control. There is no
`UIElement.Removed()` cleanup, and `MouseEvent` (registered in the ctor at :129) is never
unregistered, so the target keeps the handler → `DragControl` alive.
**Fix:** guard the Add with `if (!m_doDrag)`, and unregister both the hook and the mouse
listener from a new `UIDragHandler.Dispose` called on `Removed()`.
**Severity:** correctness / perf · **Confidence:** high

### R-43 · `FSO.UI/Controls/UIButton.cs:263-266` · four public events with no teardown
`OnButtonClick / OnButtonHover / OnButtonDown / OnButtonExit` are ordinary field-like events
and `UIButton` does not override `Removed()`. Panels that subscribe a capturing lambda from a
`Refresh()` accumulate handlers — e.g. `tso.client/UI/Archive/UIArchiveUserList.cs:292` and
`:313` subscribe per list rebuild, and `PersonSelection.cs:484-487` has the `-=` lines
commented out with a note. The button also keeps the panel alive through the delegate.
**Fix:** override `Removed()` in `UIButton` to null the four delegates, and audit the
subscribe sites for handlers added outside a constructor.
**Severity:** perf · **Confidence:** high

### R-44 · `FSO.UI/Framework/UIElement.cs:822-835` · every VFont string draw costs a batch `End`/`Begin`
`batch.End(); style.VFont.Draw(…); batch.Begin(…)` runs per label, per frame. Each pair is a
full sprite-batch flush plus a state re-apply, and — see R-24 — the re-`Begin` is also
missing half the state.
*Why it matters:* a lot screen or a populated EOD draws dozens of labels; this doubles the
number of batch submissions for the UI.
**Fix:** batch all VFont glyph quads for a frame and submit them once (MSDF renders to a
shared vertex/index buffer), or at minimum route the `Begin` through the shared
`Restart()` from R-24.
**Severity:** perf · **Confidence:** high

### R-45 · `FSO.UI/Framework/UIElement.cs:1061-1085` · a failed texture lookup allocates a 1×1 texture, per call
```csharp
public static Texture2D GetTexture(ulong id)
{
    try { return Content.Content.Get().UIGraphics.Get(id).Get(GameFacade.GraphicsDevice); }
    catch (Exception e) { }
    return new Texture2D(GameFacade.GraphicsDevice, 1, 1);
}
```
The `catch` swallows the exception into an unused local and the fallback is a **fresh GPU
texture on every call** from `Draw` code, never disposed. The declared-but-unused
`private static List<ulong> UI_TEMP_CACHE` (:1060) is the fossil of the intended memo. Both
overloads also dereference `GameFacade.GraphicsDevice` *outside* the try, so a null device
throws out of the fallback path.
*Why it matters:* one missing EOD/catalog texture turns a per-frame allocation storm.
**Fix:** one cached 1×1 white `Texture2D` (or reuse `TextureGenerator.GetPxWhite`), populate
`UI_TEMP_CACHE`, and log the failure once per id.
**Severity:** perf · **Confidence:** high

### R-46 · `tso.world/Utils/_2DWorldBatch.cs:244-257` · unbounded sprite-group growth under software depth
```csharp
while (!added) {
    if (i >= Sprites.Count) Sprites.Add(new _2DSpriteGroup(FSOEnvironment.SoftwareDepth));
    if (FSOEnvironment.SoftwareDepth && Sprites[i].SprRectangles.SearchForIntersect(sprite.AbsoluteDestRect)) i++;
    else { … added = true; }
}
```
With `SoftwareDepth` on, a screen full of mutually overlapping sprites (a dense floor with
the parallax mask, a headline stack) walks the list linearly, allocating a group per
overlap, and each `SprRectangles` is itself a linear scan — O(n²) with a group allocation
per step. `Begin` clears `Sprites` but never releases the groups, so the list is reused
(cheap) but can be very large.
**Fix:** sort by draw order / bucket by tile before partitioning, or use a real
per-pixel overlap test on the target.
**Severity:** perf · **Confidence:** medium

### R-47 · `tso.common/Utils/PPXDepthEngine.cs:26-30, 219-225` · static `SpriteBatch` and an unguarded `SSAAFunc`
`InitGD` does `SB = new SpriteBatch(gd)` on every call, abandoning the previous one. It is
called from `World.Initialize` (`World.cs:107`), so a second `World` (a sub-world, a
thumbnail world) leaks one. `DrawBackbuffer` does `SSAAFunc(GD, Backbuffer)` with no null
check, and `SSAAFunc` is only ever assigned inside `World.ChangeAAMode` (:973) — so a
`PPXDepthEngine.SSAA > 1` reached any other way is an NRE.
**Fix:** dispose the old `SB` in `InitGD`; default `SSAAFunc = (gd, rt) => { }` or guard the
call.
**Severity:** perf / crash (latent) · **Confidence:** high

### R-48 · `tso.world/Platform/WorldPlatform3D.cs:259-265` · O(n²) re-lookup and duplicate-match hazard in object thumbnails
```csharp
var objs = objects.OrderBy(x => { x.UpdateDrawOrder(state); return x.DrawOrder; }).ToList();
…
var tilePosition = positions[Array.IndexOf(objects, obj)];
```
`Array.IndexOf` is a linear scan with `EqualityComparer<ObjectComponent>.Default` (reference
equality, since `ObjectComponent` does not override `Equals`) performed once per object, and
if the same component appears twice in `objects` both get the first index.
**Fix:** build an `ObjectComponent → index` dictionary (or a `(component, position)` tuple
list) before sorting, and sort that.
**Severity:** perf · **Confidence:** high

### R-49 · `tso.world/Components/ParticleComponent.cs:199-205` · `Matrix.Invert` per particle system, per frame
`var pos = Vector3.Transform(Vector3.Zero, Matrix.Invert(world.View));` and
`Matrix.Invert(rot)` at :199 — two full 4×4 inversions per drawn particle system, on the
2D path where `world.View` is an *orthographic* camera whose inverse is meaningless for the
billboard maths. `Matrix.Invert` also allocates nothing but is ~64 flops; `in * Matrix.CreateScale(0.5f)`
at :240/:249 is another two matrix multiplies.
**Fix:** compute once per frame in `WorldState.PrepareCamera` and cache.
**Severity:** perf · **Confidence:** medium

### R-50 · `tso.world/Components/ObjectComponent.cs:550-568` vs `tso.world/Model/Blueprint.cs:384-389` · dead particles escape disposal
`ObjectComponent.Draw` removes a dead particle from `Particles` (`if (part.Dead) Particles.RemoveAt(i--)`).
The particle removes *itself* from `Blueprint.ObjectParticles` in `ParticleComponent.Update`
(`ParticleComponent.cs:117, 133`) but not from its owner's `Particles`, and
`Blueprint.RemoveObject` disposes via `component.Particles` only. A particle dropped by the
`Draw` path is no longer in either list that disposes it — its `VertexBuffer`/`IndexBuffer`
were already freed by the `Dispose` in `Update`, but any later respawn bookkeeping is lost.
**Fix:** have one owner. Either `ObjectComponent.Draw` also removes from
`Blueprint.ObjectParticles`, or `ParticleComponent` be given an `Owner` it can deregister
from.
**Severity:** perf · **Confidence:** medium

---

## 4. CONSISTENCY / DEAD CODE / MAGIC

### R-51 · `tso.world/WorldState.cs:694-701` · `GetWorldFromTile(Rectangle)` compounds the multiply on Y and Height
```csharp
tile.X     = (int)(tile.X * WorldUnitsPerTile);
tile.Y    *= (int)(tile.Y * WorldUnitsPerTile);      // tile.Y = tile.Y * (int)(tile.Y * 3)
tile.Width = (int)(tile.Width * WorldUnitsPerTile);
tile.Height*= (int)(tile.Height * WorldUnitsPerTile);
```
`X` and `Width` use plain assignment; `Y` and `Height` use `*=`, so they become `y * (int)(3y)`
— quadratic. Currently unreferenced, so latent; the moment anyone uses it the result is
garbage.
**Fix:** change both `*=` to `=`, and add a unit test.
**Severity:** correctness · **Confidence:** high

### R-52 · `tso.world/WorldState.cs:750-751` · `TileSin60` / `TileSin30` divide by √5
```csharp
TileSin60 = TilePxWidth  / (float)Math.Sqrt(5.0);
TileSin30 = TilePxHeight / (float)Math.Sqrt(5.0);
```
`sin 60° = √3/2` and `sin 30° = 1/2`; the divisor should be `2` (and the √3 in the numerator).
Both fields are unused, so this is currently a landmine.
**Fix:** `TilePxWidth / 2f` and `TilePxHeight / 2f` respectively, or delete them.
**Severity:** correctness · **Confidence:** high

### R-53 · `FSO.UI/Framework/Matrix2D.cs:11-12, 41-59` · public mutable statics and a dead local
`IDENTITY` and `DEFAULT_SCALE` are `public static float[]`, handed out by reference. Any
caller that mutates them corrupts the shared identity used by `UIElement._Mtx`
(`UIElement.cs:131` clones it, so the damage is bounded but real). `Rotate` declares
`Vector2 tempTransform = new Vector2(0, 0);` and initialises `tempM0..tempM3` immediately
before overwriting all four.
**Fix:** make them `readonly` properties returning a fresh array, or `ReadOnlySpan<float>`;
delete `tempTransform` and the redundant initialisers.
**Severity:** consistency · **Confidence:** high

### R-54 · `FSO.UI/Utils/MathCache.cs:23-30` · double dictionary lookup, no locking, defeated by `Invalidate()`
`Calculate` does `ContainsKey` then `Add` then `m_Value[id]` — three hashes per call and a
TOCTOU window if anything ever calls it off-thread. `Invalidate()` clears the whole
dictionary, so any per-id `Invalidate(id)` is undone by the next full clear.
**Fix:** `if (m_Value.TryGetValue(id, out var v)) return (TResult)v;` and either document
that it is game-thread-only or lock it.
**Severity:** perf / consistency · **Confidence:** high

### R-55 · `FSO.UI/Framework/UIContainer.cs:275, 286-298` + `UICachedContainer.cs:109` · `lock (Children)` held across driver calls
The codebase's own `//todo: why are all these locks here, and what kind of problems might
that cause` appears twice. `UICachedContainer.PreDraw` takes `lock (Children)` around
`child.PreDraw(batch)` (:50-57) and again around `child.Draw(batch)` (:76-83) — i.e. a lock
is held across sprite-batch submission and `RenderTarget2D` binding. `GetChildrenSafe` also
returns the **shared** `ChildCopy` array by reference, so two consumers in the same frame
would see each other's copies.
**Fix:** resolve to a local `UIElement[]` inside the lock, then draw outside it. Copy
`ChildCopy` out or make `GetChildrenSafe` return a fresh array.
**Severity:** perf / consistency · **Confidence:** high

### R-56 · `tso.world/LMap/LMapBatch.cs:151-164` · `Projection` hard-codes 16 px/tile while everything else is `resPerTile`
`resPerTile = ultra ? targetResPerTile : targetResPerTile / 2;` (:151) drives every target
size, but `Projection = Matrix.CreateOrthographicOffCenter(new Rectangle(0, 0, (w-1)*16, (h-1)*16), -10, 10)`
(:163) and `LightEffect.SSAASize = new Vector2(1f / 600)` (:177) are absolute. Moving
`WorldConfig.ShadowDetail` off 16 silently changes the shadow projection with no code
acknowledging it, and `ChangedWorldConfig` documents `ShadowDetail` as *the* setting that
lands here (`World.cs:1016-1021`).
**Fix:** derive both from `resPerTile`; keep 16 only where world units (not shadow-map
pixels) are meant.
**Severity:** consistency · **Confidence:** high

### R-57 · `tso.world/LMap/LMapBatch.cs:213-231, 291-323` · `Priority` overflows and the iteration is fragile
`room.Priority = (important || room.Priority == int.MaxValue) ? int.MaxValue : (room.Priority * 2);`
doubles on every dirty mark with no ceiling, so after 31 marks `Priority` goes negative and
`ParseInvalidated`'s `if (unimportantRoomsProcessed >= rm.Priority) continue;` (:304) never
lets that room redraw. Separately, `ParseInvalidated` removes from `DirtyRooms` (:300, :317)
while iterating a lazy `DirtyRooms.OrderBy(...)` (:291) and uses `RemoveAll` per room — safe
only because `OrderBy` buffers on first `MoveNext`, and O(n²) besides.
**Fix:** clamp the doubling (`Math.Min(room.Priority * 2, 1 << 20)`), snapshot the list before
sorting, and drop the removed ids into a `HashSet` to apply after the loop.
**Severity:** correctness · **Confidence:** high (code), low (impact)

### R-58 · `tso.world/WorldContent.cs:26-48, 75-111` · `LoadEffects` would orphan every live renderer
`LoadEffects` replaces all nine effect objects. `_2DWorldBatch` captures
`this.Effect = WorldContent._2DWorldBatchEffect` in its constructor (`_2DWorldBatch.cs:128`)
and `LMapBatch` captures `WorldContent.Grad2DEffect` / `Light2DEffect` in its constructor
(`LMapBatch.cs:101-102`) — so a reload leaves every existing renderer holding a stale
`Effect`. `SunLightingModel.cs:946` documents "re-run `WorldContent.LoadEffects`" as the way
to add a technique, i.e. this path is *intended* to be used. `_be`, `_TextureVerts` and
`_TextureVertsInv` are static with no reset.
**Fix:** make the effect holders resolve through `WorldContent` at draw time, or have
`LoadEffects` re-bind the existing instances. Also dispose the effects it replaces.
**Severity:** correctness · **Confidence:** high (code), low (currently only called from `Init`)

### R-59 · `tso.world/WorldContent.cs:76-105` · the "Inv" vertex buffer is the *non*-flipped one, and it is dead
`GetTextureVerts` writes `[(-1,-1), (-1,1), (1,-1), (1,1)]` — strip winding `(0,1,2)` has
`z = 0*0 - 2*2 = -4`, i.e. back-facing. `GetTextureVertsInv` writes the indices out of order
so the array becomes `[(-1,-1), (1,-1), (-1,1), (1,1)]` — front-facing. The names are
inverted. `GetTextureVertsInv` has **no callers**; the three callers of `GetTextureVerts`
(`SSAADownsample.cs:17`, `AbstractSkyDome.cs:309`, `EntityComponent.cs:228`) all draw full-screen
quads with `CullNone`, which is why the flipped winding is invisible.
**Fix:** swap the two bodies (or the two names) and delete the unused one; add a comment
saying which way round each winds.
**Severity:** consistency · **Confidence:** high

### R-60 · `tso.world/Platform/WorldPlatform2D.cs:39-128`, `WorldPlatform3D.cs:40-158`, `Facade/LotFacadeGenerator.cs:89-171` · thumbnail paths mutate global state with no `try/finally`
All three save `Zoom`, `Rotation`, `Level`, `CenterTile`, `PreciseZoom`, `Cutaway`,
`OutsideColor`, `BuildMode`, `RenderingThumbnail` (and `LotFacadeGenerator` also
`SilentLevel`, `WallOffsetView`, `ScissorRectangle`) and restore them at the end of the
method. Any throw in the middle — and these methods call into shaders, `BuildableReset`,
`bp.Objects` loops and `WCRC.Generate` — leaves the lot in a corrupted state: wrong zoom and
rotation, `RenderingThumbnail` stuck `true` (which then keeps camera transitions disabled
forever, per R-18), `Cutaway` replaced by a scratch array, and the 2D batch left in a paused
state. The two `WorldPlatform` thumbnail methods also hand back the platform's *own*
`RenderTarget2D` (which the next call overwrites and, per R-14, leaks).
*Why it matters:* a single bad lot or missing mesh turns one thumbnail into a permanently
broken lot view.
**Fix:** wrap the body in `try { … } finally { restore }`, and return a copy
(`TextureUtils.Clip`/`GetData` into a fresh `Texture2D`) rather than the live target.
**Severity:** correctness · **Confidence:** high

### R-61 · `tso.world/Components/TerrainComponent.cs:208-214, 855-862` · disposal guarded on the wrong field
Both `RegenTerrain` and `Dispose` gate on `if (VertexBuffer != null)` and then dereference
`IndexBuffer`, `GridIndexBuffer`, `TGridIndexBuffer` unguarded (the grids use `?.`).
`RegenTerrain` is the hot rebuild path (every wall/floor edit).
**Fix:** null-check each buffer independently, or centralise in a `ReleaseBuffers()` helper
used by both.
**Severity:** crash (latent) · **Confidence:** high

### R-62 · `tso.world/Utils/_2DWorldBatch.cs:879-886` · `Dispose` has no idempotence guard
`SpriteIndices.Dispose(); foreach (var buf in Buffers) buf.Dispose();` — no `IsDisposed`
field, no null checks, and the `Buffers` list is reassigned wholesale by `GenBuffers` and
`ResizeBuffer`. `World.Dispose` reaches it directly (`World.cs:1223`), and sub-worlds share
the parent's `_2D` (guarded only by `!(this is SubWorldComponent)`).
**Fix:** add a `Disposed` flag and set it in `Dispose`; null the fields after disposing.
**Severity:** crash (latent) · **Confidence:** medium

### R-63 · `tso.world/Utils/_2DWorldBatch.cs:768` · `if (count > 50) //completely arbitrary number`
The VertexBuffer-vs-`DrawUserIndexedPrimitives` threshold, with the comment admitting it.
`count` is `indices.Length / 3`, so "50" means 50 quads, not 50 anything else.
**Fix:** name it (`MinSpritesForGPUBuffer`), put it in `WorldConfig`, and say what it trades.
**Severity:** cosmetic · **Confidence:** high

### R-64 · `tso.world/Utils/_2DWorldBatch.cs:131, 555, 730` · three TODO-shaped real gaps
`:131` `//TODO: World size` sits directly above the `Effect.WorldToLightFactor`
initialisation whose value is `(1/(3*75), 1/(3*2.95f), 1/(3*75))` — a hard-coded 75-tile lot
that `LMapBatch.SetMapLayout` later overwrites with the real `Blueprint.Width`/`:Height`. If a
lot view is ever drawn before `PrepareLighting`, the first frame uses 75. `:555` notes
"no depth sprites have fixed depth relative to their position". `:730` notes that sprite
vertices are rebuilt on the CPU every frame.
**Fix:** derive the `WorldToLightFactor` from the blueprint at `World.Initialize` rather than
a literal; treat the other two as known costs, not TODOs.
**Severity:** consistency · **Confidence:** high

### R-65 · `FSO.UI/Utils/UIUtils.cs:38-101` · `WordWrap` drops any word wider than the box, and `MaxWidth`/`Positions` drift
When the first word on a line overflows, `lineBuffer` is emptied at :65, the inner
`for (j = words[i].Length-1; j > 0; j--)` finds no prefix that fits and falls through
**without adding anything**, and :85 then emits an **empty line** while :93's
`words.RemoveRange(0, i)` still removes the offending word — so the word silently vanishes
from the rendered text. Separately, `maxWidth` is not updated in the `atMax` truncation
branch (:58-64) or in the split branch, so `result.MaxWidth` under-reports (which
`UILayer.DrawTooltip:361` uses as the tooltip box width), and `curpos` (:74, :82) adds a
trailing space for the last word of every line, so `result.Positions` drifts.
The function is O(n²) in `style.MeasureString` calls.
**Fix:** rewrite as a single pass with an explicit "long word" case (either hard-split it or
emit it on its own line unclamped), and measure each completed line once for `MaxWidth`.
**Severity:** correctness · **Confidence:** high

### R-66 · `tso.world/WorldEntities.cs:98-107` · `DrawAvatarTransparency` does not draw transparency
The method only calls `avatar.DrawHeadline3D`, and only when `!state.Cameras.Safe2D`. The
class doc at :20-21 says "Semitransparent avatars (ghosts) are drawn after all objects", and
`ObjectComponent`/`AvatarComponent` both carry ghost/headline state. The name, the call site
in `World.InternalDraw:723` (between `Entities.Draw` and the SM64 draw) and the behaviour
disagree.
**Fix:** either rename to `DrawAvatarHeadlines3D` or actually draw the ghosts.
**Severity:** consistency · **Confidence:** high

### R-67 · `tso.common/Rendering/Framework/GameScreen.cs:36, 56, 140` · a shared static list is aliased into per-frame state
`State.FrameTextInput = TextCharacters;` aliases a `static List<char>` that is
`Clear()`ed at the end of the same `Update`. Any consumer that captures the reference past
the frame sees an empty list; the reference comparison `if (IndoorsDat != indoors)` style
identity trick that `ParticleComponent` uses elsewhere makes this an easy trap.
**Fix:** hand out a per-frame copy, or make `TextCharacters` an instance field.
**Severity:** consistency · **Confidence:** high

### R-68 · `FSO.UI/Framework/UIElement.cs:1093-1110` · `ManualTextureMask` wipes the texture it is given
```csharp
Color[] data = new Color[Texture.Width * Texture.Height];
//Texture.GetData(data);
for (int i = 0; i < data.Length; i++) { if (data[i] == ColorFrom) data[i] = ColorTo; }
Texture.SetData(data);
```
The read is commented out, so `data` is all-default (transparent black) and `SetData` writes
a fully transparent texture. The working implementation is
`tso.common/Utils/TextureUtils.cs:221`. `UINeighborhoodSelectionPanel.cs:228,230` calls the
broken one (`ManualTextureMask(ref HouseTex, new Color(248,0,248,255))`).
**Fix:** delete `UIElement.ManualTextureMask` and point the two call sites at
`TextureUtils.ManualTextureMask`.
**Severity:** correctness · **Confidence:** high

### R-69 · `FSO.UI/Framework/UIElement.cs:1180-1184` · `Async` spawns a foreground `Thread` per call
`new Thread(new ThreadStart(handler)).Start()` — not background, never joined, no exception
handling. A throw inside `handler` becomes an unhandled exception and takes the process down;
nothing throttles the rate.
**Fix:** `Task.Run`, or at minimum `t.IsBackground = true` wrapped in a
`try { … } catch { log }`.
**Severity:** crash · **Confidence:** high

### R-70 · `FSO.UI/UILayer.cs:284-285, 364-365` · off-by-one window bounds and one-sided tooltip clamping
`mousePosition.X > 0 && … Y > 0` excludes the top and left pixel rows of the window from
`MouseOverWindow`, and therefore from `ProcessMouseEvents` and all mouse events. The tooltip
clamp is `position.X = Math.Min(position.X, screenWidth - width)` with no left clamp, so a
tooltip wider than the screen goes off the left edge, and `position.Y = Math.Max(position.Y, height)`
assumes the tooltip is always anchored above the cursor.
**Fix:** `>= 0` on both axes; clamp the tooltip into the viewport on both axes.
**Severity:** cosmetic · **Confidence:** high

### R-71 · `tso.world/Model/Blueprint.cs:131, 393-396, 421` · dead guards and an integer-width assumption
`if (true || WorldConfig.Current.Shadow3D || FSOEnvironment.Enable3D)` (:131) can never be
false. `GetOffset` returns `ushort`, so a lot wider than 256 tiles wraps. `GetFineBounds`
returns `new Rectangle(int.MaxValue, int.MaxValue, negative, negative)` when `FineArea` has
no set bits, and `GetThumbCenterTile` then feeds that into the offset maths at :430.
**Fix:** delete the `||` chain (or restore the setting); make `GetOffset` return `int`; have
`GetFineBounds` return `Rectangle.Empty` and have `GetThumbCenterTile` check.
**Severity:** correctness · **Confidence:** high

### R-72 · `tso.world/Model/Blueprint.cs:173-207` vs `209-241` · dead negative checks in the altitude samplers
Both `InterpAltitude` (:178) and `InterpNormal` (:215) do
`var baseX = (int)Math.Max(1, Math.Min(Width-1, …)); if (baseX < 0 || baseY < 0) return …;`
— `baseX` can never be negative after a `Math.Max(1, …)`, so the guard is dead.
`InterpNormal` additionally omits the `Math.Floor` that `InterpAltitude` has, so the two
sample different tiles for the same input, and it feeds `Vector3.Normalize` a cross product
that is zero on flat ground.
**Fix:** drop the dead checks, add `Math.Floor` to `InterpNormal`, and guard the normalise.
**Severity:** consistency · **Confidence:** high

---

## Top 10 by severity

| # | id | one-liner |
|---|----|-----------|
| 1 | R-03 | `Matrix2D.TransformPoint(ref,ref)` recomputes `y` from the new `x`; every `UIElement.LocalRect` is wrong under rotation/non-uniform scale |
| 2 | R-04 | Arrow/Home/End keys never reach `UITextEdit` — `UnhandledKeys` is unreachable because `FrameTextInput` is a never-null static |
| 3 | R-11 | 2D sprite texture cache is cleared **every frame** for the first 3600 frames; `LastCacheClear` is never written |
| 4 | R-13 | `LMapBatch.Dispose` never frees `LightBuf` or `ShadowTargBlit`; `Init` re-creates both per lot load |
| 5 | R-14 | `WorldPlatform3D.GetLotThumb` nulls `LotThumbTarget` without disposing — one leaked RT per thumbnail |
| 6 | R-15 | `World.Dispose` leaks `WCRC`, `FloorGeom`, `WallComp` and all of `WallCache2D`'s vertex/index buffers |
| 7 | R-02 | disposed `GridIndexBuffer` re-bound after a terrain regen; the `TGrid` twins are guarded and these are not |
| 8 | R-17 | `State.Light = Light` sits inside `if (Light == null)` after a non-null assign — lighting reads a disposed `LMapBatch` |
| 9 | R-20 | lighting leaves the device on a scissor `RasterizerState` and `Opaque`/`Min`/`Max`/`Mul` blend; the 2D batch never resets the rasterizer |
| 10 | R-24 | three separate `SpriteBatch.Begin` call sites drop `SpriteSortMode.Immediate` and `CullNone`, so overlapping UI is re-ordered by texture |

Runners-up worth fixing in the same pass: **R-01** (null `DepthMask` in the portal path),
**R-16** (`InterpNormal` weights), **R-40** (`Add` is O(subtree) and a no-op),
**R-65** (`WordWrap` drops over-long words), **R-05** (`GetBuffer` returns null and poisons the pool).

## Not covered / caveats

* `tso.client/Rendering/City/**` was sampled for the patterns above but not read line by line;
  the city terrain is a large, largely self-contained renderer and a single-player TS1 fork
  spends little time in it.
* `FSO.UI/Framework/UITween.cs`, `MSDFFont*.cs`, `TextRenderer.cs` and the EOD `.ts` parser
  were not read in full.
* No build was run. All findings are from static reading; the "confidence" column is the
  confidence in the diagnosis, and each item states what would have to be true for it to bite.

---

## Corrections from the fix wave (2026-09-28)

Two findings were re-diagnosed while fixing them; the fixes are in, the text above is corrected here.

- **R-16 (`InterpNormal`) — the prescribed fix was wrong.** For a bilinear patch the y-derivative
  is independent of `yLerp`; the proposed `yLerp * (xl2 - xl1) * TerrainFactor` makes one tile
  boundary shade flat and the next twice as steep, and adjacent tiles disagree. The real defect was
  the **x** blend axis and a negative-coordinate lerp sign (`% 1f` going negative and flipping both
  gradient halves). The engine's own `TerrainComponent.GetNormalAt` is the reference; the fix is
  pinned by numerical differentiation of `InterpAltitude`.
- **R-65 (`WordWrap`) — the diagnosis understated it.** The hard-split path *did* work when a
  prefix fit, so a word was not silently deleted. The real failure is worse: when no non-empty
  prefix fit it emitted an empty line, removed nothing and **looped forever**, growing
  `Lines`/`Positions` without bound — and `UILabel.NumLines` asks for a zero-width box whenever
  `Size` is unset, so it was reachable. Rewritten as a pure `WordWrapper` with a "no text is lost
  across widths 0..320" property test.
