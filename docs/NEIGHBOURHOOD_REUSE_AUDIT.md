# Connected-neighbourhood reuse audit

**Question asked:** *"the connected world should, in 3d, load the connected neighbourhood
landscape that the initial author of the project already seemed to have created and place models
there. Connectors should be procedurally generated which may need dedicated functions or is
already implemented in FreeSO."*

**Answer, in one paragraph.** The thing the initial author built is real, complete, and **already
shipped and default-on** — it is not a prototype, it is the finished Scope A implementation
(`Client/Simitone/Simitone.Client/Neighbourhood/`, 30 files, ~385 KB, 388 tests, no
`NotImplementedException` and no stubbed member in the folder). The procedural connectors the
player asks about are **already implemented, in Simitone, not in FreeSO** (`RoadGraph`,
`TerrainFill`, `TerrainSeamSolver`). There is no `NeighModel` landscape asset and no second
pre-existing 3D neighbourhood hiding anywhere; `NeighModel<mode>` is only an *output folder name*
for a debug OBJ export. So the work reduces to: reuse the finished Simitone world almost entirely,
and the one genuine open question — whether to also reuse FreeSO's `CityGeometry` — is answered
below with a measured result rather than a guess.

---

## 0. Headline findings

| # | Finding | Evidence |
| --- | --- | --- |
| **F1** | **A complete, connected 3D neighbourhood already exists and is the default.** Terrain, roads, water, trees, per-lot 3D facades, a 3D map backdrop and in-lot surroundings are all shipped. | `NeighbourhoodViewSetting.cs:95` (`Default = World3D`); `NEIGHBOURHOOD_3D.md:7-13`; `ADDON_PROGRESSION_PLAN.md:1043` |
| **F2** | **Procedural connectors already exist, Simitone-side, and are pure/deterministic.** | `RoadGraph.cs:543`, `TerrainFill.cs:197`, `TerrainSeamSolver.cs:149`; wired at `NeighbourhoodMapGenerator.cs:309-318` |
| **F3** | **`tso.client` contains no procedural generator at all.** It only *consumes* a 512×512 raster it loads from disk. There is nothing to reuse for the generation half. | `CityMapData.cs:42-81` (`Load` from `roadmap/terraintype/foresttype/...`); the only writers in-tree are the interactive `MapPainterPlugin` (`:325`, `:396`, `:501`) |
| **F4** | **The `tso.client` reference problem is real but is *not* what the existing docs say it is.** It is **CS0433** (same fully-qualified name in two assemblies), not CS0104 ambiguity. Measured: **13 errors** against the real `Simitone.Client` source set. | §4; reproduced empirically |
| **F5** | **`extern alias` does work** — it is not a dead end — but it is still the wrong answer here, for a reason that has nothing to do with the collision (see F6). | §4.3 |
| **F6** | **A plain `ProjectReference` to `tso.client` would also break the B-12 build split's hard guard**, which is a second, independent blocker. | `Simitone.Windows.csproj:862-904` |
| **F7** | **The one thing worth having out of `tso.client` — the 6 City leaf files — compiles into `Simitone.Client` today with zero reference to `tso.client` and zero source edits.** Measured: full real source set, **0 errors**. | §4.4 |
| **F8** | **Assets are present and sufficient.** Road atlas, corner atlas, tree atlas, tree models and the city shaders all ship in `tso.content` and are already in use. | §5 |
| **F9** | **Part B is genuinely unstarted** — every named Part B type is absent. But the Part A seams it needs are in place. | §6 |

---

## 1. What already exists — the connected world (REUSE AS-IS)

All of it is Simitone's own; the `Neighbourhood/` folder is 30 files:

```
Client/Simitone/Simitone.Client/Neighbourhood/
  NeighbourhoodLayout / Builder / Store        (M1)  read lots, pack rectangles, sidecar JSON
  RoadGraph / TerrainFill / TerrainSeamSolver  (M2)  <- THE PROCEDURAL CONNECTORS
  NeighbourhoodMapGenerator                     (M2)  the one deterministic pass
  NeighbourhoodMap / MapInput / MapAdapter      (M2)  the 512x512 raster + unit conversion
  NeighbourhoodCityMeshBuilder                  (M3)  terrain mesh, diamond clip, roads
  NeighbourhoodTerrainRenderer / Content /      (M3)  GPU buffers, draw loop, textures
    NeighbourhoodTerrainTextures / Foliage
  NeighbourhoodFacades / FacadeBuilder /       (M3)  per-lot 3D models from each HouseNN.iff
    FacadePlan
  Neighbourhood3DView / CameraPlacement        (M3)  the 3D map backdrop scene
  SimitoneSurroundings / ...Placement          (M4)  the world drawn around a live lot
  NeighbourhoodLifecycle / Invalidation /      (M5)  caching, invalidation, async build
    NeighbourhoodWorldCache
  INeighbourhoodWorld / INeighbourhoodClock /  (§15)  Part B prep seams, all inert
    ILotContentSource / INeighbourhoodSceneSource
```

### 1.1 The two render entry points (both live)

- **Map view.** `Neighbourhood3DView` is an `_3DScene` added to `GameFacade.Scenes`, drawn behind
  `UINeighborhoodSelectionPanel`'s existing controls. Wired at
  `UINeighbourhoodSelectionPanel.cs:589` (`_world3D`), `:904` (`AttachWorld`), `:931` (`TryCreate`).
- **In-lot surroundings.** `SimitoneSurroundings : IRCSurroundings`, attached in
  `TS1GameScreen.cs:1071` (`AttachSurroundings()`), detached at `:985`, updated at `:780`, and it
  assigns `World.Surroundings` at `TS1GameScreen.cs:441`.

### 1.2 Both are already default-on

- `NeighbourhoodViewSetting.cs:95` — `public const NeighbourhoodView Default = NeighbourhoodView.World3D;`
- `NeighbourhoodViewRow.cs` / `SurroundingLotModeRow.cs:216` — `Default = Low`, the lowest
  still-on step for in-lot surroundings.
- The 2D bitmap is **not** removed: it is the middle value of the same three-way row
  (`Off / Bitmap2D / World3D`, `NeighbourhoodView.cs:37-50`) and is the automatic fallback on any
  generation failure (`UINeighbourhoodSelectionPanel.cs:997-1030`,
  `NeighbourhoodBackdropRule.Decide(requested, HealthFor(_world3D))`).

**So "the connected world should, in 3d, load the connected neighbourhood landscape" is already the
shipped behaviour.** The player's request describes the current default.

---

## 2. Procedural connectors — the direct answer to the player's second question

**Already implemented, in Simitone, complete, tested.** FreeSO has no equivalent.

`NeighbourhoodMapGenerator.Generate` (`NeighbourhoodMapGenerator.cs:298-322`) is one
deterministic pass in dependency order:

```csharp
// 1. Streets. This is the stage that can fail, and it is meant to be the one.
var graph = RoadGraph.Build(input, map);
graph.VerifyConnected();

// 2. Ground, water and foliage, off labelled sub-streams of "generation" so that
//    retuning the water mask cannot move a single tree.
var fill = TerrainFill.Fill(input, graph, map, streams.Generation, fillOptions);

// 3. Height, from the lot borders and the road masks.
var seam = TerrainSeamSolver.Solve(input, graph, map, seamOptions);
```

| Facility | Where | What it gives a connected world |
| --- | --- | --- |
| `RoadGraph` | `RoadGraph.cs:543` | Street skeleton, driveways, rasterisation into `RoadData`, flood-fill connectivity with automatic repair, and `Entrances` (`:612`) — a queryable graph of lot doors, not just pixels |
| `RoadMask` | `RoadGraph.cs:78` | The 4-bit adjacency encoding, **derived from the shipped atlas** rather than assumed (`:16-76` documents reading it off `road01..15.png` alpha) |
| `TerrainFill` | `TerrainFill.cs:197` | Voronoi-ish nearest-lot terrain fill, water mask, Poisson-disk tree scatter with a hard budget |
| `TerrainSeamSolver` | `TerrainSeamSolver.cs:149` | Bounded Gauss-Seidel relaxation with each lot's border ring as a Dirichlet boundary; ramps rather than steps |
| `NeighbourhoodRng` | `NeighbourhoodMapGenerator.cs:40` | SplitMix64 with **labelled sub-streams**, so retuning one pass cannot perturb another |

