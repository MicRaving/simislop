# Connected mechanics — post-mortem, removal, and clean rework plan

**Instruction (owner):** the connected-mechanics rework is broken. Remove every connection-code
change, restore the previous 3D and 2D modes so they work properly, write a thorough plan, and
**keep the two modes separate**.

This document is that plan. It has four parts: an honest post-mortem, the exact inventory of the
code to remove, the removal procedure (Phase 0), and the clean rework design for the future.

---

## 1. Post-mortem — what actually went wrong

I was told, repeatedly, to make the neighbourhood and the lot one connected map. I did not
deliver that. What I actually did was **merge the two modes half-way**, which broke the mode that
worked and never produced the one map:

1. **I replaced the neighbourhood's own mesh renderer with the TSO city geometry** (RW1/RW2:
   lifted `CityGeometry`/`CityMapData`/`TLayerVertex`/`Blend` into `FSO.LotView.City`, rewrote
   `NeighbourhoodTerrainRenderer` to drive it). That is a real architectural change to the working
   3D mode, made for a merge that never landed.
2. **I changed lot placement from the synthetic packer to map-derived positions** (WS1/RW0) — a
   change whose first version mapped the bitmap onto the square instead of the isometric diamond,
   which put real lots in the cut corners with no ground. It was later made diamond-aligned, but
   it is still a change to how the previous mode placed lots.
3. **I changed the road graph from synthetic to inferred-from-the-bitmap** (WS1) — a heuristic
   classifier that first classified every tree as tarmac.
4. **I made the lot view's surround draw the shared map** (RW4/RW6) — it first drew 0 terrain
   (the lot floated in a void) and then a 137,968-triangle tarmac grid.
5. **I made the two views share one generated world** (WS3, `NeighbourhoodSharedWorld`) and park
   one scene when the other is up — the "two modes" glue, which is exactly what was asked to be
   kept separate.
6. **I moved FreeSO engine types between assemblies** (RW1), so the engine half cannot be reverted
   without the client half.

Net: the working 3D mode was destabilised, the lot surround was broken, the 2D path was made to
depend on new gating, and the promised single map does not exist.

**The lesson that shapes the plan below:** a connected map is not a refactor of the existing
renderers. It is either (a) a new, separate, opt-in mode built beside the two working modes, or
(b) not attempted. It must never replace the code that already works.

---

## 2. Inventory — the connection code to remove

### 2.1 FreeSO engine (`D:\Simitone\FreeSO`, a git repo)

| Path | State | What it is |
|---|---|---|
| `TSOClient/tso.world/City/**` | **new** | The lifted city types (`CityGeometry`, `CityMapData`, `TLayerVertex`, `Blend`, `CityAtlasLayout`, `ICityGeometryContent`). |
| `TSOClient/tso.client/Rendering/City/CityGeometry.cs`, `CityMapData.cs`, `TLayerVertex.cs`, `Blend.cs` | **deleted by the move** | Restore from HEAD. |
| `TSOClient/tso.client/Rendering/City/CityMapDataLoader.cs` | **new** | The content-bound half split out. |
| `TSOClient/tso.client/Rendering/City/CityContent.cs`, `CityFoliage.cs`, `Terrain.cs` | **modified** | `using FSO.LotView.City`, the atlas forwarders, the `ICityGeometryContent` implementation. |
| `TSOClient/tso.world/Components/AbstractSkyDome.cs` | modified | Sky-dome work for the connected view. |
| `TSOClient/tso.world/Model/Blueprint.cs` | modified | `AnchorBaseAltitude`/`MedianAltitude` (the ground-plane anchor). |
| `TSOClient/tso.simantics/Utils/VMTS1Activator.cs` | modified | Calls the anchor. |
| `TSOClient/tso.simantics/Utils/VMLotTerrainRestoreTools.cs` | modified | The landmark null guard (a real bug fix; see §2.4). |

### 2.2 Simitone client (`D:\Simitone\Client`, **not** under source control)

New files (delete):
`Neighbourhood/NeighbourhoodPixelTransform.cs`, `NeighbourhoodRoadInference.cs`.

