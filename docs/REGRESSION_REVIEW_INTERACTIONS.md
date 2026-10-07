# Regression review — interaction offering, contained objects, placement

**Read-only review of the audit-fix waves (W/X/Y + crash fixes) for the two player-reported regressions.**

1. *"Cooked dinner cannot be clicked and interacted with; the eat action is completely broken."*
2. *"Placing elements on other elements (coffeemaker on dishwasher) is broken and only sometimes works."*

Scope read: every working-tree change under `FreeSO/TSOClient/{tso.simantics, tso.world, tso.client,
FSO.UI, tso.common}` plus the Simitone `UILotControl` / `UIPieMenu` / `UIObjectHolder`.
Method: for each candidate, ask (a) does it change behaviour for clicking an object **contained in
another object**, and (b) does it change behaviour for **placing onto another object's slot**.
Nothing here was edited. `Simitone.Client` compiles clean (`-t:Compile`, 0 errors) at the time of
writing, so nothing below is a compile break — they are all behaviour.

> **Tree is live.** `EntityComponent.cs` was edited by another agent at 13:18 while this review was
> running (`UnmoddedDraw` setter now calls `OnPositionChanged()`). Line numbers below are against
> the tree as of that edit. Re-check before acting.

---

## Summary table

| # | Where | Regression | Likelihood |
|---|-------|-----------|-----------|
| 1 | `WorldPlatform3D.cs:372-373` + `EntityComponent.cs:254` | both (click **and** place) | **High** |
| 2 | `EntityComponent.cs:105-153` altitude moved set→get | both, on sloped lots | **High** (lot-dependent) |
| 3 | `EntityComponent.cs:176-189` `UnmoddedDraw` is a one-way latch | both | **High** |
| 4 | `VMThread.cs:387-420, 1047` + `VMCancelPolicy.cs:156` hard `AbortNow` | eat | **Medium-High** |
| 5 | `VMMultitileGroup.cs:454-470` `Combine` now rotates merged offsets | place | **Medium** |
| 6 | `Blueprint.cs:289` / `TerrainHeightField.cs:68` x-gradient now blended by `yLerp` | place (3D) | **Medium** |
| 7 | `VMRoutingFrame.cs:1083-1114` `PortalTurns` no longer counts ticks | eat (walk-to-counter) | **Medium** |
| 8 | `UIContainer.cs:123` `Remove` now nulls `Parent` | click (pie menu) | **Medium** |
| 9 | `VMGameObject.cs:273-282` `GetSlotHeight` can answer −1 | place | **Low-Medium** |
| 10 | `UIContainer.cs:291` `GetChildrenSafe` allocates per frame | click (perf/stutter) | **Low** |
| 11 | `UICachedContainer.cs:53-58` zero-size early return drops a frame | click (dialog) | **Low** |
| 12 | `UIElement.cs:1091-1101` `HitTestArea` now honours `IsDisabled` | click | **Low** |

---

## 1. `UnmoddedDraw` is never restored — the restore is in the wrong order

**`FreeSO/TSOClient/tso.world/Platform/WorldPlatform3D.cs:345, 352, 372-373`**

```csharp
345:  var oldUnmodded = obj.UnmoddedDraw;      // saved
...
352:  obj.UnmoddedPosition = tilePosition;    // <-- this SETTER latches UnmoddedDraw = true
...
372:  obj.UnmoddedDraw = oldUnmodded;         // restored...
373:  obj.UnmoddedPosition = oldObjPos;       // ...and immediately set to true again
```

**What changed.** The wave introduced `EntityComponent.UnmoddedDraw`
(`FreeSO/TSOClient/tso.world/Components/EntityComponent.cs:162, 176-189`) and made the
`UnmoddedPosition` **setter** latch it (`EntityComponent.cs:254`):

```csharp
251:  set { _Position = value; UnmoddedDraw = true; OnPositionChanged(); _WorldDirty = true; }
```

The comment at `WorldPlatform3D.cs:341-344` states the intent — *"It used to be left set, which is how
an object came out of a catalogue page with the terrain altitude missing from its position for the
rest of its life."* — but the restore at 372 is immediately undone by the setter call on the very
next line. The flag is a **one-way latch**: once true, `AltitudeAt` (`EntityComponent.cs:148-153`)
returns `0f` forever and the component's reported `Position` never includes terrain altitude again.

**Exact scenario it breaks.** `World.GetObjectThumb` is called on **live lot components** in
Simitone's dialog path — `Client/Simitone/Simitone.Client/UI/Panels/UILotControl.cs:497-506` builds
`objComps` from `objects[i].WorldUI` of the entity the dialog is about, i.e. real objects on the
lot. After the first dialog-with-icon in 3D camera mode, **every object in that entity's multitile
group** (and every object later thumbnailed) permanently reports a position that is short of the
terrain altitude. From then on:

* the 2D sprite vertices (`DGRPRenderer.cs:316-321`, `ObjectComponent.OnPositionChanged` at
  `ObjectComponent.cs:365-368`) and the hit box used by the OBJID pick
  (`WorldPlatform2D.GetObjectIDAtScreenPos` at `WorldPlatform2D.cs:157-232`, which culls with
  `ObjectComponent.DoDraw` → `world.WorldRectangle.Intersects(dgrp.Bounding)`) disagree with
  `VMEntity.Position`, so a click either misses a contained dinner entirely or lands on the counter;
* a slot's world position (`ObjectComponent.GetSLOTPosition`, `ObjectComponent.cs:191-192`) is the
  container's `Position + centerRelative` while `centerRelative.Z` still carries the authored slot
  height — the two halves come from different conventions, so every contained object is offset by
  the container's altitude;
* `UIObjectHolder.MoveSelected` (`FreeSO/TSOClient/tso.client/UI/Panels/UIObjectHolder.cs:277-279`
  and `Client/Simitone/.../UIObjectHolder.cs:277-279`) reads `target.VisualPosition` and feeds it
  straight back into `SetVisualPosition`, so the buy-mode cursor tiles inherit the missing altitude
  and the ghost the player is dragging is not where the engine will seat the object.

**Fix.** Swap the two lines, and set the flag explicitly rather than relying on the setter:

```csharp
obj.UnmoddedPosition = oldObjPos;   // write the position back first
obj.UnmoddedDraw   = oldUnmodded;   // then restore the flag (setter latches it to true)
```

Better still: make the thumbnail path save/restore `UnmoddedDraw` in a `try/finally` around the
whole loop body, and give `UnmoddedPosition`'s setter no side effect on the flag at all — the
thumbnail can set `UnmoddedDraw` itself, explicitly, for the duration of the draw.

---

## 2. Terrain altitude moved from the `Position` setter to the getter

**`FreeSO/TSOClient/tso.world/Components/EntityComponent.cs:105-153`** (audit item R‑31)

```csharp
// was: setter folded altitude into _Position
//  _Position = value;
//  if (blueprint != null) _Position.Z += blueprint.InterpAltitude(...) + MTOffset.Z / 16f;
//
// now:
148:  private float AltitudeAt(Vector3 raw) { ... return bp.InterpAltitude(0.5f + raw - MTOffset/16) + MTOffset.Z/16f; }
113:  return WithAltitude(_Position);
```

**What changed.** The altitude used to be baked in at *assignment* time and read straight out; it is
now added on *read*, from the current stored tile position. For an object positioned once the value
is identical, which is why the comment claims "nothing else changes". It is not true for anything
that mutates `_Position` outside the `Position` setter, and there is such a thing.

**Exact scenario it breaks.** `EntityComponent.PrepareSlotInterpolation`
(`EntityComponent.cs:225-232`) and `PrepareSnapInterpolation` (`212-221`) write
`SnapSelfPrevious = _Position` and then `PreviousSlotOffset = Container.GetSLOTPosition(...) - Container.Position`.
With the old code both sides of that subtraction were the *raw* tile position, so the offset was the
pure authored slot offset. With the new code the right side is `raw + altitude` and the left side
(`GetSLOTPosition` → `this.Position`) is also `raw + altitude`, so that one is still fine — but
`EntityComponent.cs:127`:

```csharp
127:  var oldP = Container.Position + PreviousSlotOffset.Value;
128:  var newP = Container.GetSLOTPosition(ContainerSlot, false);
```

adds the (now altitude-carrying) `Container.Position` to a `PreviousSlotOffset` that was computed as
a *difference* of two altitude-carrying values — i.e. a difference in which the altitude cancels.
On a sloped lot the interpolated position of a contained object therefore **gains the terrain
altitude twice** for the whole 20-frame `PrepareSnapInterpolation` window, which is exactly the
window a dinner passes through when the sim puts it on the counter. The dinner visibly hops, and
because `dgrp.Bounding` is derived from the same position, the OBJID pick target for those 20
frames is somewhere the object is not drawn.

**Fix.** Keep the raw/alted split out of the interpolation entirely: store
`PreviousSlotOffset` and `SnapSelfPrevious` as raw tile positions and apply `WithAltitude` once, at
the point of return, exactly as the non-interpolated branch does.

---

## 3. `ObjectComponent.GetSLOTPosition` reads a position whose conventions now disagree

**`FreeSO/TSOClient/tso.world/Components/ObjectComponent.cs:182-193`**

```csharp
188:  var centerRelative = new Vector3(off.X/16f, off.Y/16f,
         ((item.Height != 5 && item.Height != 0) ? SLOT.HeightOffsets[item.Height-1] : off.Z) * (1/5.0f));
191:  return this.Position + centerRelative;
```

