# Visual features: lighting, shadows and water

What the lot viewer's lighting pipeline is, what Simitone exposes of it, what changed in
this pass, and - deliberately - what is still missing.

Scope note: this covers the **lot view** (the 2D/2D-hybrid/3D lot renderer,
`FSO.LotView`). The city/world renderer (`tso.client/Rendering/City`) is a different
pipeline with its own weather, skybox and shadow code and is not covered here.

---

## 1. How a lot is lit

`FSO.LotView.World` owns a `Light`, an `LMapBatch`, which is the lot's light-map and
shadow-map renderer. When `WorldConfig.AdvancedLighting` is on (that is,
`LightingMode > 0`), `World.ChangedWorldConfig` builds one:

```csharp
Light = new LMapBatch(gd, config.ShadowDetail);   // resolution per lot tile
Light.Init(Blueprint);
```

`LMapBatch` owns every lighting render target on the lot, all sized from that one
resolution figure:

| target | size (64x64 lot, `resPerTile` = 16, ultra) | what it is |
| --- | --- | --- |
| `LightMap` | 3024 x 2016 | per-room accumulated light, 3 floors x 2 floors of slots |
| `LightMapDirection` | 756 x 504 (half-float) | the directional (sun-facing) component, written when `Directional` is on |
| `ShadowTarg` | 1008 x 1008 | indoor wall shadows, per light |
| `ObjShadowTarg` | 2016 x 2016 | indoor object shadows (2x in ultra, where objects are drawn as 3d meshes) |
| `OutsideShadowTarg` (+`-Post`) | 2016 x 2016 each | the outdoor sun shadow map, drawn from the sun's direction and then separably blurred |

The pipeline per room, per frame the room is dirty:

1. scissor to the room;
2. for each light in the room, generate its wall shadow map and its object shadow map,
   then additively blend the light into the room's slot of `LightMap`, masked by the
   room map so light does not leak into neighbouring rooms;
3. if the room is outdoors (or 3d wall geometry exists), do the same with a
   *fixed-direction* sun light - this is the path that produces the long outdoor
   shadows, and it goes through `WallComponentRC` and the terrain/roof geometry;
4. multiply the whole thing by the outdoor ambient colour, and write the directional
   component into `LightMapDirection` in the same pass.

`LMapBatch` does not repaint everything every frame. `InvalidateRoom` / 
`InvalidateOutdoors` push rooms into a priority queue, and `ParseInvalidated` spends a
budget per frame repainting the most important ones; a change to the outdoor colour
larger than a threshold escalates to a full repaint.

The sun's position and colour come from the in-game clock:
`VMArchitecture.SetTimeOfDay` -> `LMapBatch.BuildOutdoorsLight(Blueprint.OutsideTime)`
-> `SunVector`, `LightVec`, `ShadowMultiplier`, and the outside colour (with the
weather tint folded in). It is re-run whenever the clock moves by more than 0.001 of a
day, so the sun and its shadows track time of day and weather without anything on the
Simitone side having to push them.

## 2. What the player can change

Everything below is on **Options > Graphics** and in the in-game **Settings** strip.
Each is a `GlobalSettings` property, written to `config.ini` the moment it is clicked,
and pushed into the live `WorldConfig` by `Simitone.Client.Utils.GraphicsOptions`, which
is the single mapping used by the Options page, the settings strip and the startup path.