The connectivity invariant the player cares about is explicit and asserted: `graph.VerifyConnected()`
(`RoadGraph.cs:1400`), `RoadComponents`/`IsSingleNetwork` (`:423`, `:1182`), and `RepairConnectivity`
(`:1194`) which re-rasterises the cheapest axis-aligned edge until there is one component.

**No dedicated functions need writing for connectors.** They exist.

---

## 3. FreeSO's city rendering — what it is and what it is worth

`FreeSO/TSOClient/tso.client/Rendering/City/**` is 20 files. It **consumes** a raster; it does
not generate one.

| File | Role | Reusable by Simitone? |
| --- | --- | --- |
| `Terrain.cs` (92 KB) | The whole city screen: cameras, sky, weather, picking, `DrawSurrounding` | **No.** Needs `FSO.Client.Controllers`, `FSO.Client.UI.Panels.*`, `FSO.Client.UI.Screens` — the whole online client. |
| `CityGeometry.cs` (48 KB) | `RegenMeshVerts` / `SubRegenMeshVerts` — turns `CityMapData` into GPU buffers | **Possibly — see §4** |
| `CityMapData.cs` | The 512×512 container + `Load`/`Save` of five PNGs | **Possibly — see §4** |
| `CityContent.cs` | **The statics**: `RoadLayout` (`:56`), `RoadCLayout` (`:57`), `FlagLayout` (`:49`), `RoadWidth`/`RoadHeight` (`:54-55`) + the content loader | Statics already copied; loader is TSO-game-data and correctly avoided |
| `TLayerVertex.cs` | The vertex layout the city shaders read | Already mirrored in `NeighbourhoodCityMeshBuilder.cs:107-138` |
| `CityFoliage.cs` | Trees, keyed off `ForestTypeData` **as a colour→species dictionary that throws on anything else** | **No** — Simitone's generated tints are not TSO's four colours. Documented at `NeighbourhoodFoliage.cs:23-30`. |

The plan's central claim (`NEIGHBOURHOOD_SCOPE_A_PLAN.md:70-76`) — that `RegenMeshVerts` needs only
`CityMapData` and the `static` `CityContent` tables, not a `CityContent` instance — **holds**. That
is why this question is worth a careful answer rather than a shrug.

### 3.1 `VMLotTerrainRestoreTools` — the per-lot surroundings machinery

`VMLotTerrainRestoreTools.cs` is 1122 lines and **is** a connected-landscape renderer: it builds up
to 8 neighbour sub-worlds in a 3×3 grid (`RestoreSurroundings`, `:1001-1112`), each with a temp
`VM`, a `Blueprint`, a `VMArchitecture`, real terrain, roads, water, and scattered objects
(`PopulateBlankTerrain`, `:978`), positioned at `(size−2)` spacing (`:1106`).

**None of it is used by Simitone today** (grep: 0 hits outside one comment at
`NeighbourhoodViewRow.cs:198`). It is TSO-shaped, as the handoff says
(`NEIGHBOURHOOD_WORLD_HANDOFF.md:399-402`): it keys on `vm.TSOState.Terrain` (a 3×3 `BlendN`/`Roads`
+ 4×4 `Height` grid, `VMTSOSurroundingTerrain.cs:9-11`) and on `vm.TSOState.LotID` as a packed grid
coordinate, and it is only ever entered from TSO's own net commands
(`VMStateSyncCmd.cs:38`, `:48`) and TSO UI (`UIOptions.cs:591`).

