# Simitone — Full Code Refactor Plan

**Status: PLAN ONLY. Nothing in this document has been implemented. The tree is unchanged.**

Date: 2026-09-30. Line numbers, member names and file sizes below were read from the working
tree on this date. Where a number is inherited from an earlier audit it is marked
*"audit as-of its writer"* and must be re-checked in the pre-flight (section 3).

Companion documents (do not duplicate them — this plan points at them):

| Doc | Role |
| --- | --- |
| `docs/MODERNIZATION_PLAN.md` | what has already been modernized (net10, MonoGame, saves, services) |
| `docs/ADDON_PROGRESSION_PLAN.md` | the wave log; §15.3 is the last clean backlog table, §16–§18 are later waves |
| `docs/AUDIT_SIMITONE_CLIENT.md` | SIM-01..SIM-17 + S4 notes (partly stale — see §3) |
| `docs/AUDIT_CONTENT_SAVE.md` | F-01..F-28 and T-* test findings (partly stale) |
| `docs/AUDIT_BUILD_DEPS.md` | A-*/B-*/C-*/H-* build and hygiene findings (partly stale) |
| `docs/AUDIT_ENGINE.md` | E-01..E-54 — **FreeSO submodule, not Simitone code** |
| `docs/AUDIT_RENDERING_UI.md` | R-01..R-72 — **FreeSO submodule, not Simitone code** |

---

## 1. Purpose, scope and non-goals

### 1.1 What "full refactor" means here

A structural refactor of **Simitone-owned code**: make the code smaller per unit, single-sourced,
null-safe and testable, while preserving behaviour and the public surface that the tests and the
FreeSO integration rely on. It is **not** a feature pass and **not** a rewrite.

### 1.2 In scope

| Area | Files | Lines (2026-09-30) |
| --- | ---: | ---: |
| `Client/Simitone/Simitone.Client/**` | 199 | 87,313 |
| `Client/Simitone/Simitone.Windows/**` | 13 | 1,549 |
| `Client/Simitone/Simitone.Tests/**` | 250 | 91,964 |
| `Client/Simitone/Simitone.GenerateMeshes/**` | 2 | 880 |
| `Client/Simitone/Simitone.PackageRemeshes/**` | 1 | 76 |
| Root build files (`Directory.Build.targets`, `Client/Simitone/Directory.Build.props`, `.editorconfig`, `global.json`, `NuGet.config`, csproj/sln) | — | — |

### 1.3 Out of scope / needs an explicit decision

* **The `FreeSO/` subtree** (`alexjyong/FreeSO` submodule, pinned). Every E-*/R-* finding lives
  there, and the docs record repeatedly that in-tree FreeSO fixes are lost on a submodule bump.
  Workstream G is a decision, not a plan to edit it.
* **Retail TS1 data**, the `FSO.Remeshes-main/` pack, `GeneratedMeshes/`, `mods/`,
  `SimsCompleteCollectionUSA/` — data, not code.
* **New gameplay features** (build/buy undo already landed; nothing further).
* **The `.uis`/TSO art product decisions** already recorded as out of scope in
  `ADDON_PROGRESSION_PLAN.md` §12.

### 1.4 Non-negotiable invariants

1. **Behaviour-preserving.** No refactor PR changes a player-visible outcome unless it is closing a
   named correctness finding (Workstream C), and then the change and its test land together.
2. **Tests stay green and unmodified** except where a test is itself named as defective
   (`AUDIT_CONTENT_SAVE.md` T-01..T-03).
3. **Public surface stays.** Classes/methods forwarded by a split keep their names and signatures;
   new collaborators are internal unless a test needs them.
4. **One workstream per change set.** No mixed A+C+B PRs.
5. **FreeSO edits are a separate, explicitly-approved exception** (Workstream G).

---

## 2. Baseline and guardrails (Workstream R0)

### 2.1 Commands

```powershell
# build (game-only is the default; -ide is opt-in and currently the default per Wave 18)
dotnet build Client\Simitone\Simitone.sln -c Release
# tests
dotnet test  Client\Simitone\Simitone.Tests\Simitone.Tests.csproj -c Release
# compile-only fast loop used by the audits
dotnet build Client\Simitone\Simitone.Client\Simitone.Client.csproj -t:Compile -c Release
```

### 2.2 Metrics to record before R0 and after every wave

| Metric | Source of truth |
| --- | --- |
| build result / warnings count | `dotnet build … -consoleloggerparameters:Summary` |
| test count + 0 failures | `dotnet test` tail |
| files > 400 lines, > 1000 lines | the scan in Appendix A |
| `#nullable` directive count (currently **0**) | `Select-String '#nullable'` |
| `public static` mutable collections | `Select-String 'public static (readonly )?Dictionary'` |
| duplicate settings/file owners | `Select-String 'const string FileName'`, `'\.ini"'` |

### 2.3 Gates

* **G1** build green, 0 errors.
* **G2** test suite green, count not reduced except for the three cannot-fail tests being rewritten.
* **G3** warning census not increased.
* **G4** no new `public static` mutable state.
* **G5** for any extracted pure logic: a unit test exists that calls it directly.
* **G6** for any behaviour change: a test fails before and passes after (fault injection where the
  path needs a live game).

---

## 3. Pre-flight: reconcile the audits against the current tree (Workstream R0.1)

**This is mandatory and comes before any code move.** The audits were written before the fix
waves recorded in `ADDON_PROGRESSION_PLAN.md` §14–§18, and several findings listed as open in the
audit text are already fixed in the tree. Verified examples from this read:

| Finding | Audit says | Tree says (2026-09-30) |
| --- | --- | --- |
| SIM-01 `simitone.ini` two-writer erase | open | **fixed** — `Utils/SimitoneIniFile.cs` is the single read-merge-write owner |
| SIM-02 `AtomicFileCommit` copy-not-rename | open | **fixed** — `AtomicFileCommit.cs:157` uses `File.Replace` |
| SIM-03 `UIClockPanel` hand-copied speed map | open | **fixed** — `UIClockPanel.cs:64` derives from `GameplaySpeed.RemapSpeed` |
| SIM-07 `int.Parse` on lot prefix | open | **fixed** — `GameController.ResolveEntryRequest` (`GameController.cs:99`) |
| SIM-09 reserved-name chars duplicated | open | **fixed** — panel forwards `NeighbourhoodManager.ReservedNameCharsText` (`…ManagementPanel.cs:221`) |
| SIM-12 `FloorNames` duplicated | open | **fixed** — `UIDesktopUCP.cs:79` aliases `UIMainPanel.FloorNames` |
| SIM-16 stale `_open` | open | **fixed** — `Removed()` clears it (`…ManagementPanel.cs:1320`) |
| SIM-17 `ConfirmOnceGate` latch/dialog split | open | **fixed** — `_dialogShown` (`DialogService.cs:117`) |
| R-16 `InterpNormal` | open | **fixed + re-diagnosed** (Wave W corrections) |
| R-65 `WordWrap` | open | **fixed** (rewritten as `WordWrapper`) |
| **SIM-04** clock bg re-resolve | open | **PARTLY fixed** — `UIClockPanel` caches `_runningBg`/`_pausedBg`; **`UIDesktopUCP.cs:409-410` still calls `Content.Get().CustomUI.Get(...)` per transition** |

**Procedure for every remaining finding in the five audit docs:**

1. Grep the current tree for the exact symbol/method named in the finding.
2. If it is fixed: strike it in the audit doc with a one-line "fixed by <file>:<line>" note.
3. If it is open: file it under the right workstream below, with the *current* line number.
4. Produce a single reconciled backlog table `docs/REFACTOR_BACKLOG.md` (input to Waves R3/R6).

Until step 4 exists, treat §15.3 of `ADDON_PROGRESSION_PLAN.md` as the worklist skeleton, noting
that Wave X (§16) claims to have closed most of it.

---

## 4. Workstream A — God-class decomposition

### A.0 Rule and method

* **A-0.1** Target: no source file over **~600 lines** except generated/interop-shaped files;
  no class over **~400 lines**.
* **A-0.2** Mechanical type-splitting first: every file with more than one top-level type gets
  **one file per public type** (namespaces already match folders). This is the lowest-risk win.
* **A-0.3** Then method-group extraction into collaborators, keeping the original class as a thin
  facade that forwards. This is the pattern already used successfully for
  `TS1GameScreen → UI/Services/*` and `UIBuyBrowsePanel → BuyCatalogService`.
* **A-0.4** Extract **pure static logic** (no `World`, `vm`, `UIElement`, `GameFacade`) first — it
  is testable with zero runtime.
* **A-0.5** Never move the VM/world/screen *fields* off a screen-or-control class just to split it;
  the `MODERNIZATION_PLAN.md` §3 decision (services take the screen back-reference) stands.

### A.1 File-size registry (2026-09-30) — the worklist

Files > 500 lines in Simitone-owned code. `Types` = top-level types currently sharing the file.