Modified files that carry the connection work (revert each to its pre-rework behaviour):
`NeighbourhoodLayoutBuilder.cs`, `NeighbourhoodMapInput.cs`, `NeighbourhoodMapAdapter.cs`,
`NeighbourhoodMapGenerator.cs`, `NeighbourhoodRasterCache.cs`, `NeighbourhoodWorldMapping.cs`,
`RoadGraph.cs`, `TerrainSeamSolver.cs`, `TerrainFill.cs`, `NeighbourhoodCityMeshBuilder.cs`,
`NeighbourhoodTerrainRenderer.cs`, `NeighbourhoodContent.cs`, `NeighbourhoodMap.cs`,
`NeighbourhoodTerrainTextures.cs`, `Neighbourhood3DView.cs`, `NeighbourhoodFoliage.cs`,
`NeighbourhoodFacades.cs`, `NeighbourhoodFacadePlan.cs`, `NeighbourhoodFacadeBuilder.cs`,
`SimitoneSurroundings.cs`, `SimitoneSurroundingsPlacement.cs`, `SimitoneSurroundingsPlan.cs`,
`SimitoneSurroundingsRestore.cs`, `SimitoneNeighbourTerrain.cs`, `NeighbourHollowLoader.cs`,
`NeighbourhoodLifecycle.cs`, `NeighbourhoodWorldUnification.cs`, `TS1LotContentSource.cs`,
`LotLocationParser.cs`, `NeighbourhoodViewSetting.cs`, and the integration sites
`UI/Screens/TS1GameScreen.cs`, `UI/Panels/UINeighbourhoodSelectionPanel.cs`,
`UI/Panels/UINeighbourhoodSwitcher.cs`, `UI/Services/NeighborhoodService.cs`.

### 2.3 The two modes to restore

- **3D mode**: the neighbourhood's own renderer and synthetic layout as they were before §2.2 —
  the `NeighbourhoodCityMeshBuilder`/`NeighbourhoodTerrainRenderer` pair, the packer, the
  synthetic road graph, the facades.
- **2D mode**: the painted-map backdrop (`NeighbourhoodViewSetting` = 2D/off), untouched by any
  connected-map gating.

### 2.4 What is NOT connection code (keep it)