**Verdict: adapt, do not reuse the entry point.** The four leaf helpers are worth keeping as
reference implementations for Part B's B2 (`RestoreTerrain(vm, TerrainBlend, byte, RestoreLotType)`
at `:579`, `RestoreHeight` at `:461`, `GetBaseLevel` at `:444`, `EnsureCoreObjects` at `:728`,
`PositionLandmarkObjects` at `:751`) — they are the *only* code in the tree that knows how to turn
a blend mask + a road byte into lot terrain. But TS1 has no `vm.TSOState.Terrain`
(`NEIGHBOURHOOD_WORLD_HANDOFF.md:403-405`), so a layout-aware sibling is required, exactly as the
handoff says. Note the helpers live in `tso.simantics` (FSO.SimAntics), which Simitone.Client
**already references** — so unlike the City renderer, these are reachable today.

---

## 4. The `tso.client` reference problem — measured, not assumed

The existing docs call it a type collision on `UICustomLotControl` / `UICatalogElement`
(`Simitone.Client.csproj:72-93`, `EODHost.cs:27-34`, `ADDON_PROGRESSION_PLAN.md:825-830`). That is
correct as far as it goes, but it is **incomplete and mis-diagnosed in kind**. I reproduced it.

### 4.1 The actual collision set

Census of every public/internal type in `FSO.UI` (153) against `tso.client` (419): **8 identical
fully-qualified names**, all in one namespace:

```
FSO.Client.UI.Panels.LotControls.  UICustomLotControl   <- abstract class in FSO.UI, interface in tso.client
                                    UIFloorPainter  UIGrassPaint  UIRoofer
                                    UITerrainFlatten  UITerrainRaiser
                                    UIWallPainter  UIWallPlacer
```

Plus one *different-FQN* clash: `tso.client` adds
`FSO.Client.UI.Controls.Catalog.UICatalogElement` (a **struct**, `UICatalog.cs:461`) while Simitone
declares its own `Simitone.Client.UI.Panels.LiveSubpanels.UICatalogElement` (a **class**,
`UIBuyBrowsePanel.cs:1031`).

`Simitone.Client` names all 8 duplicates today (6× `UICustomLotControl`, 5× `UIWallPlacer`, 4× each
of `UIFloorPainter`/`UIGrassPaint`/`UIRoofer`/`UITerrainFlatten`/`UIWallPainter`, 3×
`UITerrainRaiser`).

### 4.2 Measured: 13 errors, and **CS0433, not CS0104**

I built the **entire real `Simitone.Client` source set** against the same seven FreeSO
`ProjectReference`s it has today, plus one added `ProjectReference` to `tso.client`:

```
UILotControl.cs(170,16):      error CS0433: The type 'UICustomLotControl' exists in both
                                          'FSO.Client' and 'FSO.UI'
BuyCatalogService.cs(437):    error CS0104: 'UICatalogElement' is an ambiguous reference between
BuyCatalogService.cs(473):              'Simitone.Client.UI.Panels.LiveSubpanels.UICatalogElement' and
BuyCatalogService.cs(510):              'FSO.Client.UI.Controls.Catalog.UICatalogElement'
BuyCatalogService.cs(544):
BuyCatalogService.cs(576):
BuyCatalogService.cs(726):
BuyCatalogService.cs(768):  (x2)
BuyCatalogService.cs(858):  (x2)
BuyCatalogService.cs(883):
BuyCatalogService.cs(905):
                              = 13 errors, 2 files
```

**This matters.** CS0104 is cosmetic: a `using` alias or a qualification fixes it. **CS0433 is not
cosmetic** — the two types share a fully-qualified name, so *no* `using`, *no* qualification and
*no* local alias can disambiguate them. The only fixes are an `extern alias` or deleting one of the
two declarations. The existing docs' framing ("makes every bare use ambiguous", i.e. CS0104) is
therefore **too optimistic**: it implies a rename or an alias would do it, and it would not.

Note the blast radius is small (2 files) but concentrated: `UILotControl.cs:170` needs the FSO.UI
*abstract class* because it sets `CustomControl.Modifiers` / `.MousePosition`
(`FSO.UI/.../UICustomLotControl.cs:9-10`), members the `tso.client` interface does not have
(`tso.client/.../UICustomLotControl.cs:5-12`).