| ID | File | Lines | Types | Primary action |
| --- | --- | ---: | --- | --- |
| A-UI-01 | `UI/Panels/UINeighbourhoodSelectionPanel.cs` | 2824 | 11 | split types + extract backdrop/camera/health/bitmap |
| A-SC-01 | `UI/Screens/TS1GameScreen.cs` | 2238 | 3 | extract remaining surroundings/travel/weather services |
| A-UI-02 | `UI/Panels/UINeighbourhoodManagementPanel.cs` | 2127 | 9 | split model/layout/alert/row out of the panel |
| A-NB-01 | `Neighbourhood/NeighbourhoodCityMeshBuilder.cs` | 2053 | 13 | split mesh / LOD / draw-call / probe types |
| A-NB-02 | `Neighbourhood/NeighbourhoodLifecycle.cs` | 2010 | 7 | split cache / job / timings / lifecycle |
| A-NB-03 | `Neighbourhood/RoadGraph.cs` | 1965 | 10 | split mask / node / edge / components / graph |
| A-SC-02 | `UI/Screens/TS1CASScreen.cs` | 1836 | 3 | extract CAS edit/persistence/rendering helpers |
| A-NB-04 | `Neighbourhood/NeighbourhoodViewSetting.cs` | 1812 | 12 | split setting / decision / health / frame report |
| A-UI-03 | `UI/Panels/UILotControl.cs` | 1781 | 2 | extract pie menu, cutaway, auto-follow panel |
| A-NB-05 | `Neighbourhood/NeighbourhoodFacadeBuilder.cs` | 1653 | 7 | split diagnostics / builder / budget |
| A-SV-01 | `UI/Services/NeighborhoodService.cs` | 1412 | 3 | extract rezone/reset/pricing helpers |
| A-NB-06 | `Neighbourhood/NeighbourhoodFacades.cs` | 1374 | 6 | split facade / surface / cache |
| A-NB-07 | `Neighbourhood/OffscreenSimulator.cs` | 1358 | 5 | split record / result / simulator |
| A-NB-08 | `Neighbourhood/SimitoneGlobalLink.cs` | 1287 | 2 | split link into capability groups |
| A-NB-09 | `Neighbourhood/SimitoneSurroundingsPlacement.cs` | 1230 | 7 | split crop/plan/selection types |
| A-UI-04 | `UI/Panels/UIObjectHolder.cs` | 1201 | 4 | extract placement/undo/selling |
| A-UT-01 | `Utils/NeighbourhoodManager.cs` | 1126 | 2 | split slot model out |
| A-UI-05 | `UI/Panels/UIPictureInPicture.cs` | 1119 | 6 | split framing/state out |
| A-NB-10 | `Neighbourhood/NeighbourhoodTransitLot.cs` | 1103 | 7 | split pathfinder out |
| A-NB-11 | `Neighbourhood/NeighbourhoodTerrainRenderer.cs` | 1083 | 3 | split buffers/shader state |
| A-UI-06 | `UI/Panels/UIMainPanel.cs` | 1012 | 2 | extract free-will toggle, undo buttons, search box |
| A-NB-12 | `Neighbourhood/NeighbourhoodFacadePlan.cs` | 995 | 5 | mostly constants/structs — split types only |
| A-UI-07 | `UI/Panels/LiveSubpanels/UIBuyBrowsePanel.cs` | 985 | 5 | extract catalog element types |
| A-NB-13 | `Neighbourhood/NeighbourhoodFoliage.cs` | 963 | 5 | split plan/chunk/tint |
| A-NB-14 | `Neighbourhood/NeighbourhoodInvalidation.cs` | 949 | 7 | split patch/schedule types |
| A-UI-08 | `UI/Panels/Options/UIOptionsSubpanelBase.cs` | 915 | 12 | split option-descriptor types |
| A-NB-15 | `Neighbourhood/NeighbourhoodLayoutStore.cs` | 891 | 1 | extract layout codec |
| A-SV-02 | `UI/Services/BuyCatalogService.cs` | 856 | 1 | split tables from builders |
| A-NB-16 | `Neighbourhood/SimitoneSurroundings.cs` | 852 | 2 | extract terrain |
| A-MS-01 | `WeatherSounds.cs` | 828 | 5 | split parser/state/static service |
| A-UI-09 | `UI/Panels/UIHouseSelectPanel.cs` | 820 | 2 | extract family list |
| A-NB-17 | `Neighbourhood/NeighbourhoodWorldCamera.cs` | 774 | 3 | split bounds type |
| A-NB-18 | `Neighbourhood/NeighbourhoodQuery.cs` | 727 | 1 | extract predicate helpers |
| A-UT-02 | `Utils/AutoFollow.cs` | 654 | 5 | split subject/offer/enums |
| A-UI-10 | `UI/Panels/Desktop/UIDesktopUCP.cs` | 624 | 1 | extract speed/floor/bg-cache helpers |
| A-MS-02 | `Neighbourhood/NeighbourhoodLayoutBuilder.cs` | 609 | 2 | split placement |
| A-UI-11 | `UI/Panels/EODs/EODLayouts.cs` | 606 | 2 | split writer out |
| A-NB-19 | `Neighbourhood/NeighbourhoodLayout.cs` | 595 | 6 | split lot/extent/entrance types |
| A-WN-01 | `Simitone.Windows/Program.cs` | 592 | 1 | extract locator/bootstrap/settings-apply |
| A-UT-03 | `Utils/RemeshPackage.cs` | 591 | 7 | split plan/alias types |
| A-NB-20 | `Neighbourhood/TerrainFill.cs` | 590 | 4 | split noise/options/report |
| A-UI-12 | `UI/Panels/LiveSubpanels/UIRelationshipSubpanel.cs` | 583 | 5 | split filters/drag/display |
| A-UI-13 | `UI/Panels/UICreditsPanel.cs` | 566 | 2 | extract scrollbar |
| A-NB-21 | `Neighbourhood/Neighbourhood3DView.cs` | 561 | 1 | extract mesh cache from view |
| A-SV-03 | `Services/UpdateChecker.cs` | 550 | 3 | split result/status |
| A-NB-22 | `Neighbourhood/SimitoneSurroundingsPlan.cs` | 541 | 2 | split neighbour struct |
| A-NB-23 | `Neighbourhood/NeighbourhoodMap.cs` | 534 | 2 | split terrain static |
| A-NB-24 | `Neighbourhood/SimitoneNeighbourTerrain.cs` | 503 | 1 | extract sampling helpers |
| A-NB-25 | `Neighbourhood/INeighbourhoodWorld.cs` | 501 | 5 | split the four implementations out |

> `UINeighbourhoodSelectionPanel.cs` and the other multi-type files are **not** single god classes;
> a large share of their length is 5–11 unrelated types. That is why A-0.2 (type split) is first and
> cheap.

### A.2 Exact extraction specs for the highest-value targets

#### A-UI-01 · `UI/Panels/UINeighbourhoodSelectionPanel.cs` (2824)

Current single class `UINeighborhoodSelectionPanel` (line 30) holds four unrelated jobs plus nine
other types. Split:

| New unit | Members to move (current line) |
| --- | --- |
| `NeighbourhoodScenePresenter` (internal) | `AttachWorld` (1129), `DetachWorld` (1296), `RefreshBackdrop` (1343), `SyncPaintedMapLayers` (1436), `ApplyDecision` (1868), `ApplyFrame` (1901), `BuildSceneSource` (1960/1963), `RefreshCachedWorld` (2030), `SceneSourceFor` (2093), `CurrentNeighbourhoodView` (2128) |
| `NeighbourhoodHealthProbe` (internal static) | `HealthFor` (1697), `SafeFacadeEligible` (1797), `ProbeGeometry` (1816), `SafeTriangles` (1843), `SafeFacades` (1849) |
| `NeighbourhoodBitmapResolver` (internal static) | `BitmapOf` (2170), `ResolveBitmapMode` (2205), `AppendTried` (2241), `HasBackground` (2249), `ViewIsInstalled` (2261) |
| `NeighbourhoodLotInteraction` (internal) | `OnBackdropMouseDown` (1479), `TryLotUnderMouse` (1533), `TryWorldTileUnder` (1599), `StepToAdjacentLot` (564), `RequestAddLot` (581), `OpenHouseSelectPanel` (2534) |
| `NeighbourhoodRefreshGate` (internal) | `RefreshInFlight` (1225), `JoinRefresh` (1247), `IsRefreshing` (1270) |
| one file each | `NeighCameraMode` (2630), `NeighCameraTarget` (2648), `NeighCameraMachine` (2694), `UINeighborhoodAnimationLayer` (2784), `NeighborhoodViewConfig` (2836), `NeighborhoodImageAnim` (2847), `UINeighborhoodHouseButton` (2863), `NeighbourhoodWorldProxy` (2973), `UINeighborhoodSelectionOptions` (43) |
| stays | `Update`, `Draw`, `Removed`, `GameResized`, `SelectHouse`, `PopulateScreen`, `FinishPopulate`, `ResetZoom`, `UpdatePosition`, filter methods, test seams (`NeighbourhoodPanelModel`-style statics) |

#### A-UI-02 · `UI/Panels/UINeighbourhoodManagementPanel.cs` (2127)

Types already separate the model: `NeighbourhoodPanelModel` (165) + `NeighbourhoodPanelLayout` (919)
are already the extraction pattern. Remaining work is moving them into their own files and pulling
the panel's IO out:

| New unit | Members to move |
| --- | --- |
| `NeighbourhoodPanelModel.cs` | struct `NeighbourhoodOpenFacts` (42), `NeighbourhoodAction` (455), `NeighbourhoodActionButton` (477), static class (165–917) |
| `NeighbourhoodPanelLayout.cs` | static class (919–1112), struct `Vertical` (922) |
| `NeighbourhoodSlotRow.cs` | nested `NeighbourhoodSlotRow` (2304) |
| `RenameNeighbourhoodAlert.cs` | nested `RenameNeighbourhoodAlert` (2228) |
| `NeighbourhoodPanelIo` (internal) | `ReadDirtyAvatarCount` (1644), `ReadLoadedSlotId` (1598), `ExistsPerSlot` (2161), `SaveOpenNeighbourhood` (2055), `PutBackLoadedSlot` (2095), `ApplyOpen` (1955) |
| `NeighbourhoodPanelActions` (internal) | `CreateBlank` (1756), `CopyFromInstall` (1783), `ApplyRename` (1826), `ResetName` (1845), `ApplyDelete` (2144) |
| stays | the `UINeighbourhoodManagementPanel` view: `Show`, `Removed`, `MakeRow`, `BuildActionColumn`, `RefreshList`, `RefreshButtons`, layout methods |

#### A-SC-01 · `UI/Screens/TS1GameScreen.cs` (2238)

The earlier pass moved save/dialog/buy/lot-lifecycle to `UI/Services`. Remaining groups:

| New unit | Members to move (current line) |
| --- | --- |
| `SurroundingsCoordinator` (internal, takes screen ref) | `RequestSurroundingsWorld` (382), `SurroundingsSelection` (403), `AttachSurroundings` (441), `AttachSurroundingsNow` (503), `AttachSurroundingsWhenReady` (560), `DetachSurroundings` (595), `SurroundingsSlotFor` (612), `UpdateSurroundings` (628) |
| `NeighbourhoodWorldBridge` (internal) | `UpdateNeighbourhoodWorld` (718), `TryGetNeighbourhoodWorld` (762), `RecordNeighbourhoodTransit` (831), `MirrorTransitToProvider` (896), `AttachHollowNeighbours` (959) |
| `LotEntryService` (internal) | the four `InitializeLot` overloads (1873, 1987, 2007, 2064, 2106), `TryRestoreTravelSlot` (2262), `ShowLoadErrors` (2013) |
| `WeatherRuntime` (internal) | `UpdateWeatherEffects` (1688), `ApplyWeatherAction` (1719), `PersistWeather` (1761) |
| `LotTravelHost` | already a nested `private sealed class` (1057) — move to its own file |
| stays | `vm`, `World`, `Driver`, `LotControl`, `Frontend`, `SavedLots`, `SwitchLot`, `Downtown`, `ActiveFamily` (per the standing decision) and the public facade methods |

#### A-NB-01 · `Neighbourhood/NeighbourhoodCityMeshBuilder.cs` (2053)

13 types. Split `NeighbourhoodCityMeshReport` (305), `NeighbourhoodDrawCall` (576),
`NeighbourhoodMeshLayer` (632), `NeighbourhoodCityMesh` (657), `NeighbourhoodMeshLod` (760),
`NeighbourhoodTerrainVertex` (129), `NeighbourhoodRoadLayout` (33) and the whole
`NeighbourhoodCityMeshProbe` half (2038–end) into their own files. Then extract the vertex
emitters (`AddQuad` 1864, `AddRoadQuad` 1913, `Road` 1936, `AddRoadQuadsForTile` 1656,
`AddChunkRoads` 1707, `AddCoarseChunkGround` 1764, `CoarseCornerAt` 1822) into
`NeighbourhoodCityMeshGeometry` (internal static).