**What changed.** Nothing in this file — but it is the join point for #1 and #2. `centerRelative.Z`
is authored in the SLOT chunk's own units; `this.Position` now has the altitude added by the getter.
For a container that has ever been thumbnailed (#1) the two are in different frames, and the offset
is applied to a position that no longer contains what it used to.

**Exact scenario it breaks.** The dinner in the counter slot. `EntityComponent.Position` for a
contained object is *entirely* `Container.GetSLOTPosition(ContainerSlot, false)`
(`EntityComponent.cs:124`), so any error in the container's `Position` propagates one-for-one onto
the child's draw position, its `dgrp.Bounding`, and therefore the click target.

**Fix.** Covered by #1 and #2. Add a regression test: build a container with a known slot offset on
a sloped tile, thumbnail it, and assert `GetSLOTPosition(0, false) == expected`.

---

## 4. A cancel now hard-aborts an interaction that has no live route

**`FreeSO/TSOClient/tso.simantics/Engine/VMThread.cs:387-420, 441, 1047`** and
**`FreeSO/TSOClient/tso.simantics/Engine/VMCancelPolicy.cs:146-157`**

```csharp
// VMThread.cs:1035
var isRunning = index <= ActiveQueueBlock && stackTopOnActionTree;
...
// VMThread.cs:1047
if (isRunning) PendingCancel = interaction.UID;   // settle it at the top of the next tick.
// VMThread.cs:441  (top of Tick, every entity, every tick)
ResolvePendingCancel();
...
// VMCancelPolicy.cs:156
return liveRouteInActionFrames ? Resolution.LetRouteInterrupt : Resolution.AbortNow;
// VMThread.cs:402-405
case VMCancelPolicy.Resolution.AbortNow:
    PendingCancel = -1;
    AbortCurrentInteraction();
    break;
```

**What changed.** Previously `CancelAction` set `VMEntityFlags.InteractionCanceled` and zeroed the
avatar's priority, and that was *all* it did. The flag has exactly one consumer — the interrupt
check at the top of `VMRoutingFrame.Tick` — so a cancel of an interaction that was **not currently
on a route** did nothing visible until the tree happened to reach a routing frame. This wave adds a
deferred request that, on the next tick, finds no `VMRoutingFrame` between the
`VMSpecialResult.Interaction` frame and the top of the stack and **unwinds the whole interaction
immediately**.

**Exact scenario it breaks.** TS1's "eat" is not one interaction, it is a chain: *Use Dining
Table* → queue *Sit* / *Use Countertop* → *Eat* → animation, with the parent tree parked in
`idle_for_input` / Allow Push between the legs. At the moment the child is enqueued, the parent is
the running action and has **no routing frame on the stack** — it is waiting. If anything cancels
the parent by UID during that window (`UIInteractionQueue`'s X, `VMNetInteractionCancelCmd`, a
script `Cancel Interaction`, or `VMAvatar.LeaveLot`'s sweep at `VMAvatar.cs:712-740`, which
cancels *every* queue entry):

* old: flag + priority 0. The parent tree breaks out at its own convenience and, critically, its
  `Exit Tree` / release-parent-idle instructions still run, so the counter is put back and the
  sim is left standing.
* new: `AbortCurrentInteraction()` pops every frame above the interaction frame with no exit path
  at all. The counter keeps its parent idle, the sim keeps its "using" state, and the child
  interaction never runs. To the player: *"I click Eat and nothing happens."*

There is a second, cheaper trigger: `PendingCancel` is only cleared by `AbortNow` or `None`
(`VMThread.cs:402, 414`). `ResolveOnTick` returns `Deferred` for `isCheckTree` and
`nonInterruptable` — and `Deferred` **keeps holding the request**, so a cancel that lands while
the interaction is briefly flagged non-interruptable is re-applied a tick later against whatever
is running then. The `None` branch guards this with
`ActiveAction != null && ActiveAction.UID == PendingCancel`, so a *different* action is safe — but a
re-queued action that reuses the same UID is not.

**Fix.** Do not `AbortNow` on the tick. Keep the old semantics (flag + priority 0) and let the tree
notice, or at minimum:

1. only `AbortNow` when the action has been *running* across more than one tick, and
2. run the callee's exit/parent-idle release before popping the frames — the point of the
   `LetRouteInterrupt` comment ("skipping the route-fail tree is what strands objects in use")
   applies equally to a direct unwind.

Add a test: enqueue a parent interaction, park it in Allow Push with no route, cancel it, assert the
object's parent idles are released and the stack is back to the allow-push frame.

---

## 5. Dynamic-multitile merge now inverse-rotates the merged tile's offset

**`FreeSO/TSOClient/tso.simantics/Entities/VMMultitileGroup.cs:454-470`** (with `:408-426`)

```csharp
// before
foreach (var obj in other.Objects) {
    var diff = obj.Position - bObj.Position;
    var vec = new Vector3(diff.x, diff.y, 0);
    Vector3.Transform(vec, rotMat);                 // <-- result discarded
    AddDynamicObject(obj, new LotTilePos(round(vec.X), round(vec.Y), diff.Level));
    obj.MultitileGroup = this;
    obj.Direction = bObj.Direction;
}

// after
var moving = new List<VMEntity>(other.Objects);
foreach (var obj in moving) {
    other.RemoveObject(obj);                        // <-- new: empties the donor group
    AddDynamicObject(obj, LocalOffset(bObj.Position, bObj.Direction, obj.Position));
    obj.MultitileGroup = this;
    obj.Direction = bObj.Direction;
}
```

**What changed.** Two behaviour changes, not one.

1. The stored offset is now the **inverse-rotated** world delta, so a tile that merges into a group
   whose base faces EAST/SOUTH/WEST lands on a *different tile* than it did before. The old
   behaviour — the un-rotated world delta — is what the rest of the group maths assumes:
   `ChangePosition` (`:239-252`) and `SetVisualPosition` (`:371-393`) rotate `Offsets` **forward**
   by `Dir`, so an offset that was already a world delta gets rotated a second time. For `Dir == 0`
   (NORTH) the two agree, which is why this passes on a lot where nothing is rotated.
2. `other.RemoveObject(obj)` empties the donor group. `RemoveObject` (`:155-171`) also does
   `WorldGroup.Objects.Remove(component); component.MultitileGroup = null;`. After `Combine`, the
   donor group has `Objects.Count == 0`, so its `BaseObject` getter (`:44-63`) returns **null**.
   Anything still holding a reference to that group — a save in flight, `VMArchitectureTools`'s
   edit set, a `VMEntity` whose `MultitileGroup` was not updated because it was null-skipped — now
   dereferences null.

**Exact scenario it breaks.** *Placing elements on other elements.* Two identical objects placed
adjacent (a second dishwasher, a second counter section, a pair of stoves) merge on placement.
`UpdateDynamicMultitile` (`VMEntity.cs:1666-1695`) calls `Combine` and then, for every object in
the (now correctly snapshotted) `oldObjects`, `UpdateDynamicMultitile` again — which re-runs
`ExecuteEntryPoint(8)`. The merged tile's `Offsets[i]` is now a *local* offset, so on the next
`ChangePosition` the tile is drawn and seated **one rotation away from where it was placed**, and
its footprint/room checks run against the wrong tile. That is exactly a "only sometimes works"
placement failure: it depends on the base object's direction and on which of the four rotations the
player has applied.

**Fix.** Verify the direction of the rotation against `ChangePosition` before shipping. If the
world-delta behaviour was load-bearing, `LocalOffset` must return the *un*-rotated delta (i.e. drop
`InverseRotation`) — in which case the honest fix is to keep the discarded-`Vector3.Transform` shape
and rename the helper, because the frame in which `Offsets` live is a documented invariant of this
class. Separately, make `BaseObject` return the *last* known base rather than `null` for an
emptied group, or null-check every `MultitileGroup.BaseObject` caller.

---

## 6. Terrain normal: the x-gradient is now blended by the **y** fraction

**`FreeSO/TSOClient/tso.world/Model/Blueprint.cs:257-289`** →
**`FreeSO/TSOClient/tso.world/Model/TerrainHeightField.cs:61-77`**

```csharp
// before (Blueprint)
float xDist1 = (h10 - h00) * TerrainFactor;   // row at baseY
float xDist2 = (h11 - h01) * TerrainFactor;   // row at nextY
float xChange = xLerp * xDist1 + (1 - xLerp) * xDist2;   // <-- blended by X
float yChange = (xl2 - xl1) * TerrainFactor;             // no yLerp

// after (TerrainHeightField.Gradient)
var xChange = yLerp * dHdxAtNext + (1 - yLerp) * dHdxAtBase;   // <-- blended by Y
```

**What changed.** A deliberate, documented semantic change: the finite differences come off the
quad's two *rows*, so the correct blend factor is `yLerp`, and the class comment argues it at
length. The argument is right about the math, but this is a **behaviour change to every object's
and avatar's ground orientation on sloped tiles**, and the old value was what every existing
save's and screenshot's expectation was built on. `InterpNormal` has one caller,
`AvatarComponent.cs:113` (`AltitudeNormal`).

**Exact scenario it breaks.** On a sloped lot, a sim standing on a diagonal gradient gets a
different normal, so the avatar's ground-aligned pose and the 3D object's `GroundAlign` tilt change
— and `VMMultitileGroup.ChangePosition`'s (currently disabled) ground-align block, plus anything
that reads `AltitudeNormal` to decide which side of a slope an object may sit on, moves with it.
Combined with #1 and #2, an object that is simultaneously short of its altitude and tilted by a new
normal is drawn far enough from its logical tile to miss its own click box.

**Fix.** Ship the normal change separately from the altitude change, behind a config flag
(`WorldConfig.Current.TerrainNormalYBlend`, default `false` = old behaviour), so it can be A/B'd.
If it is kept on, add a test that pins the normal at the four corners and the centre of a single
bilinear quad against the expected value.

---

## 7. `PortalTurns` no longer counts calls, which changes route entry-direction pinning

**`FreeSO/TSOClient/tso.simantics/Engine/VMRoutingFrame.cs:1083-1114`** (consumed at `:395`)

```csharp
// before
return (rf.State != VMRoutingFrameState.ROOM_PORTAL || rf.PortalTurns++ == 0);
// after
return AllowsPortalTurn(PortalTurnFrame().State, PortalTurnFrame().PortalTurns);
// ...increments moved to CountPortalTurn() at the two real turn sites (:1137, :1159)
```

**What changed.** The old predicate incremented `PortalTurns` on **every call** while the route was
in `ROOM_PORTAL`, and `CanPortalTurn` is called from `BeginWalk`, `EndWalk` *and* the `WALKING`
branch of `Tick` — i.e. once per tick. So `PortalTurns` went positive within a tick or two of
entering a portal, whether or not a turn ever happened.

`AttemptWalk` reads that as a decision input (`:395`):

```csharp
var inDir = (parent.State == VMRoutingFrameState.ROOM_PORTAL || parent.PortalTurns > 0)
          ? Caller.RadianDirection : (float?)null;
```

— "has this route already turned at a portal, so pin the path's entry direction". With the old
counter, a route that crossed a portal *without turning* still had `PortalTurns > 0` and got its
entry direction pinned. Now it does not. `PortalTurns` is also a save field (`:1346`, `:1393`), so
routes restored from a save made by the old build carry a counter with the old meaning.

**Exact scenario it breaks.** *Eat.* The "eat from the counter" interaction routes the sim to the
chair/sofa slot, frequently through a doorway. With the entry direction no longer pinned, the sim
walks in facing an arbitrary direction, the approach to the chair slot is attempted from the wrong
notch, `VMSlotParser` finds no reachable location, and the interaction takes the route-fail path
(or sits in `WaitTime` and times out). The player sees "I clicked Eat and the sim never got there."
This is also the shape of "only sometimes works" — it depends on whether the route happened to cross
a portal in a state where a turn was attempted.

**Fix.** Keep the old counter semantics for the `AttemptWalk` decision and use a *separate* field
for "a portal turn has actually been taken". i.e. add `PortalTurnsActual` (incremented only by
`CountPortalTurn`) and leave `PortalTurns` incrementing where it used to, or migrate the save
field. Either way, pin the two meanings in a test rather than in a comment.

---

## 8. `UIContainer.Remove` now nulls `Parent` and invalidates the matrix

**`FreeSO/TSO.Client/FSO.UI/Framework/UIContainer.cs:113-130`**

```csharp
113:  public void Remove(UIElement child) {
           ...
122:      if (child.Parent == this) {
123:          child.Parent = null;                 // <-- was commented out
128:          child.InvalidateMatrix();
       }
```

**What changed.** The `Parent = null` line was previously commented out
(`//if (child?.Parent == this) child.Parent = null;`). It is now live, and `InvalidateMatrix()` was
added with it.

**Exact scenario it breaks.** *The pie menu.* `UILotControl.ShowPieMenu` /
`ClosePie` (`Client/Simitone/.../UILotControl.cs:743-748, 765-774`) remove the pie, and
`UIPieMenu.RenderMenu` (`Client/Simitone/.../UIPieMenu.cs:200-204`) removes and re-adds its
buttons. `UIElement.WillDraw` (`:580-590`), `FindParent<T>` (`:1356-1373`) and `FindController<T>`
(`:1337-1354`) all walk `Parent`. Any Simitone element that is removed and still touched on a later
frame — the lot control across a travel-picker round trip, the auto-follow panel, a dialog's
buttons while the dialog's `Removed()` cascade runs — now answers from a **truncated tree**:
`FindController<GameController>()` returns `default`, `WillDraw()` returns `true` for an element
whose real container is invisible, and the element draws and takes mouse input while invisible.
`InvalidateMatrix()` compounds it: the cached `TransformMatrix` is dropped at the moment the
element is detached, and `GlobalPoint` (which `UIPieMenu.Update` calls every frame at
`UIPieMenu.cs:194`) recomputes against a null-parent chain.

**Fix.** Only null `Parent` when the element is genuinely being discarded, not on every `Remove`.
Either restore the commented-out line, or set `Parent = null` in `Removed()` overrides rather than
in the container's `Remove`, and re-parent on `Add` (which already does — `UIContainer.cs:67-73`).

---

## 9. `GetSlotHeight` can now answer −1, which `GetObjPlace` cannot distinguish

**`FreeSO/TSOClient/tso.simantics/Entities/VMGameObject.cs:273-282`**, consumed at
**`FreeSO/TSOClient/tso.simantics/VMContext.cs:1500-1524`**

```csharp
// before
if (slot < TotalSlots()) return Slots.Slots[0][0].Height;   // always slot 0
else return -1;
// after
if (!VMEntitySlots.IsValidIndex(Contained, slot)) return -1;
if (Slots?.Slots == null) return -1;
if (!Slots.Slots.TryGetValue((ushort)slot, out var items)) return -1;
if (items == null || items.Count == 0) return -1;
return items[0].Height;
```

**What changed.** For the only caller (`slot == 0`) this is *nearly* equivalent —
`VMGameObject.Init` (`:126-132`) only allocates `Contained` when `Slots.Slots` has key 0 — but it
is not equivalent in two cases:

* `LoadHollowCrossRef` (`:574-586`) and `VMStandaloneObjectMarshal` size `Contained` from the
  **save**, not from the SLOT chunk. A save written on a machine with a fuller object definition
  (or a hollow/blueprint marshal) can produce `Contained.Length > 0` with no key 0 in the local
  SLOT chunk. Old: `Slots.Slots[0]` → `KeyNotFoundException` (a crash, caught by the VM funnel).
  New: a clean `-1`.
* an object whose `SlotID` resolves to a missing SLOT chunk entirely.

**Exact scenario it breaks.** `VMContext.GetObjPlace:1506`:

```csharp
if (((1 << (obj.GetSlotHeight(0) - 1)) & allowedHeights) > 0)
```

`GetSlotHeight(0) == -1` gives `1 << -2`, which C# masks to `1 << 30` — a bit no `short` can carry,
so the test is always false and control falls to the `else`. If `noFloor` is false the status stays
`VMPlacementError.CantIntersectOtherObjects` and the placement is **refused**. That is precisely
*"placing elements on other elements only sometimes works"*: it works for every stock counter
(SLOT chunk present, key 0 present) and fails for a modded or partially-installed one, with no
message that points at the cause. The `else` branch's diagnostics at `:1519-1522`
(`CounterHeight` / `CannotPlaceComputerOnEndTable`) are also wrong for this case, because
`GetSlotHeight(0) == 8` is false when the answer is −1.

**Fix.** Give `GetSlotHeight` a distinguishable "unknown" and handle it in `GetObjPlace` rather
than folding it into `-1`:

```csharp
// VMContext.cs:1506
var slotHeight = obj.GetSlotHeight(0);
if (slotHeight > 0 && ((1 << (slotHeight - 1)) & allowedHeights) > 0) { ... }
else if (slotHeight <= 0) { status = VMPlacementError.Success; }  // unknown: let it try
```

A `VMGameObject` with a containment slot but no readable SLOT height should be *permissive*, not
refusing — it is exactly the "we don't know" case, and refusing on it is the regression.

---

## 10. `GetChildrenSafe` allocates on every draw/update (perf → click timing)

**`FreeSO/TSOClient/FSO.UI/Framework/UIContainer.cs:291-300`**

```csharp
protected UIElement[] GetChildrenSafe() {
    lock (Children) { var copy = new UIElement[Children.Count]; Children.CopyTo(copy); return copy; }
}
```

**What changed.** The cached `ChildCopy` array was removed; every call now allocates. This is called
from `PreDraw`, `Draw`, `Update`, `GameResized`, `DeviceReset` — so **four arrays per container per
frame**, on the UI thread, in the middle of the input/update pass.

**Exact scenario it breaks.** Not a correctness break, but a plausible contributor to both reports
if they are frame-timing sensitive: a GC pause during `UILotControl.Update` delays
`ProcessMouseEvents`, so a click is delivered a frame or two late, and `GetObjectIDAtScreenPos`
(which renders the whole lot into a 1×1 target) runs against a `state.CenterTile` the player has
already scrolled past. A GC-induced hitch while hovering makes the hover/pie flicker — which reads
to a player as "sometimes I can't click the dinner".

**Fix.** Keep the cached array but rebuild it only when `Children.Count` changes *and* add a
version counter bumped by `Add`/`AddAt`/`Remove`/`SendToBack`/etc. so the copy is not stale. The
original bug (a shared array mutated while another thread read it) is real; the fix is a version
stamp, not "stop caching".

---

## 11. `UICachedContainer` drops a frame (and the invalidation) on a zero-size container

**`FreeSO/TSOClient/FSO.UI/Framework/UICachedContainer.cs:53-58`**

```csharp
if (size.X < 1 || size.Y < 1) { Invalidated = false; DynamicOverlay.PreDraw(batch); return; }
```

**What changed.** The guard is new and correct in intent (a zero-sized `RenderTarget2D` is a device
exception), but it **clears `Invalidated` and then returns without drawing the children**. The
comment says "a container with no size yet is simply skipped and will pick itself up on the frame
its size arrives", which relies on the `Size` setter calling `Invalidate()`. `UIDialog.SetSize`
does, but a hand-built `UICachedContainer` whose `Size` is assigned by direct field write, or one
whose size is set *before* it is added to a container, is not guaranteed to.

**Exact scenario it breaks.** A dialog that is created, sized and shown inside one frame: the first
`PreDraw` clears `Invalidated` and draws nothing, and if nothing invalidates it again the dialog
renders empty or not at all — which for a dialog raised *by* the pie menu (e.g. "Eat?" confirmation
or the travel picker) is indistinguishable from "the eat action does nothing".

**Fix.** Do not clear `Invalidated` on the skip path; return before the flag is touched, or set a
separate `_WaitingForSize` so the next frame retries unconditionally.

---

## 12. `HitTestArea` now returns false for disabled elements

**`FreeSO/TSOClient/FSO.UI/Framework/UIElement.cs:1091-1101`**, with `UIButton.IsDisabled` at
**`FreeSO/TSOClient/FSO.UI/Controls/UIButton.cs:364`**

```csharp
public bool HitTestArea(MouseState state, Rectangle area, bool cache = false)
{
    if (!Visible || IsDisabled) { return false; }
    return area.Contains(GlobalPoint(state.Position.ToVector2()));
}
```

**What changed.** `IsDisabled` is a new virtual, overridden by `UIButton` to report `m_Disabled`.
Correct and desirable, but it is a **whole-tree behaviour change** for every `ListenForMouse`
consumer, because `UIElement.Update`'s listener loop (`:532-545`) now short-circuits on it — that
path was also changed from `HitTestArea(..., true)` to `HitTestArea(...)`, dropping the (unused)
cache flag.

**Exact scenario it breaks.** Low likelihood, but check the Simitone auto-follow panel and the
interaction queue: `Client/Simitone/.../UILotControl.cs:1819-1820` sets
`inviteButton.Disabled` / `homeButton.Disabled`. A `UIDialog`'s background `UIDragHandler` also
listens for mouse on the dialog; if a container under it is a disabled `UIButton`, the dialog no
longer receives the drag. Nothing in the pie menu itself is a disabled button, so this does not
explain the dinner report on its own — flagged so it is checked rather than assumed.

**Fix.** None required if the tree is audited. Add a test: a `UIButton` with `Disabled = true`
inside a `UIDragHandler`'s target must not block the target's drag.

---

## Ruled out (checked, not the cause)

Recorded so the two fixers do not re-derive them.

* **`VMGameObject.GetSlot` / `ClearSlot` → `VMEntitySlots.GetOrDefault`**
  (`VMGameObject.cs:268-297`, `VMAvatar.cs:1338-1360`). Semantically identical to the old code for
  every in-range slot; the only difference is out-of-range and empty, which used to throw or no-op.
  `VMEntitySlots.IsValidIndex` is `contained != null && slot > -1 && slot < contained.Count`, which
  is exactly the old guard. **Not a cause.**
* **`VMGameObject.GetSlotHeight(0)` returning the same value as before for slot 0** — see #9 for
  the one case where it does not.
* **`VMSlotParser`** (`VMSlotParser.cs:107, 168-171, 197-244`). The `Results = OrderBy(-Score)` line
  is byte-identical apart from a rewritten comment. The `HasUsableResolution` guard only changes
  what happens for `Resolution <= 0`, which previously threw.
* **`Matrix2D.TransformPoint(ref x, ref y)`** (`Matrix2D.cs:75-84`). The R‑03 fix is correct; a
  pure-scale matrix has `M[1] == 0`, so the pie menu's `ScaleX/ScaleY` and the lot control's
  identity matrix are both unaffected. `IDENTITY`/`DEFAULT_SCALE` becoming properties is safe —
  every in-tree caller either clones or copies out.
* **`_2DStandaloneSprite.PrepareVertices` partial upload**
  (`_2DStandaloneSprite.cs:39-72`, `MSDFGlyphBuffer` unchanged in shape). Verified against the
  MonoGame 3.8.4 API: `VertexBuffer.SetData<T>(T[] data, int startIndex, int elementCount)` takes
  **element** counts, not bytes, so `SetData(verts, firstChanged, count)` with `count ∈ [1,4]` is a
  correct partial update. The `verts` mirror and `GPUVertices` are updated in lockstep and the
  recreate branch forces `firstChanged = 0, lastChanged = 3`, so they cannot diverge.
* **`DGRPRenderer.ValidateSprite` sprite reuse** (`DGRPRenderer.cs:186-335`). Every `_2DSprite`
  field that affects a vertex (`SrcRect`, `AbsoluteDestRect`, flips, `AbsoluteWorldPosition`,
  `ObjectID`, `Room`, `Floor`) plus `Mask`/`Depth`/`RenderMode` are written on every reuse, and the
  surplus tail is trimmed. Dynamic-sprite visibility changes the item↔sprite pairing but every
  field is rewritten, so a stale pair is not reachable.
* **`DGRPRenderer.Room`/`Level` becoming properties** (`:33-65`) only adds a `_2D` dirty flag —
  strictly more invalidation, never less.
* **`UISpriteBatch` / `UIBatchPolicy` BeginUI/EndUI/Restart** (`UISpriteBatch.cs:167-236`,
  `UICachedContainer.cs:87-100`, `UIListBox.cs:449-456`, `UIElement.cs:878-889`,
  `UILayer.cs:487-494`). Pushes and pops are balanced; `UIBegin` clears `Scopes` and
  `BatchMatrixStack` once per frame before any scope is pushed. The only `UIBegin` call inside a
  draw is in `UIExternalContainer.Draw` and is inside a comment block.
* **`GameScreen` / `InputManager` text-input rework** (`GameScreen.cs:56-72`,
  `InputManager.cs:252-330`). `State.FrameTextInput` is now a per-frame copy and
  `TextInputAvailable` is set by both producers (`GameScreen.Update`,
  `UIExternalContainer.Update:180-192`). The mouse loop's `return`→`continue` and the
  `LastMouseDownState` sync are both fixes. No path leaves `FrameTextInput` null while
  `TextInputAvailable` is true.
* **`VMStackFrame.SizeLocals`/`SizeArgs` and `VMRoutineResolver`**
  (`VMStackFrame.cs:137-190`, `VMThread.cs:644-648, 662-678`, `VMThread.cs:878-884`).
  `SizeArgs(null-routine) → new short[max(0,4)] = short[4]`, matching the old literal;
  `SizeLocals(frame.Routine, null) → new short[max(Locals, Arguments)]`, matching the old
  `Math.Max`. The `while`→`if` in `GetAttribute` (`VMEntity.cs:147-152`) is behaviour-identical
  (the body returns).
* **`VMArchitecture.IsUserOutOfBounds`** (`VMContext.cs:1299-1360`). The three inverted early
  returns are now correct, and the new `index >= fineBuildableArea.Length` guard is a no-op because
  `VMTS1Activator.ResizeFlags(flags, size)` produces exactly `Width * Height` entries. The
  `BuildBuyEnabled` bool→derived-set change (`VMArchitecture.cs:47-70, 411-419, 1076-1112`) only
  affects build/buy gating, not slot placement — but note it *is* a wedge risk: an object that runs
  TS1 call 10 and never reaches call 11 still holds build/buy off for the lot's life, and
  `PruneBuildBuyHolders` only frees it when the holder is `Dead`. If "placing" reports include
  "I can't place anything in buy mode", that is where to look.
* **`VMFindBestAction` weight curve** (`VMFindBestAction.cs:125-170`, `VMFindBestActionHelper`) and
  `AddCandidateOptions`. Both are autonomy-only (`FreeWillEnabled` gate) and do not touch the
  player-driven pie menu.
* **`VMActionCallback.Run`** (`VMActionCallback.cs:50-100`). The `(byte)`→`(uint)` interaction-index
  fix is real and correct; the two new guards only replace throws with `false`. The return value is
  `true` on the success path exactly as before.
* **`MSDFFont` / `MSDFGlyphBuffer`** (`MSDFFont.cs:74-264`, `MSDFGlyphBuffer.cs`). Quads, winding,
  index order and per-font run order are unchanged; only the storage is reused. `MeasureString` is
  untouched, so `UIButton` auto-widths and hit regions are unchanged.
* **`UIButton` event accessors / `UIEventSubscription`** (`UIButton.cs:318-345`). De-duplicates
  subscriptions. The pie menu builds a fresh `UIButton` per `RenderMenu` call, so it subscribes once
  each. `Size`'s setter now also writes `m_Height` and the click region — correct, and nothing in
  the pie-menu construction path assigns `Size`.
* **`UIListBox` / `UIDialog` / `UIClickableLabel` / `UITextEdit`** — text-entry and dialog-layout
  only; no bearing on lot object interaction.
* **`UIUtils.WordWrap` → `WordWrapper`** (`UIUtils.cs:31-60`). Behaviour-preserving by construction
  (the adapter documents that the `(int)` truncation of `MeasureString` is kept deliberately). It
  now throws `ArgumentNullException` on a null `TextStyle` where it used to `NullReferenceException`
  — same class of failure, better message.
* **`World.InternalDraw` `ResetMatrices()`** (`World.cs:736-743`) and `PrepareCulling`
  (`WorldState.cs:168-180`). The R‑19/R‑34 fixes are consistent: `PrepareCulling` is now the single
  owner of `WorldRectangle` and reads `_2D.LastWidth/LastHeight`, which `ResetMatrices(width,height)`
  maintains in both overloads. The OBJID pick overwrites `WorldRectangle` itself
  (`WorldPlatform2D.cs:172`) so it never depended on either.