### 4.3 `extern alias` — works, but is still the wrong answer

Verified: `ProjectReference ... Aliases="tso"` + `extern alias tso;` compiles, and
`tso::FSO.Client.Rendering.City.CityGeometry` resolves while the unaliased
`FSO.Client.UI.Panels.LotControls.UICustomLotControl` continues to bind to FSO.UI. **0 errors.**

So the alias route is *not* a dead end, contrary to what the csproj comment implies. But it is
still wrong here, for a reason unrelated to the collision:

- **The B-12 build split forbids it outright.** `Simitone.Windows.csproj:177-178` references
  `tso.client` only when `BuildIde != false`, and `Directory.Build.props:152` defaults `BuildIde` to
  `false`. Worse, `_SimitoneAssertNoIdeReferences` (`Simitone.Windows.csproj:862-904`) **hard-errors
  the build** if `FSO.Client` appears anywhere in the resolved `Simitone.deps.json` when
  `BuildIde == false`. A `Simitone.Client -> tso.client` reference would put it there and fail
  Release. This guard exists deliberately (B-12: it removes 19 projects and most of the 10m39s build).
- **The alias hides FSO.Client from everything.** 100 files in `Simitone.Client` `using FSO.Client.*`
  today. The census of `tso.client`-only type names actually named in Simitone.Client is ~38 sites
  (6 names; the rest are false positives from `Message`/`Terrain`/`Gender` word-matching). Small —
  but the alias also removes FSO.Client from the *implicit* resolution of every type that is only
  reachable through it, which is exactly the fragility the existing comment describes.
- **It buys almost nothing.** The whole reason to reach for `tso.client` is `CityGeometry`. §4.4
  shows you can have `CityGeometry` without the assembly at all.

### 4.4 The finding that changes the recommendation: the City leaf files are self-contained

`CityGeometry.cs`'s only `FSO.*` dependency is `FSO.Common.Utils` (one line, `GameThread`).
`CityMapData.cs`, `TLayerVertex.cs`, `Blend.cs`, `MeshVertex.cs` need nothing FSO-specific.
`CityContent.cs` needs `UIElement` and `GameFacade` — **both of which live in `FSO.UI`**, which
Simitone.Client already references (`FSO.UI/Framework/UIElement.cs`,
`FSO.UI/GameFacade.cs`). `tso.client` declares none of `GameFacade`, `GameThread`, `UIElement`,
`DynamicTuning`, `FSOMesh`.

I verified this two ways:

**(a) Link the 6 leaf sources into a project that references only FSO.UI / FSO.Common / FSO.Files /
FSO.Content** — no `tso.client`:

```
0 Error(s)
```

(the one initial failure was `CS0246: 'Stream'` at `CityContent.cs:250`, caused only by
`tso.client`'s `ImplicitUsings=enable`; supplying `<Using Include="System"/>` / `System.IO` /
`System.Linq` / `System.Collections.Generic` as project-level global usings fixes it **without
editing a single FreeSO line**.)

**(b) The same, against the entire real `Simitone.Client` source set** (all `**/*.cs` under
`Simitone.Client`, minus `bin`/`obj`, same seven FreeSO references, plus the 6 linked City files):

```
85 Warning(s)
0 Error(s)
```

### 4.5 Options, ranked

| Option | Cost | Verdict |
| --- | --- | --- |
| **1. Link the 6 City leaf sources** via `<Compile Include="…\tso.client\Rendering\City\X.cs" Link="City\X.cs" />` + 4 `<Using>` items | ~10 lines of csproj, zero source edits, **measured 0 errors** on the real source set | **RECOMMENDED if `CityGeometry` is wanted.** It is upstream's mesh algorithm, tracked, with no assembly-reference change and no build-split violation. |
| 2. Keep the existing Simitone-native `NeighbourhoodCityMeshBuilder` | zero | **RECOMMENDED if it is not.** It already reproduces everything that matters and is 388-test-covered. See below. |
| 3. `extern alias` | works, but violates the B-12 split guard | Not recommended |
| 4. Shared assembly (move City into its own project) | a FreeSO fork change, upstream divergence | Not worth it for 6 files |
| 5. Copy the tables (status quo) | done | Fine — the tables are 32 ints and already test-verified |