#### A-NB-02 · `Neighbourhood/NeighbourhoodLifecycle.cs` (2010)

Types: `NeighbourhoodGeneratedWorld` (32), `NeighbourhoodCacheCounters` (218),
`NeighbourhoodWorldCache` (308), `NeighbourhoodRefreshJob` (533), `NeighbourhoodGenerationPhase`
(859), `NeighbourhoodGenerationTimings` (926), `NeighbourhoodLifecycle` (1210).
Move each to its own file; keep `NeighbourhoodLifecycle` as the only public entry point. Extract
`NeighbourhoodWorldCache`'s counters into `NeighbourhoodCacheCounters` (already a type — just make
the cache not own the statics).

#### A-NB-03 · `Neighbourhood/RoadGraph.cs` (1965)

Types: `RoadMask` (79), `RoadEdgeKind` (210), `RoadNode` (241), `RoadEdge` (288),
`NeighbourhoodEntrance` (365), `RoadRepair` (431), `RoadComponents` (476), `RoadNetworkStats`
(553), `RoadGraph` (753), `StreetBand` (960). One file each. `RoadGraph` is then the routing
engine only; move `Rasterise`/`WriteMasks`/`WriteSidewalk`/`Mark`/`IsRoad*` into
`RoadRasteriser` (internal), and `BuildStreets`/`MeasureBands`/`Grow*`/`FreeRun*`/`BandFree*`
into `RoadStreetBuilder` (internal).

#### A-SC-02 · `UI/Screens/TS1CASScreen.cs` (1836)

| New unit | Members to move (current line) |
| --- | --- |
| `CasEditPersistence` (internal) | `LoadFamilyForEdit` (840), `SaveFamilyInPlace` (1122), `LoadPendingEditFamily` (758), `BuildMemberForEdit` (914), `GetCharacterIffPath` (1237), `BackupCharacterIff` (1266), `RemoveSupersededCharacterIff` (1311), `NormalisePath` (1341) |
| `CasSimReader` (internal static) | `BodyString` (996), `ReadSimName` (1008), `ReadSimBio` (1024), `StripUserPrefix` (1041), `ResolveSkinColor` (1054), `ApplyGenderAndAge` (1070), `FindNeighbour` (1092) |
| `CasFamilyBuilder` (internal) | `CASToNeighGen` (1744), `BuildMember` (1840), `BuildMemberForEdit` (914), `ApplyTemplatePersonData` (1352), `PersonalityPoint` (1365), `SetPersonData` (1371) |
| `CasFamilyArt` (internal) | `MissingFallback` (232), `PopulateSimType` (237), `FindHG` (280), `RemoveExt` (287), `ExtractID` (295), `PopulateReal` (456), `PopulateSimType` |
| stays | `Update`, `Draw`, `PreDraw`, `GameResized`, `Accept`, `GoBack`, `SetMode`, `SetFamilies`, `ClearFamily`, `SaveFamily`, `AcceptMember`, the carousel |
| move to own file | `CASFamilyMember` (1990), `UICASMode` (2024) |

#### A-UI-03 · `UI/Panels/UILotControl.cs` (1781)

Extract: pie menu (`ShowPieMenu` 649, `ClosePie` 780, `ShowErrorTooltip` 766), auto-follow
(`UpdateAutoFollowTarget` 935, `DescribeAutoFollowSubject` 977, `OnAutoFollowRequested` 1017,
`RunAutoFollow` 1064, `AnswerAutoFollowThread` 1130), cutaway (`UpdateCutaway` 1596,
`RefreshCut` 1147), and move nested `UIAutoFollowPanel` (1711) to its own file. `UILotControl`
keeps `Update`, `Draw`, `LiveModeUpdate`, `OnMouse`, `SimulateMD/MU`, `GetBounds`, `Removed`,
`GameResized`.

#### A-UI-04 · `UI/Panels/UIObjectHolder.cs` (1201)

Extract: `ObjectPlacement` (`SetSelected` 189, `MoveSelected` 234, `ClearSelected` 383,
`RecursiveDelete` 415, `RotateObject` 1254, `DescribePlacementSubject` 351/364),
`ObjectUndoRedo` (`RecordSale` 586, `RecordPutDown` 644, `TickUndo` 714, `UndoLast` 818,
`RedoLast` 869, `SendMove` 927, `SendBuy` 941, `SendDelete` 953, `FindPlacedObject` 761,
`FindBaseObjectAt` 777, `FindObjectAt` 921, `HighestObjectId` 996), and `ObjectPricing`
(`WearOf` 976, `DiscountFor` 987). Move `PendingPlacement`, `ArchitectureType`,
`UIObjectSelection` to their own files.

#### A-UI-05 · `UI/Panels/UIPictureInPicture.cs` (1119)

Types: `PipCorner`, `PipFeed`, `PipLotSource`, `UIPictureInPicture` (112), `PictureInPictureFraming`
(static, 989–1200), `PictureInPictureState` (1228). One file each. `PictureInPictureFraming` is
already pure and testable — keep it a standalone static class.

#### A-NB-04 · `Neighbourhood/NeighbourhoodViewSetting.cs` (1812)

Types: `NeighbourhoodView` (39), `NeighbourhoodViewSetting` (70), `NeighbourhoodViewSelection`
(328), `NeighbourhoodBackdrop` (440), `NeighbourhoodWorldFault` (475), `NeighbourhoodWorldHealth`
(607), `NeighbourhoodViewDecision` (955), `NeighbourhoodBackdropRule` (1119),
`NeighbourhoodBackdropAction` (1465), `NeighbourhoodBackdropFrame` (1494),
`INeighbourhoodRenderSignal` (1616), `NeighbourhoodFrameReport` (1691). One file per type; the
whole file is exactly the "several unrelated value types + one decision engine" shape A-0.2
targets.

