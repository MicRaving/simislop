# Scope A implementation plan — a procedurally connected neighbourhood

**Scope A**: render every lot of a TS1 neighbourhood inside one connected, generated 3D
world — terrain, roads, water and foliage are generated, lots are drawn as facades, and the
player still enters lots discretely. No cross-lot simulation, no change to any TS1 save file,
and the existing 2D neighbourhood bitmap stays as a fallback.

This document is **Part A** of a two-part handoff; Part B (a live, simulated world) is
`docs/NEIGHBOURHOOD_WORLD_HANDOFF.md`. It is grounded in what already exists in this tree;
the "reuse" column below is deliberate, because most of the renderer is already here.

Scope A must remain complete and fully functional **on its own**, even if Scope B is never
finished. Part B may add preparation seams inside M0–M5 (see §15), but every one of them is
inert: an interface with a trivial Scope-A implementation, an extra forward-compatible field
in the layout sidecar, or a no-op tick hook. Removing all of them must leave Scope A exactly
as described below.

## 0. Confirmed decisions

These were open in §14 and are now locked:

| # | Decision | Value |
| --- | --- | --- |
| 1 | Default state of the 3D connected neighbourhood | **Off** (opt-in), until the look is proven |
| 2 | Generated map shape | **Accept `CityGeometry`'s isometric diamond** (no fork) |
| 3 | Terrain blend masks (`transa/transb`) | **Omitted** in the first version |
| 4 | Scope | **One generated world per mode**, matching the existing per-mode 2D maps |
| 5 | Scope B | Implemented **only after M5 is fully functional**; A must work without it |

---

## 1. Goal and non-goals

### Goal
- A neighbourhood is shown as a zoomable **3D scene**: one continuous terrain surface with
  roads, sidewalks, water and trees, with every lot placed on it and drawn as a low-detail
  3D facade (walls, roof, floor) via `LotFacadeGenerator`.
- Standing on a lot, the generated world is drawn **around** it as `World.Surroundings`, so
  the horizon is the rest of the neighbourhood instead of a void / static bitmap.
- The generated connecting pieces (road graph, terrain fill, elevation seams, foliage) are
  **procedural**: deterministic from a seed plus the neighbourhood's own lot data.
- Everything is derived from, and never written back into, the TS1 `UserData` folder.

### Non-goals (hard boundaries for Scope A)
- No avatar travelling between lots; no shared time/economy; no off-screen lot simulation.
  Entering a lot remains a discrete load (`NeighborhoodService.PlayHouse`).
- No edits to `Neighborhood.iff`, `LotZoning.iff`, `Houses/HouseNN.iff` or any TS1 file.
  Generated data lives in Simitone's own user directory sidecar.
- No network/online anything.
- The 2D `UINeighborhoodSelectionPanel` bitmap path is kept and must still work if generation
  fails, is disabled, or the GPU cannot build the scene.

---

## 2. What is reused vs built

| Concern | Reuse (already in tree) | Build (new) |
| --- | --- | --- |
| Terrain/road mesh from a map | `FSO.Client.Rendering.City.CityGeometry` — `RegenMeshVerts`, `SubRegenMeshVerts`, layer + road buffers | draw loop + textures (own content, see §9) |
| Map container | `FSO.Client.Rendering.City.CityMapData` (512×512 arrays: `TerrainType`, `ElevationData`, `RoadData`, forest) | the generator that fills the arrays |
| Road / blend / flag tables | `CityContent.RoadLayout`, `RoadCLayout`, `FlagLayout`, `RoadWidth/Height`, `RoadCorner` (all `static`) | — |
| Lot in the world | `FSO.LotView.Facade.LotFacadeGenerator` (walls/roof/floor mesh for "city or neighbourhood view") | per-lot generated-asset cache + draw |
| Terrain around the active lot | `FSO.LotView.RC.IRCSurroundings`, `FSO.LotView.World.Surroundings`, `WorldStatic.DrawBg` | `SimitoneSurroundings` implementation |
| Multiple lots in one world | `FSO.LotView.Components.SubWorldComponent`, `World.InitSubWorlds` (reference only — TSO grid, not used for layout here) | — |
| Lot data source | `FSO.Content.TS1.TS1NeighborhoodProvider` (`LotLocations`, `ZoningDictionary`, `GetHouse`, `FamilyForHouse`), `FSO.SimAntics.Utils.VMTS1Activator` (`SIMI.GlobalData[23]` size, `HOUS.CameraDir`, `ARRY(0)` heights, `ARRY(6)` grass, `HouseNumToType`) | layout builder |
| Map→world projection precedent | `Simitone.Client.Utils.SimitoneNeighOBJExporter` (`baseV = (x + 2y, 0, 2y − x)/scale`) | layout packer |
| Sidecar persistence | `Simitone.Client.Utils.NeighbourhoodManager` (names sidecar), `AtomicFileCommit`, `SimitoneLog` | layout sidecar store |
| Options plumbing | `GraphicsPreferences` / `GraphicsOptions.Apply`, `GlobalSettings.SurroundingLotMode`, `UIOptionsPanel` | `NeighbourhoodView` setting |