**Which of 1 and 2?** The honest answer: **keep option 2, and do not do option 1.** Reasons:

- `NeighbourhoodCityMeshBuilder` already reproduces the parts that make a city look like a city and
  *deliberately drops* the parts we don't want — its own header at
  `NeighbourhoodCityMeshBuilder.cs:231-240` enumerates both lists: reproduced (isometric diamond
  clip, ten-tile fade skirt, five-layer split, per-vertex normals, elevation ÷12 and ÷6, four
  repeats per tile, road + corner quads) vs omitted by decision (blend-mask pass, per-tile vertex
  tint, shadow map, near-view subdivision).
- It is **cross-checked against a second independent derivation**:
  `NeighbourhoodCityMeshProbe` (`:1205`) walks the same map analytically and the two must agree
  (`NeighbourhoodCityMeshTests.cs:241` `TheMeshBuilderAndTheProbeAgreeOnAGeneratedMap`). Upstream
  `CityGeometry` has no such second opinion, so adopting it would be a net *loss* of test coverage.
- `CityGeometry` allocates and uploads GPU buffers; Simitone's returns a pure CPU mesh so the build
  can run on a worker and upload on `GameThread.NextUpdate` (`NeighbourhoodTerrainRenderer.cs:40-44`).
  `CityGeometry.RegenMeshVerts` does its upload internally (`CityGeometry.cs:516`), which is the
  opposite of the threading contract.

Option 1 becomes worth doing only if a specific gap appears that is cheaper to fix upstream than to
fix in the copy. No such gap is known today.

---

## 5. Assets (REUSE AS-IS — all present, all already used)

| Asset | Where | Status |
| --- | --- | --- |
| Road atlas cells `road00..15.png` | `tso.content/Content/Textures/terrain/` | **Present**, ships via `FSO.Content.csproj:65-67`. In use. |
| Corner atlas `roadcorner00..15.png` | same | **Present**, in use. |
| Tree atlas `trees.png` | same (88 KB) | **Present**, in use. |
| Tree models `tree1-4`, `pine1-4`, `snow1-3`, `cactus1-3`, `palm1-4` `.obj` + `TEX_1.png` | `tso.content/Content/3D/` (20 files) | **Present**, in use by `NeighbourhoodFoliage` (`ModelFolder = "Content/3D"`) |
| `Effects/VerShader.xnb`, `Effects/PixShader.xnb` | `tso.content/Content/Effects/` | **Present**. **Already loaded by Simitone** at `SimitoneSurroundings.cs:330-331` and `Neighbourhood3DView.cs:208-209` — the city shaders are already the shaders. |
| TSO `Cities/**` | — | **Absent.** `tso.content/Content/Cities` does not exist. Correctly so: `CityContent.LoadContent:59-76` reads TSO *game data* (`cities/city_XXXX`, `gamedata/terrain/newformat/*.tga`, `userdata/houses/defaulthouse.bmp`) which Simitone must not depend on. |
| TS1 ground textures | — | Not usable as a source. Four of the five ground textures are **generated** at 64×64 by `NeighbourhoodTerrainTextures.cs` (deterministic value noise, wrap-checked, test-pure). Only `autumn.png` ships. |
| A shipped **landscape / terrain asset** | — | **Does not exist.** The Sims install under `SimsCompleteCollectionUSA/` is four `.bin`/`.cue` disc images, not extracted. `mods/Custom_Neighbourhood_Graphics/` is 2D `NScreen_*.bmp` paint-over maps, not 3D. |

**The one thing the player may be thinking of as a "landscape asset"** is `NeighModel<mode>`, and it
is not one: `UINeighbourhoodSelectionPanel.cs:1222-1226` builds that path as
`Path.Combine(userDir, "NeighModel" + mode)`, and it is the **output folder** of a debug OBJ export
gated on the env var `SIMITONE_EXPORT_NEIGHBOURHOOD_OBJ` (`:1194`, `:1239`). It is authored output,
not a shipped input, and nothing reads it back.