| row | ini key | values | default | what the renderer gets |
| --- | --- | --- | --- | --- |
| Lighting | `LightingMode` | Off / Basic / + Shadows / Ultra (0-3) | `3` (Ultra) | `WorldConfig.LightingMode`; 1 = flat 2d light maps, 2 = + 3d wall shadows, 3 = + 3d object shadows and the blurred sun shadow map |
| Shadows | `ShadowDetail` | 8 / 16 / 24 px per lot tile | `16` | `WorldConfig.ShadowDetail` -> the `LMapBatch` resolution above |
| Water Reflections | `WaterReflections` | Off / On / Strong (0-2) | `1` (On) | `WorldConfig.WaterShininess` = 0 / 0.02 / 0.06, the specular exponent the 3d floor shader gives water and pool tiles |
| Shaders | `ComplexShaders` | on/off | `true` | `WorldConfig.Complex` - parallax floors and walls |
| Anti-Alias | `AntiAlias` | off / 4x MSAA / 2x SSAA | `0` | `WorldConfig.AA` |
| Weather | `Weather` | on/off | `true` | rain/snow particles, the fog colour and the outdoor darkening |
| Directional Light | `DirectionalLight3D` | on/off | `true` | allocates `LightMapDirection`; without it, surfaces are lit but flat |
| Transitions | `EnableTransitions` | on/off | `true` | camera rotation smoothing; forced off unless the game was launched with `-3d` |
| Smooth Zoom | `SmoothZoom` | on/off | `true` | see the note in section 5 - there is no engine field for this one |
| Windowed | `Windowed` | on/off | `true` | windowed vs full screen |
| Texture compression | `TexCompression` | on/off | off | next launch (textures are already resident) |
| Graphics mode | `GlobalGraphicsMode` | Full 2D / Hybrid / Full 3D | `Hybrid2D` | the 2D/3D renderer split |

### The two rows this pass added

**Shadows (`ShadowDetail`)** is the row that used to be decorative. The page used to
show a "Shadows" low/medium/high group wired to `GlobalSettings.ShadowQuality`, which
was written to `config.ini` and read by *nothing*: the lot view's shadow resolution was
a hard-coded `16` at the `new LMapBatch(gd, 16)` call site. The key is still declared
(it is a legacy TSO key and something may still want it) but the page no longer offers
it, and `ShadowDetail` takes its place.

The default is `16` - the number that was hard-coded - so a player who changes nothing,
and every existing `config.ini` that predates the key, gets exactly the same lighting as
before. Raising it to `24` is the "High" setting: sharper wall/object shadow edges and a
finer sun shadow map, at a real memory cost (see below).

**Water Reflections (`WaterReflections`)** is new. The lot view has no reflection
renderer: there is no cube map, no planar reflection pass and no screen-space reflection
anywhere in `tso.world`. What it *does* have is a grazing-angle specular term on floor
surfaces, and water and pool tiles are the only floors that use it - as a literal `0.02`
in `_3DFloorGeometry`. That exponent is now the setting:

- `Off` (0) is a completely flat, unreflective water surface,
- `On` (1) is `0.02`, what the game has always drawn,
- `Strong` (2) is `0.06`, a brighter, tighter sun-and-sky sheen.

It is a shading approximation, not a reflection of the world: it brightens the surface
as the viewing angle flattens towards it, and it only applies in the 3D view, because
the 2D lot draws water as flat floor sprites.

## 3. Memory cost of the shadow ladder

Lighting render target memory scales with the *square* of `ShadowDetail`, because the
largest targets are 2x or 3x the light map in each axis. On the largest lot the game
has (64x64 tiles), in ultra lighting:

| Shadows | px/tile | largest target | total lighting targets |
| --- | --- | --- | --- |
| Low | 8 | 1008 x 1008 | ~20 MB |
| Medium (default) | 16 | 2016 x 2016 | ~79 MB |
| High | 24 | 3024 x 3024 | ~178 MB |

"High" is genuinely a machine-dependent choice, which is why it is opt-in. The engine
clamps the value to [4, 32] (`WorldConfig.MinShadowDetail` / `MaxShadowDetail`); 32 is
the largest value whose biggest target (4032) still fits in a 4096 texture, the minimum
every DirectX 9-era card guarantees.

## 4. Log evidence

`SimitoneGame` logs the configuration the renderer was actually handed, because the ini
values are not what the renderer sees - the mapping repairs and clamps them on the way
in, so "the ini says 4" and "the renderer got 8" are both possible and only one of them
is a bug worth chasing:

```
[INFO] Graphics config: mode=Full3D lighting=3 shadowDetail=24 waterShininess=0.06
       weather=on directional=on complex=on transitions=on aa=2 surroundingLots=2
```

## 5. Known gaps and deliberate non-changes

**The lot view has no reflection renderer.** No cube maps, no planar reflection, no SSR.
"Water Reflections" is a specular sheen, deliberately named for what it does rather
than for reflections it does not perform. Real reflections need either a scene-colour
copy plus a screen-space pass or a reflection probe per lot; both are renderer work, not
options work. See section 6.

**No shadow cascades.** The outdoor sun light *is* shadow-mapped (it is projected along
the sun vector into `OutsideShadowTarg` and blurred), but it is a single map covering
the whole lot, so its resolution is spent on the whole lot rather than on what the
camera is looking at. Cascades are the roadmap's next step and are a renderer
rewrite, not a setting.

**Upper-floor light repaints are dropped, not deferred.** `LMapBatch.ParseInvalidated`
discards a dirty room whose floor is above the floor being drawn, rather than keeping it
queued. A light switch on a floor you are not looking at therefore does not repaint that
floor's light map until something escalates to a full repaint - which does happen
regularly, because `InvalidateOutdoors` escalates whenever the outside colour moves
enough, roughly once per sim-minute. Fixing this properly means re-queueing the rooms,
which would keep the "still dirty" flag set every frame while an unseen floor has
pending work and cost a static-surface repaint per frame in 2D. Left alone on purpose.

**`SmoothZoom` has no engine field.** `WorldConfig.SmoothZoom`'s setter is empty in
FreeSO and its getter reads the transitions flag, so writing it does nothing. Simitone
eases its own 2D zoom (`UILotControl.TargetZoom` and the fractional backbuffer scale),
so the ini preference is carried in the settings snapshot and reported, but the thing
it would switch on does not exist. Renaming the row would be the honest fix; it is left
for a pass that can also decide what un-eased zoom should mean for the 3D camera.

**`World.ChangedWorldConfig` still hard-codes two lighting decisions.** It always builds
`Blueprint.WCRC` (the 3d wall geometry the sun shadow map needs) and always calls
`ChangeAAMode` - there are `if (true)` markers in both places. Gating the wall geometry
on `Shadow3D` would save the geometry for players on Lighting Basic, but at that level
the outdoor path currently uses the sun shadow map for outside rooms, so gating it
changes what Lighting Basic looks like. Not changed here.

**Surrounding-lot light maps are fixed at 6px/tile** (`new LMapBatch(gd, 6)` in
`SubWorldComponent.ChangedWorldConfig`) and are not covered by the Shadows setting. They
are the lots seen through a window, and are not where anyone looks for shadow quality.

## 6. Next steps, in the order they would pay off

1. **A real reflection for water.** The floor shader already has a
   `LightSpecular(normal, pos)` term with a per-pixel view vector; the missing piece is
   a colour to reflect. The cheapest honest version is a screen-space reflection pass
   over the floor draw (sample the already-rendered scene colour mirrored about the
   floor plane, reject off-screen hits), which needs one extra render target and one
   extra technique in `GrassShader`. A cube map per lot is more stable and much more
   work, and is what the roadmap's "cube map and specular tools" is pointing at.
2. **Shadow cascades for the outdoor sun.** Split `OutsideShadowTarg` into 2-3 maps
   along the sun's axis, centred on the camera, and blend between them. The existing
   `ShadowTargQualityDivider` machinery already varies the map size with the sun's
   elevation, which is the same idea at one resolution.
3. **Deferred upper-floor repaints** (section 5), with a bounded "deferred" count so the
   2D static surface is not repainted every frame.
4. **A `LMapBatch` for surrounding lots on the same ladder**, or an explicit statement
   that they are deliberately low detail.