**Key finding:** `CityGeometry.RegenMeshVerts(gd, async)` only reads the `CityMapData`
instance and the *static* `CityContent` tables. It does **not** need a `CityContent` instance
to build the mesh — an instance is only passed to `DrawAll`/`DrawSlice` for textures. So we can
reuse the proven mesh generator unchanged, with our own content and draw loop, and avoid TSO's
`CityContent.LoadContent` (which reads TSO game data: `gamedata/terrain/newformat/*.tga`, TSO
`cities/city_XXXX/`, `userdata/houses/defaulthouse.bmp`). This is what makes Scope A tractable
without shipping TSO assets.

---

## 3. Architecture

```
                 Neighbourhood.iff / LotZoning.iff / LotLocations.iff / Houses/HouseNN.iff
                                          │
                         NeighbourhoodLayoutBuilder            (reads lots, sizes, terrain)
                                          │
                         NeighbourhoodLayout (pure model) ─────┐
                                          │                     │
             NeighbourhoodLayoutStore ────┘  sidecar: UserDir/Neighbourhoods/generated/<slot>.json
                                          │
          seed + layout ──► NeighbourhoodMapGenerator ──►  CityMapData (512×512 arrays)
                                   │   ├─ RoadGraph           (driveways → axis-aligned roads)
                                   │   ├─ TerrainFill         (types, water, forest scatter)
                                   │   └─ TerrainSeamSolver   (lot border heights → field)
                                   ▼
                        NeighbourhoodTerrainRenderer  ──► reuses CityGeometry buffers
                                   │
        ┌──────────────────────────┴───────────────────────────┐
        ▼                                                      ▼
  Neighbourhood3DView (UINeighborhoodSelectionPanel)     SimitoneSurroundings : IRCSurroundings
  + NeighbourhoodFacades (LotFacadeGenerator)            set on World in TS1GameScreen.InitializeLot
  drawn behind the existing buttons/switcher             drawn around the active lot
```

Two render entry points, one generated world:
1. **Neighbourhood view** — replaces the bitmap backdrop of `UINeighborhoodSelectionPanel`.
2. **In-lot surroundings** — `World.Surroundings` during a live lot.

Both share the same layout + generated map + facade cache.

---

## 4. Data model and persistence