---

## 6. Simitone exporter / handoff work the initial author left

| Document / file | What it actually is |
| --- | --- |
| `Utils/SimitoneNeighOBJExporter.cs` (210 lines) | A **debug authoring tool**. Per lot: stand a throwaway `World`+`VM` up, `VMTS1Activator.LoadFromIff`, `LotFacadeGenerator.Generate`, `AppendOBJ`. It is not a 3D neighbourhood — it emits a per-lot `.obj` for external 3D packages. **Its value is that it is the only proven "TS1 house → facade" pipeline in the tree**, and `NeighbourhoodFacadeBuilder.cs:30-37` reuses that exact sequence deliberately. The isometric projection `baseV = (x+2y, 0, 2y−x)/scale` (`:95`) is the packer's seed arrangement (`NEIGHBOURHOOD_SCOPE_A_PLAN.md:174-176`). |
| `docs/NEIGHBOURHOOD_SCOPE_A_PLAN.md` | The plan, **fully executed** (M0–M5, waves Q/R/S in `ADDON_PROGRESSION_PLAN.md:757-864`). §12 named the fallback taken when `CityGeometry` is unreachable: "a Simitone-native mesh writer (a few hundred lines) fed by the same arrays" — that is what shipped. |
| `docs/NEIGHBOURHOOD_WORLD_HANDOFF.md` | Part B design. §2's reuse table is accurate. **Nothing in Part B has been built.** |
| `docs/ANIMATION_MERGE.md` | About `DGRP3DAnim` object animation. **Irrelevant** to the neighbourhood. |
| `docs/MODERNIZATION_PLAN.md` | General. The neighbourhood is one line: `ADDON_PROGRESSION_PLAN.md:1043` "Connected 3D neighbourhood (Scope A M1-M5) + lighting (sun/weather) — **Done (default on)**". |

**No "pre-existing 3D neighbourhood", no "NeighModel" landscape asset, no prototype.** One working
implementation, and it is the shipped one.

---

## 7. What is genuinely missing (BUILD)

Part B, per `NEIGHBOURHOOD_WORLD_HANDOFF.md`. Verified absent by name:

| Part B type | Hits in `Simitone.Client` |
| --- | --- |
| `SimitoneSurroundingsRestore` | **0** |
| `INeighbourhoodQuery` / `class NeighbourhoodQuery` | **0** |
| `SimitoneNeighbourhoodWorld` | **0** |
| `WorldClock` | **0** |
| `SimitoneGlobalLink` | **0** |
| `OffscreenSimulator` | **0** |

Present seams Part B needs, all inert as designed: `INeighbourhoodWorld` + `Tick`
(`INeighbourhoodWorld.cs:275-426`, consumed only by `NeighbourhoodPrepSeamTests`),
`INeighbourhoodClock` (`:33`), `ILotContentSource` (`ILotContentSource.cs:116`, two methods),
`INeighbourhoodSceneSource` (`INeighbourhoodSceneSource.cs:27`), and the road-graph nodes/edges/
**entrances** that are the routing substrate (`RoadGraph.cs:594`, `:603`, `:612`).

Also untouched by Simitone today: `SubWorldComponent` / `Blueprint.SubWorlds` /
`World.InitSubWorlds` (0 hits). They are in `FSO.LotView`, which **is** referenced, so they are
available the moment real neighbour lots are wanted.

---

## 8. Recommended path

> **Reuse X, adapt Y, build Z.**

### REUSE (unchanged, zero work)
1. The whole generated world: layout, sidecar, generator, `RoadGraph`, `TerrainFill`,
   `TerrainSeamSolver`, `NeighbourhoodCityMeshBuilder`, `NeighbourhoodTerrainRenderer`,
   `NeighbourhoodContent`, `NeighbourhoodFoliage`, `NeighbourhoodFacades`,
   `Neighbourhood3DView`, `SimitoneSurroundings`, lifecycle/invalidation. It is finished and
   default-on.
