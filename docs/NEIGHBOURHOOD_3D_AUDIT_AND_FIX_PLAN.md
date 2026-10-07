# The 3D neighbourhood — full audit and fix plan

**Scope.** The whole uncommitted "3D neighbourhood" rework under
`Client/Simitone/Simitone.Client/Neighbourhood/` (~3 MB of C#), plus its integration
sites in `TS1GameScreen.cs`, `UINeighbourhoodSelectionPanel.cs`,
`NeighborhoodService.cs`, `GraphicsOptions.cs`, and its FreeSO touch-points
(`City/Terrain.cs`, `CityContent.cs`, `CityGeometry.cs`, `WorldStatic.cs`,
`VMLotTerrainRestoreTools.cs`, `VMTS1Activator.cs`).

**Method.** Read-only audit in five parallel passes — generation/mesh, rendering,
surroundings/placement, world-model/flags/persistence/offscreen, and
travel/transit/integration — each compared against the design docs
(`NEIGHBOURHOOD_3D.md`, `NEIGHBOURHOOD_SCOPE_A_PLAN.md`,
`NEIGHBOURHOOD_WORLD_HANDOFF.md`, `NEIGHBOURHOOD_REUSE_AUDIT.md`) and the FreeSO
originals it is meant to match. Every claim carries a `file:line`. Two findings
marked **[verified]** were re-read by hand.

**Severity legend.**

| Sev | Meaning |
|---|---|
| **C** | Critical — default configuration is broken, or a subsystem does not do what it is named after. |
| **H** | High — a stated product guarantee or a documented contract is inverted/unimplemented. |
| **M** | Medium — correctness or contract defect, gated by a flag or an off-by-default model. |
| **L** | Low — latent, dead code, performance, robustness. |
| **X** | Cosmetic / documentation only. |

**Reachability note that governs everything.** `NeighbourhoodViewSetting.Default =
NeighbourhoodView.World3D` (`NeighbourhoodViewSetting.cs:97`). So the 3D view and the
in-lot surroundings are **on by default**, while all seven Part B capability flags
default **off** (`NeighbourhoodWorldFlags.cs:152`). That single combination is the
source of the two critical findings below, and it contradicts every doc that says the
feature is opt-in.

---

## Part 1 — Executive summary

### The headline defects

1. **A fresh install cannot select a lot by clicking.** The default view is 3D, which
   hides the painted-map lot buttons, but the map click that is supposed to replace
   them is disabled unless B1 or B2 is on — and both default off. The only remaining
   way to choose a house is Next/Previous. **[verified]**
   (`UINeighbourhoodSelectionPanel.cs:813-816, 840-847, 1511-1534, 1559-1581`;
   `NeighbourhoodViewSetting.cs:97`; `NeighbourhoodWorldFlags.cs:152`.)
2. **The per-chunk cull is dead in the common case.** The whole-map shortcut tests
   `frustum.Contains(box) != ContainmentType.Disjoint`, which is true for an
   *intersecting* frustum, not just a containing one — i.e. almost every camera aimed
   at the map selects all 1024 chunks and the real per-chunk test never runs. At lot
   zoom the full-detail level submits the whole map. **[verified]**
   (`NeighbourhoodTerrainRenderer.cs:1099`; same predicate in the log at
   `NeighbourhoodViewSetting.cs:1976`.)
3. **A device reset permanently kills the world.** Only textures are rebuilt; the
   terrain/foliage/facade vertex buffers are not, and the facade cache disposes its
   meshes without clearing `_hashes`, so it never rebuilds. The proxy falls back to
   the painted map for the rest of the session.
   (`Neighbourhood3DView.cs:640-648`; `NeighbourhoodFacades.cs:768, 971, 1501-1513`;
   `SimitoneSurroundings.cs:730-742`.)
4. **Two of the seven capabilities do nothing in production.** B7 (persist) writes an
   empty default session and never saves the live one; B6 (offscreen) iterates an
   always-empty store, so no neighbour is ever simulated.
   (`NeighbourhoodLifecycle.cs:2085, 2143-2150`; `SimitoneNeighbourhoodWorld.cs:664,
   712, 837`; `OffscreenSimulator.cs:1877`.)
5. **The active lot's ground is sunk by its own altitude on any non-flat lot.** The
   surround is shifted to `y = 0` at the lot's base altitude, but the lot view draws
   its own terrain at the absolute `ARRY(0)` altitude because `Blueprint.BaseAlt` is
   never set. The bank that should bridge the step is skipped on exactly that case.
   (`SimitoneSurroundingsPlacement.cs:213-220, 366-393`; `SimitoneSurroundings.cs:2106-
   2128`; FreeSO `VMTS1Activator.cs:214, 322`.)

### The two systemic themes

- **The default and the docs disagree, and the flags and the view disagree.** The
  product story is "3D is opt-in"; the code is "3D is default, capabilities are
  opt-in", which is not the same thing and produces defect #1. Every "is it
  reachable" judgement in this document depends on which of the two the team wants.
- **Large subsystems are built, tested, and unreachable.** `SimitoneNeighbourhoodWorld`
  lot store + `SaveWorldState`/`ApplyWorldState`, `NeighbourhoodLifecycle.SaveWorldState`,
  `NeighbourhoodWorldUnification` (~1 200 lines), the `OffscreenSimulator`'s room/income/
  fidelity models, `NeighbourhoodWorldCamera`'s focus/free/zoom surface, and the transit
  walk camera all have **no production caller**. They are documented as shipped in places
  (`NEIGHBOURHOOD_3D.md:92-108`) and as "not built yet" in others.

---

## Part 2 — Findings

### 2.1 Defaults, reachability, selection

| ID | Sev | Site | What is wrong | Fix |
|---|---|---|---|---|
| **D1** | **C** | `UINeighbourhoodSelectionPanel.cs:813-816, 840-847, 1511-1534, 1559-1581`; `NeighbourhoodViewSetting.cs:97` | Default 3D hides the lot buttons (`PaintedMapLayersVisible = !Show3D`) but the replacement map-click is gated on B1/B2 (`WorldClickIsEnabled`), both off. Fresh install: 3D world, no clickable lots. | Make `WorldClickIsEnabled` follow `IsShowing3D` (the thing that hid the buttons), not the Part B flags; or make the flags' defaults follow the view default. The gate and the hide must be decided by the *same* rule. |
| **D2** | **H** | `NeighbourhoodViewSetting.cs:97` vs `NEIGHBOURHOOD_3D.md:7,10`; `NEIGHBOURHOOD_WORLD_HANDOFF.md:20` | `Default = World3D`; docs and the handoff decision say "off / 2D (default), opt-in until the look is proven". | Pick one. If the docs are the contract, `Default = Bitmap2D`. If 3D is intended default, rewrite the docs and fix D1. |
| **D3** | **L** | `NeighbourhoodViewSetting.cs:111-121` | `Current` caches with no file stamp and never re-reads; the Part B flags in the *same file* deliberately hot-reload. Two settings, opposite behaviour; `NEIGHBOURHOOD_3D.md:323-324` claims the file is re-read. | Give `NeighbourhoodViewSetting` the same `SidecarStamp` watch the flags use. |
| **D4** | **M** | `NeighbourhoodWorldFlags.cs:328-342` | `AllOff(save:true)` stamps the cache as if the write succeeded *before* `TryWriteAll` (which swallows errors); a failed write leaves memory "all off" and disk unchanged. | Restamp only after a confirmed write, or re-read on failure. |
| **D5** | **M** | `NeighbourhoodWorldFlags.cs:257-276, 536-546` | `Set` releases the lock before `Save`; `Save`→`IsOn`→`LiveValues` can reload the file and discard the value just set. | Do the record and the write under one lock/stamp. |
| **D6** | **L** | `NeighbourhoodWorldFlags.cs:239, 615-632` | Every `IsOn` stats the file; the flags are read several times per frame (`TS1GameScreen.cs:2995, 3011, 3012, 3021, 3029`). | Cache the stamp check to once per frame (or per tick) via a small "begin frame" call. |

### 2.2 Rendering — culling, device reset, resources

| ID | Sev | Site | What is wrong | Fix |
|---|---|---|---|---|
| **R1** | **C** | `NeighbourhoodTerrainRenderer.cs:1099` (log `NeighbourhoodViewSetting.cs:1976`) | Whole-map shortcut uses `!= ContainmentType.Disjoint`, true for `Intersects`. Selects all chunks for any camera that merely sees the map; the per-chunk cull at `:1117` never runs. | Use `== ContainmentType.Contains`. The intent is written in the surrounding comments and in `NeighbourhoodCityMeshBuilder.cs:2024-2042` ("25 chunks where 361 would do"). |
| **R2** | **H** | `Neighbourhood3DView.cs:640-648`; `NeighbourhoodFacades.cs:768, 971, 1501-1513`; `NeighbourhoodFoliage.cs` (no handler); `SimitoneSurroundings.cs:730-742` | Device reset rebuilds textures only. Terrain/foliage/facade buffers are stale device resources; the facade store disposes its meshes but never clears `_hashes`, so `NeedsBuild` stays false forever. World dead after reset. | Add a real `DeviceReset` path: dispose+rebuild terrain/foliage/facade buffers, clear `_hashes`/`_subWorldLots`, wire `SimitoneSurroundings.OnDeviceReset`. |
| **R3** | **H** | `Neighbourhood3DView.cs:386-394, 419-423`; `NeighbourhoodCityMeshBuilder.cs:3090-3096`; `UINeighbourhoodSelectionPanel.cs:227-258` | `Focus()` recomputes the full-map mean (262 144 bytes) on every camera-tween tick. | Cache the mean per map (invalidate on map change). |
| **R4** | **H** | `SimitoneSurroundings.cs:649-694` vs `Neighbourhood3DView.cs:577` | The in-lot surround draws terrain + facades but **no foliage**; the map view has trees. Standing in a lot shows bare ground. | Instantiate a foliage pass in the surround (share `NeighbourhoodFoliage`), or document the divergence as intentional and remove it from the map view too. |
| **R5** | **M** | `NeighbourhoodContent.cs:113, 253-267, 341-345` | `TreeAtlas` is loaded but never added to `_owned`, so it is never disposed; one leak per content instance and per device reset. | Add it to `_owned` (or dispose explicitly). |
| **R6** | **M** | `NeighbourhoodTerrainRenderer.cs:813-824` | `RoadSampler` allocates a `SamplerState` on every access (per frame, both renderers). | Static readonly instance. |
| **R7** | **M** | `NeighbourhoodTerrainRenderer.cs:957-958, 1005`; `NeighbourhoodFacades.cs:1228`; `NeighbourhoodFoliage.cs:591`; `SimitoneSurroundings.cs:1065-1066, 1085` | `SamplerStates[0]/[1]` are set but never saved/restored (Blend/Rasterizer/Depth are). | Save/restore sampler states with the others. |
| **R8** | **M** | `NeighbourhoodTerrainRenderer.cs:633, 1144-1155` | Per-frame closure in the slice sort; `frustum.GetCorners()` (new array) per frame in `IsUsable`. | Precompute a comparer keyed on a field; reuse a corner buffer. |
| **R9** | **L** | `Neighbourhood3DView.cs:571` + `:176-179`; `NeighbourhoodTerrainRenderer.cs:903-910` | `TerrainDrawn` compares against the *previous* frame's count; `_drawnChunks` is not reset. Diagnostics lie ("drew NOTHING" while drawing). | Compare `DrawnPrimitives > 0`; reset `_drawnChunks`. |
| **R10** | **L** | `NeighbourhoodTerrainRenderer.cs:799-810, 1276-1282` | `WholeMapBounds` y-range is ±half the max ground height; comment claims the full range. Only the shortcut/log are affected. | Use `0..255/ElevationScale` (or the real min/max). |
| **R11** | **L** | `Neighbourhood3DView.cs:307-327` | `TryCreate` catch path leaks a half-built scene if the ctor throws after `_terrain` exists. | `scene?.Dispose()` in the catch. |
| **R12** | **L** | `Neighbourhood3DView.cs:632, 635` vs `NeighbourhoodViewSetting.cs:588-589` | `DrawFailures => 0`, `DeviceLost => false` contradict the interface's "live" contract; runtime failures only counted in the proxy. | Surface the proxy's counter (or wire the scene's own). |
| **R13** | **L** | `Neighbourhood3DView.cs:532` | `DepthStencilState` set without restore; leaks into the UI layer. | Save/restore. |
| **R14** | **L** | `NeighbourhoodFacades.cs:1231-1240, 796-808` | Facade `Draw` never skips the active lot (only withheld sub-worlds); `AdoptShared` calls `ShouldDraw(..., activeLot: null)`. Latent today. | Consult the active lot in `Draw`/`AdoptShared`. |
| **R15** | **L** | `NeighbourhoodFacades.cs:1366-1371, 1460-1475` | Facade store keyed globally by lot number; two neighbourhoods with the same number+hash+plot+size+rotation collide. | Key on (slot/mode, lot) or include the layout identity. |
| **R16** | **L** | `NeighbourhoodFacades.cs:1563-1575` | Static `DeviceReset` subscription is never released for the last device. | Weak reference / explicit `Unwatch` on shutdown. |
| **R17** | **L** | `NeighbourhoodFacades.cs:718-724, 1080-1084` | `Outstanding`/`Describe` allocate a list + dictionary per call, several calls per frame — the class remarks claim the opposite. | Reuse buffers; return counts without building the list. |
| **R18** | **L** | `NeighbourhoodFoliage.cs:469-472` | Chunk bounds top out at max ground height; trees extend above and can be culled while visible. | Add the model height to the box. |
| **R19** | **X** | `NeighbourhoodFoliage.cs:827-849` | `TilesOutsideClip` under-counts (breaks on the first in-diamond tile). | Count the remainder. |
| **R20** | **X** | `Neighbourhood3DView.cs:46-50` vs `:522-531` | Class doc says depth-only clear; the code clears colour+depth (fixed this session). | Update the class comment. |
| **R21** | **X** | `NeighbourhoodFacadePlan.cs:360-363` | `DefaultBuildRadiusTiles` cites a 900-tile fog the code no longer uses (placement fog ≈1966). | Correct the justification. |
| **R22** | **X** | `NeighbourhoodCameraPlacement.cs:157-185` vs `Neighbourhood3DView.cs:538-541` | The placement's `ViewProjection`/`Frustum` are dead; the scene builds its own. The "one matrix" guarantee is coincidence (equal FOV). | Draw from the placement's matrix, or delete the dead pair. |
| **R23** | **X** | `NeighbourhoodContent.cs:219-230` | Double-dispose of the atlas in the catch path (idempotent, harmless). | Remove the redundant dispose. |

### 2.3 Rendering — LOD, mesh, textures

| ID | Sev | Site | What is wrong | Fix |
|---|---|---|---|---|
| **M1** | **H** | `NeighbourhoodCityMeshBuilder.cs:1326-1337, 1375-1380` (prose `:1293-1300`) | LOD deadband applies `(1±Hysteresis)` to the *wanted* level's own boundary. Full↔Half adopts Full at `160·1.15 = 184`, not `320·0.85 = 272`: a 42.5% margin instead of the documented 15%. `IsInsideDeadband` hard-codes the same wrong low edge. | Transition boundary = `MinTilesAcross · max(wanted.Factor, from.Factor)`; margin sign chosen per direction. |
| **M2** | **M** | `NeighbourhoodCityMeshBuilder.cs:2854, 2858` | Coarse ground clamps `x1/y1` to `MapSize-1`, dropping the last row/column; the fine walk produces a degenerate quad instead. Levels disagree at the map edge. | `min(x0+factor, MapSize)` with the same convention as the fine walk. |
| **M3** | **M** | `NeighbourhoodCityMeshBuilder.cs:452-477` vs `:2327-2328, 2357` | The report probe counts road quads on `Nothing` (255) terrain that the builder skips. The "two independent derivations" are not from the same predicate. | Make the probe use the builder's predicate. |
| **M4** | **M** | `NeighbourhoodFoliage.cs:442, 471` | Hard-coded `/12f` duplicates `ElevationScale`/`WorldHeightFor`. | Route through the mesh builder's helper. |
| **M5** | **L** | `NeighbourhoodCityMeshBuilder.cs:2658` vs `:2958, 2966, 2982` | Ground UV rule `/TilesPerTextureRepeat` written twice; the elevation rule was centralised, the UV rule was not. | One helper. |
| **M6** | **L** | `NeighbourhoodCityMeshBuilder.cs:448-450`; `:3177-3186` | Diamond tile counts over-count by one per row (`IsInsideDiamond` is strict). | Match the strict predicate. |
| **M7** | **X** | `NeighbourhoodCityMeshBuilder.cs:2605-2622` | Sample-layout comment describes a transposed assignment (arithmetic is correct/separable). | Fix the comment. |
| **M8** | **X** | `NeighbourhoodCityMeshBuilder.cs:987-1014` vs `NEIGHBOURHOOD_3D.md` §Zooming | The 160-tile boundary is "Full at ≤160"; the doc's band wording implies otherwise. | Align doc wording. |

### 2.4 Generation — layout, roads, terrain, elevation

| ID | Sev | Site | What is wrong | Fix |
|---|---|---|---|---|
| **G1** | **H** | `NeighbourhoodLayoutBuilder.cs:562-565, 673-678, 713-716` | Relaxation's keep-clear box is `[X−C, X+size+C)` (extends both sides), so it reserves **2×** `StreetCorridorTiles`; `GridFallback` reserves 1×. `HasOverlap` disagrees with `NeighbourhoodLot.Overlaps`; a grid layout is "overlapping" by one predicate and not the other. Relaxed layouts leave ~16 tiles of bare ground where the grid leaves 8. | Use `X + size` as the high edge in `HasOverlap`/`Separate` (match `Overlaps` and the grid), or make `Footprint` extend only one side. |
| **G2** | **H** | `NeighbourhoodMapGenerator.cs:276` (dead) + `NeighbourhoodRasterCache.cs:366, 545` | `NeighbourhoodMapGenerator.GeneratorVersion` is never read; the `.raster` stamp uses `layout.GeneratorVersion`. A map-generator change cannot invalidate the 3 MB cache. Latent while both are 2. | Stamp the raster with the map generator's version (or a combined version) and check both. |
| **G3** | **M** | `RoadGraph.cs:144-150` | `RoadMask.GetStep` treats `bit` as an ordinal (1..4) but documents it as a mask bit; `GetStep(North=8)` throws. | Take the ordinal explicitly (rename the parameter) or switch on the real bit. |
| **G4** | **L** | `NeighbourhoodRasterCache.cs:649-651` | Doc says a bulldozed lot keeps its file, but `HasHouse` is hashed (`:674`) and removing a house removes its driveway (`RoadGraph.cs:1543`). The stamp correctly moves; the comment is wrong. | Fix the comment. |
| **G5** | **L** | `NeighbourhoodRasterCache.cs:367-368, 550` | Header `mapSize` is written from `map.Width`, compared against `layout.MapSize`; safe only because `Save` pre-checks. | Write `layout.MapSize`. |
| **G6** | **L** | `NeighbourhoodMapAdapter.cs:386-400` | `DescribeInput` puts a quantised elevation byte into `NeighbourhoodLot.BaseHeight` (raw `ARRY(0)` units) and stamps generator version 0. Diagnostic only. | Use raw units; stamp the real version. |
| **G7** | **L** | `NeighbourhoodMapAdapter.cs:270` | `(int)ElevationScale` truncates if the scale becomes fractional. | Keep it float or assert integrality. |
| **G8** | **L** | `NeighbourhoodWorldMapping.cs:111-128` | `Fitted` assumes `MinProjectedX/Y == 0`; `BitmapToWorld`/`WorldToBitmap` are wrong for a general cloud (only `For` compensates). | Construct with the real minima. |
| **G9** | **X** | `NeighbourhoodMap.cs:572` | Debug colour histogram iterates a `Dictionary` unsorted (run-to-run noise). | Sort keys like `AppendHistogram`. |
| **G10** | **X** | `NeighbourhoodLayoutBuilder.cs:105-113` | `SidewalkMargin` now means the whole street corridor, not a sidewalk. | Rename or alias. |

### 2.5 Surroundings — placement, elevation, seams

| ID | Sev | Site | What is wrong | Fix |
|---|---|---|---|---|
| **S1** | **H** | `SimitoneSurroundingsPlacement.cs:213-220, 366-393`; `SimitoneSurroundings.cs:2106-2128`; FreeSO `VMTS1Activator.cs:214, 322` | The surround is shifted to `y = 0` at the lot's base altitude, but the lot view draws its own ground at the absolute `ARRY(0)` altitude (`Blueprint.BaseAlt` left 0). Whole neighbourhood sunk by `GroundOriginFor·3` on non-flat lots; the bank is skipped because it measures only generated heights. | Either set `Blueprint.BaseAlt` for TS1 lots (so both agree at 0), or do not subtract `GroundOriginFor` in `WorldMatrix` and let the bank carry the seam. Decide which frame is authoritative. |
| **S2** | **H** | `SimitoneSurroundings.cs:463-473` | `AttachSubWorlds(null)` NREs; the doc at `:437-443` promises the pre-B2 behaviour for a null plan. | Null-check before `_subWorlds.SubWorldsInside`. |
| **S3** | **M** | `SimitoneSurroundings.cs:473-480, 227-235, 241-244, 540-548`; `NeighbourhoodFacadePlan.cs:1091-1102` | On the "plan wider than the cull" refusal, `_subWorlds` stays set but no facades are withheld, so `FacadeFallbackCount`=0, `IsRegionFullyReal`=true, and the log says "the whole drawn region is real" while every lot is a facade. | Clear `_subWorlds` on refusal (call `UseSubWorlds(empty)`), and derive the counts from what was actually withheld. |
| **S4** | **M** | `SimitoneSurroundings.cs:907-946, 962-981, 744-754, 1098-1105` | A worker-thread mesh upload can run after `Dispose` (lot switch inside the build window): leaks GPU buffers, touches disposed terrain. | Guard `BeginMeshBuild`/`Upload` on `_disposed`; cancel pending builds in `Dispose`. |
| **S5** | **M** | `SimitoneSurroundingsRestore.cs:1294-1300` vs FreeSO `VMLotTerrainRestoreTools.cs:672-677` | `RestoreTerrain` overwrites the lot direction when global 11 is −1 *or* `ObjectLimit==0`; the guard only covers the latter. A hollow lot with global 11 = −1 gets `PickRoadDir`, moving the mailbox/bin/car portal off `DoorSide`. | Extend the guard / assert the restored direction matches `LotDirectionFor`. |
| **S6** | **M** | `SimitoneSurroundingsPlan.cs:1499` vs `:1345` | The active base level is recomputed per candidate via `placement.ActiveBaseLevel` (O(N) redundant work) instead of reusing `activeTerrain.BaseLevel`. | Reuse the computed value. |
| **S7** | **L** | `SimitoneSurroundings.cs:204-208` | `SubWorldCount` reports withheld facades, not built sub-worlds. | Rename or count the sub-worlds. |
| **S8** | **L** | `SimitoneSurroundingsPlacement.cs:448-450` | `SubWorldAltitudeFor` silently saturates to `short`. | Log/clamp with a reason. |
| **S9** | **L** | `SimitoneSurroundingsPlacement.cs:263, 288, 852, 905, 920, 940`; `SimitoneSurroundingsPlan.cs:848-852`; `SimitoneSurroundingsRestore.cs:1125, 1192` | A cluster of public arithmetic that only tests call (`CorridorBetween`, `HalfStreetDepthFor`, `StreetWidthFor`, `TarmacOffsetFor`, `FacadePointInLotView`, `FacadeGroundInLotView`, the seam checkers). The band re-derives the same offsets, so drift has no production alarm. | Fold the test seams into the production path, or mark them test-only and assert them against the band. |
| **S10** | **L** | `SimitoneNeighbourTerrain.cs:130, 136` | `LotTerrainFactor`/`LotAltitudeUnitsPerArryUnit` hand-copy FreeSO constants. | Single shared constant. |
| **S11** | **X** | `SimitoneSurroundingsPlacement.cs:143-152`; `SimitoneNeighbourTerrain.cs:177-186`; `SimitoneSurroundingsPlan.cs:461` | Stale docs: "eighth of a tile" (it is a twelfth), "one tile of sidewalk" (it is 8), "smallest legal pitch `Size+1`" (it is `Size+8`). | Correct. |
| **S12** | **X** | `SimitoneSurroundingsRestore.cs:14, 17` | Duplicate `using`. | Remove. |

### 2.6 Surroundings — facade vs sub-world decision

| ID | Sev | Site | What is wrong | Fix |
|---|---|---|---|---|
| **F1** | **M** | `TS1GameScreen.cs:1241, 1254-1262`; `SimitoneSurroundingsRestore.cs:197-205` | The documented B2 "engineering kill switch" (`SubWorldAttachEnabled`) is set true unconditionally whenever B2 is on, so its false branch is unreachable and a build cannot turn it off. It is also a process-global static that is **never reset**. | Keep the switch as an explicit opt-out that B2 does not overwrite (e.g. `SubWorldAttachEnabled && flag`), and reset it in `DetachSurroundings`/lot switch. |
| **F2** | **M** | `TS1GameScreen.cs:1247-1253`; `SimitoneSurroundingsRestore.cs:197-203` | Both doc-comments describe the field as "off in practice / nothing sets it", which is no longer true. | Rewrite to describe the real gate. |
| **F3** | **L** | `NeighbourhoodFacadePlan.cs:662-674` vs `:874-909` | `ShouldDraw` and `NeighbourhoodRenderDecision.Decide` encode the same per-lot rule twice; only `Decide` knows `insideCut`. | One predicate; `ShouldDraw` delegates. |
| **F4** | **X** | `NeighbourhoodFacadePlan.cs:52-53` | Stale `16/3` inflation comment (the adapter already uses the correct 9/4 path). | Delete. |

### 2.7 World model — lifecycle, flags, persistence, offscreen

| ID | Sev | Site | What is wrong | Fix |
|---|---|---|---|---|
| **W1** | **H** | `NeighbourhoodLifecycle.cs:2085-2120, 2143-2150`; `SimitoneNeighbourhoodWorld.cs:879, 925` | `SaveWorldState` has no production caller; `LayoutToWrite` writes `NeighbourhoodWorldState.CreateDefault()` (no clock, active lot 0) when Persist is on. B7 is worse than absent: it hands back "no clock". | Wire the live session write (clock, active lot, transit) on switch/exit, and never write a default over a live value. |
| **W2** | **H** | `SimitoneNeighbourhoodWorld.cs:664, 712, 837`; `OffscreenSimulator.cs:1877`; `TS1GameScreen.cs:2443, 2458, 2468, 3118, 3126, 3133, 3395` | The world's `LotStore`/`ActiveLotNumber` are dead; `TS1GameScreen.SavedLots` is the real authority. `SimulateLots` iterates the empty store, so **B6 decays nothing**. | Make `SavedLots` and `LotStore` one authority (feed the world's store from the screen, or point the simulator at `SavedLots`). |
| **W3** | **H** | `NEIGHBOURHOOD_3D.md:92-108, 297-306` | Docs describe B6/B7 as shipped ("keep living their days", "real motive decay"); production does neither. | Correct the docs, or land W1/W2 before claiming it. |
| **W4** | **M** | `TS1GameScreen.cs:1763-1776`; `SimitoneNeighbourhoodWorld.cs:786-811` | The clock handshake pulls the lot's VM clock (which stops when paused) over the wall-clock world clock on every save/switch, so pausing then leaving **rewinds** neighbourhood time. | Make one clock authoritative, or take `max`/advance-only. |
| **W5** | **M** | `SimitoneNeighbourhoodWorld.cs:309-321`; `OffscreenSimulator.cs:905-919` | B6 frame-invariance is documented as false for first-seen lots (seeding draw advances the stream). The invariance test cannot catch it. | Seed independently of the step stream; add a test that lands near a boundary. |
| **W6** | **M** | `OffscreenSimulator.cs:2007, 2238-2243` | `DecayResidentLot` adds `steps` per avatar; `PaydaysBetween` is called with the per-avatar total, so an N-Sim lot is paid ~N× the elapsed hours. | Call `PaydaysBetween` with the per-lot step count. |
| **W7** | **M** | `SimitoneNeighbourhoodWorld.cs:47-52, 515, 788`; `TS1GameScreen.cs:879` | `WorldEnabled` is always true in production (the flag check is in the caller), so the class's own off-gate is dead. | Pass the flag through, or delete the field. |
| **W8** | **M** | `SimitoneNeighbourhoodWorld.cs:886`; `OffscreenSimulator.cs:2728-2749` | The (dead) save path mutates the shared layout in place via `ApplyStamps`; `LastSimulated` never reaches the layout in production. | Copy-on-write for the stamps; wire it or drop it. |
| **W9** | **M** | `SimitoneNeighbourhoodWorld.cs:491-502` vs `NeighbourhoodWorldFeatures.cs:1255-1256` | Features claim "not behind a flag"; the world gates them on B1. | Align the two, or make B8 flag-free as documented. |
| **W10** | **M** | `NeighbourhoodWorldFeatures.cs:265-271, 346` | `IsNight` doc says Dusk-or-Night, but the Dawn band makes it false at 04:00–05:59 while `SunAngleDegrees` is still negative; `SunIsUp` disagrees with the angle. | Fix the predicate or the doc. |
| **W11** | **M** | `SimitoneGlobalLink.cs:171-175` | The constructor defaults `worldServicesEnabled = true` — a default-on capability, the exact shape the "all seven default off" promise forbids. | Default false. |
| **W12** | **L** | `OffscreenSimulator.cs:1808` vs `SimitoneNeighbourhoodWorld.cs:804` | `Math.Round` here, `Math.Floor` there: turning B6 on changes clock advancement for sub-tick deltas, so "flag off is byte-identical" is not symmetric. | One rounding rule. |
| **W13** | **L** | `OffscreenSimulator.cs:1361, 1405, 1432, 1450-1466`; `NeighbourhoodWorldFeatures.cs` | The visitor/room/income/fidelity models have no production setter (documented). | Wire a setting, or mark the whole B8 sub-model experimental and keep it out of the shipping build. |
| **W14** | **L** | `NeighbourhoodWorldFeatures.cs:804, 872` | `Classify` snow threshold uses the rain, not the wet, threshold; `At` can report `Kind=Storm` with the previous segment's near-zero precipitation. | Align thresholds; report a consistent (kind, amount). |
| **W15** | **L** | `SimitoneNeighbourhoodWorld.cs:127-143` | `MaxTicksPerCall`'s caption (~600 game hours) contradicts its own old doc. | Rename/comment. |
| **W16** | **L** | `OffscreenSimulator.cs:2757-2763` | `ResolveActiveLot` narrows an unvalidated `int` to `short`. | Validate the range. |
| **W17** | **L** | `NeighbourhoodLifecycle.cs:119-136, 2164-2172` | `WithLayout`'s documented precondition (a prior compare) is not enforced by the helper. | Assert, or make it private to the checked callers. |
| **W18** | **L** | `NeighbourhoodWorldUnification.cs:508` | ~1 200 lines with no production caller (documented as a plan). | Keep behind a clear "not wired" marker, or delete until it is needed. |

### 2.8 Travel and transit

| ID | Sev | Site | What is wrong | Fix |
|---|---|---|---|---|
| **T1** | **H** | `NeighbourhoodTransitLotVm.cs:795-801, 1843, 2009-2011, 2196-2197` | `LocalTileAt` returns **whole tiles**, but `LotTilePos.x/y` are sixteenths. Every B4 sim is placed at 1/16 of the intended tile (≈ the sub-world corner) while the camera follows the true path. Tests encode the wrong convention. | Multiply by 16 (or use `LotTilePos.FromBigTile`-style conversion), and fix the tests. Compare `TS1GameScreen.cs:1851-1859`, which does it right. |
| **T2** | **H** | `TS1GameScreen.cs:1590, 1609, 1575-1582, 1826-1835, 1858-1861, 2674-2681` | A walked arrival can be rebound at `OUT_OF_WORLD` and never placed (no door recorded, or the destination already has a controlled sim); or dropped after `PendingTransitFrameLimit`. | Always resolve a door for a walk arrival and record it before rebinding; never give up on a live walk without returning the sim. |
| **T3** | **M** | `NeighbourhoodTransitLot.cs:654-657, 792-801, 849-876`; `TS1GameScreen.cs:2082`; `NeighbourhoodTransitLotVm.cs:1698-1701` | A zero-length walk (same door tile, or origin == destination) is begun, consumes the switch, and is never advanced/completed/cancelled. | Treat a zero-length path as an immediate arrival (complete on `Begin`). |
| **T4** | **M** | `LotTravelService.cs:618-621`; `TS1GameScreen.cs:2142-2167, 2641` | The recorded mode keys on `adopted` (incoming avatar), so a `Walk` with no transit VM is recorded as a discrete/VM-request hop. | Key on `TransitionReason.Walk`. |
| **T5** | **M** | `TS1GameScreen.cs:1146` | The transit→provider mirror is gated on B3 only, so a B4-only walk leaves `LotTransitInfo`/`DowntownSimGUID` stale. | Mirror for walks too (or gate on `Travel || Walk`). |
| **T6** | **M** | `NeighbourhoodTransitLotVm.cs:2075-2095`; `TS1GameScreen.cs:2250` | `Dispose()` discards the travelling sim's marshal (signature is `void`), so a failed cancel loses the sim. | Return the marshal and re-bind it on the cancel path. |
| **T7** | **M** | `SimitoneGlobalLink.cs:963-989`; `TS1GameScreen.cs:2435-2445` | Leave-lot deletes the local avatar before the switch stashes the lot, so a B5 transition cannot carry identity/inventory. | Stash first, delete after the handoff. |
| **T8** | **L** | `NeighbourhoodTransitLotVm.cs:2206-2223, 2231-2240` | Sub-world leak if `InitSubWorlds` throws (added but not removed); `Shutdown` disposes before removing. | Remove-before-dispose, matching `SimitoneSurroundingsRestore`'s unwind. |
| **T9** | **L** | `NeighbourhoodTransitLot.cs:70-78, 597-600, 797-798, 1111-1131` | Same-tile end waypoint carries the origin number; default ctor violates the one-active-lot invariant; waypoint doc vs `Reconstruct`; `Advance` no-op. | Correct each. |
| **T10** | **L** | `NeighbourhoodTransitLotVm.cs:384-392, 598-623` vs `SimitoneNeighbourTerrain.cs:299-307` | `TransitStreetTerrain.BaseLevel`/`MostCommonTerrain` duplicate the neighbour-terrain arithmetic the doc says is shared. | Share the helper. |
| **T11** | **X** | `NeighbourhoodTransitLotVm.cs:1499, 1862, 311-315`; `NeighbourhoodTransitLot.cs:70-78` | Stale doc references (`TransitionTo` does not exist; "the same expression"; waypoint lot numbers). | Correct. |
| **T12** | **L** | `TS1GameScreen.cs:2297, 3011-3012, 3097` vs `NeighbourhoodWorldRow.cs:91-101`; `NEIGHBOURHOOD_3D.md:88-90` | B4/B6 run with the master flags off, contradicting the Options-row and doc "inert without it". | Decide the dependency graph and make code and docs agree. |

### 2.9 Integration, camera, dead code

| ID | Sev | Site | What is wrong | Fix |
|---|---|---|---|---|
| **I1** | **M** | `NeighbourhoodCameraPlacement.cs:325-343, 370-390`; `UINeighbourhoodSelectionPanel.cs:2822-2826` | The 3D focus ignores the panel's `HouseFocusCenterOffset`, so 2D and 3D frame the same selected lot differently. | Apply the same offset in `ResolveFocus`. |
| **I2** | **L** | `INeighbourhoodSceneSource.cs:36-40`; `Neighbourhood3DView.cs:444-447` | `ActiveLot` is never set on the in-lot path; `SetActiveLot` has no production caller, so the facade pass's active-lot exclusion is always fed `null`. | Set it from the in-lot path, or delete the seam. |
| **I3** | **L** | `NeighbourhoodWorldCamera.cs:504-731`; `TS1GameScreen.cs:1389-1413, 2109-2114` | Lot-focus/free/zoom camera surface and `TransitWalkState`/`TransitWalkCamera` have no production reader; the transit camera is advanced but never rendered. | Wire the transit camera, or delete the dead API and correct the docs. |
| **I4** | **L** | `NeighbourhoodCameraPlacement.cs:229-244`; `NeighbourhoodWorldCamera.cs:720-731` | `FogDistanceFor` has no production caller; `IsSettled` ignores yaw. | Use it / include yaw. |
| **I5** | **L** | `LotTravelService.cs:553-618`; `TS1GameScreen.cs:2638` | The "pure, host never throws" service is called unguarded from `PerformLotSwitch` but guarded from `NeighborhoodService`; two callers, two contracts. | Guard at the host, or document the exception. |
| **I6** | **L** | `NeighbourHollowLoader.cs:135-139, 496-527, 668-675`; `TransitStore.cs:104, 111-120`; `SimitoneGlobalLink.cs:131`; `TS1LotContentSource.cs:100` | Assorted: a null-device refusal that materialises late; misleading `Describe`; `Active` is a shallow snapshot; a dead `_world` field; lot 99 accepted where 99 is a sentinel. | Per item. |
| **I7** | **X** | `NeighborhoodService.cs:501-507, 590-600` vs `NEIGHBOURHOOD_SCOPE_A_PLAN.md:294` | Invalidation is wired differently from the plan (not on `PlayHouse`/return-from-lot). | Reconcile plan and code. |
| **I8** | **X** | `NEIGHBOURHOOD_SCOPE_A_PLAN.md:291` | The `tso.client` `ProjectReference` was never added; the native mesh builder was used instead (documented in `NEIGHBOURHOOD_REUSE_AUDIT.md`). | Mark the plan historical. |

### 2.10 Documentation and stale comments

| ID | Sev | Site | What is wrong | Fix |
|---|---|---|---|---|
| **X1** | **H** | `NEIGHBOURHOOD_3D.md:7,10`; `NEIGHBOURHOOD_WORLD_HANDOFF.md:20` vs `NeighbourhoodViewSetting.cs:97` | "off / 2D by default" vs default 3D. | Pick one (see D2). |
| **X2** | **H** | `NEIGHBOURHOOD_3D.md:92-108` | B6/B7 described as shipped. | Correct or land W1/W2. |
| **X3** | **M** | `NeighbourhoodCityMeshBuilder.cs:1743, 1816-1818`; `NeighbourhoodFacadePlan.cs:52-53` | Three comments say `QuantiseAltitude` multiplies by `ElevationScale`/12 (the `16/3` error); it uses `ElevationByteForArryUnit`/9-4. Contradicts `NeighbourhoodMapAdapter.cs:34-43`. | Delete/correct — this is the highest-risk stale comment, because "fixing" it back would reintroduce the inflation. |
| **X4** | **M** | `NeighbourhoodMapAdapter.cs:76-77, 158-161, 196-197, 239, 248-249` | Street-width narrative is pre-`MainStreetWidth=6`: "four-tile corridor", deficit "three"/"12 units", "adds two". Actual: 8, 1, 24. Tests assert the new values. | Correct. |
| **X5** | **M** | `RoadGraph.cs:1049-1051, 1326-1331` | "two-tile margin / two tiles of street" after the margin became 8. | Correct. |
| **X6** | **L** | `NEIGHBOURHOOD_3D.md:231-234` | Says the region is measured to the "lowest ground on the border ring"; the code uses the centre-four mean. | Correct. |
| **X7** | **L** | `NEIGHBOURHOOD_3D.md:36-39` | Conflates the layout sidecar and the raster cache. | Correct. |
| **X8** | **L** | `NEIGHBOURHOOD_REUSE_AUDIT.md:33, 335` | "Part B is genuinely unstarted" is stale. | Correct. |
| **X9** | **X** | `Neighbourhood3DView.cs:46-50, 643-647`; `NeighbourhoodFacadePlan.cs:360-363`; `NeighbourhoodCityMeshBuilder.cs:1294-1301`; `SimitoneSurroundings.cs:204-208` | Assorted stale comments (depth-only clear; a re-upload that does not exist; a 900-tile fog; a deadband band; `SubWorldCount`). | Correct. |

---

## Part 3 — Cross-cutting themes

1. **Stale doc-comments are a live regression vector, not cosmetics.** X3 is the sharpest:
   three comments instruct a maintainer to "restore" the `9/4` multiplier that is already
   there, and the code's own adapter remarks contradict them. X4/X5 are the same shape for
   street width. Fix these *before* touching the code they describe.
2. **The same rule is written twice and can drift.** `HasOverlap` vs `NeighbourhoodLot.Overlaps`
   (G1); the ground UV rule (M5); `ShouldDraw` vs `Decide` (F3); the neighbour-terrain
   arithmetic (T10, S10); the diamond predicate vs its counters (M6); the road-band offsets vs
   the test-only arithmetic (S9).
3. **Default-on vs opt-in is unresolved.** D1/D2 and F1 are the same question — which switch
   owns "is the 3D world on" — asked in three places (view setting, Part B flags, the attach
   kill switch).
4. **Two authorities for lot state.** `TS1GameScreen.SavedLots` (live) vs
   `SimitoneNeighbourhoodWorld.LotStore` (dead), W2. Until they are one, B6 and B7 cannot work.
5. **Dead-but-documented subsystems.** W1, W2, W13, W18, I3. The docs claim shipped behaviour;
   the code has no caller. Each is either wired or deleted, not left.
6. **Reachability is gated inconsistently.** The view is gated by `NeighbourhoodViewSetting`,
   the click by the Part B flags (D1), the surround by both, the transit mirror by B3 only (T5),
   B4/B6 by nothing (T12).

---

## Part 4 — Fix plan

Ordered so that each phase leaves the product honest and shippable, and so that later phases
build on stable ground. "Acceptance" is what must be true to call the phase done.

### Phase 0 — Stop the bleeding (1–2 days)

The default configuration must work, and the two "does not do its job" defects must go.

| # | Item | Acceptance |
|---|---|---|
| 0.1 | **D1** — make the map-click gate follow `IsShowing3D` (the same rule that hides the buttons), not the Part B flags. | On a fresh profile (3D default, all flags off) clicking a house on the generated map opens it. |
| 0.2 | **D2/X1** — decide the default and make docs match. Recommended: `Default = Bitmap2D` (as the docs and handoff say), so the feature is opt-in; if 3D stays default, keep 0.1. | `simitone.ini` with no key resolves to the documented view; docs agree. |
| 0.3 | **R1** — `== ContainmentType.Contains` in `CollectVisibleChunks` and the log. | A lot-focused camera selects a handful of chunks, not 1024; the frame log's chunk count matches `ChunksInFrustum`. |
| 0.4 | **R2** — a real device-reset path (rebuild terrain/foliage/facade buffers; clear `_hashes`; wire `SimitoneSurroundings.OnDeviceReset`). | Alt-tab/device-lost in the neighbourhood and in a lot both keep drawing after the reset. |
| 0.5 | **R3** — cache the mean ground height. | No full-map loop per camera tick. |
| 0.6 | **F1** — make the B2 kill switch a real opt-out that is not overwritten and is reset per lot. | A build can force facades with B2 on; leaving a lot resets the attach state. |
| 0.7 | **S2** — null-guard `AttachSubWorlds`. | `AttachSubWorlds(null)` is a no-op, as documented. |

### Phase 1 — Correctness players can see (3–5 days)

| # | Item | Acceptance |
|---|---|---|
| 1.1 | **S1** — make one frame authoritative for ground altitude (set `Blueprint.BaseAlt` for TS1 lots, or stop subtracting `GroundOriginFor`; the bank must then carry the seam). | On a lot with a non-zero `ARRY(0)` median, the active lot's ground and the surround meet with no step; add a test with a raised lot. |
| 1.2 | **T1** — `LocalTileAt` in sixteenths; fix the tests. | A walked sim stands on the path tile, not the sub-world corner. |
| 1.3 | **T2** — always resolve+record a door for a walk arrival before rebinding; never give up on a live walk. | A walk into a lot that already has a controlled sim (resumed from `SavedLots`) places the sim at the door. |
| 1.4 | **T3** — zero-length walk completes immediately. | Two lots sharing a door tile switch without a stuck transit state. |
| 1.5 | **G1** — one "reserved corridor" definition. | `HasOverlap` agrees with `NeighbourhoodLot.Overlaps`; a relaxed layout's inter-lot gap matches the grid's. |
| 1.6 | **M1** — the LOD deadband's low edge. | The Full/Half band is centred on 320 with a 15% margin both ways; the doc comment matches. |
| 1.7 | **R4** — foliage in the in-lot surround (or an explicit, documented divergence). | A lot shows the same trees as the map view. |
| 1.8 | **T4/T5** — record walks as walks and mirror them to the provider. | A B4 walk updates `LotTransitInfo`; the transit table says "walk". |
| 1.9 | **S3** — the refusal path clears `_subWorlds` and the counts come from what was withheld. | The log/counts never say "region is real" when it is facades. |

### Phase 2 — Wire or delete the Part B capabilities (1–2 weeks)

This is the decision phase: each of these is either finished or removed, and the docs follow.

| # | Item | Acceptance |
|---|---|---|
| 2.1 | **W2** — one lot authority. Feed `SimitoneNeighbourhoodWorld`'s store from `SavedLots` (or make the simulator read `SavedLots`). | With B6 on, a neighbour's motives decay across a real save/switch. |
| 2.2 | **W1** — save the live session (clock, active lot, transit) on switch/exit; never write a default over a live value. | Toggling B7 on and switching lots preserves the world clock and active lot. |
| 2.3 | **W4** — one authoritative clock (or advance-only). | Pausing in a lot then leaving does not rewind neighbourhood time. |
| 2.4 | **W6** — payday per lot, not per avatar. | An N-Sim household is paid once per elapsed period. |
| 2.5 | **W5** — frame-invariant seeding for first-seen lots + a boundary test. | One long tick and many short ticks produce identical motive tables. |
| 2.6 | **W7/W9/W11/W12** — make the gates and defaults consistent (world flag through the ctor; B8 gating; global-link default false; one rounding rule). | "Flag off is byte-identical to the previous stage" holds for every flag. |
| 2.7 | **W8/W13/W16/W17/W18** — wire `ApplyStamps`/`LastSimulated` or drop them; give the B8 models a setting or mark them experimental; validate `ResolveActiveLot`; enforce `WithLayout`; decide `NeighbourhoodWorldUnification`'s fate. | No "documented but unreachable" subsystem remains. |
| 2.8 | **W3/X2** — update `NEIGHBOURHOOD_3D.md` to match whatever 2.1–2.7 decided. | Docs and code agree on what B6/B7 do. |

### Phase 3 — Polish and resources (2–4 days)

| # | Item | Acceptance |
|---|---|---|
| 3.1 | **R5–R8, R13, R16–R18, M2–M4** — resource lifetime, sampler save/restore, allocation removal, chunk bounds, coarse-edge and probe/UV fixes. | No per-frame allocations in the draw paths; no leaked textures; no state leaks; levels agree at the map edge. |
| 3.2 | **R14/R15** — active-lot skip in the facade draw; cache key includes the layout identity. | No facade for the active lot; no cross-neighbourhood collision. |
| 3.3 | **I1/I2** — 3D focus offset; set (or delete) `ActiveLot`. | 2D and 3D frame a selected lot the same way. |
| 3.4 | **I3/I4** — render the transit camera or delete the dead surface; include yaw in `IsSettled`. | No dead public API that a doc claims is live. |
| 3.5 | **G2–G10, M5–M8, S6–S10, T6–T12, I5–I6** — the remaining correctness/latent items. | Each fixed or explicitly deferred with a tracked issue. |

### Phase 4 — Documentation sweep (1 day)

| # | Item | Acceptance |
|---|---|---|
| 4.1 | **X3–X9** — every stale comment corrected. | A maintainer reading a comment cannot be led to reintroduce a fixed bug. |
| 4.2 | **X7/X8, I7/I8** — mark the plan/reuse docs historical where superseded. | The three docs do not contradict each other. |

### Suggested sequencing rationale

- **Phase 0 first** because D1 is a shipping blocker and R1/R2 make the feature unusable under
  ordinary play (zoom, alt-tab). Nothing else can be evaluated until the default works.
- **Phase 1** because S1/T1/T2 are the defects a player actually sees (sunken ground, a sim
  standing in a corner, a walk that loses the sim).
- **Phase 2** because B6/B7 are the difference between "a rendering of a neighbourhood" and
  "a neighbourhood that lives"; it needs the lot-authority decision (W2) before anything else
  in it can be built.
- **Phases 3–4** are cleanup and cannot regress behaviour.

---

## Part 5 — Verification strategy

- **Unit/pure seams already exist** for layout, roads, seams, mesh probes, flags, transit and
  camera placement. Extend them, do not replace them:
  - G1: assert `HasOverlap == NeighbourhoodLot.Overlaps` on grid and relaxed layouts.
  - M1: assert the Full↔Half boundary is 320 ±15% both ways.
  - S1: a raised lot (non-zero `ARRY(0)` median) whose surround meets with no step.
  - T1: a transit path whose `LocalTileAt` is a sixteenth of a tile.
  - T3: a zero-length path completes on `Begin`.
  - W5: a seed that lands near a step boundary; one long vs many short ticks.
- **Reachability tests** for D1/D2: build the panel's decision from default settings and assert
  that the lot buttons and the click agree (both on or both off).
- **Device-reset test** (R2): simulate a reset and assert the terrain still submits primitives.
- **Per-frame allocation checks** (R6–R8, R17): a debug counter or a test that draws twice and
  asserts no new managed allocations in the draw path.
- **Manual matrix**: {flat lot, raised lot} × {map view, in-lot view} × {fresh profile, all
  flags on} × {before/after a device reset}. Each cell must show a lot that is clickable, a
  ground that meets the surround, and a walk that places the sim.

---

## Part 6 — Appendix: verified correct (do not re-investigate)

So the next pass does not re-derive these:

- **Road atlas assembly and UVs** — `NeighbourhoodContent.DrawCells` fills straights/corners
  from their own tables exactly as `CityContent.CreateRoadAtlas`; road UVs in
  `NeighbourhoodCityMeshBuilder.AddRoadQuad` reproduce `CityGeometry.cs:356-402` byte for byte;
  ground UV `/4` matches `CityGeometry.cs:263`. *(The corner-table bug was fixed earlier this
  session.)*
- **Alpha conventions** — terrain premultiplied (matches `NCityPSFog`), facades/foliage straight
  (matches `CityObjPSFog`). The earlier suspicion that terrain used the wrong blend was wrong.
- **Shader wiring** — technique names, pass indices (`FogPass = 3`, `CityObjectOffsetVertexPass
  = 5`), and `BaseMatrix = viewProjection` / `MV = view` all match `Terrain.cs`.
- **Map ↔ lot-tile ↔ world scale** — `LotViewUnitsPerMapTile = 3`, sub-world `GlobalPosition`
  as the raw tile delta, `SubDraw`/`PreDraw`/`ToLotView` agree algebraically.
- **Sub-world altitude merge** — `SubWorldAltitudeFor` and `BaseAltitudeFor` are mutually
  consistent; no double-add for neighbours (S1 is active-lot-only).
- **Road band grammar** — `SimitoneLotViewRoad`'s offsets match `VMLotTerrainRestoreTools.RestoreRoad`.
- **Cull excludes the active lot for facades**, includes it for the road band (correct).
- **The flag machinery's defaults** — all seven resolve to off for missing/empty/unknown values.
- **`LotStore`/`TransitStore` invariants**, the clock handshake's unsubscription, the transit
  binder's ownership ordering, and the loader's cache/budget/refusal rules.

---

## Part 7 — Appendix: fixes already landed this session (not to be redone)

For completeness, these were applied and verified in the live build before this audit:

- `NeighbourhoodContent.LoadRoadAtlas` — corner cells now use the `roadcorner` textures
  (R17/§2.3 verified correct).
- `Neighbourhood3DView` — the frame clear now clears colour+depth to the fog colour, not
  depth only (R9's background half).
- `Simitone.GenerateMeshes` — `FillFolds` enabled (generator-side; needs a pack regeneration).
- Engine-side, outside this subsystem: `VMThread.TryRunImmediately` sets the running action's
  priority; `VMMultitileGroup.SetVisualPosition`/`VMAvatar.PlaceInSlot`/`ObjectComponent.Room`
  fix the contained-object `Room == 0` cull; `RCMeshProvider` treats an empty `.fsom` as a miss.

**One of those changes is itself flagged by this audit (F1):** tying
`SimitoneSurroundingsRestore.SubWorldAttachEnabled` to B2 defeated the documented kill switch
and never resets it. Phase 0.6 corrects it.

---

# Part 8 — The unified connected map (owner directive)

This part supersedes the "two views" framing of Parts 1–7 wherever they disagree with it.

## 8.1 The requirement, restated

1. **One connected map, not two views.** There must be no separate "neighbourhood view" and
   "lot view". The player sees the full connected map zoomed out at the very beginning; zooming
   in and clicking a lot takes control of that lot, in the same map.
2. **Lots at their real locations.** Lots are placed where `LotLocations.iff` says they are,
   not where the packer put them.
3. **Connectors inferred from the 2D map.** The connecting pieces (streets, driveways, paths)
   are derived from the 2D neighbourhood map and placed in 3D, because lots are often not
   directly adjacent.
4. **UI preserved.** The "create a family" button stays top-left; clicking a lot takes control
   of it.
5. **Lot view fixed.** (a) surrounding heightmaps correct; (b) the glue between lots is the
   real connectors, not a green mini-street; (c) the lot view loads the full connected map, not
   an odd crop with generated neighbours.

## 8.2 What the 2D data actually is (investigated)

- **Background bitmap, per mode**: `Nbhd\NScreen.BMP` (1), `Downtown\DScreen.bmp` (2),
  `VIsland\visland.bmp` (3), `Community\NScreen_unleashed.bmp` (4, the default),
  `Studiotown\DScreen.bmp` (5), `Magicland\DScreen.bmp` (7). All **800×600, 8-bit indexed**,
  decoded to BGRA. Filenames at `UINeighbourhoodSelectionPanel.cs:75-148`; loaded at
  `:2374-2390` through `TS1Global`; readable **headlessly** via `ITextureRef.GetImage()` →
  `TexBitmap` (`TextureRef.cs:206-210`).
- **Lot positions**: `LotLocations.iff` STR chunks, one per mode, one `"number,x,y"` line per
  lot (`LotLocationParser.cs:50-65`). **x/y are the lot's centre in the 800×600 bitmap**, not a
  corner (proved by `SimitoneNeighOBJExporter.cs:94-100`).
- **There is no road art or road mask.** Roads are only pixels in the background. The only road
  textures in the tree are TSO's generated-world atlas (`Content/Textures/terrain/road*.png`).
- **The lot size is `SIMI.GlobalData[23]` lot-tiles** (`TS1LotContentSource.cs:152-156`), not a
  fixed 6; 6 is the tarmac width.

**The load-bearing bug.** `TS1LotContentSource.ReadLot` never sets `MapX/MapY`
(`TS1LotContentSource.cs:115-125`), and `NeighbourhoodLayoutBuilder.ReadLots` parses `x,y` but
discards them (`:354-363`). So on the real path every `NeighbourhoodLot.MapX/MapY` is 0, and
the packer's "seed" is degenerate. `NeighbourhoodMapAdapter.InputFor` then drops them again at
the M1→M2 seam (`:342-367`). This is why lots are not at their real locations today.

## 8.3 Architecture

**One authoritative map, two camera states.** Keep one generated world (the existing
`NeighbourhoodWorldUnification` slot space, currently unused, is the substrate). The map view and
the lot view both render **that** map. The lot is not a different world: it is the active lot
rectangle rendered in place, at `GlobalPosition = 0` in the same scene, exactly as
`NeighbourhoodTransitLotVm` already stands up a real `World`+`VM` over a generated street
region. The screen switch (`NeighborhoodService.SuspendNeighbourhoodForLot`) becomes a camera
transition, not a teardown.

**Real positions replace synthetic packing.** `WorldX/WorldY` become the map-derived position:
`LotLocations (x,y) → tile position` through one explicit, invertible transform. The
`Seed → Relax → Separate → GridFallback → Clamp` pipeline (`NeighbourhoodLayoutBuilder.cs:572-
789`) and `DoorSideFor` (`:318-329`) are replaced by map-derived values (`DoorSide` from the
road mask).

**Connectors are inferred, then encoded.** A new `NeighbourhoodRoadInference` reads the
background bitmap and produces a boolean/nibble road mask in the same tile space; the existing
`RoadData` nibble vocabulary is preserved so `NeighbourhoodCityMeshBuilder.AddRoadQuadsForTile`
and `SimitoneNeighbourTerrain.RoadByteFor` keep working. Where pixel classification is
ambiguous, the fallback is lot-lattice inference (cluster lot centres into rows/columns and
connect adjacent ones), which is deterministic and needs no pixels.

**The lot view renders the same map.** The 40-tile crop + facade budget
(`SimitoneSurroundingsPlacement.cs:141`, `TS1GameScreen.cs:725`) becomes a camera-driven LOD of
the one map. The band-vs-map-road either/or (`SimitoneSurroundingsPlacement.cs:1892-1894`) is
replaced by "draw the map's real connectors, including driveways"; the band is kept only for the
lot-view seam.

**The ground join is one plane.** Set `Blueprint.BaseAlt` for TS1 lots to the lot's own base
level so the lot view's absolute ground and the surround's relative ground share a plane, and
use the real per-tile `ARRY(0)` field for the lot's own ground instead of the pinned median
(`TerrainSeamSolver.cs:358-378`).

## 8.4 The interface (frozen, so workstreams can proceed in parallel)

```csharp
// New. The single pixel<->tile transform. 800x600 bitmap <-> NeighbourhoodMap tiles (MapSize).
public static class NeighbourhoodPixelTransform
{
    public const int BitmapWidth = 800, BitmapHeight = 600;
    public static Vector2 TileForPixel(Vector2 pixel, int mapSize);
    public static Vector2 PixelForTile(Vector2 tile, int mapSize);
    public static int MapSizeForBitmap();            // the tile grid the bitmap maps onto
}

// New. Infers the connector mask from the 2D backdrop (falls back to lot-lattice inference).
public static class NeighbourhoodRoadInference
{
    // Reads mode's backdrop via ITextureRef.GetImage(); returns a MapSize*MapSize bool[] of
    // road-bearing tiles, in the SAME space as NeighbourhoodMap.RoadData.
    public static bool[] Build(ushort mode, ITextureRef backdrop, int mapSize,
                               IReadOnlyList<NeighbourhoodLot> lots, out string reason);
}

// Extended. Carries the map position and the inferred mask through the M1->M2 seam.
public struct NeighbourhoodLotInput { /* existing */ public int MapX, MapY; }
public sealed class NeighbourhoodMapInput { /* existing */ public bool[] RoadMask; }

// Extended. Consumes a supplied mask instead of synthesising streets.
public static class RoadGraph
{
    public static void Build(NeighbourhoodMapInput input, NeighbourhoodMap map, bool[] roadMask = null);
}
```

`NeighbourhoodLot.MapX/MapY` become authoritative; `WorldX/WorldY` are computed from them via
`NeighbourhoodPixelTransform`. `NeighbourhoodWorldMapping` is rebuilt on the same transform so
camera zoom/pan, house buttons and hit-testing agree with the world. Version bumps:
`NeighbourhoodLayoutBuilder.CurrentGeneratorVersion` (71), `NeighbourhoodMapGenerator.GeneratorVersion`
(276), `NeighbourhoodRasterCache.FormatVersion` (94), and `LayoutHash` must include the mask
source.

## 8.5 Workstreams

Partitioned by file ownership so they can be implemented concurrently without conflicts.
Dependencies are stated; a dependent workstream codes against §8.4 and does not edit the
foundation's files.

| WS | Owns (files) | Depends on | Deliverable |
|---|---|---|---|
| **WS1 — Data foundation** | `TS1LotContentSource.cs`, `NeighbourhoodLayoutBuilder.cs`, `NeighbourhoodMapInput.cs`, `NeighbourhoodMapAdapter.cs`, `RoadGraph.cs`, `NeighbourhoodMapGenerator.cs`, `NeighbourhoodRasterCache.cs`, `NeighbourhoodWorldMapping.cs`, `LotLocationParser.cs`, new `NeighbourhoodPixelTransform.cs`, new `NeighbourhoodRoadInference.cs` | — | `MapX/MapY` propagated; lots at real positions; connectors inferred from the bitmap; the mask reaches `RoadData`; the transform is the one mapping everywhere. |
| **WS2 — Lot view** | `SimitoneSurroundingsPlacement.cs`, `SimitoneSurroundings.cs`, `SimitoneNeighbourTerrain.cs`, FreeSO `VMTS1Activator.cs`/`Blueprint.cs` | WS1 (mask), WS4 (one map) | Correct ground plane (BaseAlt), real per-tile ARRY, real connectors in the crop (no green mini-street). |
| **WS3 — One connected map** | `NeighbourhoodWorldUnification.cs`, `TS1GameScreen.cs`, `Neighbourhood3DView.cs`, `NeighborhoodService.cs` | WS1 | One authoritative map; the lot rendered in place; zoom-out at start; the screen switch becomes a camera transition. |
| **WS4 — UI** | `UINeighbourhoodSelectionPanel.cs`, `UINeighbourhoodSwitcher.cs`, `NeighborhoodService.cs` (click path only) | WS1, WS3 | CAS button stays top-left; clicking a lot takes control (collapse the `UIHouseSelectPanel` intermediary). |

WS3 and WS4 both touch `NeighborhoodService.cs`; WS3 owns the suspend/resume and WS4 owns only
`OnHouseSelect`/`PlayHouse`, so the two edits are disjoint regions and are merged by hand if
they collide.

## 8.6 Acceptance

- Fresh profile: the full connected map is shown zoomed out, the CAS button is top-left, and
  clicking a lot enters it.
- A lot sits at its `LotLocations` bitmap position, relative to its neighbours, within the
  transform's precision.
- The streets and driveways between lots are the ones drawn on the 2D map, in 3D.
- Standing in a lot: the surrounding lots' ground matches the active lot's plane (no step) and
  shows their real graded heightmaps; the space between lots is street, not green.
- No screen teardown/rebuild on entering or leaving a lot.

## 8.7 Risks

- **Pixel road inference is new and heuristic.** Mitigation: the lot-lattice fallback, plus a
  per-mode cache keyed like the raster cache, plus a debug dump of the mask.
- **Real positions overlap** (TSO pitch is `Size−2`). Every `Size + StreetCorridorTiles`
  invariant (Part 2 G1, S1, and the sub-world budget) must be re-checked; this is why WS1
  bumps the layout generator version and forces a repack.
- **One scene is the largest change.** Mitigation: land WS1/WS2 first so the map and the lot
  view are already identical in content, then make the switch a camera transition.

---

# Part 9 — Ground-up rework on the Sims Online city model

**Owner directive: the bespoke `Neighbourhood*` renderer is rejected. Rework the neighbourhood
from the ground up, using the TSO city code that already exists in this repo as the model.**

## 9.1 Why the current code is rejected (diagnosed, not asserted)

`NeighbourhoodPixelTransform.TileForPixel` maps the 800×600 bitmap onto the full 512×512
**square** (`NeighbourhoodPixelTransform.cs:81-85`), but every draw and cull path clips to TSO's
**isometric diamond** (`NeighbourhoodCityMeshBuilder.DiamondLeft/Right/IsInsideDiamond`
`:1875-1920`, the road gate `:2357/:2745`, the surround band `SimitoneSurroundingsPlacement.cs:2220`).
Real `LotLocations` positions land in the cut corners, where **no ground quads and no road quads
are emitted** — "disconnected houses floating in a void, without lighting". The old packer was
diamond-aligned (`ProjectMapPosition`), so it happened to stay inside; the rework threw that
contract away. Separately, moving every lot's `WorldX/WorldY` invalidates the cross-visit facade
cache (`NeighbourhoodFacades.Adoptable` `:1408-1412`), so every facade is rebuilt on the game
thread, one per frame, at seconds each — the 20 s freeze. Both are structural, which is why the
owner wants the renderer replaced rather than patched.

## 9.2 The TSO model (what already exists and works)

The TSO city renderer is in-tree and is exactly the system Simitone needs:

- **`Terrain`** (`tso.client/Rendering/City/Terrain.cs:29`) — the scene: `_3DAbstract`,
  added to `GameFacade.Scenes`, own camera (2D iso / 3D orbit), zoom states `Far/Near/Lot`
  (`:1996-2001`), lighting + fog + day/night (`:1273-1327`, `:1481-1519`), a directional
  shadow map (`:1386-1403`), the sky dome (`:1556`), foliage (`:1602`), lot quads/facades
  (`:788-888`, `:1842-1991`), and a camera **`InheritPosition`** that focuses a lot
  (`:1332-1336`).
- **`CityGeometry`** (`CityGeometry.cs`) — the mesh: the 5-layer vertex format, the blend
  masks, the isometric clip + 10-tile fade (`:208-225`), Catmull-Rom subdivision of a 32×32
  slice (`:546-842`), and chunked submission (`:914-1008`).
- **`CityMapData`** (`CityMapData.cs:12-17`) — the arrays (terrain type, road, elevation
  bytes at 1/12 tile, forest density/type, terrain colour) and `Load`/`Save`.
- **`CityFoliage`** (`CityFoliage.cs`) — 18 tree models from `Content/3D`, deterministic scatter,
  the `TreeVS` offset trick.
- **`AbstractSkyDome`** — already in `FSO.LotView` (which Simitone references) and its sky strip
  ships.
- **The shaders** (`VerShader`/`PixShader`, `RenderNCity`/`RenderCityObj`/`TreeVS`) already ship
  and Simitone already draws with them.

**Every one of those is content-free** except `CityContent.LoadContent` and `CityMapData.Load`,
which read TSO's `gamedata/terrain/newformat/*.tga` ground + 60 blend-mask TGAs and a
`Cities/city_XXXX` folder. Those assets exist nowhere in this repo and cannot ship.

## 9.3 The two gates, and the decisions

1. **Assembly gate.** `Simitone.Client` deliberately does not reference `FSO.Client` (name
   collisions, `Simitone.Client.csproj:72-93`), so `CityGeometry`/`CityMapData`/`TLayerVertex`
   cannot be used literally. **Decision:** move the content-free city types down into
   `FSO.LotView` (already referenced by both `FSO.Client` and `Simitone.Client`), leaving
   `Terrain`/`CityContent`/`CityNeighGeom`/`LotThumbContent` in `FSO.Client`. `FSO.Client` then
   uses the moved types; nothing about TSO behaviour changes.
2. **Content gate.** **Decision:** keep generating the ground and blend textures
   (`NeighbourhoodTerrainTextures`) and the map (`NeighbourhoodMap`/`CityMapData`-shaped), and
   drive the **TSO geometry and draw code** with them. This is the only substitution; the road
   atlas, tree models, sky strip and shaders already ship.

**The lot-placement rule that the rework must state and obey:** a bitmap position becomes a
diamond-interior tile position through the bitmap's own **isometric projection** (the shape
`ProjectMapPosition` had), *not* an axis-aligned square mapping. Every consumer — layout,
camera, house buttons, click hit-test, road inference, and the mesh — uses that one transform.

## 9.4 The rework, as workstreams

| WS | Owns | Depends on | Deliverable |
|---|---|---|---|
| **RW0 — Stabilise** | `NeighbourhoodPixelTransform.cs`, `NeighbourhoodFacades.cs` | — | Make the transform diamond-aligned (lots land in the drawn region) and stop the facade cache rebuilding on a position change, so the game neither floats nor freezes while the rework lands. |
| **RW1 — Lift the city core** | `FreeSO/TSOClient/tso.world/**` (new `City/` types), `FreeSO/TSOClient/tso.client/Rendering/City/**` (make `FSO.Client` use the moved types), `.csproj`s | RW0 | `TLayerVertex`, `Blend`, `CityGeometry`, `CityMapData` live in `FSO.LotView`; `FSO.Client` still builds and behaves identically. |
| **RW2 — Drive it from Simitone** | `Client/Simitone/.../Neighbourhood/NeighbourhoodTerrainRenderer.cs`, `NeighbourhoodCityMeshBuilder.cs`, `NeighbourhoodContent.cs`, `Neighbourhood3DView.cs` | RW1 | The map view is drawn by the lifted `CityGeometry`/`Terrain`-shaped path with Simitone's generated content; the bespoke mesh builder is retired. |
| **RW3 — Foliage, sky, light** | `NeighbourhoodFoliage.cs`, `Neighbourhood3DView.cs` (sky/light) | RW1 | Foliage and the sky dome/light/fog come from the TSO path. |
| **RW4 — Lots on the city** | `NeighbourhoodFacades.cs`, `NeighbourhoodFacadePlan.cs`, `SimitoneSurroundings*.cs` | RW0, RW2 | Lots placed by the one transform, drawn as facades/sub-worlds at their diamond-interior tile positions, camera `InheritPosition`-style focus. |
| **RW5 — Lot in place** | `TS1GameScreen.cs`, `NeighborhoodService.cs`, `UINeighbourhoodSelectionPanel.cs` | RW2, RW4 | The lot is a camera state on the same city (the Part 8 §8.3 target). |

**Acceptance:** the map is the TSO-shaped isometric island; every real lot has ground and roads
under it; the connectors are the inferred ones; no floating houses, no per-visit freeze; the sky,
light and fog are the TSO model; and the lot is the same map at a closer zoom.

**Sequencing note.** RW0 is a stopgap only — it makes the current renderer not-broken so the
game is playable while RW1–RW5 replace it. If RW1 lands first, RW0's transform work is absorbed
by RW1.

## 9.5 RW0 as landed

**The transform.** `NeighbourhoodPixelTransform` now composes the bitmap's own projection with a
separable fit into the diamond. Write a tile position as `sum = tileX + tileRow` and
`difference = tileX - tileRow`; the projection sends bitmap x straight to `difference` and bitmap y
straight to `sum`, and the fit is a scale and a shift per diagonal:

```
sum        = sumMid        + sumInside        * (bitmapY / 600 - 0.5)   // 511.5 ± 195.5
difference = differenceMid + differenceInside * (bitmapX / 800 - 0.5)   //  0.5 ± 296.5
tileX      = (sum + difference) / 2
tileRow    = (sum - difference) / 2
```

scaled by `mapSize / 512`, so a smaller map is the same neighbourhood at a smaller scale. Pure,
invertible to the last bit (`PixelForTile`/`TileForPixel` round-trip), no floor in the transform,
and `OriginForCentre`/`ClampOrigin` unchanged in shape.

The spans are the diamond's own interior minus one `FadeRange` at each end: the 411-wide `sum`
diagonal and the 613-wide `difference` diagonal less 20 each. That margin is what makes the
guarantee unconditional — *every* position on the bitmap lands inside the drawn region, so no
`LotLocations.iff` and no modded lot can name one that does not. It is a fit keyed on the
backdrop's own extent rather than on where a neighbourhood's lots happen to sit, because a second
answer is the bug.

**Why separable rather than one uniform scale.** A single scale can only bind on the narrower
diagonal, and would then put the bitmap's 800 pixels of width through 261 of the 613 tiles the
`difference` diagonal has — a neighbourhood a further 2.3× smaller in each axis, about three
houses deep inside one another. Measured on the 41 real Unleashed lots, a uniform fit gives 201
overlapping pairs and a 248×241-tile lot spread; the separable fit gives 107 pairs and 381×388.
Both generate, so this is a judgement and the separable one is the larger neighbourhood.

**The consequence to know about.** The diamond is 48% of the map's area, so anything fitted into
it is smaller than the square mapping put it: real lots sit roughly 1.8× closer together than the
old square mapping left them, and overlap by more than TSO's own `Size - 2` pitch. That is §8.7's
"real positions overlap" arriving in a larger dose, not a new defect — but it does mean any
fixture written against the square mapping needs recalibrating, which is what the widened
four-lot and four-quadrant fixtures in `NeighbourhoodCachedWorldTests`,
`NeighbourhoodAsyncGenerationTests`, `NeighbourhoodWorldPersistenceLifecycleTests`,
`NeighbourhoodRasterCacheTests` and `NeighbourhoodWorldStateTests` are. At forty pixels of
backdrop pitch the sixteen-tile rectangles overlap and the road graph refuses the neighbourhood
as two components; at a hundred they clear each other by a street corridor.

**The cache.** `NeighbourhoodFacadeMeshCache.Adoptable` compares house hash, size and rotation,
and nothing else — the plot is not part of a *render*. `Reposition` is the step that lets it stay
that way: it translates the already-placed vertices (and re-reads the ground height under the new
plot), rewrites the vertex buffer, and updates `Lot`/`GroundHeight`/`Bounds`. Everything is built
and checked before any buffer is touched; a refusal leaves the mesh as it was, and `AdoptShared`
then drops and disposes it so the lot goes back to the build queue, which is the old behaviour.

**Versions.** `NeighbourhoodLayoutBuilder.CurrentGeneratorVersion` stays at 71 and no raster or
map-generator version is bumped. The placement rule changed but no sidecar in the wild was written
by it: the rule replaced a square mapping that predates the rework and every sidecar it wrote
holds positions the diamond fit would place elsewhere anyway, and a forced repack on a map nobody
is using is the §8.7 cost for no gain. RW1 supersedes the whole scheme.

**Verified.** `dotnet build Client\Simitone\Simitone.sln -c Release` — 0 errors.
`dotnet test Client\Simitone\Simitone.Tests\Simitone.Tests.csproj -c Release` — 8272 passed,
0 failed. `NeighbourhoodPixelTransformTests` includes the acceptance test that pins all 89 lots
of the six real `LotLocations.iff` chunks strictly inside `IsInsideDiamond`, worst margin 20
tiles.