### 4.1 `NeighbourhoodLayout` (pure, testable, no graphics, no I/O)
Per lot:
- `Number` (short), `Zone` (`LotZoning.ZoneResidential/Community/Studio`),
- `Size` (tiles, from `SIMI.GlobalData[23]`; lots are `size × size`),
- `MapX/MapY` (the `LotLocations` STR screen position — kept for the 2D fallback and for the
  packer's seed arrangement),
- `WorldX/WorldY` (packed rectangle origin in generated-map tile space),
- `Rotation` (from `HOUS.CameraDir`; 0 for Scope A unless needed),
- `TerrainType` (from `HouseNumToType`), `BaseHeight` (min/median of `ARRY(0)`),
- `DoorSide` (which rectangle edge the driveway is on),
- `HouseHash` (size+mtime of `HouseNN.iff`, so a changed lot invalidates its contribution),
- `HasHouse` (file present / bulldozed).

Plus `Slot`, `Seed`, `GeneratorVersion`, `MapSize` (512), `Extent`.

### 4.2 Sidecar
`<UserDir>Neighbourhoods/generated/<slot>.json`, version-stamped, written with
`AtomicFileCommit` — the same contract as `NeighbourhoodManager`'s `names.json`. Store **only
the layout and seed**, not the raster maps: the 512×512 arrays are regenerated
deterministically at load (a few hundred KB of JSON per map would be unnecessary). A version
this build does not understand is ignored and regenerated, never half-read.

```json
{
  "version": 1,
  "generatorVersion": 1,
  "slot": 4,
  "seed": 123456789,
  "mapSize": 512,
  "lots": [
    { "number": 21, "zone": 0, "size": 24, "mapX": 102, "mapY": 88,
      "worldX": 40, "worldY": 55, "rotation": 0, "terrain": 0,
      "baseHeight": 0, "doorSide": 2, "houseHash": "a1b2c3d4", "hasHouse": true }
  ]
}
```

Invalidation: slot folder missing → drop; `generatorVersion` or `seed` changed → regenerate
all; a lot's `houseHash` changed → regenerate that lot's placement and the roads/terrain that
touch it. Regeneration is cheap and always produces the same bytes for the same inputs, so a
`NeighbourhoodMapGenerator(seed, layout)` round-trip is a unit test.

Filenames mirror `NeighbourhoodManager.SlotFolderName` so slot ids, not paths, are the key.

---

## 5. Layout construction

`NeighbourhoodLayoutBuilder.Build(provider, slot, seed)`:

1. Read `LotLocations` STR for the slot's mode and parse with `LotLocationParser` (skip 99 and
   malformed entries). This gives `MapX/MapY` per selectable lot.
2. For each lot, `provider.GetHouse(id)` → `SIMI` (size), `HOUS` (CameraDir), `ARRY(0)`
   (heights), `ARRY(6)` (grass). Read `provider.ZoningDictionary` and
   `provider.FamilyForHouse` (occupied / community).
3. Map each lot's terrain type with the same table `VMTS1Activator.HouseNumToType` uses, so a
   lot's generated surroundings match the lot's own ground.
4. **Pack** rectangles:
   - Seed position: project `MapX/MapY` through the exporter's isometric transform
     (`(x + 2y, 0, 2y − x)/scale`) so the generated world keeps the rough arrangement players
     already recognise from the bitmap.
   - Footprint = `Size + margin` (margin for sidewalks).
   - Resolve overlaps by a bounded relaxation (push apart along the smallest overlap axis,
     then re-snap `WorldX/WorldY` to an integer tile grid). Fall back to a grid ordered by
     `MapY, MapX` if relaxation fails to converge.
   - Clamp into `0..MapSize−footprint`.
5. `DoorSide` = the empty rectangle edge nearest `WorldX/WorldY` of the map's road skeleton
   (or, pre-road-graph, the edge nearest the map border). Used by the road graph.

This step is pure once the provider reads are captured as inputs, so it is unit-testable with
canned lot records (including a lot with no house, a community lot, and two overlapping lots).

---

## 6. Procedural connecting pieces

`NeighbourhoodMapGenerator.Generate(layout, seed) → CityMapData` (512×512). All arrays are
produced in one deterministic pass; `CityMapData`'s parameterless constructor + public arrays
are used directly.

### 6.1 Roads (`RoadGraph`)
- Inputs: lot rectangles, `DoorSide`.
- Build a **street skeleton**: cluster lot centres into rows/columns by proximity (or, for a
  small neighbourhood, run an axis-aligned Manhattan MST over driveway points) and snap
  segments to a coarse street grid.