2. `FSO.LotView.Facade.LotFacadeGenerator` — already used, already the proven path.
3. `Effects/VerShader` + `Effects/PixShader` — already loaded.
4. Road/corner/tree atlases + tree models — already shipped and loaded.
5. The `VMLotTerrainRestoreTools` leaf helpers (`RestoreTerrain`, `RestoreHeight`, `GetBaseLevel`,
   `EnsureCoreObjects`, `PositionLandmarkObjects`) as the *reference* for Part B's B2 — reachable
   today because they are in `FSO.SimAntics`, which is already referenced.

### ADAPT (small, well-bounded)
6. **Do not add a `ProjectReference` to `tso.client`.** It is CS0433-fatal (§4.2) *and* it breaks
   the B-12 split guard (§4.3). The existing decision in `Simitone.Client.csproj:72-93` is correct;
   only its *diagnosis* needs correcting — the blocker is a same-FQN duplicate (CS0433), not a
   bare-name ambiguity (CS0104), so "add a `using` alias" is not a fix.
7. If upstream `CityGeometry` is ever genuinely wanted, **link the 6 City leaf sources** (§4.4) —
   measured 0 errors, ~10 csproj lines, no assembly reference, no build-split change. Keep this as
   a documented escape hatch rather than doing it speculatively.
8. Part B B2: a **layout-aware sibling** of `RestoreSurroundings`, reusing the helpers. Do not edit
   the TSO path.

### BUILD (only these)
9. Part B, in the handoff's order, each independently gate-able: B1 world/clock → B2 layout-aware
   surroundings over hollow lots → B3 discrete lot travel → B5 global link → B7 persistence.
   B4 (walkable streets) stays optional and is the one to cut.
10. One genuinely open gap worth naming: `NeighbourhoodWorldMapping` currently derives a lot's map
    position from `MapX/MapY`, whereas B3 wants arrival at a real entrance. `RoadGraph.Entrances`
    already carries `NeighbourhoodEntrance { lotNumber, x, y, facingX, facingY }`
    (`RoadGraph.cs:344-367`) — promoting that into the `ILotEntrance` seam Part A §15 promised is
    the first thing B3 needs.

### Explicitly not recommended
- Referencing `tso.client` (alias or otherwise).
- `SubWorldComponent` for anything except real hollow neighbour lots — the facade path already
  draws every lot in the world and looks right.
- Porting `CityFoliage` (its colour→species dictionary is TSO-shaped and throws on our tints).

---

## 9. Method / reproducibility

- **Builds run (read-only, no processes killed):** `Simitone.Client -t:Compile` → **0 errors, 179
  warnings**; `tso.client -t:Compile` → **0 errors, 729 warnings**. Nothing was edited.
- **Collision measured**, not guessed: a scratch project outside the repo referencing both
  `FSO.UI` and `tso.client` reproduces CS0433; a second with `Aliases="tso"` proves the alias
  works; then the **entire real `Simitone.Client` source set** was compiled with and without an
  added `tso.client` reference — 13 errors with, 0 without.
- **Link claim measured:** the same real source set with the 6 City leaf files linked →
  **0 errors**.
- **Type censuses** were computed over all public/internal type declarations in `FSO.UI` (153),
  `tso.client` (419) and the seven already-referenced FreeSO assemblies (1349).
- **Scratch projects** live under `%TEMP%\opencode\` and `%TEMP%\collide2\`; the repo tree was not
  modified except for this file.

### One correction this audit makes to existing docs

`Simitone.Client.csproj:76-93` and `EODHost.cs:27-34` describe the blocker as making "every bare use
of those two names ambiguous", and note that `Aliases="tso"` is "worse rather than better". The
first half is right about the outcome but wrong about the mechanism (**CS0433**, not CS0104, for
`UICustomLotControl` and the seven sibling build tools — an alias cannot fix CS0433, only delete or
`extern alias` can). The second half is wrong on its facts: **the alias does work** (measured), and
it is blocked by the B-12 split guard, not by visibility. Worth correcting so the next person does
not rule out a working option for a reason that does not hold.