### A.3 Sequencing inside A

1. **A-0.2 type splits** for the multi-type files in A.1 (mechanical, compiler-verified, no logic change).
2. **A-0.4 pure-logic extractions** (`PictureInPictureFraming`, `NeighbourhoodBackdropRule`,
   `RoadMask`, `NeighbourhoodFacadeDiagnostics`, `CasSimReader`, `ObjectPricing`).
3. **A-0.3 collaborator extractions** for the panels/screens/services (`NeighbourhoodScenePresenter`,
   `SurroundingsCoordinator`, `LotEntryService`, `CasEditPersistence`).
4. Re-measure and update the registry.

---

## 5. Workstream B — Duplication and single-source-of-truth

Most of the audit's duplication findings are already fixed (§3). What remains, verified now:

| ID | Exact point | Problem | Single-source target |
| --- | --- | --- | --- |
| B-01 | `UI/Panels/Desktop/UIDesktopUCP.cs:86-104` **and** `UI/Panels/UIClockPanel.cs:54-69` | each independently derives `RemapSpeed`/`ReverseRemap` from `GameplaySpeed.RemapSpeed` minus `Disabled` | put the derived pair on `GameplaySpeed` (or a `SpeedButtons` holder) once; both forward |
| B-02 | `UI/Panels/UIClockPanel.cs:89-97` vs `UIDesktopUCP.cs:409-410` | clock-background swap implemented twice; only `UIClockPanel` caches the textures | extract `ClockBackground` (caches both `Texture2D`s in a ctor) and use it in both (this is **SIM-04**, still open in `UIDesktopUCP`) |
| B-03 | `UI/Panels/Options/SoundVolumeSetting.cs`, `SunCycleSetting.cs`, `NeighbourhoodViewSetting.cs` | three settings classes, three ad-hoc save call sites | all three already share `Utils/SimitoneIniFile`; standardise one `ISettingSidecar` shape and one save policy (B-01 pattern), and make each `Save` log a `SimitoneLog.Warn` on failure (the audited swallow) |
| B-04 | `Utils/SimitoneStrings.Substitute` (`SimitoneStrings.cs:281`) | reimplements FreeSO `ContentStrings.GetString` substitution with no pinning test | add a differential test that runs the same inputs through both |
| B-05 | `Simitone.Windows/Program.cs` first-run defaults vs `Utils/GraphicsOptions.Defaults` (`GraphicsOptions.cs:393`) vs `Utils/StartupSettings` | the "untouched install" set is stated in three places (SIM-13) | make `Program.cs` apply defaults **through** `GraphicsOptions`; leave only resolution clamps in `StartupSettings`; add an agreement test |
| B-06 | `Utils/LotZoning.TryParse` (`LotZoning.cs:173`) and `LotZoning.Format` use `CurrentCulture` | culture-coupled pair (SIM-11); the provider uses `InvariantCulture` | make both invariant (or add the guard test the doc asks for) |
| B-07 | `UI/Panels/UINeighbourhoodManagementPanel.Confirm` (`…ManagementPanel.cs:2192`) | second hand-rolled confirm idiom beside `DialogService.ShowConfirm` | route through `DialogService` with a width/buttons parameter |
| B-08 | `public static` mutable tables (`UICatalogItem.IconCache` is now bounded + clearable; check `UIIconCache`, `BuyCatalogService`, `UIBuyBrowsePanel`) | any remaining `public static` non-readonly collection | re-run the metric scan in §2.2 and convert the stragglers to `static readonly` |

---

## 6. Workstream C — correctness / data-loss tail (audit close-out)

**The five audit docs are the worklist.** This workstream does not re-invent findings; it schedules
their reconciliation (§3) and fixes. Exact points are the file:method named in each finding, with
line numbers refreshed by the pre-flight.

### C.1 Verified-still-open examples (from this read)

| Finding | Exact current point | Fix shape |
| --- | --- | --- |
| SIM-04 (remainder) | `UI/Panels/Desktop/UIDesktopUCP.cs:409-410` | resolve `clockinbg.png` / `clockinbg_pause.png` once; swap the reference |
| F-* / E-* / R-* tail | see `docs/AUDIT_CONTENT_SAVE.md`, `AUDIT_ENGINE.md`, `AUDIT_RENDERING_UI.md` | per reconciled backlog |
| B-* tail | `docs/AUDIT_BUILD_DEPS.md` | see Workstream F |

### C.2 Priority order (from the audits' own suggestions, still valid)

1. **Persistence durability** — anything on the save path (`AtomicFileCommit`, `TravelSave`,
   `LotPersistence`, `NeighbourhoodManager`, `SaveService`): every write through `.tmp → .bak →
   File.Replace`, every load tolerant of damaged input.
2. **Parser bounds** — `Utils`-adjacent FreeSO parsers callable from Simitone
   (`VMMarshal`, `IffFile`, `IoBuffer`, `PIFF`, `DBPF`) — bounds checks + parser tests (T-11).
3. **Crash guards on paths script data can reach** — the E-* tail that survived the fix waves.
4. **The three cannot-fail tests** (`AUDIT_CONTENT_SAVE.md` T-01..T-03) — rewrite or delete.
5. **Everything lower severity** — the S3/S4/hygiene tail.

### C.3 Product-decision items (cannot be "refactored" without a decision)