- Connect each driveway to the nearest skeleton point with a short spur.
- Rasterize segments into `RoadData`: low nibble = `CityContent.RoadLayout` index for
  straight/T/cross by neighbour degree; high nibble = `RoadCLayout` corner index at turns.
  Distances: main streets 2 tiles wide, spurs 1.
- Add **sidewalks** as a 1-tile ring around every lot rectangle (a separate cosmetic strip;
  does not need a road bit).
- **Connectivity check:** flood-fill the road graph; if a component is isolated, add the
  cheapest axis-aligned edge to the main component and re-rasterize. A neighbourhood must be
  one connected road network — this is an explicit, assertable invariant.

### 6.2 Terrain fill
- `TerrainType`: lot interiors get the lot's own type; the connective area is filled by
  nearest-lot-type (Voronoi over lot rectangles) so ground colour changes are coherent.
  Optional water: keep any region the 2D map art implies is water as `WATER` (cheap first
  version: leave it to the generator's edge falloff / a simple noise mask; the panel's
  animated water overlays are a later refinement).
- `ForestType`/`ForestDensity`: scatter trees outside lots/roads/sidewalks with a seeded
  Poisson-disk pass, using the existing tree atlas (`tso.content/Content/Textures/terrain/trees.png`).
- `TerrainTypeColorData`: derived from type (needed only if we draw the TSO-style tint; can be
  identity initially).

### 6.3 Elevation and seams (`TerrainSeamSolver`) — the hard part
- Base plane at `min over lots of BaseHeight` (quantized to the `ARRY(0)` scale, the same
  `byte` `CityMapData.ElevationData` holds; `CityGeometry` divides by 12).
- Lot interiors are **fixed boundary conditions** set from each lot's own heights along its
  border ring; the generated connective region between lots is solved by a bounded
  relaxation (Gauss–Seidel / multigrid on the free tiles) with those rings as Dirichlet
  boundaries, so terrain meets every lot edge continuously.
- Roads and sidewalks are flattened (their elevation = the local lot/door height).
- If two adjacent lots' border heights disagree beyond a threshold, insert a short embankment
  ramp across the margin rather than a vertical step.
- Because TS1 lots are usually near-flat, this must degrade to "copy the lot ring height" with
  no visible work; the solver exists for the general case, not the common one.

Each of §6.1–6.3 is a pure function of the layout + seed, so each gets its own test and a
debug dump (`CityMapData.Save` already writes the five PNGs) for visual inspection.

---

## 7. Rendering

### 7.1 Terrain mesh
`NeighbourhoodTerrainRenderer`:
- Owns a `CityGeometry` + our generated `CityMapData`.
- Calls `RegenMeshVerts(gd, async: true)` (and, for close zoom, the chunked
  `SubRegenMeshVerts` the TSO renderer already uses) to build `LayerVertices[0..4]` and
  `RoadVertices`.
- Draws those buffers with our own effect and texture atlases (see §9); the blend-mask pass is
  optional and initially omitted (flat per-layer terrain + roads), which is enough for a
  readable neighbourhood and can be added later.
- Because `CityGeometry`'s tile loop clips to the TSO isometric diamond
  (`xStart = 306 − i`, …), the generated map is rendered as that diamond. That is acceptable
  and TSO-like. If a full square is wanted, fork `CityGeometry` into Simitone (already
  diverging in content) rather than editing FreeSO.

### 7.2 Lot facades
`NeighbourhoodFacades`:
- Per lot, build a `LotFacadeGenerator` mesh once from the lot's `Blueprint` (the
  `SimitoneNeighOBJExporter` already does exactly this: load IFF → `VMTS1Activator.LoadFromIff`
  → `Generate`), cache by `HouseHash`.
- Draw as static geometry, culled by frustum; skip the active lot (it is drawn in full by the
  lot view, and `surroundNumber == 1` already tells us subworlds/other lots exist).
- Lots with no house / bulldozed draw nothing (or a bare plot).

### 7.3 Neighbourhood view
- `Neighbourhood3DView` is a `_3DScene` added to `GameFacade.Scenes`, drawn behind
  `UINeighborhoodSelectionPanel`'s existing controls.
