# Fork ingestion assessment — `alexjyong/Simitone`

Status: **analysis + recommendation for review.** Read 2026-09-26 against
`alexjyong/Simitone` (default branch **`simitone-forked`**), which the issue #8 comments point
to as the fork where development continued.

## 1. Shape of the fork

| | |
|---|---|
| Fork default branch | `simitone-forked` (NOT `master`; `master` is identical to upstream) |
| Position vs upstream | **51 commits ahead, 0 behind** of `riperiperi/Simitone@master` (merge-base `4906db3156`) |
| Detached branches | `fame-implementation`, `custom-skin-fix`, `catalog-name-search-functionality`, `evict-fix`, `iff_analyzer`, `unix-support`, `fix-mac-sdl2`, `JDrocks450-addition` |
| Base | The fork descends cleanly from upstream master, so its commits apply to our tree's base — the work is portable, not a rewrite. |
| Recurring gotcha | ~a dozen commits also move the **`FreeSO` submodule pointer** to the author's FreeSO fork. Per decision 1 we ingest only the Simitone-side files and skip the pointer. Commits whose *only* Simitone change is the pointer (censor blur, fresh lots) are therefore **FreeSO-side** and deferred. |

The 51 commits are mostly README/version/CI noise; ~15 are substantive. Several are the open
PRs we have already ported or deferred (eyedropper #59, delete-family #64, free-will #63,
camera #61/#62, return-to-hood #57, CAS captions #56, animation/DPI #65, steam #58, weather
#67, fresh-lots #60, and the JDrocks450 Options menu #68). This assessment is about the
**remainder**.

## 2. Recommended to ingest (Simitone-side, not yet in our tree)

Ordered by value.

### 2.1 `evict-fix` branch — 25 commits (highest value)
**Roadmap link:** "Lot Bulldoze (and move out)". All commits are in `TS1GameScreen.cs`
(our `EvictLot` now lives in `NeighborhoodService`).
Key commits and what they do:
- `e366061337` "evict wouldn't actually remove the family from the house" (+125)
- `c7548e8f30` "save the mailbox/trashcan" (+19)
- `2344ca946a` "don't remove controllers on evict" (+7/-2)
- `c9753af649` "price fix" (+4/-1)
- `36b9ee1628` "more evict fixes" (+7/-11), `22ee8ffaf0` thread-loading fix (+13/-2)
- plus `19f0c5eb75` image fix, `19f0c5eb75`/`77a1e33916`/`dbd3973e47` stashing/WIP noise.
**Action:** port the final state of the branch's `TS1GameScreen` eviction logic into
`NeighborhoodService.EvictLot` (and `LotAccounting` where it overlaps our refund work).
This directly completes the roadmap's move-out/bulldoze and supersedes some of our own
eviction handling. **No FreeSO dependency.**

### 2.2 `#27` catalog search box — `abd1785f92`
"Adding support for a search filter in Build/Buy catalog. Fixed an issue where floor tile
placement was crashing the game."
- `UIBuyBrowsePanel.cs` (+97/-1), `UIMainPanel.cs` (+96/-1), `Catalog/UICatalogItem.cs` (+11/-2)
- plus a `FreeSO` pointer bump (the floor-placement crash fix is FreeSO-side).
**Action:** port the Simitone search UI onto our `BuyCatalogService` refactor. Adopt the
Simitone half only; the floor crash is deferred with the other FreeSO items.

### 2.3 `fame-implementation` branch — `5decaaec5b` (unmerged, 1 commit)
"adding in support for fame career track" — only `UI/Panels/LiveSubpanels/UIRelationshipSubpanel.cs`
(+12/-1). Small, but it is the roadmap's StudioTown "Fame career track" surface.
**Action:** port; confirm against FreeSO's existing Fame support (FreeSO has ~8 Fame hits).

### 2.4 `custom-skin-fix` branch — 6 commits (unmerged)
Custom-content skin loading + search:
- `GameController.cs` (better logging, more intelligent loading with search)
- `UIBuyBrowsePanel.cs` (+14/-13, better loading)
- `b8ba6e6134` "updating freso" (pointer only)
**Action:** port the Simitone-side loading/search; the FreeSO pointer is skipped. Maps to the
roadmap's "Skin CC".

### 2.5 `#16` update checker — `a998dc6592`
Adds `Services/UpdateChecker.cs` (+176) and `SimitoneVersion.cs` (+25), plus the camera /
right-click-select changes **we already ported** in A1/B2.
**Action:** port the update checker + version constant (self-contained). Optional, but it is
the roadmap's "official release" plumbing and cheap.

### 2.6 Steam saves gap — `cba3904b8f`
`Simitone.Windows/Program.cs` (+44/-4): "handling a potential gap where saved files for the
Steam version weren't properly generated first."
**Action:** port. This is the same class of first-run bootstrap bug as the deferred
`TemplateUserData` fix, on the Simitone side.

## 3. Already ingested / superseded (no action)

eyedropper (`c2853a567f` → our A2a), delete-family (`a0baaeaf86` → Phase 1 + verified in A1c),
free-will + camera (`3f3a614aa4` → #63 deferred / B2), animation+DPI (`6ab6784e12` → #65
deferred), weather (`6e7e126838` → #67 deferred), return-to-hood (`81ced68b2c` → A1), CAS
captions (`cdffe1f334`, `94df…` → A1), Options/JDrocks450 (`JDrocks450-addition` → B1/B2),
`catalog-name-search-functionality` branch is superseded by #27, `alex-main` is empty.

## 4. Skip (out of scope or FreeSO-only)

- `unix-support` (#4), `fix-mac-sdl2`, `#6` launcher/desktop pack, `#7` install-selector-linux —
  **non-Windows**; Simitone is Windows-only by decision.
- `#11` censor blur — Simitone diff is only the `FreeSO` pointer; the feature is FreeSO-side.
- `#14` fresh lots — `FreeSO` pointer only (already analysed in Wave A2; the controller-spawn
  and `TemplateUserData` fixes stay in the deferred FreeSO pass).
- `#28` slimming FreeSO assets — build scripts/CI; our `TrimSingleplayerOutput` already trims
  the output, so compare-and-reuse only if it covers something ours does not.
- `iff_analyzer`, devcontainer, website, README/version bumps — tooling/docs.

## 5. Proposed ingestion order (appended to the wave plan)

| Wave | Content | FreeSO dep? |
|---|---|---|
| **C1** | `evict-fix` → `NeighborhoodService.EvictLot`; Steam saves gap | no |
| **C2** | `#27` catalog search → `UIBuyBrowsePanel`/`UIMainPanel`/`UICatalogItem` | Simitone half only |
| **C3** | `fame-implementation`; `custom-skin-fix` | Simitone half only |
| **C4** | `#16` update checker + version | no |
| **Deferred (D)** | the FreeSO pass: #60 fresh-lot controllers + `TemplateUserData`, then #63/#65/#58/#67/#11/#14 | yes (forked FreeSO) |

Each wave: parallel subagents over disjoint files, then build + tests (currently 938) + runtime
smoke + plan update.

## 6. Notes

- The fork's branches and commits are the author's own continuation of the same upstream line,
  so this is a normal port, not a rewrite.
- The local `Simitone.GenerateMeshes` mesh job running in this workspace is a **separate**
  parallel effort and is unrelated to this fork.
- If we later decide to take the forked FreeSO (decision 1's option A), the pointer commits in
  #11/#14/#16/#27/#5/#63/#65/#67 become directly relevant; until then they stay skipped.

---

## 7. Ingestion progress — 2026-09-26

All six recommended items are **ingested** (C1/C2 waves), each verified with build + tests +
runtime smoke:

| Item | Result |
|---|---|
| `evict-fix` | Ported into `NeighborhoodService.EvictLot` + a new pure `LotAccounting.LotEviction`. It is effectively **lot Bulldoze**: resets `House{NN}.iff` (removes avatars/furniture, keeps build fabric + mailbox/trashcan/phone, keeps controllers but wipes their data, restamps globals/SIMI, rebuilds FSOV/SIMI/thumbnails). Two real bugs found: a null SIMI chunk label NREs `IffFile.WriteChunk`, and a SIMI at the wrong chunk id made the lot price 0 on the next move-in. +110 tests. |
| Steam-saves gap | Ported as a startup pre-flight + new `Utils/TS1UserData` (with tests I added): refuses to boot with an actionable dialog when the install has no `UserData\Neighborhood.iff` (never-launched retail **and** Legacy Collection first run), instead of a `DirectoryNotFoundException` inside content init. |
| `#27` catalog search | Search box on `UIMainPanel` filtering the buy/build catalogue via pure `BuyCatalogService.MatchesSearch`/`SearchCategory`; re-click deselects. +46 tests. FreeSO floor-placement crash deferred with the other FreeSO items. |
| `custom-skin-fix` | "Intelligent loading": catalogue row display names are now **lazy + memoised** (no content reads at category-open, none per-frame, one per row on first search) and the strip's row list is a materialised `IList` instead of a deferred `Where`. +21 tests. The branch's failed-content notification could not be ported — `Content.FailedContentFiles` does not exist in our pinned FreeSO. |
| `fame-implementation` | Ported as a pure `RelationshipFameFilter` (bounds-checked) so the fame tab actually filters. **Important finding:** FreeSO has *no* fame career track — `VMGenericTS1CallMode.PromoteFameIfNeeded`/`DemoteFameIfNeeded` (34/36) are unimplemented and nothing ever writes `TS1FameScore`, so the tab is always empty until the **FreeSO pass** adds promotion/demotion. +32 tests. |
| `#16` update checker | New `Services/UpdateChecker` + `SimitoneVersion` (upstream `riperiperi/Simitone`, pure parse/compare, https-only release URL, never blocks or throws), 105 tests. Deliberately left unwired — product call. Caveat: neither csproj sets `<Version>`, so the SDK default `1.0.0` makes it report "up to date" until a version is stamped. |

Suite 938 → **1220**; every wave: full build + runtime smoke green. Remaining from the fork:
only the deferred **FreeSO-side** set (#11 censor blur, #14 fresh-lots, #28 slim-assets, and the
previously deferred #63/#65/#58/#67) and the out-of-scope platforms (unix/mac, launcher/desktop,
`iff_analyzer`).

---

## 8. FreeSO ingest — 2026-09-26

The FreeSO submodule was moved from `riperiperi/FreeSO@f86bbd3` to
**`alexjyong/FreeSO@7e76c196`** (35 commits, fast-forward; the parallel mesh session's files
`Simplify.cs` / `DGRP3DMesh.cs` / new `DGRP3DFusion.cs` were untouched by the range and kept).
That supplies the engine half of everything previously deferred:

| Engine feature | Commit(s) |
|---|---|
| Fresh-lot controller + expansion-pack data spawn | `c033163`, `02d0cf1` (+ `a8d23de`/`1debb45` cleanup) — spawns missing `ControllerObjects` in `VMTS1ActivatorNew.LoadFromIff` |
| Censor blur | `0918e50`…`b1c93b0` |
| Free will | `6ffae86`/`3072dfc` (`VM.FreeWillEnabled`; visitors/pets exempt) |
| Animation-load errors | `fc2781d` (`Content.FailedContentFiles` now exists) |
| Weather | `cd1dbf9` |
| Steam data / install paths | `2dea902`, `5d679c4`, `884dec2` |
| Floor-placement crash | `7e76c19` |
| UIListBox keyboard nav, non-Windows support | `1e02ef2`, `9c5382b` |

**Still not implemented upstream: fame promotion/demotion** — `VMGenericTS1CallMode`
`PromoteFameIfNeeded`/`DemoteFameIfNeeded` (34/36) remain comments and nothing writes
`TS1FameScore`, so the ingested fame filter is inert until someone adds the career track in
FreeSO.

Then their Simitone halves were un-deferred and ported (Wave D):

- **Weather** (#67): `WeatherSounds` + pure `LotWeatherState`/`WeatherCommandParser`, the
  `TS1GameScreen` weather loop and `Save`/`CleanupLastWorld` hooks, cheat-box commands, and two
  CC0 freesound clips (rain by *Rubaoliva*, thunder by *Raclure*; credited in the class header).
- **Free will** (#63): `FreeWillSetting` bridges `GlobalSettings.TS1FreeWill` ↔ `VM.FreeWillEnabled`,
  exposed as a Gameplay-tab checkbox **and** a one-tap Options-strip button.
- **Animation-load errors + DPI** (#65): `FailedContentService` shows a one-shot modal naming the
  first few `Content.FailedContentFiles`, and `Simitone.Windows/app.manifest` enables proper DPI
  awareness.
- **Steam installs** (#58): pure `SimsInstallCandidate`/`TS1InstallSelection` + WinForms
  `InstallationSelector`/`InstallationInfoDialog`; `-path` stays authoritative and the ladder is
  unchanged, but the roster is sentinel-filtered so a stale registry entry can't mask a Steam
  install.

Suite 1220 → **1426**; build + smoke green (log now reports free-will on and weather sounds
loaded).