| Item | Doc | Decision needed |
| --- | --- | --- |
| `ObjectComponent` dynamic→static transition | `AUDIT_RENDERING_UI.md` | keep dynamic or add a transition API (FreeSO-side) |
| `GetLotThumb` returning a shared/copied RT | `AUDIT_RENDERING_UI.md` | ownership contract (FreeSO-side) |
| capture aliasing lifetime | `AUDIT_ENGINE.md` E-22/E-39 | freeze-vs-document (FreeSO-side) |

---

## 7. Workstream D — nullability and warnings-as-errors

### D.1 Current state

* `#nullable` directive count in the whole Simitone tree: **0**.
* `.editorconfig` exists and is deliberately warning-focused, with an explicit "no formatting rules"
  policy because ~85% of the tree is the FreeSO submodule (do not add `dotnet format` rules).

### D.2 Plan (incremental, never a big-bang)

| ID | Step | Exact points |
| --- | --- | --- |
| D-01 | Enable nullable **per-file** starting with `Utils/**` pure logic | add `#nullable enable` to `AtomicFileCommit.cs`, `LotPersistence.cs`, `TravelSave.cs`, `SimitoneIniFile.cs`, `NeighbourhoodManager.cs`, `LotZoning.cs`, `BudgetService.cs`, `CasValidation.cs`, `CatalogQueryService.cs`, `GameInstallLadder.cs` first |
| D-02 | Then `UI/Services/**` (they already have null guards to make explicit) | `SaveService`, `DialogService`, `NeighborhoodService`, `BuyCatalogService`, `LotLifecycleService` |
| D-03 | Then `Neighbourhood/**` value types (structs/enums) | the A-NB splits make this tractable per file |
| D-04 | Last: `UI/Panels/**` and `UI/Screens/**` where `UIElement` children are assigned in `Init` | needs `= null!`/`?` judgement per field |
| D-05 | Promote the first warning categories to errors in `.editorconfig` **for Simitone paths only** | `CS8073` (already found, B-09), `CS0108`/`CS0114` (B-06), obsolete-API (`B-08`), `CA1416` (`B-07`) |
| D-06 | Keep `NU1701` suppression scoped and documented | `AUDIT_BUILD_DEPS.md` B-02 |

**Rule:** a nullable pass never changes behaviour. Where the compiler proves a possible null on a
path the audits already flagged, the fix belongs to Workstream C, not here.

---

## 8. Workstream E — architecture seams and testability

### E.1 Static coupling that blocks tests

| Coupling | Where | Exact seam to introduce |
| --- | --- | --- |
| `GameFacade.GraphicsDevice` / `Content.Get()` static | `UIClockPanel.cs:89`, `UIDesktopUCP.cs:409`, most `UI/Panels/**` | `ITextureSource` (or reuse the existing `Content` abstraction) injected; the `ClockBackground` extraction in B-02 is the proving ground |
| `FSOEnvironment.UserDir` read directly | `NeighbourhoodManager`, `NeighbourhoodLayoutStore`, `TS1UserData`, `SimitoneStrings` | `IUserDataPaths` with a test double |
| `DateTime.Now` / `Random` | `NeighbourhoodLayoutStore`, `OffscreenSimulator`, `WeatherSounds` | `IClock` / seeded `NeighbourhoodRng` (already present — route the rest through it) |
| screen back-reference couplings | `UI/Services/*` constructors | already the agreed pattern; formalise as an `ILotHost` interface so services are unit-testable with a fake |

### E.2 Test-architecture goals

* **E-01** Extract every remaining pure decision engine to a static/pure class and pin it with
  property tests (the `WordWrapper` "no text lost" test is the model).
* **E-02** One `[Collection]` for anything that mutates a global (per the existing
  `GlobalMutationGuardTests` sweep) — do not re-enable parallelisation until §2.2 shows zero
  cross-test statics.
* **E-03** Every Workstream A extraction ships a test that calls the new collaborator directly
  (G5). No "coverage" PRs with no assertion.

---

## 9. Workstream F — build, dependency and repository hygiene

From `AUDIT_BUILD_DEPS.md` and the tree, still-open / worth scheduling:

| ID | Exact point | Action |
| --- | --- | --- |
| F-01 | `C:\Simitone\LightingPassOwnershipTests.cs` — **0-byte stray file at repo root**, referenced by no project | delete (or move into `Simitone.Tests` if it was meant to hold the named test) |
| F-02 | root `.DS_Store`, `Simitone.lnk` | delete + `.gitignore` (H-02) |
| F-03 | `Directory.Build.targets` (25 KB) at root vs `Client/Simitone/Directory.Build.props` | document the split of responsibility; move MSBuild logic to the narrowest scope that works |
| F-04 | `B-12` game build vs `-ide` closure | settle the default (`BuildIde=true` as of Wave 18) and encode it in one place |
| F-05 | `H-06` NuGet.config | `NuGet.config` now exists — verify it pins sources and `NuGetGlobalPackages` is removed from csproj (`C-10`) |
| F-06 | `C-07` / `C-09` / `C-11` | de-hardcode `generate-meshes.ps1` paths, fix `<EmbeddedResource>`+`CopyToOutputDirectory`, prune Xamarin-era `.sln` configurations |
| F-07 | `A-03`/`A-05`/`A-07`/`A-10` | upstream package upgrades (Roslyn 3.4-beta, Portable.JWT/BouncyCastle, SqlClient/SQLite, AWSSDK/Octokit) — **file as FreeSO issues; do not fork-patch** |
| F-08 | `B-11` dead `FSO.Server.Api.Core` defects | they are compiled then trimmed — decide whether they are worth any attention; if not, record as accepted |
| F-09 | `H-08` `.editorconfig` warning gate in CI | wire the existing error set into the CI warning budget |