- The panel keeps its house buttons, switcher and selection logic. `HousePositions` (bitmap
  x,y) is mapped to world tiles with the same transform the packer used, so
  `NeighCameraMachine` still drives "full screen" vs "focus this lot" — the camera target
  vector is mapped from bitmap space into the 3D camera's pan/orbit.
- Clicking the backdrop still resets zoom (`ResetZoom`).

### 7.4 In-lot surroundings
`SimitoneSurroundings : IRCSurroundings`:
- `DrawSurrounding(gfx, camera, fogColor, surroundNumber)` draws the generated terrain mesh
  (translated so the active lot's rectangle aligns with the lot's own origin) and the
  neighbouring lot facades, using the active camera and the lot's fog/lighting.
- Set in `TS1GameScreen.InitializeLot()` right after
  `World = new FSO.LotView.World(GameFacade.GraphicsDevice);` (line ~805), and cleared in
  `CleanupLastWorld()`. Gated on the `NeighbourhoodView`/`SurroundingLotMode` setting; when
  off, `World.Surroundings` stays `null` and behaviour is exactly today's.
- This is the click that replaces the line-1018 comment (“surrounding lots are not implemented
  in this mode”) with a real implementation for the *generated-terrain* case.

---

## 8. Integration points (exact)

| File | Change |
| --- | --- |
| `Client/Simitone/Simitone.Client/Simitone.Client.csproj` | add `ProjectReference` to `FreeSO/TSOClient/tso.client/FSO.Client.csproj` (for `CityGeometry`/`CityMapData`); it already ships in the output |
| `Simitone.Client/UI/Screens/TS1GameScreen.cs` | set/clear `World.Surroundings` in `InitializeLot()` / `CleanupLastWorld()`; own the `NeighbourhoodTerrainRenderer` + facade cache lifetime |
| `Simitone.Client/UI/Panels/UINeighbourhoodSelectionPanel.cs` | add a 3D backdrop mode; map `HousePositions` → world for `NeighCameraMachine`; keep bitmap fallback |
| `Simitone.Client/UI/Services/NeighborhoodService.cs` | build/refresh layout on `NeighSelection`; invalidate on `PlayHouse`, `ExitLot`, `EvictLot`, `ResetHouse`, `RezoneLot`; keep 2D path on failure |
| `Simitone.Client/Utils/GraphicsOptions.cs` + `GlobalSettings` + `UIOptionsPanel` | new `NeighbourhoodView` (Off / 2D / 3D) and reuse `SurroundingLotMode` for in-lot surroundings |
| `docs/` | this file, plus a short player-facing note when it lands |

New code lives in `Simitone.Client/Neighbourhood/`; tests in
`Simitone.Tests/Neighbourhood/`.

---

## 9. Content and asset strategy

| Asset | Source |
| --- | --- |
| Road atlas (`road*.png`, `roadcorner*.png`) | already in tree and shipping: `FreeSO/TSOClient/tso.content/Content/Textures/terrain/` |
| Tree atlas (`trees.png`) | same |
| Terrain base textures (grass/sand/rock/snow/water) | map from TS1's own terrain textures where available; otherwise ship small generated/CC0 textures (Simitone already ships its own Content, e.g. weather clips) |
| Blend masks (`transa*/transb*`) | **not required initially**; the first version omits cross-fades. If wanted, generate a small alpha-ramp set rather than sourcing TSO's |
| Shader | reuse `Effects/VerShader` + `Effects/PixShader` (both ship in the output) or add a small `NeighbourhoodTerrain.fx` |
| Sky / weather | reuse `World`'s existing sky (`SkyDomeComponent`) and the lot's weather, not TSO's `AbstractSkyDome`/`WeatherController`, to stay consistent with the lot view |
| `defaulthouse.bmp`, TSO `city_XXXX` | not used — facades come from each lot's own IFF |

Choosing our own draw loop over `CityContent` is what removes the TSO-content dependency.

---

## 10. Performance

- Mesh generation: `RegenMeshVerts(gd, async: true)` off the game thread + upload on
  `GameThread.NextUpdate`; use `SubRegenMeshVerts` chunked regeneration for near view, exactly
  as `Terrain.Draw` does.