These are bug fixes that predate or are orthogonal to the connected work and should survive the
removal:
`VMThread.TryRunImmediately` (the running action's priority), the contained-object `Room` cull
fixes (`VMMultitileGroup`, `VMAvatar.PlaceInSlot`, `ObjectComponent.Room`), the empty-`.fsom`
fallback (`RCMeshProvider`, `DGRP3DMesh.HasGeometry`), the shadow scale (`LMapBatch`), and the
landmark null guard (`VMLotTerrainRestoreTools`). Remove only the connected work.

### 2.5 The blocker to executing this removal

**The Simitone client has no source control and no backup.** The connection code is interwoven
with the pre-existing rework across ~30 files, and 12 of them now reference the lifted
`FSO.LotView.City` types. So:

- the engine cannot be reverted without the client (the client would not compile), and
- the client cannot be reconstructed byte-for-byte from what is on disk.

**To execute Phase 0 cleanly I need one of:** a backup of `D:\Simitone\Client` from before the
connected work, a git repository for it, or the owner's confirmation to do a *functional* revert
(§Phase 0B) instead of a byte-exact one.

---

## 3. Phase 0 — restore the previous modes

### 0A. Byte-exact removal (preferred, needs the source)

1. `git -C FreeSO checkout HEAD -- TSOClient/tso.client/Rendering/City/CityGeometry.cs CityMapData.cs TLayerVertex.cs Blend.cs` (restore the moved files).
2. Delete `FreeSO/TSOClient/tso.world/City/` and `FreeSO/TSOClient/tso.client/Rendering/City/CityMapDataLoader.cs`.
3. Undo the `FSO.LotView.City` edits in `CityContent.cs`, `CityFoliage.cs`, `Terrain.cs`, `AbstractSkyDome.cs`, `Blueprint.cs`, `VMTS1Activator.cs` (keep the landmark guard).
4. Restore the pre-rework versions of every client file in §2.2 from the backup.
5. Delete the two new client files.
6. Bump `NeighbourhoodLayoutBuilder.CurrentGeneratorVersion` and `NeighbourhoodRasterCache.FormatVersion` once more, so any cache written by the connected code is discarded.
7. Build and test.

### 0B. Functional revert (if no source exists)

Where a pre-rework file cannot be restored, restore *behaviour*, not text:

- Point `NeighbourhoodTerrainRenderer` and `SimitoneSurroundings` back at the bespoke
  `NeighbourhoodCityMeshBuilder` path (it still exists — RW2 kept it for outside callers).
- Restore the synthetic packer/road graph as the default and gate the map-derived path off.
- Make the two views independent again: no `NeighbourhoodSharedWorld`, no scene parking, no
  surround drawing the map view's geometry.
- Set `NeighbourhoodViewSetting.Default = Bitmap2D` and turn the connected capabilities off by
  default, so the shipped game is the previous 2D mode plus the previous 3D mode, unchanged.

### 0C. Acceptance for Phase 0

- The 2D painted-map neighbourhood opens and plays exactly as before.
- The 3D neighbourhood opens and draws the same picture it drew before the connected work.
- Entering and leaving a lot does not touch the neighbourhood's renderer.
- No `NeighbourhoodSharedWorld`, no `DrawShared`, no inferred-connector or map-derived-position
  code is reachable.

---

## 4. The clean rework design (for later) — two modes, separate

The owner wants the modes **separate**. The correct shape is therefore:

### 4.1 Two independent modes, one shared *data* contract — no shared *renderer*

- **Mode A — Lot (3D, unchanged):** `TS1GameScreen` + `FSO.LotView.World`. The lot, its objects,
  its sims. The surround, if any, is drawn only from the lot's own content.
- **Mode B — Neighbourhood (unchanged):** `Neighbourhood3DView` + its own mesh renderer. Lots are
  placed by whatever rule the neighbourhood chooses.
- **The only shared thing is the lot list and its bitmap position** — data, not geometry, not a
  scene, not a camera, not a world cache. Neither mode may hold a reference to the other's
  renderer.

### 4.2 If a connected view is wanted later, it is a *third* opt-in mode

- **Mode C — Connected (new, off by default):** a single scene that draws the whole map with the
  active lot in place. It is built beside A and B, reuses their *data*, and is reachable only by
  its own setting. It must not replace A or B, and turning it off must leave A and B exactly as
  they were.
- Its acceptance is the owner's original ask: zoomed out at the start, click a lot to control it,
  real heights, real connectors, real houses — and **it is the only place any of that code runs.**

### 4.3 Rules that would have prevented this

1. Never change a working mode to prepare for a new one. Add the new one beside it.
2. Every new mode ships behind its own switch, default off.
3. A shared *renderer* between two modes is forbidden; a shared *data* contract is fine.
4. No engine type moves between assemblies for a mode that is not shipped.
5. Each mode has a screenshot acceptance test before it is called done.

---

## 5. Phases for the connected mode (only if/when it is wanted)

| Phase | Work | Acceptance |
|---|---|---|
| C0 | Design the data contract (lot list + bitmap position + heights) with no renderer sharing. | A pure type both modes can read; no references between scenes. |
| C1 | New `ConnectedMapView` scene, off by default, drawing the map with its own geometry. | It draws and the two existing modes are byte-for-byte unaffected. |
| C2 | The active lot rendered in place (its live `World` drawn inside the scene). | Entering a lot is a camera move, not a screen swap. |
| C3 | Real heights (per-tile `ARRY`) and real connectors (from the 2D map). | Terrain relief and streets match the painted map. |
| C4 | Real neighbouring houses (sub-worlds), bounded and cached. | Neighbours are real, and the cost is bounded. |
| C5 | Retire the connected code if it is not adopted. | The tree returns to two modes. |

---

## 6. Immediate next step

The plan's Phase 0 cannot be executed byte-exactly without the pre-rework client source. Please
provide one of: a backup of `D:\Simitone\Client` from before the connected work, a git repo for
it, or a decision to do the functional revert (0B). Until then I will not make further changes to
the connected code, because every change so far has made a working mode worse.
