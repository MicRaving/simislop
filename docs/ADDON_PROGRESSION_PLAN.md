# Addon progression integration plan (issue #8 roadmap + open PRs)

Status: **proposal for review — not implemented.** Sources: riperiperi/Simitone
[issue #8 "1.0 Roadmap"](https://github.com/riperiperi/Simitone/issues/8) and the repository's
[pull requests](https://github.com/riperiperi/Simitone/pulls), read 2026-09-26.

This plan has two halves that share a lot of files, so they are sequenced together:

- **Part A — ingest the pull requests.** Twelve open PRs (plus one closed PR worth
  cannibalising) contain the missing/buggy expansion-progression wiring and a large batch of
  UI fixes. Most were written against the *old* tree and every one of their touchpoints has
  since been changed by the modernization work, so this is a hand-port, not a cherry-pick.
- **Part B — close the roadmap's progression gaps** that the PRs do not cover (per expansion,
  plus the neighbourhood-management and UI items).

Nothing here is implemented yet.

---

## 0. Constraints and ground rules

1. **The engine is the FreeSO submodule** (`D:\Simitone\FreeSO`, pinned at `f86bbd3`). It is
   treated as read-only; changes there are lost on a submodule update. Four of the incoming
   PRs (weather, free will, DPI/animation-load, Steam saves) depend on *forked* FreeSO PRs
   (#295, #290, #293, #287). **This is the single biggest decision in the plan — see §6.**
2. **The tree is already heavily diverged** from the PRs' base: services extracted
   (`UI/Services/*`), `UIBuyBrowsePanel` tables moved to `BuyCatalogService`, travel-save
   slots, remesh packaging, ImageSharp decoder, `Simitone.sln` slimmed, net10. Patches will
   not apply; each hunk is re-typed against current code.
3. **No git checkout** in this workspace, so "merge" means applying diffs by hand and
   verifying with the build/tests; we cannot literally merge a PR branch.
4. Tests: headless (`Simitone.Tests`, currently 829) for pure logic; runtime smoke against
   `D:\Program Files\Sims`; per-expansion playtest in `docs/PHASE3_TEST_MATRIX.md`.

---

## 1. What "addon progression" means here

The roadmap's **QA** and **Engine Features** sections are the progression definition:

- Core: career, child/school/military school, the relationship chain
  (walk-by → phone → romance → propose → move-in → baby → child), death.
- **Downtown**: restaurant lifecycle (done), purchasable clothing (done), auto-follow, cars.
- **Vacation**: saving (we did a first pass), revisit cleanup, renting (igloo/snowman/picnic),
  "Vacation Score", souvenirs, hotel booking.
- **Unleashed**: buy pets (done), pet AI (random starvation), pet shows/training, object-pet
  lifecycle, boat rental.
- **StudioTown**: the Fame career track, Fame special objects (skydiving/aquarium/satellite),
  obsessed fan, studio tours at home.
- **Makin' Magic**: living in Magic Town via MagiCoin houses, quests, purchasable items,
  spells, craftables, pyrite autonomy.
- Object CC (iff/far, custom animations/sounds) and Skin CC.

Plus the enabling UI/management work the roadmap marks "must have": Options Menu, Credits,
Spellbook/Cookbook UI, Picture-in-Picture, localisation, Lot Bulldoze/Rezone, neighbourhood
bootstrap and a management page.

**Simitone vs FreeSO split (important):** most *progression rules* live in FreeSO's SimAntics
engine (the `VMGenericTS1Call` global calls, fame, magic, pets). Simitone owns the *UI, lot
lifecycle, save/load, neighbourhood screens and content wiring*. The plan therefore marks each
item **[S]** (Simitone-side, in scope here), **[F]** (FreeSO-side, needs §6 decision) or
**[V]** (verification-only: believed to work, needs a playtest).

---

## 2. Baseline: what our tree already has

- Multi-slot travel + cross-session travel/vacation save slots (`Utils/TravelSave`,
  `LotTravel`) — roadmap "Saving the game when on Vacation" **[S, done]**.
- Delete family in CAS, with the family-name confirm and IFF backup — PR #64 and roadmap CAS
  items **[S, done]**.
- Installer discovery (Steam/registry/portable) behind `IRegistryReader` +
  `GameInstallLadder` — supersedes PRs #10/#11/#13/#50/#55's locator work **[S, done]**.
- Animation-load error tolerance and DPI-scaled touch thresholds (`Utils/TouchInput`,
  defensive frame loading) — overlaps PR #65 **[S, done]**.
- In-game settings (`UISettingsPanel`) as a stopgap for the Options Menu — roadmap item
  **[S, partial]**; PR #68 has the real Options architecture.
- Catalogue/buy-build query extraction, price display, unaffordable dimming, roof names,
  build-tool budget sync — roadmap buy/build **[S, done]**.
- StudioTown/Nessie/MagicTown neighbourhood overlays **[S, done]**.
- Test suite 829, the remesh addon packaging, output trim, service extraction — prior sessions.

Confirmed still missing in Simitone's own code (0 hits): Spellbook, Cookbook, Credits,
FreeWill UI, PIP, Options panel, eyedropper, installation selector, neighbourhood-visit panel,
`TS1Constants`, `SimitoneUIExtensions`. `UIHouseSelectPanel` has null **Bulldoze/Rezone/Export**
button delegates.

---

## 3. Part A — PR ingestion

Ordered by (value ÷ conflict). "Touchpoints" lists the files we must reconcile by hand.

### Wave A1 — small, self-contained, unblocks play (no FreeSO dependency)

| PR | What it fixes | Touchpoints | Notes |
|---|---|---|---|
| **#62** | Entering a lot left the camera at the lot's top and no sim selected, so the lot is unplayable until you click a sim. | `UILotControl.cs` | 42 lines. Port into our refactored `UILotControl.Update`. High value. |
| **#61** | Right-click selects a sim; mouse-wheel press rotates the camera. | `UILotControl.cs` | 57 lines. Same file as #62 — port together. |
| **#57** | "Return to neighbourhood" goes to the *current* neighbourhood (StudioTown/Vacation), not always Old Town. | `TS1GameScreen.cs` | 23 lines. Fits our `ExitLot`/`NeighborhoodService`. |
| **#56** | CAS family/last-name text box had no caption. | `UIFamilyCASPanel.cs` | 13 lines. Cosmetic, trivial. |
| **#64** | Delete-family button did nothing. | `UIFamiliesCASPanel.cs`, `TS1CASScreen.cs` | **We already implemented delete-family** (Phase 1). Diff-review; port only if #64 fixes a case ours misses, else close as done. |

### Wave A2 — buy/build quality (after A1, shares `UIBuyBrowsePanel`)

| PR | What it fixes | Touchpoints | Notes |
|---|---|---|---|
| **#59** | Eyedropper tool (copy wall/floor/terrain style) + tooltips. | `UIBuyBrowsePanel.cs` (+315), `UIObjectHolder.cs` (+70), `UIDesktopUCP.cs`, `UISimitoneFrontend.cs`, new `Utils/SimitoneCursors.cs`, 2 cursor pngs | Panel hooks must be re-targeted onto `BuyCatalogService`; the tool itself is `UIObjectHolder` logic. Wallpaper/fences deliberately unsupported. |
| **#60** (closed) | "Lots won't load in a fresh Simitone install that was never played in vanilla TS1" (+ a launcher/desktop/install-selector bundle). | `TS1GameScreen.cs`, `SimitoneGame.cs`, `UIHouseSelectPanel.cs`, lots more | Only the **fresh-lot fix** is wanted; the rest is superseded by #58/#59. Extract that hunk specifically. |

### Wave A3 — UI/UX overhaul (the big one)

| PR | What it fixes | Touchpoints | Notes |
|---|---|---|---|
| **#68** | **Options Menu** (roadmap must-have), Neighborhood **Visit** panel, eviction cleans up people, ~30 UI fixes (BigButton text fit, touch-scroll hover, catalog hover outline, house-select title scaling, neighbourhood resize/wipes, speed handling, lot centring, pickup panel layout, dead speed buttons, transitions with arbitrary icons, call-neighbour dialog fix, elastic shadows, transient actions fix). 50 files, +1963/-304. | `TS1GameScreen`, `UILotControl`, `UINeighbourhoodSelectionPanel`, `UINeighbourhoodSwitcher`, `UIHouseSelectPanel`, `UIMainPanel`, `UIBuyBrowsePanel`, `UICatalogItem`, `LoadingGameScreen`, `UITransDialog`, `UIMobileAlert`, `UIQueryPanel`, `UIPickupPanel`, `UISwitchAvatarPanel`, `UIBigButton`, `UIElasticButton`, `UITouchScroll`, + new `UI/Panels/Options/*` (4 files), `UITS1NeighborhoodVisitLotPanel`, `TS1Constants`, `SimitoneUIExtensions` | Split into 4 sub-batches (options menu; neighbourhood visit/transitions; speed/pickup/labels; misc UI controls). **This is the roadmap's "Options Menu" and "Auto-follow" enabler.** Highest conflict risk — every file has our changes. |

### Wave A4 — features that need a FreeSO decision (§6)

| PR | What it fixes | Touchpoints | Depends on |
|---|---|---|---|
| **#63** | Free-Will toggle button (roadmap "Accurate Free Will"). | `UIMainPanel.cs`, `SimitoneGame.cs`, icon, + FreeSO hook | FreeSO PR **#290** |
| **#65** | DPI awareness on load (`app.manifest`) + graceful custom-animation load failure. | `GameController.cs`, `SimitoneGame.cs`, `Simitone.Windows/app.manifest` | FreeSO PR **#293** (we already tolerate bad animation frames — reconcile) |
| **#58** | Friendly multi-install selector + "Steam saves" handling. | `WindowsLocator.cs`, `Program.cs`, new `InstallationSelector`/`InstallationInfoDialog` | FreeSO PR **#287** |
| **#67** | Cosmetic weather (console-enabled) + rain/thunder sounds. | `TS1GameScreen.cs`, `UICheatTextbox.cs`, `WeatherSounds.cs`, 2 wavs, a GitHub Action | FreeSO PR **#295** |

### PRs to skip

Already merged into our pinned base: **#55** (net9), **#26** (cheat system), **#13**, **#10**
(locators). Closed/superseded: **#54**, **#53**, **#50**, **#11**, **#4**, and the
non-fresh-lot parts of **#60**. Every PR also carries a `.github/workflows/build.yml` (from
PR #56) and a `FreeSO` submodule pointer bump — adopt the CI workflow once, ignore the pointer
bumps (we have our own Azure Pipelines + plan).

**Acceptance for Part A:** build green, tests green, runtime smoke green, and each ported PR's
stated repro verified by hand (documented in `docs/PHASE3_TEST_MATRIX.md`). Where a PR depends
on a FreeSO change we do not have, the Simitone side must degrade safely (feature hidden/no-op),
never crash.

---

## 4. Part B — roadmap progression gaps

Each item: current state → proposed work → owner → risk.

### 4.1 UI must-haves

| Item | Current | Proposed | Owner | Risk |
|---|---|---|---|---|
| **Options Menu** | stopgap `UISettingsPanel` | Adopt PR #68's `UI/Panels/Options/*` as the canonical menu; fold our six settings in; reachable from live + neighbourhood + main menu. | **[S]** | Med (big UI port) |
| **Credits** | none | New `UICreditsPanel`: TS1/Complete Collection credits + FreeSO contributors; reachable from Options. Text data can be a static table (not EA assets). | **[S]** | Low |
| **Spellbook / Cookbook UI** | none | Research how TS1 drives these (VM dialog / global call). Build a Simitone panel that issues the same VM command; gate on the engine call existing. Needs a spike first. | **[S]** (+[F] if the call is missing) | Med/High |
| **Picture-in-Picture** | none | FreeSO has a PIP/headline system; port the event window into the Simitone UI. Spike to confirm engine support. | **[S]**/[F] | Med |
| **Localisation** | TS1 strings via `UIText.iff`; custom strings hardcoded English | Add a small `SimitoneStrings` table loaded from `Content/UI/uitext/<lang>.dir` (FreeSO's `ContentStrings` already loads that path), and route every hardcoded Simitone string through it. Track the ~15 hardcoded strings now in the tree. | **[S]** | Low |
| **Desktop rearrangement** | PR #68 | Covered by A3. | **[S]** | Low |

### 4.2 Neighbourhood management

| Item | Current | Proposed | Owner | Risk |
|---|---|---|---|---|
| **Lot Bulldoze + move out** | `EvictLot` exists; Bulldoze button is `null` | Implement bulldoze: refuse if occupied (or evict first), delete/neutralise `House{NN}.iff`, clear zoning/objects, re-save neighbourhood; wire the button. PR #68 also makes eviction clean up people. | **[S]** | Med (destructive; back up first) |
| **Lot Rezone** | `null` button | Write the zoning entry for the lot and persist (`LotZoning`/`MasterLotZoning`); wire the button. | **[S]** | Med (data format) |
| **Neighbourhood bootstrap** | works on our played install; unverified on a virgin install | Port the fresh-lot hunk from closed PR #60, guarded, with a unit test around the path that failed. | **[S]** | Med |
| **Neighbourhood management page** | none | Copy-from-TS1, create named neighbourhood, delete/rename. Large: new screen + provider work. Proposal: phase 2 (after A/B), scope separately. | **[S]** | High |

### 4.3 Core progression (mostly engine)

| Item | Current | Proposed | Owner | Risk |
|---|---|---|---|---|
| Career progression | FreeSO has careers; TS1 tracks unverified | **[V]** playtest per track; fix Simitone-side UI (job subpanel) bugs found. | [F]/[S] | Med |
| Child / school / military school | unknown | **[V]** then [F] for the global calls. | [F] | High |
| Relationship chain (walk-by→baby) | unknown | **[V]**; Simitone-side phone/move-in UI exists. | [F]/[S] | High |
| Death (adult/child/pet) | unknown | **[V]**; Simitone needs the death/ghost presentation wiring. | [F]/[S] | Med |
| **Promote/Demote Fame logic** | FreeSO has some Fame code | **[V]** + [F] gap-fill; roadmap explicitly lists it as missing. | [F] | High |
| All TS1 global calls (incl. Pet→Adult) | partial | Audit `VMGenericTS1Call` against TS1's call table; [F] gap-fill. | [F] | High |
| Free Will | FreeSO #290 | Adopt #63 UI + FreeSO hook (§6). | [F]/[S] | Med |

### 4.4–4.8 Expansion progression

| Expansion | Items | Server-side work |
|---|---|---|
| **Downtown** | Auto-follow ("invite sim downtown"/"take sim home"); the "god damn cars"; restaurant lifecycle (done); purchasable clothing (done). | Auto-follow is likely Simitone UI + FreeSO travel command (`AutoFollow` symbols exist in FreeSO). Cars = the neighbourhood car overlays; we added StudioTown's, Downtown's are separately named — investigate. |
| **Vacation** | Revisit cleanup (other families/npcs/objects), renting (igloo, snowman, picnic), Vacation Score + prize, souvenirs, hotel booking. | Mostly engine [F]; Simitone needs the rental UI and the score/souvenir HUD if any. Our travel slots fix saving; this is the deeper semantics. |
| **Unleashed** | Pet AI (no random starvation), pet shows/training, object-pet lifecycle, boat rental. | [F] mostly; Simitone may need training/show UI. |
| **StudioTown** | Fame career, Fame special objects (skydiving/aquarium/satellite), obsessed fan + studio tours at home. | [F] event/global work + [S] presentation. |
| **Makin' Magic** | Magic Town MagiCoin houses, quests, purchasable items, spells, craftables, pyrite autonomy. | [F] mostly; [S] for the Spellbook/Cookbook panels (4.1). |
| **CC** | Object iff/far with custom anims + sounds; Skin CC. | [S] content loader wiring; PR #65's graceful failure is the first step. |

### 4.9 Verification (the "[V]" items)

The roadmap's QA section is a per-expansion playtest matrix. Extend
`docs/PHASE3_TEST_MATRIX.md` with a progression section (one row per bullet above), because
these cannot be automated — and several will fail, which is the point: the failures become the
[F] work items for a FreeSO pass.

---

## 5. Proposed sequencing

| Wave | Contents | Why this order |
|---|---|---|
| **A1** | PRs #62, #61, #57, #56, #64(verify) | Small, independent, immediately improves lot entry playability. |
| **A2** | PRs #59 (eyedropper), #60 fresh-lot hunk | Buy/build + fresh-install bootstrap. |
| **B1** | Options Menu + Credits + Localisation (roadmap UI) | Unblocks user-facing completeness; PR #68's options work lands here. |
| **A3** | PR #68 remaining batches (neighbourhood visit, transitions, speed, controls) | Big UI port, best done after Options. |
| **B2** | Lot Bulldoze + Rezone; eviction cleanup | Roadmap neighbourhood management. |
| **A4** | PRs #63/#65/#58/#67 | Gated on the §6 FreeSO decision. |
| **B3** | Vacation/Unleashed/StudioTown/Magic [V] playtest matrix; convert failures into work items | Engine truth before engine work. |
| **B4** | Neighbourhood management page; Spellbook/Cookbook; PIP; [F] gap-fill | Largest/least certain, last. |

Each wave: subagent-parallel where files are disjoint, then a housekeeping pass (build, 830+
tests, runtime smoke, plan/PR-notes update) before the next.

---

## 6. Decision needed: FreeSO-side dependencies

PRs #63, #65, #58, #67 cannot be fully honoured at FreeSO `f86bbd3`. Options:

- **(A) Adopt alexjyong's FreeSO fork/commits** containing PRs #287/#290/#293/#295. Biggest
  functional gain, but pulls an unreviewed fork into the build and may drag unrelated changes;
  we would need to pin and re-verify the whole engine.
- **(B) Port just the four FreeSO changes** into our submodule as a local patch series.
  Contradicts "don't edit FreeSO" (lost on update) but is surgical.
- **(C) Simitone-side only, feature-gated.** Land the UI and no-op/degrade when the engine hook
  is absent; the buttons exist but do nothing until FreeSO catches up.
- **(D) Defer those four PRs** and do everything else ([S]/[V]) first.

**Recommendation: (D) then (A)** — do all Simitone-side work first, then take the fork as one
reviewed submodule bump if you want weather/free-will/DPI/Steam-saves.

---

## 7. Risks

1. **#68 is a 1,963-line, 50-file rewrite** across every file we changed; high regression risk,
   no way to auto-merge. Mitigation: port in 4 sub-batches, build+tests+smoke after each.
2. **Destructive lot ops** (bulldoze/rezone) can destroy player saves. Mitigation: back up,
   confirm dialog, unit-test the guard predicates.
3. **Engine-side progression** may simply not exist in FreeSO; the playtest matrix will find
   out, but the [F] work is unbounded until it does.
4. **Forking FreeSO** risks breaking the currently-green engine.
5. **Licensing** — do not commit EA assets; the PRs' new art (cursors, icons, sounds, options
   art) is Simitone-authored and can be committed, but rain/thunder wavs need a source check.

## 8. Decisions (resolved 2026-09-26)

1. **FreeSO policy: (D) then (A)** — land all Simitone-side work first; take the forked FreeSO
   commits (for #63/#65/#58/#67) later as one reviewed submodule bump.
2. **Scope** — the **Neighbourhood Management Page is in scope**; the **Cross-System install
   file-transfer tool is out of scope** (dated/obsolete).
3. **Lead with PR ingestion (Part A)** — concrete fixes first, then the roadmap gaps.
4. **Assets** — commit the Simitone-authored PNGs; **verify the weather WAVs' provenance
   before committing** (source substitutes or omit if unclear).

Implementation begins with Wave A1. The unblocked order is A1 → A2 → B1 (Options/Credits/
Localisation) → A3 (#68) → B2 (bulldoze/rezone) → B3 (progression matrix) → B4
(management page, Spellbook/Cookbook, PIP). A4 (FreeSO-gated) is deferred per decision 1.

---

## 9. Progress log

### Wave A1 — landed 2026-09-26

- **#62** (lot-entry camera/selection) and **#61** (right-click selects a sim, middle-click
  rotates) ported into `UI/Panels/UILotControl.cs`. Deviations from the raw diff, all
  deliberate: middle-click rotation routes through `TS1GameScreen.WorldRotationStep` (keeps the screen
  state and the desktop/touch rotate paths in sync) and is skipped in 3D and touch mode;
  both click handlers skip software-keyboard/touch; a pie-menu teardown was added on a
  successful select; `ScrollAnchor` is cleared in every fallback branch (the diff only did it
  for the empty-lot case, which would otherwise re-centre every frame).
- **#57** (return to the correct neighbourhood) ported into `UI/Services/NeighborhoodService.cs`
  + a `CurrentNeighborhoodMode` field/facade on `TS1GameScreen`. The upstream 81..90 Studio /
  90..99 Magic overlap bug was fixed to 81..89. Travel-lot return behaviour is deliberately
  unchanged. Covered by the new `NeighborhoodHouseMappingTests` (the 90→Magic regression).
- **#56** (CAS family-name caption) ported into `UI/Panels/CAS/UIFamilyCASPanel.cs`, plus a
  resize fix for the caption width.
- **#64** (delete-family button) verified as **already fixed** by our Phase 1 implementation,
  in a stricter form (index-carrying event, selection re-validation, localised confirm,
  chunk surgery). No changes.
- **CI**: adapted GitHub Actions workflow at `.github/workflows/build.yml` (net10, our
  solution + test project; no Protobuild/net45).
- Tests 829 → 850; solution build + runtime smoke green.

### Wave A2 — landed 2026-09-26

- **#59 (eyedropper) ported.** New `Utils/SimitoneCursors.cs` + `Content/Cursors/eyedropper.png`
  and `Content/uigraphics/desktop/d_live_eyedropper.png` (both valid 18×18 RGBA PNGs, downloaded
  from the PR branch). `UIObjectHolder` gains `EyedropperMode` + pick events; `UIDesktopUCP`
  gains the button/shortcut/tooltips; `UIBuyBrowsePanel`'s ~315-line hunk was re-targeted onto
  `BuyCatalogService`/`CatalogQueryService`. Deliberate hardening on top of the PR: bounds-check
  the floor pick (the PR indexed `GetFloor` off the lot edge and could throw), release a held
  build tool when arming the eyedropper, and keep the subcategory re-open behaviour correct when
  the pick is outside the open subcategory. Wallpaper/fences remain unsupported (as upstream).
- **#60 (closed) analysed, not applied.** Its "fresh lot" fix is **entirely inside the FreeSO
  submodule pointer bump** (`f86bbd3` → `7a59f11`), so our pinned tree has none of it. The
  minimal patch is two hunks:
  1. new `FreeSO/TSOClient/tso.content/TS1/TS1ControllerObjects.cs` (the 33 system/controller
     GUIDs vanilla TS1 bakes into `OBJM`), and
  2. `FreeSO/TSOClient/tso.simantics/Utils/VMTS1ActivatorNew.cs` spawning any missing controller
     after `VM.Load` in `LoadFromIff`.
  Proven necessary: comparing `TemplateUserData\Houses\House05.iff` (never played) with
  `UserData\Houses\House05.iff` (played) shows vanilla **adds** the phone + visit-generator
  controllers; a fresh lot has none, so it has no phone/visitor systems.
  **Deferred per decision 1 (FreeSO-side).** When the FreeSO pass happens, the patch must also
  trim `TragicClownGenerators`/`OtherSystemObjects` (as written it would spawn a tragic clown on
  every controller-less lot, and the first save persists it).
- **Separate virgin-install bug found (not in any PR):** `TS1NeighbourProvider`'s first-run copy
  reads `<install>/UserData/`, which the **installer does not create** (vanilla TS1 creates it on
  first launch; a never-launched install has only `TemplateUserData/`). A virgin install therefore
  throws `DirectoryNotFoundException` during content init — before any lot is reachable. Also
  FreeSO-side; deferred to the same pass, with a `TemplateUserData` fallback patch prepared.
- Tests 850 → 860; build + runtime smoke green.

### Wave B1 — landed 2026-09-26 (Options Menu + Credits)

- **#68's Options foundation ported** (the roadmap's "Options Menu"): new
  `UI/Panels/Options/{UIOptionsPanel, UIOptionsSubpanelBase, UIOptionsSubpanels,
  UIOptionsAlert}.cs`, `TS1Constants.cs`, `UI/SimitoneUIExtensions.cs`; upgrades to
  `UIStyle`, `UIIconCache`, `UIBigButton` (desktop/mobile art split + caption elision),
  `UIElasticButton` (shadows), `UITouchScroll` (hover); `UIMainPanel` Options submenu now
  Save / Neighbourhood / **Settings** / Quit, with Settings opening the tabbed Options dialog
  (Gameplay/Graphics/Sound) that reads/writes `GlobalSettings` and applies graphics live.
  Also a window 800×600 minimum and language selection. 12 approved PNGs downloaded from the
  PR branch. Deliberate fixes on top of the PR: `FitAvailableSpace` measures the label's own
  size (the PR hard-coded 37), `UIShadow` actually applies its colour/opacity, option pages get
  a real content width (`Parent.Size.X` is always zero for a container), and the "restart
  required" string falls back to English instead of a TSO-only table.
- **Credits** built (`UI/Panels/UICreditsPanel.cs` + `Utils/CreditsData.cs` + 60 tests),
  reachable from the live Options submenu. Text-only, no EA assets: Maxis/EA attribution,
  FreeSO, the remesh authors, and Simitone, with the unofficial/no-EA-assets position.
- **Localisation (partial):** the Options language picker + an `OptionsStrings` fallback were
  added. Note the finding that TS1's `UIText.iff` only ships English, so switching language is
  presently cosmetic until a non-English string source exists.
- Tests 860 → 920; build + runtime smoke green.

**Cross-wave note:** `UIOptionsAlert.cs` currently carries the `TS1GameScreen` Options helpers
as extension methods (`ShowOptionsDialog`, `RuntimeChangeLanguage`, `LockSimSpeed`,
`UnlockSimSpeed`) because `TS1GameScreen.cs` is owned by the next wave; those become shadowed
when the real instance members land. The new `UIBigButton` intrinsic size also ripples into
`UIHouseSelectPanel`/`UIMobileAlert`/`UICallNeighborAlert`/`UISelectSkinAlert`, which the next
wave's #68 batches address.

### Wave B2 — landed 2026-09-26 (#68 neighbourhood + live/lot)

- **Neighbourhood half:** `UINeighbourhoodSelectionPanel` now drives the camera through a pure
  `NeighCameraMachine` (full-screen vs house-focus states), so "No" to move-in, neighbourhood
  switches and window resizes always return the camera cleanly; the switcher does wipe
  transitions and casts `ngbh_shadow`; `UITransDialog` gained arbitrary icons +
  `CreateWithCustomUIIcon`; new `UITS1NeighborhoodVisitLotPanel`; house-select/pickup/query/
  switch-avatar/mobile-alert layout fixes. `NeighCameraMachineTests` (18 cases) added.
- **Live/lot half:** `TS1GameScreen.ChangeSpeedTo` is now the single speed authority (with
  `LockSimSpeed`/`UnlockSimSpeed` restoring the previous multiplier when leaving Buy/Build;
  pause button lights red when locked), lot load centres on the lot, catalogue hover outline +
  selection clearing, job/motive/personality subpanels read their real localised string tables,
  `UICallNeighborAlert`'s `obj < 0` crash fixed, loading-screen localized text, CAS string
  tables, `RestoreLotUi` for a declined travel (fixes a pre-existing black-screen). Several
  real bugs fixed over the PR: the speed-button lock never redrew, the personality loop
  described Neat for all five traits, and a null `lastSelectedAvatar` NRE.
- Also fixed the `UIHouseSelectPanel` option-row pitch for the new 48px desktop buttons (it was
  clipping the 5th row at 600px-tall windows).
- Tests 920 → 938; build + runtime smoke green.

### Blocker found (not mine)

A **parallel session** is running an offline mesh generator in this repo:
`Simitone.GenerateMeshes` (with `docs/MESH_GENERATION_RESEARCH.md` and a
`FreeSO/TSOClient/tso.files/RC/DGRP3DFusion.cs` edit) has been packing ~840 MB of generated
meshes into `FSO.Remeshes-main/remeshes` for ~2 hours. It is **not** part of this plan; it holds
build outputs and intermittently breaks full builds with `MSB3021/3027` copy locks. I have left
it running untouched (it looks like deliberate work) and worked around it where needed. The
build target still consumes the enlarged pack, so this is additive.

---

## 10. Current roadmap status (audited 2026-09-27)

A read-only, code-verified audit of every issue #8 bullet against the tree (FreeSO now at
`alexjyong/FreeSO@7e76c196`). Of 51 tracked items: **8 Done, 18 Partial, 17 Missing, 8
FreeSO-side**, and nearly all of the 14 QA rows are **unverified** (the test matrix has no
progression section and every result cell is empty).

Verified done since this plan was written: Options Menu; Credits; buy/pet/clothing purchase;
free will; vacation saving; purchasable outfits; lot Rezone; move-out + bulldoze (inside
eviction); multi-slot travel; and (via the FreeSO ingest) weather, censor blur, fresh-lot
controllers, animation-failure reporting and the floor-placement fix.

Largest gaps, in priority order:

1. **Neighbourhood Management Page** - the backend (`Utils/NeighbourhoodManager`, ~1,200 lines,
   156 tests) has **zero production callers** and `DefaultInstallPath` is never assigned; only
   the screen + entry point are missing.
2. **Neighbourhood bootstrap is a refusal, not a bootstrap** - `TS1NeighbourProvider` copies
   `<install>/UserData/` with no `TemplateUserData` fallback, and `Program.cs` shows "launch
   The Sims first" then exits 1.
3. **Fame promote/demote do not exist** (`VMGenericTS1Call` 34/36 are comments; nothing writes
   `TS1FameScore`), so the ported fame tab is permanently empty and StudioTown is blocked.
4. **Spellbook/Cookbook, Picture-in-Picture** - absent in both trees (engine + UI).
5. **Localisation of custom strings** - only the Options dialog reads a table; no
   `SimitoneStrings` and no shipped `Content/UI/uitext/*.dir`.
6. **~40% of TS1 global calls unimplemented**, incl. `ChildToAdult` (39) and `PetToAdult` (40)
   (both now filled in - see §11 - leaving 0, 3, 7, 8, 9, 12, 35, 37, 38, 41, all of which are
   either UI-only or have no recorded register contract).
7. **Auto-follow** per-sim invite/take-home (Downtown QA) - only a visitor picker exists.
8. **`.gitmodules` still points at `riperiperi/FreeSO`** while the tree is the fork at
   `7e76c196`; a fresh clone would get the wrong engine.

Also revised: the Options "ShadowQuality" row was previously wired to nothing; it now drives
`WorldConfig.ShadowDetail` (see `docs/VISUAL_FEATURES.md`). The update checker and website
plumbing exist but are unwired and unstamped.

#### The §10 list, re-checked 2026-09-27 (after Waves H–L)

Five of the eight gaps above are now **closed**; the list is kept for history, and the live
backlog is `docs/REMAINING_ENGINE_PLAN.md` §6.

- **1 Neighbourhood Management Page — DONE.** `UI/Panels/UINeighbourhoodManagementPanel.cs` ships
  (page + pure `NeighbourhoodPanelModel` + layout + tests) and is wired from
  `UINeighbourhoodSwitcher.cs:56`; `NeighbourhoodManager.DefaultInstallPath` is assigned in
  `GameController.cs:56`.
- **2 Neighbourhood bootstrap — DONE.** `TS1NeighbourProvider.ResolveBootstrapSource` now falls
  back `UserDataN` → `Template*N` → `TemplateUserData`, and `Program.cs` treats a virgin install as
  bootstrappable (`TS1UserData.IsBootstrappable`/`HasNeighborhood`) instead of refusing.
- **3 Fame promote/demote — DONE** (Wave G1, `VMTS1FameHelper`).
- **4 Spellbook/Cookbook + Picture-in-Picture — DONE** (Waves G2/G3); the engine dialog cases
  12/15 are handled in `VMDialogPrivateStrings`.
- **8 `.gitmodules` — DONE** (it already points at `alexjyong/FreeSO`).
- Still open: **5 localisation infrastructure** (Wave M2), **6 the last TS1 call** — call 3
  `PullDownTaxiDialog` (Wave M1), **7 auto-follow** (Wave M3), and the QA matrix (human).

### Fixes landed alongside this audit (2026-09-27)

- **Move-in / live-mode / economy fix (critical).** `LotLifecycleService.BlueprintReset` passed
  `ActiveFamily == null` into `IsSimless(hasActiveFamily, ...)` - the negation of the flag it
  asks for - so **every lot loaded as simless** (global 32 = 1): live mode disabled, the money
  HUD hidden, the game opened in buy mode and the VM at speed -1. Fixed with a call-site-shaped
  `SimlessForLoad(FAMI, bool)`; runtime-verified - a moved-in family now opens in Live mode with
  the money HUD live (SS7,447) and the clock running.
- **Generated-mesh ingest.** 6,104 generated meshes sanity-tested (0 hard-invalid; 1,520 carry
  NaN normals from the pre-existing `GenerateNormals` on degenerate triangles, ingested because
  the in-game fallback produces the same). 6,110 files placed in `Content/MeshReplace/`; that
  folder now ships 9,363 files (3,253 pack + 6,110 generated, 0 collisions). See
  `docs/MESH_INGESTION_REPORT.md`.
- **Visual features.** FreeSO `WorldConfig.ShadowDetail` (8/16/24 px per tile) now actually
  sizes every light/shadow target, and `WaterShininess` drives the water/pool specular; both
  exposed in Options > Graphics (defaults identical to before) with `docs/VISUAL_FEATURES.md`
  and +85 tests. (No true reflections exist in the engine yet - documented.)
- Suite 1426 -> **1761**; build + smoke green.

---

## 11. Wave G - implementing the remaining roadmap gaps (2026-09-27)

Next wave targets, chosen from the §10 gap list for being tractable in parallel with disjoint
ownership. Housekeeping (build/tests/smoke/doc update) runs when each sub-wave lands.

| Wave | Item | Owner | Notes |
|---|---|---|---|
| G1 | **Fame promote/demote + TS1 global-call gap-fill** (`ChildToAdult` 39, `PetToAdult` 40, etc.) | FreeSO `VMGenericTS1Call.cs` | Unblocks StudioTown and makes the ported fame tab functional. |
| G2 | **Spellbook/Cookbook UI + plumbbob motive state** | Simitone `UILotControl.cs` + new panels | Engine has the enum values but no handling; ui + engine lookup. |
| G3 | **Picture-in-Picture** | new Simitone overlay (+ engine research) | Absent in both trees. |
| G4 | Wiring bundle: update checker + version stamp, standalone bulldoze on an empty lot, remaining localisation call sites, 3D-neighbourhood export re-enable, NaN-normal generator fix | Simitone | Small independent items. |

Remaining after G (if not closed within it): the per-expansion progression playtest matrix and
the website - both of which need a human at the controls / product decision.

### Wave G - landed 2026-09-27

- **G1 Fame engine (FreeSO):** `PromoteFameIfNeeded` (34) / `DemoteFameIfNeeded` (36) implemented
  with a researched half-star ladder (`VMTS1FameHelper`), writing `TS1FameScore`/
  `TS1FameStarPower`/`TS1FameStarHighWatermark` and mirroring them into the NBR chunk; the
  Simitone fame tab turns on at the first rung (score > 1). `ChildToAdult` (39) / `PetToAdult`
  (40) are now implemented as **in-place** transforms over `VMTS1BodyTransformHelper`: body
  strings 0/1/2/12-22/27-34, the live avatar re-init (new `SimAvatar` on the adult skeleton,
  `AvatarComponent` re-pointed, adult walk/swim sets, unscaled), and the NBR's
  `PersonsAge`/`SkinColor`/`Gender`/`TS1ScalingSim` mirrored so the next lot load does not undo
  it. Identity, name, family and relationships are untouched. Also filled in: `AbortInteractions`
  (13), `ChangeNormalOutfit` (16), `GetDowntownTimeFromSOInventory` (23) and
  `FamilySpellsIntoController` (43, `VMTS1MagicHelper`); `VMTS1MakeNewCharacter`'s adult branch
  (which threw a `KeyNotFoundException` on a "mm"/"fm" BCF key) is fixed; and
  `VMTS1PurchasableOutfitHelper.GetValidOutfits` no longer erodes the shared BCF list. The rest
  (0, 3, 7, 8, 9, 12, 35, 37, 38, 41) are documented in place with the reason each is blocked.
- **G2 Spellbook/Cookbook + plumbbob motive:** the engine's magic dialog carries no list, so a
  `MakingMagicTables` reader of `GameData/magic.iff` (25 spell titles / 9 recipes, localised,
  label-indexed) drives a paged spellbook/cookbook panel answering via the standard dialog
  response; the plumbbob spin now reflects the lowest watched motive (colour/scale are not
  client-reachable). +70 tests. The engine's `ExecuteGeneric` needs a one-line case for 12/15
  (documented, FreeSO-side).
- **G3 Picture-in-Picture:** new self-contained `UIPictureInPicture` overlay that crops the lot
  backbuffer (falls back to the live headline) for a subject, with a `Notify` API and F9; the
  live feed needs AA off in 2D because the backbuffer is MSAA otherwise (documented). +56 tests.
- **G4 Wiring bundle:** standalone Lot Bulldoze on an empty lot (reusing `ResetHouse`); update
  checker wired into Options with `<Version>0.9.0</Version>` (and `SimitoneVersion` fixed to
  read the entry assembly); localisation call sites converted; 3D-neighbourhood OBJ export
  re-enabled behind `SIMITONE_EXPORT_NEIGHBOURHOOD_OBJ=1`; NaN-normal repair added to the mesh
  generator (2,043 existing files need a regeneration to benefit). +80 tests.

Suite 1974 -> **2218**; build + smoke green.

### Wave H - in progress (from the Wave G findings)

- **CAS move-in candidate list is filtered to CAS-created families only**
  (`TS1CASScreen.SetFamilies` requires `(Unknown & 16) > 0`), so on a stock install the
  "select a family to move in" list is empty until a family is created in CAS. Fix so
  shipped/unplaced families are selectable.
- **Window-resize crash** (pre-existing): `PPXDepthEngine.InitScreenTargets` throws
  `SharpDX CreateTexture2D E_INVALIDARG` and `_2DWorldBatch.WithBuffer` throws
  `ArgumentOutOfRangeException` on the `InitializeLot -> ChangeAAMode` path after a resize.
- **Progression playtest matrix + remaining localisation ids** (docs + `.cst`).

### Wave I - landed 2026-09-27

- **`SIMI.Write` was zeroing the lot globals** (a `GlobalData = new short[38]` guard typo) - fixed;
  the VM's globals now round-trip through a save, with tests.
- **`$` -> `§`** in the six FreeSO build-tool cost strings.
- **Spellbook/Cookbook answers now take effect** (`VMDialogPrivateStrings` handles 12/15).
- **`ChildToAdult` (39) and `PetToAdult` (40) implemented** as in-place transforms preserving
  identity/name/relationships and persisting to the NBR + character IFF; plus calls 13, 16, 23,
  43. Drive-by fixes: `VMTS1MakeNewCharacter` read `"mm"`/`"fm"` (no such BCF key -> threw) and
  `GetValidOutfits` mutated the cached provider list. +111 tests.

### Wave J - landed 2026-09-27

- **FreeSO bug fixes**: TS1 call 30 (`GetSuitIndex`) could only ever return -1; `TilEQ` off-by-one
  corrupted all six hand slots on every suit change (sims lost their hands); new-character slot 21
  named the open hand in the closed slot; several crash paths on script-supplied values; new-sim
  personality was five identical bars. +47 tests.
- **Neighbourhood management switching wired**: an Open action with a save/discard confirm, safe
  `InitSpecific` switching (re-init the previous slot on failure) and a `RefreshNeighbourhoodUi`
  screen hook. +44 tests.
- **Vacation/Unleashed engine**: `ReturnNumberOfAvaiableVacationLotsInTemp0` now counts the
  neighbourhood's real `House4x.iff` files; a narrow revisit-cleanup hook on call 26; and a
  disassembly-verified finding that **pets cannot randomly starve** (the shipped pet globals clamp
  hunger above the failure threshold) and that **rentals are pure lot-script content** - so the
  matrix rows claiming a missing rental UI are wrong. +44 tests.

Suite 1974 -> **2568**; build + smoke green.

### Wave K - in progress
- Remaining expansion engine hooks (StudioTown fame events, Makin' Magic quests/craftables/pyrite)
  via retail-script disassembly.
- Client polish: the `TS1CASScreen` closed-hand slot bug, PIP AA handling, the WinForms DPI warning.
- FreeSO 2D/3D 2.0 concrete items.

### Wave K - landed 2026-09-27

- **StudioTown/Magic/remaining-call audit (disassembly-backed).** The StudioTown fame
  objects and Makin' Magic quests/craftables make **no** unimplemented calls except one:
  every spell and recipe funnels through **call 38 `GetTokensFromString`**. Calls 41
  (`HeadFlush`) and 12 (`GetDistanceToCameraInTemp0`) were implemented; call 31 was writing
  Temp0 (clobbering the suit type) and now writes Temp1 like its siblings; call 22 now returns
  false so the shipped picker fallback runs. Calls 0/8/9/31/37 have **zero call sites in the
  whole install** (dead, not missing). +41 tests.
- **CAS closed-hand slot** fixed (slot 21 wrote the open hand) via a shared helper; PIP feed
  fallback is now a pure tested helper; the WinForms DPI warning is resolved with the manifest
  unchanged.
- **FreeSO 2D 2.0:** the roadmap's "partial vertex buffer update" did **not** exist. Object
  sprites now keep a persistent CPU mirror, skip unchanged uploads, and reuse their vertex
  buffer across zoom/rotation rebuilds; a real room/level vertex-cache staleness bug was fixed.
  Measured ~4-24x faster on-device with always-on counters (`SpriteVertexStats`). The 2D wall
  rewrite was found unnecessary (batching already exists) and the shader/specular items are
  blocked on `tso.content`/`tso.files/RC` (documented, not started).

Suite 2568 -> **2685**; build + smoke green.

### Verification rule (2026-09-27)

**No subagent may use mouse, keyboard, window-focus/position, or game-launch automation, and
none may probe the running game's UI.** A prior wave did so; it disrupted the user's desktop,
hit nothing usefully, and is prohibited from here on. Verification is headless only - build,
unit tests, and reading `simitone.log` - and anything needing a human at the controls is
handed to the user as a matrix row, not automated.

### Wave L - landed 2026-09-27

- **L1 Call 38 `GetTokensFromString` implemented.** The previous "per-tree literal table" reading
  was wrong: the call takes **Temp0 = an `STR` chunk id, Temp1 = an index into that chunk**, not a
  literal id. `VMTS1PresentationHelper` gains `TokenListRequest`/`ReadTokenList`/
  `TokenizeTokenList` plus a `ResolveLocalLiteral` resolver, and `VMGenericTS1Call` case 38
  delegates to it. The shared contract of the 2,224/2,287 shipped sites is verified; failure
  returns `-1`, absent tokens are `0`. Tests: `VMTS1TokenTableTests`, `VMTS1PresentationHelperTests`.
  Residual QA only: the `[4] Temps[0] = Literal[1]` instruction anomaly (V-MAG-2).
- **L2 2D dirty-region wall recache.** `WallDirtyRegion.cs` (per-storey tile region + one-tile
  halo; pure union/clamp/dilate/diff) plus `WallComponent.Draw(gd, world, region)` replay the
  unchanged tiles' sprite runs and rebuild only the changed tiles; `WorldPlatform2D.RecacheWalls`
  diffs an inputs snapshot to size the region and falls back to a full recache on a structural
  change. Output is identical to the old path by construction. Pure-region tests added.
- **L3 remaining-event audit** -> `docs/REMAINING_ENGINE_PLAN.md` (871 lines, disassembly-backed).
  Headline: the TS1 call surface is **not** the blocker (545 sites; only call 3 unimplemented,
  calls 35/15 documented no-ops), and Vacation Score/souvenirs/hotel booking, the StudioTown fame
  objects/obsessed fan and the Unleashed pet shows/training are **already fully implemented** -
  they are QA rows, not engine work. "Pyrite autonomy" does not exist in the shipped data and is
  re-scoped to Magic Growth (blocked only by call 3) + the quest controller.

Suite 2685 -> **2771**; build + smoke green.

### Wave M - landed 2026-09-27

- **M1 Call 3 `PullDownTaxiDialog` implemented** - the last genuinely missing TS1 global call
  (12 sites: the Vacation/Studio/Magic arrival markers and phone plugins). It raises the TS1
  departure prompt through the existing blocking `dialog_private` handshake
  (`VMDialogPrivateStrings.ExecuteGeneric` + a synthesized operand/STR table), via the new
  `VMDialogType.TS1TaxiLeave = 16` (values 5-15 were taken) and a `tso.client` route in
  `UILotControl`'s dialog switch that falls through to the generic alert - no bespoke panel is
  needed for a two-button prompt. It always returns `GOTO_TRUE`.
  **Correction to the earlier note:** the old claim that a `GOTO_FALSE` would "fall through to
  `[14] breakpoint` and self-loop" was **wrong**. `VacationPedMarker.iff` 4128 `[13]` is
  `T->61 F->253`, and `VMThread.MoveToInstruction`'s `case 253` picks the *non-253* pointer, so
  the false branch lands on `[61]` exactly like the true one and `[14]` is dead code. The
  conclusion (always `GOTO_TRUE`) is unchanged; it now rests on a verified reason.
- **M2 Localisation infrastructure** - `Utils/SimitoneStrings.cs` loads FreeSO's existing
  `ContentStrings` table `_f900_simitone.cst` from `Content/UI/uitext/<lang>.dir/`, with a
  hardcoded English fallback for a missing file/dir/key, `***MISSING***`, empty values and
  control characters (plus a `FormatException` guard on the switch-confirm message). English
  source (153 entries) shipped; the neighbourhood page, Options subpanels, the free-will setting
  and Credits now route through it. 139 tests.
- **M3 Engine hardening + auto-follow audit** - `VMPushInteraction` null-guards its
  `interactionSource`; `VMMemory` clamps the `NeighborPersonData` (scope 32) index into the
  88-short array instead of throwing. The auto-follow audit found the per-sim invite/take-home
  path **already implemented and wired** (`UILotControl` + `AutoFollow`, calls 19/20) - so the
  §10 "only a visitor picker exists" note was stale - but surfaced two real defects (closed in
  Wave N).

Suite 2771 -> **2897**; build + smoke green.

### Wave N - landed 2026-09-27

- **N1 Core/expansion engine audit + gap-fill (FreeSO).** Career, child/school/military school,
  death/ghost, the relationship chain, boat rental, object-pet lifecycle and MagiCoin all
  disassemble clean (calls implemented, opcodes registered) - except **opcode 33 `find 5 worst
  motives`, which was genuinely missing** (a bare TODO in `VMContext`; `VMThread` substituted
  `GOTO_TRUE` and left `Temp0` stale, killing every branch of `PedPortal.iff` 4107's walk-to-
  school controller). Implemented as `VMFind5WorstMotives` + a pure `VMTS1MotiveHelper`
  (`FindWorstMotives`), pinned by its one shipped consumer's exact need set. Also added the
  `SpawnDowntownDateOfPersonInTemp0`/`SpawnTakeBackHomeDataOfPersonInTemp0` null-neighbour guard
  (a latent NRE from a malformed slot), and corrected six stale code comments (including the
  call-3 self-loop claim above). Census re-run: 611 sites, only 0/7/8/9 remain zero-site.
- **N2 Auto-follow closure (Simitone).** `AutoFollow.TryQueueRequest` now clears the opposite
  direction (giving `ClearRequest` its first production caller), so a sim can never carry both a
  stale downtown and a fresh take-home request; the panel reports the direction actually queued
  and never strands a sim with both buttons greyed out. A non-positive neighbour id is refused
  rather than written as `65535` (the NRE N1 guarded against engine-side). `AutoFollowTests`
  61 -> 98 cases.
- **N3 Downtown overlay audit (Simitone).** There is **no Downtown car sprite to position** - the
  install ships no car art under `Downtown\` (the traffic is *painted into* `DScreen.bmp`; the
  only animated layer is the sea/jetty `dscreen00-04`). The QA row's "cars missing/mispositioned"
  premise is therefore wrong. Implemented the real overlay (the wave, via `NumberedFrameSeries`,
  stopping at index 4 with the `dscreen05` blank-plate reasoning documented) plus a general
  `OverlaysForMode`/`ForeignFrames` per-view frame-folder filter that drops silently-missing
  frames instead of throwing mid-screen-build. 42 test cases.

Suite 2897 -> **3000**; build + smoke green.

### Wave O - landed 2026-09-28

- **O1 Engine robustness (FreeSO).** Opcode 30 in TS1 mode is **decidably `gosub_found_action`**,
  not the wiki's "syslog" (evidence: 31 sites, all zero-operand autonomy trees; 31 of 32 pair 1:1
  with `find_best_interaction`; the `MyAttrs[6]` handover bracket; **no `.iff` in the install
  ships `STR 308`**, so a syslog would have nothing to print). The registration was moved out of
  the shared block into the two mode branches so the last-wins accident can no longer silently
  disable free will. Also guarded the `0x7FFF`/`0xFFFF` sub-routine sentinel (182 sites, 131 on
  GLOB-less objects) against the null `SemiGlobal` it dereferences. No behaviour change; +20 tests.
- **O2 FreeSO 3D 2.0 (FreeSO).** The "blocked" items were mis-scoped: **the `3DFloorGeometry`
  ceiling branch is already implemented and live**, the 3D **roof is already normal-mapped**, and
  `LightSpecular` is already called by every base 3D pass. The build genuinely does **not**
  compile shaders (`MonoGame.Content.Builder.Task` is commented out; editing a `.fx` is inert),
  so floor normal maps, 3D mesh specular and cube maps stay **hard-blocked** (they need a `.xnb`
  rebuild and, for meshes/cubes, a `tso.files/RC` vertex-format change). Landed instead: a shared
  `ThreeDSpecular` config (`GrassShininess`/`RoofShininess`/`RoofEdgeShininess`/`Ceilings3D`,
  defaults identical to the old literals) and a `GrassPassSelection` that names the bare pass
  indices at 7 call sites. +61 tests.

Suite 3000 -> **3081**; build + smoke green.

---

### Wave P - landed 2026-09-28

- **P1 Localisation sweep completed.** The 44 strings left hardcoded by Wave M were routed
  through `SimitoneStrings` (the auto-follow panel, the in-game settings strip and its status
  lines, the desktop tooltips/floor readout, the CAS field captions, the call-neighbour /
  Makin' Magic / skin-select alerts, the query panel, the job subpanel, and the custom-content
  warning). The shipped English table grew 153 -> **197** entries; logs, exceptions, TS1-owned
  text and format-only placeholders stay untouched. New `UiPanelStringsTests` proves the
  "no table, same words" property on every reachable surface.
- **P2 Picture-in-Picture fixed.** The Wave G3 limitation was **wrong**: the engine already
  binds the MSAA backbuffer as a sprite source (MonoGame 3.8.5.1 resolves it on texture bind),
  so no render-path rewrite was needed. `PPXDepthEngine.CaptureLotSnapshot` now copies the
  backbuffer into a small non-MSAA target through a box-filtered halving chain, and the PIP
  overlay prefers that snapshot over the raw backbuffer - so **PIP works with AA on**. The
  fallback is preserved and the misleading "Live view needs AA off" caption is gone. +132 tests.

---

Suite 3081 -> **3281**; build + smoke green. Version stamped **1.0.0**.

---

## 12. Where this stands (2026-09-28)

The **automatable** roadmap is complete. Every issue #8 bullet is now either implemented,
verified-implemented, or explicitly blocked/human:

- **Implemented this campaign:** Options Menu, Credits, buy/pet/clothing purchase, free will,
  vacation saving, lot Rezone, bulldoze + move-out, multi-slot travel, weather, censor blur,
  fresh-lot controllers, animation-failure reporting, floor-placement fix, multi-slot travel,
  the Neighbourhood Management Page (wired), the virgin-install bootstrap, fame promote/demote,
  Spellbook/Cookbook, Picture-in-Picture, localisation (`SimitoneStrings` +
  `_f900_simitone.cst`), auto-follow (per-sim invite/take-home), **call 3 `PullDownTaxiDialog`**,
  **call 38 `GetTokensFromString`**, **opcode 33 `find 5 worst motives`**, `ChildToAdult`/
  `PetToAdult`, and the engine hardening guards.
- **Disassembly-verified already-working** (now QA rows, not work): Vacation Score / souvenirs /
  hotel booking, StudioTown fame objects and the obsessed fan, Unleashed pet shows and training,
  careers, child/school/military school, death/ghost, the relationship chain, boat rental,
  object-pet lifecycle, MagiCoin, object/skin CC.
- **Hard-blocked / out of scope:** the `3D 2.0` shader items (no content-pipeline shader compile;
  mesh/cube maps need a `tso.files/RC` vertex-format change), call 35 `TakeTaxiHook` (no data
  records trip ownership — a documented permanent no-op), "pyrite autonomy" (not in the data),
  studio tours at home (server-side), the cross-system install file-transfer tool (out of scope),
  and the website (product decision).
- **Release readiness:** version stamped **1.0.0** (`Simitone.Windows.csproj`, read by the
  update checker), build green, **3,281** tests green, no rogue processes. The
  `TheGameProjectStampsARealVersion` guard was updated: its old "must not be 1.0.0" check existed
  only because 1.0.0 was the SDK default while the releases were 0.x; the presence-and-parse
  guards it also made are kept. (A stale `obj` file list from the finished mesh generator caused
  one transient `MSB3030`; it cleared on the next build.)
- **Remaining:** the human playtest matrix (`docs/PHASE3_TEST_MATRIX.md` §11, corrected in §11.8,
  plus `docs/REMAINING_ENGINE_PLAN.md` §6.1). These cannot be automated — mouse/keyboard/game-
  launch automation is prohibited — and the failures they surface become the next work items.

---

## 13. New work items (player reports + investigations, 2026-09-28)

Reported from a live 1.0.0 play session, plus three read-only investigations and the Scope A
project. These add to the backlog; they supersede nothing.

| # | Item | Owner | Size | Notes |
| --- | --- | --- | --- | --- |
| 1 | **Neighbourhood Management panel theming** | S | M | The page draws `UIButton`s over runtime-rasterised `GenerateRoundedRectangle` plates. It must use the game's themed `UIBigButton` (the desktop/mobile-art button the Options menu uses) so it matches the rest of the game, especially the buttons. |
| 2 | **Plumbbob on hover** | S | S | Hovering an avatar shows the auto-follow panel, which is inert/uninformative outside Downtown and gives the player nothing to do. Decide the exposure: show it only when actionable, and never as a dead overlay. |
| 3 | **Remove the Credits button from the Options strip** | S | XS | It is boilerplate, reuses the "general" glyph and overlaps. Remove it from the settings menu. |
| 4 | **Magic cauldron ingredient inventory** | F (+S) | M | The shipped cauldron dialog uses the TS1 `$TokenNameLocal:N$` substitution token, which `VMDialogHandler` does not implement, so it renders literally instead of listing the ingredients the sim holds. |
| 5 | **Lighting system: godrays + time/weather sun** | F (+S) | L | New: sun placement from the world clock, weather coupling, volumetric godrays, focused on 3D. Behind a config toggle, default-preserving, pure time→sun math. |
| 6 | **Missing Sims 1 features assessment** | read-only | M | Deliverable `docs/SIMS1_FEATURE_GAPS.md`. |
| 7 | **FreeSO online-only features, backportable** | read-only | M | Deliverable `docs/FREESO_ONLINE_BACKPORT.md`. |
| 8 | **Mods folder inventory** | read-only | M | The install's `Downloads\` holds 70 `.far` lots; deliverable `docs/MODS_INVENTORY.md` — mechanics worth adopting, **not implemented**. |
| 9 | **Scope A connected neighbourhood** | S (+F) | XL | Implement `docs/NEIGHBOURHOOD_SCOPE_A_PLAN.md` (M0–M5), with the Scope B prep seams from its §15. |

### Wave Q — in progress

- **Q1** Management-panel theming (`UINeighbourhoodManagementPanel.cs`).
- **Q2** Plumbbob/hover exposure (`UILotControl.cs`, `PlumbbobMotive.cs`).
- **Q3** Remove the Credits button (`UIMainPanel.cs`).
- **Q4** Cauldron ingredient inventory (`VMDialogHandler.cs` + the magic token table).
- **Q5/Q6/Q7** The three assessments (read-only; one new doc each).
- **Q8** Scope A **M1** — `NeighbourhoodLayout` + builder + sidecar store (pure).
- **Q9** Scope A **M2** — map generator (`RoadGraph`, `TerrainFill`, `TerrainSeamSolver`), decoupled from M1 behind a minimal input struct.
- **Q10** Lighting system (FreeSO `tso.world`).

M3–M5 (renderer, in-lot surroundings, lifecycle) follow in later waves, after M0/M1/M2 land.

### Wave Q - landed 2026-09-28

- **Q1 Management-panel theming.** The page now uses the game's `UIBigButton` (the desktop/mobile
  art the Options/confirm dialogs use) instead of runtime-rasterised plates; the action set is a
  single enum+table the tests read, and the touch page's ceiling grew (940) because the themed
  plate is 100px tall. No red colourway exists, so Delete is distinguished by order + confirm.
- **Q2 Hover exposure.** The overlay the player saw was the **auto-follow panel**, not the
  plumbbob. It now appears only when it has something to press (`AutoFollow.Offer.IsShown`), so
  hovering a normal sim shows nothing. The plumbbob is a standing fact about the selected sim and
  was left alone (documented).
- **Q3 Credits button removed** from the Options strip; the free-will slot is now derived from the
  button count, and the strip fits the 800px minimum window.
- **Q4 Cauldron ingredients.** `$TokenNameLocal:N$` is a shipped TS1 token `VMDialogHandler` did
  not implement; `N` is a *local slot* holding a magic-token id, and the name comes from
  `MagicMasterSpells.iff` `STR 0x0002`. New `VMTS1MagicTokenHelper.NameFor` + the parser case.
- **Q5/Q6/Q7 assessments** → `docs/SIMS1_FEATURE_GAPS.md` (the automatable engine side is
  complete; **the real gap is the client**), `docs/FREESO_ONLINE_BACKPORT.md` (the **EOD minigame
  framework** is one commented-out line from working, with a TS1 economy already wired), and
  `docs/MODS_INVENTORY.md` (26 mechanics worth adopting, 0 needing new VM surface).
- **Q8 Scope A M1** — `NeighbourhoodLayout`, builder + pure packer, sidecar store, and the Scope B
  prep seams (`ILotContentSource`, `INeighbourhoodWorld`, `INeighbourhoodClock`).
- **Q9 Scope A M2** — `NeighbourhoodMapGenerator` + `RoadGraph` + `TerrainFill` +
  `TerrainSeamSolver`, with field-semantics parity to `CityMapData` and the road nibble encoding
  derived from the shipped art.
- **Q10 Lighting** — `SunLightingModel` (pure time-of-day + weather → sun/sky state, legacy-identical
  by construction), behind a default-off "Dynamic Sun" option. **Godrays are blocked** (no shader
  compile, no scene target/depth mask in `tso.world`) and documented in code, not faked.

Suite 3281 -> **3675**; build + smoke green.

### Wave R - in progress (from the three assessments)

- **R1 Autonomy + engine bugs** (FreeSO `tso.simantics`): `VMFindBestAction` enqueues only the first
  option of a multi-option interaction, the `HappyWeight` curve is parsed then discarded, and
  `VMOnlineJobsCall` dereferences a null `TSOState.JobUI`.
- **R2 EOD minigame framework backport** (FreeSO `tso.client` + `TS1GameScreen`/`UILotControl`).
- **R3 Missing live-mode subpanels** (Simitone): House (chores/hire), Interests, the Fame star
  meter, the career button handler, relationship tab filtering, and the hidden inventory types 2/6.
- **R4 Scope A M3** — the 3D neighbourhood-view backdrop, facades and camera mapping.
- **R5 Options Sound page, extra cheats, freeze-needs, build/buy undo**.
- **R6 Tutorial / Help system** (opcode 34).
- **R7 `tso.content` fixes**: the bare-filename neighbour-save bug and data-driven TS1 motive decay.

### Wave R - landed 2026-09-28

- **R1 Autonomy + engine bugs (FreeSO).** `VMFindBestAction` now queues every option of a
  multi-option interaction (was the first option, N times); the `HappyWeight` curve is applied
  (the retail table is **7 curves, not 9** - Mood and Room are unweighted, which is why a
  positional read had thrown); `VMOnlineJobsCall` no longer derefs a null `TSOState.JobUI`;
  `set_motive_deltas` clears only the named motive. **The `Global[20]` audit was wrong**: a
  census of all 90 `.far`s found 1,613 sites, **zero writes**, and the audit's proposed fix would
  have made ~118 objects self-delete on load. Implemented the mirror policy instead (the engine
  owns bits 1-8; bits 9-16 are object-private), documented with the census.
- **R2 EOD framework backport (FreeSO + Simitone).** The host is wired (the commented-out hook,
  an `ILotControl` refactor, a reflective `EODPanelBridge`, per-panel `try`), 18 of 27 plugins are
  eligible, and it is triple off-safe. **It reports itself off on a stock TS1 install**: the 18
  `.uis` layout scripts ship with TSO, not TS1, and every panel is built from one. Unblocking it
  needs those layouts (asset source), not code.
- **R3 Missing live-mode subpanels (Simitone).** Relationship filtering + draggable bars, the
  career page (the button had no handler), inventory types 2/6 shown with the MagiCoin balance,
  and new House / Interests / Fame panels (House chores and Interests toggling are partial: the
  data lives in lot globals with no client read path). +272 tests.
- **R4 Scope A M3.** **M0 verdict: `CityGeometry` cannot be referenced** - `FSO.UI` and
  `FSO.Client` both declare `UICustomLotControl` (class vs interface) and the alias route breaks
  `EODHost`/the new live panels. Took the plan's named fallback: a Simitone-native
  `NeighbourhoodCityMeshBuilder` fed by the M2 arrays, plus the terrain renderer, foliage,
  facades (keyed by `HouseHash`), the 3D `_3DScene` backdrop, the camera mapping, and an
  Off/2D/3D setting defaulting to 2D. +122 tests.
- **R5 Options ▸ Sound + cheats + freeze-needs.** The four volume sliders now work live (new
  `SoundVolumeSetting`, `SettingType.Slider`, a mute sidecar), the cheat list grew to 11 keywords
  with real hooks, and `freezeneeds` is data-driven through person-data slot 29. Build/buy undo
  was documented out of scope (it must be a net operation, not a placement undo). +168 tests.
- **R6 Tutorial / Help (FreeSO + Simitone).** Opcode 34 is registered and implemented as an
  enable/disable bracket around a type-4 tutorial dialog (`(Target,Variant)` over four values;
  all 58 sites answer through a `253` pointer). **Discovery: `HelpSystem.iff`/`HDHelpSystem.iff`
  are `Global=1`, so they already spawned and their 26 sites were running as a silent no-op**;
  only `Tutorial.iff` (`Global=0`) needed the opt-in trigger. The `TutHigh.bmp` overlay and the
  `Target` selector's meaning are documented, not faked. +40 tests.
- **R7 `tso.content`.** Fixed a latent **destructive** save bug: `SaveNeighbourhood(true)` wrote
  FAR-resident NPCs to a bare filename resolved against the CWD (so the change was dropped *and*
  a same-named file there could be truncated); new `TS1AvatarSavePath` resolves the reader's own
  rule and an avatar write can no longer cost the whole neighbourhood save. TS1 motive decay:
  **blocked** - the audit's premise does not hold (no readable retail constant table), so the
  data path (`TS1GlobalTuning`, BCON-backed) and a fallback were built and the remaining
  `tso.files`/`tso.simantics` step documented.

Suite 3675 -> **4492**; build + smoke green.

### Wave S - landed 2026-09-28

- **S1 Scope A M4.** `SimitoneSurroundings : IRCSurroundings` draws the generated terrain + the
  neighbouring facades around a live lot, reusing the M3 renderer unchanged. Both gates must agree
  (`NeighbourhoodView == 3D` **and** `SurroundingLotMode > 0`); off ⇒ `World.Surroundings` stays
  null and nothing is built. +32 pure tests (they caught an XNA row-vector multiply-order bug that
  put the surround 3× away). The single-slot world cache has **no invalidation** - that is M5's job.
- **S2 Scope A M5.** The invalidation graph: setting off ⇒ 2D and zero work; no/stale sidecar ⇒
  full repack; a lot whose `houseHash` moved ⇒ **that lot only** (rectangles byte-identical for the
  rest, full-repack fallback if the cheap path would overlap). Wired to `NeighSelection`,
  `PlayHouse`, `ExitLot`, `EvictLot`, `ResetHouse`, `RezoneLot`; generation runs on a worker and
  applies on `GameThread.NextUpdate`; the two Options rows (Neighbourhood View, Surrounding Lots)
  are coherent and persist. Logging added. +48 tests. Known gap: the selection panel still calls
  `Generate` synchronously instead of the new cache (a one-line swap).
- **S3 Neighbourhood view features.** Four working lot filters (occupied / vacant / residential /
  community - not retail's thirteen, because a lot *category* is unanswerable with no `OBJT`
  shipped), Next/Previous stepping through selectable lots, an **Add Lot** affordance that computes
  the next free lot number/file and refuses with a documented reason (no safe backend path exists),
  and the **base residential map is reachable again** (view 1, with Unleashed given its own
  switcher button rather than being removed). +116 tests. The service-side delegation this needed
  was applied in housekeeping.
- **S4 Remaining engine opcode.** Opcode 10 is **not** "move forward n tiles" - the 11 sites that
  run are the help system's start/poll service pair, and a `GOTO_FALSE` would be an unescapable
  retry loop. Registered as `help_service_call` (TS1 branch only), always `GOTO_TRUE`; the effect
  is documented, not invented (the work is in the packed `Sims.exe`). Census correction: `HelpSystem`
  has 5 sites, **4 live** (not two). `Ranger*.iff` are the *installer's* per-product resources and
  `Scenario.tds` is a 4096-byte zero placeholder - nothing to implement.
- **S5 EOD panels unblocked.** The TS1 install ships **no `.uis` layouts** (they are a TSO asset),
  so instead of sourcing them a pure generator authors **18 Simitone `.uis` layouts** (grid + TS1
  standard buttons, one Simitone-drawn placeholder PNG, no EA ids), and the framework now returns
  `Start`. Slots and the war game are proven end to end; a test runs all 18 through the *real*
  `UIScriptParser` (a `.uis` that fails to parse is silently ignored). Also fixed two real panel
  bugs (`UIContainer.Size` is always 0, so every panel was centred wrong). +31 tests.

Suite 4492 -> **4754**; build + smoke green.

### Wave T - landed 2026-09-28

- **T1 Scope A polish + roof pitch.** The selection panel now uses the M5 cache instead of
  calling `Generate` synchronously (two subtleties handled: `RefreshNow` does not store, so the
  panel stores; and a cache hit's `Plan.IsCacheHit` is false, so reuse is keyed on reference
  identity). **Per-room roof pitch was disproved, not ported**: TS1 has no roof array, no room
  record, no roof build command and a lot-global level chooser in its own UI - Simitone's one
  global pitch is faithful. +22 tests.
- **T2 EOD crash-hardening.** **37 null-`TSOState` guards** across the EOD plugins (owner checks
  and the `Broken` reads, including a player-mode one that would have made every blackjack betting
  round throw). Owner mode is impossible in TS1 (no owner id, and the stub's money is
  `uint.MaxValue` for non-avatars), so it degrades to player/dealer mode with a once-per-plugin
  reason. Dropped EOD messages are now logged, not silently swallowed. +13 tests.
- **T3 Backport ADAPT rows.** The local tuning path landed: `Content.GlobalTuning` is now non-null
  in TS1 (a new positional-table population path in `tso.files/Tuning.cs` + `TS1GlobalTuning`), plus
  a `Content/tuning.json` loader. **Enabling it exposed a real hazard**: `vm.Tuning` non-null makes
  `VMRelationship` (opcode 26, live in TS1) evaluate `vm.TSOState.PropertyCategory`, which does not
  exist on `VMTS1LotState` - so the payload is exposed as `Content.DynamicTuning`, *not* pushed into
  `vm.Tuning`, until a TS1 lot category exists. Object upgrades = not applicable (no TS1 data path);
  `FSOF` facades = duplicate of the landed facade path. +59 tests.

Suite 4754 -> **4891** (stable across 3 runs); build + smoke green.

### Wave U - landed 2026-09-28

- **U1 TS1 tuning enabled.** TS1 now has a lot category (`VMTS1LotState.PropertyCategory =
  residence`), `VMRelationship` asks it instead of the (absent) `VMTSOLotState` property, and
  `VM.Init` sets `vm.Tuning = Content.DynamicTuning` under `if (TS1)` only (TSO keeps its
  wire-loaded tuning). The TSO `UpdateTuning()` call is deliberately **not** made (it writes
  render state no headless test can verify). +26 tests, including a regression guard that no
  `tso.simantics` line may read `.Tuning` and dereference `TSOState.<member>` in one expression.
- **U2 Stale-comment cleanup.** Corrected the `EODPluginPolicy` owner-mode remarks (and its test),
  and the `AutoFollow` "the engine dereferences and crashes" premises that Wave N/R hardened. No
  executable line changed.

Suite 4891 -> **4917**; app compiles clean. (A full build's copy step is held by the running
`Simitone.exe -3d` playtest, which is the player's, not a build error.)

### Where the automatable work now stands

Every item the player reported in §13 is landed, and the waves above closed the roadmap gaps the
three assessments surfaced. What genuinely remains is **not automatable**:

- **Human playtests** - `docs/PHASE3_TEST_MATRIX.md` §11 + the new features (3D neighbourhood,
  EOD minigames, the new subpanels, the lighting option, PIP at AA, the magic cauldron).
- **Blocked on assets/constants** - TS1 motive-decay constants (no readable retail table;
  `Sims.exe` packed), and the TSO-authored artwork the panels deliberately do not ship.
- **Product decisions** - build/buy undo (must be a net operation), the website.
- **Small knowns** - the EOD CooldownEvent refusal string still names the old reason; a
  `NeighbourhoodLifecycle` cache hit still reads each `HouseNN.iff` (an unexploited optimisation);
  `SurroundingLotMode`/`NeighbourhoodView` have not been looked at on screen.

---

## 14. Player-reported fixes + full code audit (2026-09-28)

### Wave V - landed

- **Defaults flipped** (player request): the **Unleashed** neighbourhood is the residential default
  again (base map still one button away); **Dynamic Sun** defaults on; the **unified 3D
  neighbourhood** defaults to `World3D`; **in-lot surroundings** default on. Every toggle still
  switches, and off/2D still costs nothing.
- **Animal Control no longer wedges Build/Buy** (player report). `BuildBuyEnabled` was a bare bool
  set false by call 10 and only cleared by call 11; an NPC that left, was deleted or was waiting at
  unload never ran the enable, and the flag is marshalled, so one bad save froze build/buy forever.
  It is now **derived from a set of live holders**: call 10 acquires a hold, call 11 releases the
  caller's own, an entity's `Dead` releases its hold, and the flag is recomputed on read/tick/save
  and **not trusted from the marshal on load** (so old stuck saves recover). Both UI gates now
  re-read it per frame.
- **Cancel is immediate** (player report). `VMThread.CancelAction` only dropped an entry when it
  was `Normal` and above the active block, and the client skipped the send for
  `NotifyIdle && priority <= 0` - so a queued parent-idle was unremovable. New `VMCancelPolicy`
  drops a queued entry outright (except parent exit), records a pending request for the running
  action, and resolves it at the top of the next tick; the client sends whenever the entry is
  still queued and marks it cancelled at once. `NonInterruptable` still defers.

### Wave W - the full audit and its fixes

Five read-only audits were produced, each a standing deliverable:

| Doc | Scope | Findings |
| --- | --- | --- |
| `docs/AUDIT_ENGINE.md` | FreeSO SimAntics | 54 |
| `docs/AUDIT_RENDERING_UI.md` | tso.world / tso.client / FSO.UI / tso.common | 72 |
| `docs/AUDIT_SIMITONE_CLIENT.md` | `Client/Simitone/**` | 17 |
| `docs/AUDIT_CONTENT_SAVE.md` | tso.content / tso.files / saves / tests | 42 |
| `docs/AUDIT_BUILD_DEPS.md` | build, packages, hygiene | the build's warning census |

Fixes landed this wave:

- **Save integrity (highest severity).** `SIMI` wrote `MiscIncome` where it read `BillsExpense`
  (silent corruption on every save) and had three disagreeing version rules (a version above the
  stamp shifted globals into the lot value); `Neighborhood.iff` was truncated **in place** with no
  backup; `AtomicFileCommit` was `File.Copy(tmp, path, true)` and so not atomic at all; one
  non-`.iff` file in `Characters/` silently dropped every later character; `FAR3Archive` read the
  decompressed size big-endian and had a process-wide re-entrancy flag a throw could stick;
  `TimedReferenceCache` removed the wrong key and leaked unboundedly; `Decompresser.Compress`
  could not round-trip at all. All fixed with round-trip / damaged-input / atomicity tests.
- **Engine crashes** (E-01..E-07): the multitile filtered-list index, the missing-master
  dereference, the three unguarded `SemiGlobal` copies, six TS1 lot-load lookups, the
  `VMAvatar.ClearSlot` guard, and the `(VMAvatar)` casts on script-controlled registers.
- **Engine correctness**: `IsUserOutOfBounds`'s three inverted returns, `RefreshRoomScore`'s reused
  loop variable, the discarded `Vector3.Transform` in `Combine`, the `% 0xFFFF` "low 16 bits", and
  the `ListenBHAVChanges`/`SuppressBHAVChanges` pair that made `BHAVDirty` unsettable.
- **Rendering/UI**: `Matrix2D.TransformPoint` reading the mutated `x`, dead text-input handling,
  a `SpriteBatch.Begin` sort-mode regression, the buffer pool poisoning itself, `WordWrap`'s
  infinite loop (the audit's own diagnosis was corrected), a texture cache that cleared every frame,
  and multiple leaked `RenderTarget`s/`VertexBuffer`s/viewport state leaks.
- **Simitone client**: the `simitone.ini` **two-writer erase** (toggling one setting deleted the
  other - now one read-merge-write), `AtomicFileCommit` (above), the hand-copied speed map, the
  management page re-parsing `names.json` ~20× per refresh, `BlueprintReset`'s swallow-and-reload,
  `int.Parse` on an unvalidated lot prefix, silent settings-save failures, and two unbounded icon
  caches.
- **Build/hygiene**: the un-ignored 4.1 GB of retail data (C-01), the missing `NOTICE` (H-01), the
  176 fictitious trim/AOT warnings (B-01, root `Directory.Build.targets`), Release-only trim +
  packaging (C-05), `ContinueOnError` on the DLL copies (C-03), a TFM-agnostic trim allowlist
  (C-04), a `Directory.Build.props` that single-sources the duplicated TFMs/packages, `global.json`
  and the CI comment.

Suite 4917 -> **5648**; the app compiles with the warning census down from ~574 unique to the
genuine remainder.

### Remaining after the audit

Not automatable or deliberately deferred, and recorded rather than forced：

- **Risky upgrades**: the SimAntics compiler's `Microsoft.CodeAnalysis.CSharp 3.4.0-beta1`
  (2018 pre-release) and other packages the output trims anyway - reported, not changed.
- **Follow-ups the audit named**: the other `Marshals/` unguarded counts, `SaveHouse`'s
  `FileMode.Create`, the `VMMarshal` hollow-marshal path, and the long tail of lower-severity
  findings in the five audit docs.
- **Human playtests**: the QA matrix, plus the newly default-on 3D neighbourhood/sun to be looked
  at on screen.

---

## 15. Assessment and remaining work (2026-09-28)

### 15.1 Verified state

- `dotnet build` green (0 errors); `dotnet test Simitone.Tests` -> **5,648 passing, 0 failing**.
- Every player report from §13/§14 is implemented: the four default flips, the Animal Control
  build/buy wedge, immediate action cancel, and the whole first round of code-audit fixes.
- The build warning census dropped from ~574 unique to the genuine remainder (the 176 fictitious
  trim/AOT warnings are gone).

### 15.2 Feature completeness

**Gameplay/engine (issue #8)**: complete bar the human/product items in §12. **Client gaps** from
`docs/SIMS1_FEATURE_GAPS.md`: most are now closed.

| Area | State |
| --- | --- |
| Live-mode subpanels (House, Interests, Fame, career, relationship tabs, inventory 2/6 + MagiCoins) | Done |
| Options ▸ Sound, extra cheats, `freezeneeds`, tutorial/Help | Done |
| Neighbourhood view (filters, next/prev, add-lot, base-map reachability) | Done |
| Connected 3D neighbourhood (Scope A M1-M5) + lighting (sun/weather) | Done (default on) |
| EOD minigames | Wired + Simitone-authored layouts; needs a playtest |
| **Build/Buy undo/redo** | **Open - product decision** (must be a net operation, not a placement undo) |
| **House chores / Interests toggling** | **Partial** - the data lives in running lot globals / has no engine path |
| **Website** | **Out of scope** (product) |

### 15.3 Remaining audit backlog (lower severity; the audits are the detail)

The fix waves closed the crash, data-loss and top-correctness findings. What is left, by doc:

| Audit | Remaining |
| --- | --- |
| `AUDIT_ENGINE.md` | E-12, E-14, E-16, E-22 (capture aliasing - needs a lifetime decision), E-29..E-31 (the old `VMTS1Activator` should be deleted or delegate), E-32, E-36, E-39, E-42, E-47..E-51, E-53 |
| `AUDIT_RENDERING_UI.md` | R-18, R-19, R-26 (dialog drag units - needs DPI decision), R-29, R-30, R-31, R-34, R-37..R-39, R-43 (partial - `Removed()` handler nulling would break re-shown dialogs), R-44 (MSDF batching), R-46, R-47, R-49..R-52, R-56, R-58..R-64, R-66, R-71, R-72 |
| `AUDIT_CONTENT_SAVE.md` | `IffFile` unsigned chunk-size wrap, the other `Marshals/` unguarded counts, `SaveHouse` still `FileMode.Create`, and **T-01..T-03** (three tests that cannot fail) + **T-11** (no parser tests) + **T-12** (a live flake) |
| `AUDIT_BUILD_DEPS.md` | B-12 (split the game build from the `-ide` build), A-03/A-05/A-07/A-10 (upstream packages - file as FreeSO issues, do not fork-patch), B-02/B-06/B-08/B-11, H-06, C-07/C-09/C-11, H-08 (`.editorconfig` warning gate), and a CI warning gate + NuGet cache |
| `AUDIT_SIMITONE_CLIENT.md` | the S4 maintenance notes that remain |

### 15.4 Blocked, and why

- **TS1 motive-decay constants** - no readable retail table; `Sims.exe` is packed.
- **EOD panels' art** - the retail `.uis`/art is a TSO asset and is not shippable; Simitone authors
  its own layouts instead.
- **FreeSO 2D/3D 2.0 shader items** (floor normal maps, mesh specular, cube maps) - the build does
  not compile shaders and the mesh/cube path needs a `tso.files/RC` vertex-format change.
- **Call 35 `TakeTaxiHook`** - no shipped chunk records trip ownership; a documented permanent no-op.
- **FreeSO upstream fixes** (Roslyn 3.4-beta, TLS 1.0, `UseMvc`) - vendored submodule; file
  upstream rather than fork-patch.

### 15.5 Recommended next waves

| Wave | Contents |
| --- | --- |
| **X** | The content/save long tail: `IffFile` bounds, the other marshal counts, `SaveHouse` atomic, delete the three cannot-fail tests, add parser tests (T-11), fix the flake (T-12). |
| **Y** | The rendering long tail, plus the two decisions the audit flagged (R-26 dialog-drag units/DPI, R-44 MSDF batching) and the engine `VMTS1Activator` deletion (E-29..E-31). |
| **Z** | Build structural: split the game build from the `-ide` build (B-12), an `.editorconfig` warning gate (H-08), the NU1701 cleanup, CI cache. |
| **Handover** | The human QA matrix (`docs/PHASE3_TEST_MATRIX.md` §11) and the newly default-on 3D neighbourhood/sun looked at on screen. |

---

## 16. Wave X - audit long tail, missing features, build structural (2026-09-28)

Nine file-disjoint agents. Suite 5648 -> **6283**, build green and stable.

### Features implemented
- **Build/Buy undo + redo** (the roadmap's one open build feature). Net operations only, driven
  through the engine's own commands: Place / Sell-back / Move, each recording what the engine
  actually charged so the inverse is exact. Walls/floors/terrain and inventory-sourced objects are
  refused up front (a tooltip says so). `Utils/BuildUndoStack` + `BuildUndoPolicy` (78 tests),
  buttons + `Ctrl+Z`/`Ctrl+Y`. Objects are re-resolved by guid + tile (the VM never reuses ids).
- **House chores / hire and Interests** (the two partial panels). **Chores are provably absent**
  from the shipped data (0 hits across all 11 `.far`s; never saved) - the panel now says the real
  reason. **Hire** reads the live `NPCController` (`Global=1`) attributes 1/2/3, the same data the
  phone's `set_to_next` uses, instead of the dead person-data tickers. **Interests** were on the
  wrong scale (0-1000, not 0-10) with **15** slots, not 8; toggling is a real `SetPersonData` write
  the scripts honour and that persists.

### Audit long tail closed
- **Engine**: the entity/model capture-aliasing decision (deep copy, made self-enforcing via an
  interface `Snapshot()`), the dead `is VMAvatar` validator guard, the motive-rate clamp and
  out-of-range refusal, motive-array flooring, the **platform-state capture was a torn read** under
  `Task.Run` (now a byte round-trip snapshot), the room constant, and the `VMTS1Activator` split
  (E-29..E-31) with the old copy brought back in line.
- **`tso.world`**: scroll-sign/projection single-sourced, sprite-group partitioning bounded, camera
  transition gate, grass-mask builder, particle billboard inverses, effect reload reached live
  renderers, thumbnail `try/finally`, and ~20 more (R-18..R-72).
- **UI framework**: the DPI drag clamp (R-26), event-subscription de-duplication (R-43), and the
  per-frame MSDF allocations removed (R-44 - per-frame batching is incompatible with tree-order UI,
  documented).
- **Content/save**: `IffFile` size-wrap and seek-backwards spin, `SaveHouse` atomic + backup, every
  remaining marshal count bounded, DBPF/PIFF/`IoBuffer` bounds, the **three tests that could not
  fail rewritten**, real parser tests added (T-11), and the flake fixed and generalised behind a
  `GlobalMutationGuardTests` sweep.
- **Simitone client**: a real `DialogService` latch/dialog divergence (a modal could be shown with
  the "is a dialog up?" question answering false), install-path canonicalisation, culture coupling
  made checkable, and the `public static` tables made `readonly`.
- **Build**: the game build **split from the `-ide` closure** (24 -> 15 projects, **-65 % warnings**,
  712 -> 247 unique), a root `.editorconfig` with a real error set, a NuGet cache + warning budget in
  CI, and solution-configuration pruning (1045 -> ~140 lines).

### Remaining
The less-severe tail of all five audit docs, the items the docs mark as needing a product decision
(the `ObjectComponent` dynamic-to-static transition, returning a copy from `GetLotThumb`), and the
upstream FreeSO package upgrades. Wave Y is a completion sweep of the five audit docs.

---

## 17. Startup-crash fix and two runtime defects (2026-09-29)

A play test of the Release build failed at startup:

```
FileNotFoundException: Could not find file ...\net10.0-windows\Content\uigraphics\upgrade_star.png
  at FSO.Common.Utils.CurLoader.LoadUpgradeCursors   (CurLoader.cs:21)
  at CursorManager.Init                              (CursorManager.cs:122)
  at Simitone.Client.SimitoneGame.Initialize         (SimitoneGame.cs:171)
```

### Root causes and fixes

1. **The B-12 `-ide` build split dropped `FSO.Content.TSO`'s content** (284 MB, 1,405 files), and
   `CursorManager.Init` loaded the **TSO-only** upgrade-star cursors **even in TS1 mode** - where
   there are no upgradeable objects to show them on. Fixed by **not loading upgrade cursors for
   TS1** (`CursorManager.Init`), which removes the dependency entirely rather than restoring TSO
   content the single-player game does not need.
2. **`CurLoader.LoadUpgradeCursors` opened a hardcoded CWD-relative path** (`Content/uigraphics/...`)
   and threw on a missing file. It now resolves next to the executable, treats the star as
   optional (a plain cursor of the right size when absent), and never throws - so a missing overlay
   can never take the game down at startup again.
3. **`DGRP3DMesh.CompleteFSOMLoad` used `Dictionary.Add` for a texture key** that the live path
   (`:798`) and dynamic path (`:427`) already insert with an indexer. A group holding two geoms that
   resolve to the same texture instance made `Add` throw `ArgumentException`, which **aborted the
   whole `.fsom` load and silently fell back to the archive for every affected mesh** - the log
   showed thousands of `RCMeshProvider: replacement mesh ... could not be read` warnings. Now
   consistent with the other two call sites.
4. **`_3DLayer.Update` enumerated `Scenes`/`External` with `foreach` while a scene's `Update` can
   add or remove one** (`UIPieMenu` adds/removes its 3D head scene as the menu opens and closes),
   throwing `InvalidOperationException: Collection was modified` mid-frame. All four walks
   (`Update`/`PreDraw`/`Draw`/`Initialize`) are now by index with the count re-read.
5. **`FAR1Provider`/`FileProvider` logged one warning per duplicate entry name** - 5,813 of the
   5,927 startup log lines. Duplicates are normal in a TS1 install (every expansion re-ships
   `Sound.far`, the `_` markers, base meshes), so both now count them and report **once**.

### Verification

Smoke-launched the Release build: **alive after 30 s, 0 ERROR, 110 warnings** (from 5,813; the two
collision summaries plus benign "no object with ID" content notes), **0 replacement-mesh failures**
(from thousands). Full suite **6,462 passing**. This is the first end-to-end startup smoke test the
`-ide` split received; the build-time invariant it shipped with could not catch a missing runtime
asset, which is exactly the gap.

---

## 18. Regression fix wave (2026-09-29)

Player reports: cooked food lost its interactions ("the eat action is completely broken"), object-on-object
placement (coffeemaker on dishwasher) "only sometimes works", and the instruction to **keep every TSO
element**. A read-only regression review (`docs/REGRESSION_REVIEW_INTERACTIONS.md`) then found six
candidate causes; all were addressed.

### TSO content restored
The B-12 `-ide` split defaulted to `BuildIde=false`, which dropped `FSO.Content.TSO` and its **284 MB /
1,405 files** of content. `Directory.Build.props` now defaults `BuildIde=true` again, so the full closure
and its content ship; the fast game-only build is still opt-in with `-p:BuildIde=false`. Verified in the
output: 451 TSO objects, 136 patches, `upgrades.json`, `FSODataDefinition.dat`, ... all present.

### "Eat is broken" - the interaction number was truncated to 8 bits
`TTABInteraction.TTAIndex` is a `uint`, but the pie menu carried the id as a **`byte`**
(`VMEntity.cs:1120`, `UIPieMenuItem.ID`) and the wire command as a `ushort`, while `VMEntity.GetAction`
looked it up as a `uint`. Interaction 300 arrived as `300 & 0xFF` = 44 - a **different interaction or
none**. Cooked-dinner food interactions sit high in the tree table, which is why that object in
particular went dead. This is the exact defect the E-25 audit fixed on the *callback* path; it was left
on the path the player actually uses. Fixed with a pure `InteractionId.Resolve` that recovers the real
number from `Entry.TTAIndex` and clamps to the wire width; the pie item id widened; a refusal now logs.
Also: `push_interaction`'s null guard returned `GOTO_TRUE` where it documents itself as matching the
`action == null` case (`GOTO_FALSE`).
Regressions found alongside it and fixed: **`VMCancelPolicy` hard-aborted a parent chain parked in Allow
Push with no route** (the eat chain's shape) - now `LetTreeNotice` for that case, with `AbortNow` narrowed
and the object released before the unwind; **`PortalTurns` was carrying two meanings**, so a route that
crossed a doorway without turning lost its entry-direction pin (split into `PortalTurns` /
`PortalTurnsActual`).

### "Object on object only sometimes works"
- **`EntityComponent.UnmoddedPosition`'s setter latched `UnmoddedDraw = true`**, so the thumbnail borrow
  restored the flag and then immediately re-raised it. Any object that had been through a thumbnail (the
  lot's own objects, via the query/pickup panels) reported its `Position` **without terrain altitude for
  the rest of the process**, so a container put its contents a terrain height away from the surface and
  the hit box disagreed - only in 3D, only on sloped ground, only after the object had been looked at.
  That is "only sometimes". Fixed with an explicit `SetUnmoddedPosition`.
- **`VMGameObject.GetSlotHeight` answered `-1`** for a slot the container *has* but whose `SLOT` chunk
  does not describe; `1 << (height - 1) & mask` then never matches and a legal placement is refused.
  Now falls back to the container's described surface height.
- **`VMMultitileGroup.Combine`** inverse-rotated the merged offset but omitted the `+ leadOff` term, so a
  merged tile landed on the wrong tile for any non-north base, and the donor group's `BaseObject` became
  null. Placement and `ChangePosition`/`SetVisualPosition` now share one `ForwardRotate`.
- **Contained-object altitude during snap interpolation** and **`UIContainer.Remove` nulling `Parent`**
  (which broke the pie menu's remove-and-rebuild) were made structural - one `SlotOffset` join, and
  `Remove` no longer touches `Parent`; `GetChildrenSafe` allocates once per version instead of four
  arrays per frame.

### 3D neighbourhood
- **Facade NRE fixed** - `LotFacadeGenerator.GenerateRoof` dereferenced `MeshRectData(storey)`, which
  returns **null for a storey the roof component never meshed**, and a TS1 house only meshes storeys with
  indoor rooms under them. This was **every ordinary house**, not just one lot; the lot view's own caller
  already guarded it. The roof is now a skippable stage (walls+floor still build), `RoofTexture` is read
  after `RegenRoof`, and each pass is named on its own.
- **In-lot surroundings culled** - they were submitting the **whole 512x512 map (275,902 triangles)**
  every frame; now a 40-tile region with a hole where the player's lot is (~33,000), with the fog bound
  to the region and the facade queue filtered to the neighbours in range.
- **Roads actually connect** - the old grid could never land in a gap (the packer's pitch vs a fixed
  8-tile grid), so 12 of 41 lots had no entrance and the network was invisible BFS repair stubs. Streets
  now come from the free gaps between lots: 13 full-length runs, every lot reachable, 0 repairs.
- **Trees** - the 4,096-tree budget was spent in the map's first two rows (off-screen) and snow models
  were off by one (`cactus4.obj` does not ship). Budget now spread over the drawn world, model names an
  explicit 18-entry table.
- **The painted map is the guaranteed backdrop** - it is only swapped out once the generated world has
  demonstrably drawn something, and any later failure puts it straight back the same frame.

Suite 6462 -> **6776**; build green. Verified by smoke launch: **alive, 0 ERROR, 28 warnings**, and no
facade failures.

---

## 19. 3D neighbourhood: the real fixes (2026-09-29)

Player report: "The 3d neighborhood still does not work at all. The game freezes and then shows a
blank screen after a long time." Both halves were real, and neither was where the earlier waves
were looking.

### The freeze
A **single lot facade took ~5 s** to build, on the game thread, and the per-frame budget could not
help because one build is orders of magnitude over it: the log showed an 11-second gap in which
**two** facades were built. Now: the stage cost is measured and logged per lot; the budget builds
**one per frame with a bounded cooldown** and never stops for the visit; the cross-visit cache
returns meshes in O(1); a lot whose `SIMI` size is 0/1 is refused in microseconds before any work
(it was asking D3D for a zero-pixel render target - the `E_INVALIDARG` lots); and the wall pass is
much cheaper (per-cell geometry rather than the whole storey per cell, lighting memoised per
(normal, storey), one restore-generate, and the atlas at half resolution). Generation itself is now
**non-blocking**: `RefreshNow` is a schedule (`UseCache` / `JoinRefresh` / `StartRefresh`) and the
world is applied on `GameThread.NextUpdate`. Measured: sidecar 22 ms, lot reads 345 ms, generation
177 ms.

### The blank screen
Four independent defects, each of which alone blanks the view:
1. **The terrain wrote opacity into the shader's *transparency* slot** (`FadeAlpha` was `1 - outside`
   where the shader wants `outside`), so every ground and road fragment blended to nothing. The
   health check counted the mesh's triangles, so it happily reported 315,424 while the screen
   stayed at the clear colour.
2. **The shader pass was never applied.** `BeginCity` returns a pass; only `EffectPass.Apply()`
   binds a shader, and the renderer only read it - so the *previous* draw's shader was still bound
   and D3D rejected the input layout (`InputLayoutCache.GetOrCreate`).
3. **`MV` was the view-projection**, so the fog's `length(vPos)/FogMaxDist` was measured in clip
   space and the whole map rendered as one flat sheet of fog colour.
4. **The chunk cull was never called and framed the map's side, not its diagonal**, so a
   full-screen view culled everything.
Also fixed: the facade surfaces were uploaded as `VertexPositionTexture` for a shader that reads a
normal (`TEXCOORD1`), so the facade draw threw even with the pass applied; `DrawRanges` passed
`StartIndex` as `baseVertex`; and the facade placement "IndexOutOfRange" was a **stopwatch
off-by-one** - `Assemble` is stage 8 of an 8-slot array, so every facade that got as far as being
placed was discarded by its own timer.

### The guard
The painted map is now the **guaranteed** backdrop: `Neighbourhood3DView` reports the triangles it
actually *submitted*, and the panel only hides the map after **3 consecutive frames** have drawn
geometry. Any later failure, a device reset, or a frame that submits nothing puts the map straight
back. A blank screen is impossible even if a render bug remains - which is exactly what turned a
one-line fix into a diagnosis instead of another blank report.

### Result (verified by smoke launch)
`38 of 41` houses built and drawn, `60,660` terrain triangles in 3 draw calls at the overview LOD,
4,046 trees, **0 errors**, and the world promotes itself and stays up. Suite 6776 -> **6992**.

---

## 20. Lot-entry crash, overlay and stall (2026-09-29)

Player report: "stuck 20s in the final loading stage, then fades into the static 2d background ... the
same cutout 2d houses without background", and "when entering a lot the surrounding lots are rendered
very crudely ... while an immovable overlay map blocks the whole screen". Three real bugs.

### The crash: `VMAvatar.Tick` dereferenced a null animation
`state.Anim` was null for a sim whose animation name resolved to nothing (a save referencing custom
content that is no longer installed). `Animator.SilentFrameProgress` reads only the animation, so the
NRE was on the saved-but-unresolved state. Fixed: unresolved states are **dropped at load** (not
skipped), an avatar with no appearance skips the animation update, saves write the requested name
rather than throwing on a null, and each distinct missing file is reported **once**. The sim's tick
still runs.

### The immovable overlay: the neighbourhood world outlived its panel
`PlayHouse` removed the panel from the screen and then called `InvalidateGeneratedWorld()`, which
detached and **re-attached** a fresh `Neighbourhood3DView` into `GameFacade.Scenes` - a different
tree from the screen's children, with nothing left to remove it. So the whole map (38 facades,
315k triangles) kept drawing over the lot. Fixed: `InitializeLot` - the one path every lot route
goes through - now **suspends** the neighbourhood (detach + a latch that refuses to re-attach), and
no refresh runs while a lot is loaded. Entering a lot also no longer discards the cached world, so
the lot's surroundings can reuse it.

### The 20 s stall: the surroundings were generated on the game thread
`AttachSurroundings` ran `NeighbourhoodMapGenerator.Generate` (262,144 tiles) synchronously inside
`InitializeLot`, before the lot's first frame. It now attaches from the lifecycle's worker: the lot
appears immediately and the horizon fills in.

### The "crude cutout" surroundings
Three fixes: the crop cut the player's own rectangle out of the terrain so the ground stopped dead at
the property line - it now keeps the property (the lot view draws its own ground there) and cuts a
**bank** of the border ring's texture up to the generated ground, on every side (0 cost on flat
ground); the **lot the player is standing in** was in the neighbour facade queue (a whole World+VM to
rebuild the house visible through the window) and is now excluded; and the placement of a facade
onto its own lot rectangle, at the lot-view scale and on the ground beneath it, is now asserted
rather than assumed.

### Known limits, stated
- The in-lot surroundings are drawn only in **3D camera mode**: the engine's `WorldStatic.DrawBg`
  only calls the `IRCSurroundings` hook when the sky dome is drawn, so in 2D the lot still has no
  horizon. That needs the engine hook (or a 2D path).
- A facade drawn "without a roof or the wrong storey" lives in `NeighbourhoodFacadeBuilder`
  (`RoofGeometry` is null for a house with no meshed storey) - the facade internals still want a look.
- The neighbourhood view submitted 315,424 triangles in this build rather than the LOD's 60,660, so
  the LOD selection is not engaging at the overview zoom - a perf item.

Suite 6992 -> **7030**; build green; smoke launch clean (0 errors, no crash).

---

## 21. Rollback: the "cancel is immediate" feature (2026-09-29)

Player: "Actions are still deeply broken. Roll back the actions functions to before the immediate
cancel changes." Done.

### Removed (FreeSO, restored to the pinned `7e76c19`)
- `VMCancelPolicy.cs` **deleted**.
- `VMThread`: the `PendingCancel` field, `ResolvePendingCancel()`, `ActiveActionTreeObservesCancel()`,
  `IsIdleForInput(...)`, the `CancelAction(ushort, bool)` pair and its `DecideDisposition`/
  `RescheduleInterrupt`, the `ResolvePendingCancel()` call at the top of `Tick`, and the
  `AbortNow -> SweepCancelledParentIdles -> AbortCurrentInteraction` hand-unwind path.
- `CancelAction(ushort)` and `AbortCurrentInteraction()` restored **verbatim** from the pinned
  commit (the inline parent-idle sweep, `canQueueSkip`, `MustRun`/`Normal`/`FSODirectControl`,
  `InteractionCanceled` + `Priority = 0`).

### Restored (Simitone)
- `UIInteractionQueue.InteractionClicked` sent the cancel only when the engine had **not** already
  handled it (`!(NotifyIdle && priority <= 0)`); the wave's immediate `MarkCancelled` on click is
  gone, so the red X is drawn from the engine's `NotifyIdle` again. The gate now lives in the
  testable `InteractionQueueLayout.OnEntryClicked` rather than inlined twice.

### Kept (other fixes in the same files)
`RunInMyStack`'s `finally` (E-50), `SizeArgs` (E-24), the empty-child-bhav guard, the dead
`hitParentEnd` removal (E-51), `EndCurrentInteraction`'s non-avatar guard (E-16), the `CA2200`
rethrews, and the `DisplaySlot` display rule. `git diff 7e76c19 -- VMThread.cs` now contains only
those.

### Tests
Deleted `VMCancelPolicyTests` (34); preserved its one non-policy case as
`VMNetInteractionCancelCmdTests`; rewrote the cancel half of `InteractionQueueLayoutTests` to pin the
**original** gate (including that a *flagged but still-prioritised* entry is still cancellable).

Suite 7086(measured 7090) -> **7058**.

### Result
Build green, **7058 tests passing**, and a 60-second smoke run stayed **alive with 0 errors** and the
3D neighbourhood working (38 facades). Notably, the pre-rollback build had been **exiting cleanly
~30 s after entering a lot** with no logged error; that is gone.