- Facades: generated once per lot, cached by `HouseHash`; frustum-cull; only regenerate the
  lots whose hash changed.
- Budget: a 512×512 map is the same order as TSO's city and is already GPU-light enough on
  target hardware; the default should be 3D **off** and opt-in, so no existing player pays for
  it.
- Loading: layout build reads every `HouseNN.iff` once (the OBJ exporter already accepts this
  cost); cache the result in the sidecar and gate it behind the setting.

---

## 11. Testing strategy

Follows the existing `Simitone.Tests` style (pure logic, no GPU; graphics/guards asserted
through pure helpers).

Pure/no-GPU:
- `NeighbourhoodLayoutTests` — parse → layout; sizes from `SIMI`; terrain type per house;
  skip 99/malformed; community vs residential; occupied flag.
- `NeighbourhoodPackerTests` — no overlaps; stays in bounds; stable across runs; grid fallback.
- `RoadGraphTests` — every lot reachable (connectivity invariant); corner flags at junctions;
  isolated component gets reconnected; determinism.
- `NeighbourhoodMapGeneratorTests` — same seed+layout ⇒ identical arrays; different seed ⇒
  different forest; lot interiors preserve type/height; roads never cross lot interiors.
- `TerrainSeamTests` — border rings preserved; flat neighbourhood is a no-op; step is ramped.
- `NeighbourhoodLayoutStoreTests` — atomic write; round-trip; version rejection; unknown slot;
  corrupt JSON ignored; never writes under `UserData`.
- `NeighbourhoodViewTests` — map↔world mapping agrees with the packer; setting off ⇒ 2D path
  selected; generation failure ⇒ 2D path selected.

Guard/regression:
- A neighbourhood with a broken `HouseNN.iff` still opens (2D), with the bad lot's facade
  skipped and the rest generated.
- With the setting off, `World.Surroundings` is null and nothing changes.

Debug aids:
- `CityMapData.Save` writes `terrain/road/elevation/forest` PNGs for eyeballing in the export
  folder (`NeighModel<mode>` convention already used by `UINeighborhoodSelectionPanel`).

---

## 12. Milestones

| Milestone | Deliverable | Acceptance |
| --- | --- | --- |
| **M0 — Reuse spike** | Hand-build a `CityMapData`, run `CityGeometry.RegenMeshVerts`, draw with the road atlas + a flat terrain shader in a bare 3D scene | It renders, at acceptable fps, with **no TSO content loaded**; screenshot in the PR. De-risks the whole approach. |
| **M1 — Layout + sidecar** | `NeighbourhoodLayout`, `Builder`, `Store`; no rendering | Unit tests green; layout persists and round-trips; no `UserData` writes |
| **M2 — Generator** | `NeighbourhoodMapGenerator`, `RoadGraph`, `TerrainSeamSolver` | Determinism + connectivity + seam tests green; debug PNGs look sane |
| **M3 — Neighbourhood view** | 3D backdrop in `UINeighborhoodSelectionPanel`, facades, camera mapping | Lot selection/switcher/zoom all still work; toggling the setting switches 2D↔3D; 2D fallback on failure |
| **M4 — In-lot surroundings** | `SimitoneSurroundings` on `World.Surroundings` | Generated terrain + neighbouring facades render around a live lot; off ⇒ identical to today |
| **M5 — Lifecycle + polish** | Invalidate/regenerate on evict/reset/rezone/build-save; options UI; logging; perf pass; docs | Editing a lot is reflected on return; perf budget met; docs updated |

M0 is the gate: if reusing `CityGeometry` without TSO content does not look or perform
acceptably, the fallback is a Simitone-native mesh writer (a few hundred lines) fed by the
same generated arrays — the rest of the plan (M1, M2, M5) is unchanged.

---

## 13. Risks and mitigations