---

## 10. Workstream G — the FreeSO boundary (decision required)

Every E-*/R-* finding and the residual `FSO.Client`-pulls-the-server-stack bloat live in the pinned
submodule. Three options; **pick one before Workstream C schedules any E-/R- item**:

| Option | Consequence |
| --- | --- |
| **G-0 Keep patching in-tree** (status quo) | every submodule bump re-applies or loses the fixes; the docs already list many such re-applied patches |
| **G-1 Upstream everything** | fixes must be accepted by `alexjyong/FreeSO`; Simitone waits; no fork divergence |
| **G-2 Fork FreeSO** | Simitone owns its engine; submodule updates become merges; the "lost on update" class disappears, at the cost of maintaining the fork |

**Recommendation:** keep G-0 for the remainder of the refactor (no engine edits during a structural
pass), and record G-2 as the long-term answer once the Simitone-side refactor lands. Do **not**
start FreeSO refactors ("VMTS1Activator deletion", shader changes) inside this plan without an
explicit G decision.

---

## 11. Sequencing (waves)

Dependencies: R0/R0.1 → (A, D pure-logic) → (B, C) → (D broad, E, F) → G.

| Wave | Contents | Depends on | Exit criteria |
| --- | --- | --- | --- |
| **R0** | baseline metrics; G1–G6 gates wired | — | metrics recorded |
| **R0.1** | reconcile all five audits into `docs/REFACTOR_BACKLOG.md` | R0 | every finding marked fixed/open with current line numbers |
| **R1** | A-0.2 mechanical type splits (all multi-type files in A.1) | R0.1 | build green, zero logic diff, file-size metric drops |
| **R2** | A-0.4 pure-logic extractions + tests | R1 | G5 met per extraction |
| **R3** | C-1/C-2 (persistence + parser bounds) from the reconciled backlog | R0.1 | fault-injection tests green |
| **R4** | A-0.3 collaborator extractions (panels/screens/services) | R1, R2 | public surface unchanged; tests green |
| **R5** | B duplication / SSoT | R1 | metric scan clean |
| **R6** | C-3/C-4/C-5 tail | R3 | backlog empty or items recorded as accepted |
| **R7** | D nullability (D-01→D-04) + warning promotions (D-05) | R4, R5 | `#nullable` count rises, 0 new warnings |
| **R8** | E seams, F hygiene, CI gate | R4 | F-01..F-09 closed or accepted |

FreeSO E-/R- items are **not** in these waves until G is decided (G-0 default ⇒ they stay in the
audit docs as FreeSO backlog).

---

## 12. Acceptance criteria (definition of done)

1. G1–G6 hold after every wave; suite count does not drop (except the three rewritten tests).
2. No Simitone source file > 600 lines except files justified in Appendix A (if any remain, list
   them with a reason).
3. Zero top-level type-splitting violations: one public type per file under `Simitone.Client`.
4. `#nullable enable` on all `Utils/**` and `UI/Services/**`; zero new nullability suppressions
   without a comment.
5. The reconciled backlog (`docs/REFACTOR_BACKLOG.md`) contains no untriaged item; each is fixed,
   deferred with a reason, or marked FreeSO/blocked.
6. Metrics in §2.2 move only in the intended direction.
7. No player-visible behaviour change outside items explicitly listed in Workstream C.

---

## Appendix A — file-size scan command (reproduce the registry)

```powershell
$root = 'Client\Simitone'
Get-ChildItem $root -Recurse -File -Filter *.cs |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
  ForEach-Object {
    [PSCustomObject]@{
      Lines = (Get-Content $_.FullName | Measure-Object -Line).Lines
      Path  = $_.FullName.Replace((Get-Location).Path + '\','')
    }
  } | Sort-Object Lines -Descending | Select-Object -First 60
```

## Appendix B — member inventories for the top targets

The per-method line numbers quoted throughout §4 are the extraction contract; regenerate them with:

```powershell
Select-String -Path '<file>' -Pattern '^\s*(public|private|protected|internal)\s+(static\s+)?(override\s+|virtual\s+|sealed\s+)?[A-Za-z_<>\[\]\.\?]+\s+[A-Za-z_][A-Za-z0-9_]*\s*\('
```

`UINeighbourhoodSelectionPanel.cs`, `UINeighbourhoodManagementPanel.cs`, `TS1GameScreen.cs`,
`TS1CASScreen.cs`, `UILotControl.cs`, `UIObjectHolder.cs`, `UIPictureInPicture.cs`,
`NeighbourhoodCityMeshBuilder.cs`, `NeighbourhoodLifecycle.cs`, `RoadGraph.cs`,
`NeighbourhoodViewSetting.cs`, `NeighbourhoodFacadeBuilder.cs`, `NeighborhoodService.cs`,
`SimitoneGlobalLink.cs` and `OffscreenSimulator.cs` were inventoried this way for this plan.

---

## Appendix C — what this plan deliberately does not do

* It does not touch FreeSO (Workstream G decision first).
* It does not propose a rewrite, an architecture swap (MonoGame stays), a framework bump, or a
  formatting sweep (the `.editorconfig` explicitly forbids the latter).
* It does not re-list the audit findings as if they were new; it schedules their **reconciliation**
  and their fix, because the audits are partly stale after the fix waves.
* It does not implement anything. No file outside this plan was created or modified.