| Risk | Likelihood | Mitigation |
| --- | --- | --- |
| `CityGeometry` drags in TSO content unexpectedly | Low (confirmed static-only for generation) | M0 spike; otherwise native mesh writer |
| Terrain seams visible between unequal lots | Medium | Dirichlet borders + ramp; TS1 lots are mostly flat so default case is cheap |
| Layout build is slow on many lots | Medium | Sidecar cache + `HouseHash`; regenerate only changed lots; gate behind setting |
| Memory/frame cost of a 512² mesh | Medium | Async + chunked regen; opt-in default; lower-LOD overview |
| Divergence from upstream FreeSO | Medium | Prefer Simitone-side code; only touch FreeSO if a tiny virtual hook is unavoidable, and note it in `FORK_INGESTION.md` |
| Generated world looks wrong vs the hand-painted map | Medium | Keep the bitmap as the default and a fallback; use the exporter's projection so layout stays recognisable |

---

## 14. Decisions (confirmed)

Recorded in §0. The earlier open questions are closed: opt-in default, isometric diamond,
no blend masks initially, one generated world per mode. Scope B's relationship to the
per-mode decision is discussed in Part B (`docs/NEIGHBOURHOOD_WORLD_HANDOFF.md`, §5).

---

## 15. Scope B preparation (inside M0–M5)

These are the only concessions Scope A makes to Part B. Each is inert for A, each is
independently removable, and none is allowed to make an M0–M5 acceptance criterion depend on
Scope B existing. They are listed against the milestone that should introduce them.

| Prep item | Introduced in | What it is | Why it is safe for A |
| --- | --- | --- | --- |
| `INeighbourhoodWorld` | M1 | The world facade the screen will eventually own: active lot, `INeighbourhoodClock`, `Tick(delta)` (no-op), lot queries | A provides `ReadOnlyNeighbourhoodWorld`, a thin adapter over `FSO.Content` + layout; A calls only read members |
| `NeighbourhoodLayout` forward fields | M1 | Optional per-lot `LotState` (unloaded/hollow/active), `LastSimulated`, `Entrance` — absent ⇒ defaulted | Unknown JSON members are already ignored by the store's version rule |
| Sidecar schema versioning | M1 | `schemaVersion` separate from `generatorVersion`; A writes schema 1 (layout only) | A never reads fields it did not write; B upgrades in place |
| `NeighbourhoodQuery` (tile → lot/road/water/blocked; entrances; graph adjacency) | M2 | A first-class, queryable form of what `RoadGraph`/`TerrainFill` already produce | A already needs the road graph for rendering and `DoorSide`/entrances for facade orientation; this is the same data, exposed |
| Persisted road **graph** (nodes + edges), not only rasterized bytes | M2 | The graph A renders roads from is also the graph B routes sims on | Rendering consumes it as before; nothing about the mesh changes |
| `INeighbourhoodClock` | M1 | Clock authority; A's impl mirrors the active lot's `VM.Context.Clock` | A reads through it and gets identical values; it is never written by A |
| `ILotContentSource` | M1 | Read model for lot size/terrain/family/occupancy/hash | A's builder already needs exactly this; B swaps in a mutable implementation |
| `INeighbourhoodSceneSource` | M3 | What the renderer draws (terrain + per-lot facade/subworld/active) | A implements it statically; the renderer reads no game state directly |
| `ILotEntrance` | M2 | World tile + facing for a lot's door | A computes it for roads; B reuses it for arrival placement |
| `World.Tick(delta)` hook | M4 | One call in `TS1GameScreen.Update`; A's impl is empty | No-op; deleting the call changes nothing in A |
| Immutable generated maps + a game-thread "apply" step | M2 | Generation stays pure/worker-safe; mutation only on `GameThread.NextUpdate` | A already uploads on the game thread; B's simulation just joins that boundary |
| Derived RNG streams from the world seed | M2 | `worldSeed → {generation, simulation, foliage}` so tuning one does not disturb the others | A only uses `generation` |

Deliberately **not** prepared in A: multi-lot simulation, avatar transit, hollow-lot
management, the global link, world persistence beyond schema 1. Those are Part B's, and
building them early risks A's "will not exceed its own scope" guarantee.
