# Retail *The Sims 1* feature gaps in Simitone / FreeSO

Read-only assessment, 2026-09-28. Deliverable for `docs/ADDON_PROGRESSION_PLAN.md` §13 item 6.

**Nothing outside this file was created, edited or deleted.** No UI automation of any kind was
used, no game was launched, no process was killed. Evidence is: static reads of
`FreeSO/TSOClient/**` and `Client/Simitone/**`, static reads of the four existing audits, and
static reads / disassembly of the retail install at `D:\Program Files\Sims` (read-only) using the
throwaway disassembler at `%LOCALAPPDATA%\Temp\opencode\bhavscan2` (rebuilt, not modified).

---

## 0. Method, and what "retail" means here

### 0.1 The comparison set

| Source | Where | Used for |
|---|---|---|
| Base game + Deluxe | `GameData\Global\Global.far`, `GameData\Objects\Objects.far`, `Deluxe\Deluxe.far` | core mechanics |
| Downtown | `ExpansionPack\ExpansionPack.far` | |
| Hot Date | `ExpansionPack2\ExpansionPack2.far` | |
| Unleashed Pets | `ExpansionPack3\ExpansionPack3.far` | |
| Vacation | `ExpansionPack4\ExpansionPack4.far` | |
| Superstar | `ExpansionPack6\ExpansionPack6.far` | |
| Makin' Magic | `ExpansionPack7\ExpansionPack7.far` | |
| shared (House Party etc.) | `ExpansionShared\ExpansionShared.far` | |
| art | `UIGraphics\UIGraphics.far` | **the single most useful retail-side evidence source for UI features** — see §0.3 |
| neighbourhood data | `UserData\`, `Template*` | |

The 70 `Downloads\*\*.far` Maxis/3rd-party custom-content lots are **not** treated as retail
feature surface (they are CC; `docs/ADDON_PROGRESSION_PLAN.md` §13 item 8 inventories them).

### 0.2 What could *not* be enumerated from this install

This is the most important honesty caveat in the document:

* **`Sims.exe` is packed.** 34.6 % printable bytes and **zero** hits for `Neighborhood`,
  `Buy Mode`, `Live Mode`, `Rezone`, `Bulldoze`, `Maxis`, `Preferences`. Every hard-coded TS1 UI
  menu, the cheat string table and the neighbourhood-view chrome live there. **Everything marked
  `[A]` below is reconstructed from published descriptions of retail TS1, not read out of the
  binary.**
* No cheat keyword (`motherlode`, `klapaucius`, `rosebud`, `moveobjects`, `killskills`,
  `killtraits`, `setallmotives`, `allmotives`, `setneeds`, `sethours`, `setworks`, `setschool`,
  `sethistory`, `invisible`, `noexit`, `expand`) appears in **any** shipped `.iff`, in
  `GameData\UIText.iff`, or in `Sims.exe`. The retail cheat list is therefore **not verifiable
  from this install** and the cheat gap below is stated against the published list only.
* `GameData\Ranger*.iff` (9 files: `Ranger`, `Ranger2`…`Ranger8`, `RangerD` — one per
  neighbourhood slot plus a Deluxe variant) and `GameData\Scenario.tds` are **not read by any
  part of the tree** (0 hits for `Ranger`; 0 for `Scenario` outside the word "scenario map" in a
  test comment). I could not identify them: `Ranger.iff` is a 643-byte IFF containing two
  obfuscated `STR#` tables (single-byte-XOR ruled out), `Ranger2..8` are 7 338 bytes each.
  **Marked `[U]`.**
* `GameData\log.cht` (15 bytes, `sim_log begin`) and `stalk.ini` are also unread.
* There is **no `OBJT` chunk anywhere in the install** (already established in
  `docs/REMAINING_ENGINE_PLAN.md` §2.7), so "which object ids are on this lot" cannot be
  answered from shipped data.

### 0.3 The decisive trick: retail UI *art* is the feature inventory

`UIGraphics.far` ships the artwork for **every** TS1 screen, including screens with no script at
all. A missing `cpanel\Buttons\*.bmp` or `NghUI\*.bmp` with no Simitone counterpart is hard
evidence of a missing feature, and it is checkable without running anything. This document leans
on it heavily — e.g. `cpanel\Buttons\House.bmp` + `Housebars.bmp` + `cpanel\HouseSubBars.BMP` +
`HouseSubBarsOff.BMP` (the House/chores subpanel) and the 20 `cpanel\Buttons\Interest*.bmp`
buttons (the Interests subpanel) both have **no** counterpart in either tree.

### 0.4 Legend

**Verification level**

| | |
|---|---|
| `[V]` | verified in this session — a `file:line` or a disassembly count is cited |
| `[V-]` | verified code path exists but runtime behaviour was not observed (no game launch) |
| `[A]` | assumed from published descriptions of retail TS1; not enumerable from this install |
| `[U]` | unverifiable / identified-but-unknown |

**Owner** — the brief did not define S/V/F, so this document uses:

| | |
|---|---|
| **S** | `Client/Simitone/**` — Simitone's own client, UI panels and services |
| **V** | FreeSO **runtime vertical** the client inherits rather than forks: `tso.simantics`, `tso.client`, `tso.world`, `tso.sound`, `tso.content`, `FSO.UI` |
| **F** | FreeSO **format/content foundation**: `tso.files` codecs, the R/C mesh+texture pipeline, TS1 IFF providers, the content pipeline |

**Effort** — `XS` (<½ d) · `S` (~1 d) · `M` (2–4 d) · `L` (1–2 w) · `XL` (>2 w). "d" = one
engineer-day of implementation **plus** the headless tests this project expects.

---

## 1. Headline

The previous campaign's central claim — that the **SimAntics** side of TS1 is essentially
complete and only the UI was missing — is **correct and I re-verified it independently**:
a full opcode census over all 11 retail `.far`s finds only **three** opcodes below 256 that
`VMContext.cs` does not register (10, 34, and 33 which has since landed), and `invoke_plugin`
(62) has **0** call sites. Where the retail scripts run, they run.

**The real gap is not the VM. It is the client.** Concretely:

| # | Gap | Size | Verdict |
|---|---|---|---|
| **1** | **Hot Date's EOD minigames are structurally unreachable** — opcode 62 `invoke_plugin` has 0 sites and no EOD semi-global IFF ships, so the whole 30-handler `VMEODServer` stack is dead code for TS1 data | S (to state) / L (to restore) | `[V]` |
| **2** | **Four of the eight retail live-mode subpanels are missing** — House (chores/hire), Interests (20 Hot Date interests), Fame (star meter), and the radio/music panel | XL | `[V]` |
| **3** | **No undo/redo in build or buy mode** (retail ships `Undo.BMP`/`Redo.bmp` in the cpanel) | M | `[V]` |
| **4** | **Options ▸ Sound is an empty placeholder** — the four volume sliders are set once at startup from the ini and are unreachable in-game | S | `[V]` |
| **5** | **The tutorial and the whole Help system are absent**, including two of the three unregistered opcodes | L | `[V]` |
| **6** | **The cheat surface is 5 commands** vs retail's ~14 | M | `[V]` for ours, `[A]` for retail's list |
| **7** | **Relationship panel tabs don't filter and the bars can't be dragged** | M | `[V]` |
| **8** | **The career panel's "Career" button is dead art** — no click handler | M | `[V]` |
| **9** | **Neighbourhood view: no lot filters, no add-lot, no Next/Previous, base map unreachable** | L | `[V]` for ours, `[A]` for retail's set |
| **10** | **Autonomy: `HappyWeight` curves are never applied** (all nine motive weights hard-wired to 1.0) and `VMFindBestAction` has a real `Add(first)` bug inside a `foreach` | S + XS | `[V]` |
| **11** | **Per-room roof pitch and pattern are absent** (one global pitch only) | M | `[V]` |
| **12** | **Vacation souvenirs/prize tokens and inventory type 2 are permanently hidden** in the inventory panel | XS | `[V]` |
| **13** | **No save/load of the character IFF for a dirty avatar uses a valid path** — a latent CWD-file-writing defect on every lot save | XS | `[V-]` |
| **14** | Group interactions, phone book, photo camera/album, neighbourhood HTML export, and 8 gameplay option checkboxes (auto-snap, auto-centre, char detail, transparency, quick tips, sim-in-background, music/SFX toggles) — **all 0 hits in both trees** | L each | `[V]` for ours, `[A]` for retail |

---

## 2. Scoreboard

"Retail UI surface" = the number of distinct retail screens/panels I can evidence from
`UIGraphics.far` art for which Simitone has a counterpart.

| Area | Retail | Simitone | State |
|---|---|---|---|
| Live-mode subpanels | 8 (motive, job, personality, relationship, inventory, **house**, **fame**, **interest**) + radio strip | 5 | **partial** |
| Options sub-pages | graphics / sound / general / play / save / neighbourhood / tutorial-reset / exit | graphics / general / **sound (empty)** | **partial** |
| Options checkboxes | 16 (`PopupOpt*.bmp`) | 4 (AA, transitions, smooth-zoom, weather) + free will + edge scroll | **partial** |
| Neighbourhood chrome | area switcher (Current/Next/Previous), 11 lot filters, bulldoze, rezone, move-in, add/edit/delete person, phone, import, Inet, CAS | 4 travel buttons + bulldoze + rezone + move-in + edit family + management page + CAS | **partial** |
| Build tools | 13 `toolBtn*` + roof pitch/pattern popups + pool tool + undo/redo | 10 + 3 terrain tools, no pool tool, no roof pitch, no undo/redo | **partial** |
| Wall styles | 5 | 5 | **done** |
| EOD minigames | DDR, Blackjack, slots, bulletin, DJ, dance floor, trunk | 0 reachable | **absent** |
| Tutorial / Help | 58 `opcode 34` sites, `Tutorial.iff`, `HelpSystem.iff` | 0 | **absent** |
| Cheats | ~14 keywords `[A]` | 5 | **partial** |
| Build/buy catalogue | 30 categories + walls/floors/roofs/terrain | same, data-driven from OBJD | **done** |
| Neighbourhood slots | 8 | 8 | **done** |
| CAS | name/gender/age/skin/personality/bio + body & head carousels | same | **done** |

---

## 3. Base game

### 3.1 Careers and jobs

| | |
|---|---|
| **Feature** | Career data, career panel, promotion/firing, work schedule |
| **Current state** | **Partial.** Data side is done: `tso.content/TS1/TS1JobProvider.cs:15` reads `work.iff`, `:19` `GetJobData`, `:29` `set_to_next` for `SearchType Career` (`VMSetToNext.cs:53-59`), `:47` `GetJobLevel`. Panel reads `JobType`/`JobPromotionLevel`/`JobPerformance` person data and shows title, salary, shift, six skill requirements (`Client/Simitone/.../UIJobSubpanel.cs:136-191`). **But** (a) the `CareerButton` created at `UIJobSubpanel.cs:96-98` has **no click handler anywhere in the tree** (0 other hits for `CareerButton`) — the retail career-history/ladder page is unreachable; (b) `TS1JobProvider.cs:36,43` carry `//TODO: use STR#`, so career descriptions come from the `CARR` name, not the shipped string table; (c) **no work-hours / work-days editor** exists (`0 hits` for `sethours`/`setworks`/`setchool` as settings; the *keywords* don't exist in the install, see §0.2). |
| **What retail did** | A career page listing every rung of the track with salary, shift, required skills and which rungs are behind you, reachable from a Career button on the job subpanel `[A]`; player-editable work days/hours `[A]`. |
| **Effort** | M (dead button + a career-level list); L if work-schedule editing is included |
| **Owner** | S |
| **Recorded?** | **No.** Closest is `ADDON_PROGRESSION_PLAN.md` §12, which lists careers under "disassembly-verified already-working". That is true of the *scripts* and false of the *UI* — this is the first place I can show the claim is under-scoped. |

Career *mechanics* themselves (going to work, the car, performance decay, promotion, firing) are
BHAV-driven (`Global/CarGlobals.iff` 8195 `'go to work'`, 8202 `'auto to work'`; `Objects/CarPortal.iff`
is a `Portal - Car` OBJD) and the opcodes they need are all registered `[V]`. I did **not**
independently re-verify the full career chain instruction-by-instruction; I take
`ADDON_PROGRESSION_PLAN.md` §N1 at its word for that.

### 3.2 School (child school, military school)

| | |
|---|---|
| **Feature** | Child school attendance + grades; military school as a teen fate |
| **Current state** | **Partial, and the only partially-verified item in this section.** Engine: `VMPersonDataVariable.TS1AtWorkSchool = 87` (`Model/VMPersonDataVariable.cs:124`) is a named slot and `TS1Zodiac = 70`, `PersonsAge = 58` exist. Opcode 33 `find 5 worst motives` — the primitive the walk-to-school controller depends on — **is now implemented** (`VMContext.cs:304`, `VMFind5WorstMotives` + `VMTS1MotiveHelper`), and its two shipped sites are `Objects/PedPortal.iff` 4107 `'show leave dialog' [2]` and `ExpansionShared/HDSI_Nag.iff` 4349. |
| | `[V]` I confirmed the *string* side: `"$Me is off to military school!"` exists in `STR 0x0131[2]` of ten `Global/*Globals.iff` files, and `Objects/CarPortal.iff` `STR 0x012D[8]/[9]` carry the "grades are putting you in the express line for military school" / "Military school is your next stop" failures. So military school is pure script + a car portal. |
| | `[V]` **There is no school report / grade UI.** `0 hits` for "school report" and "report card" across every `TTA`/`STR`/`TTAB` in all 11 fars, and `0 hits` for any school panel in either tree. Retail's school result is therefore a script message, not a panel — **this is a closed item, not a gap**, and the QA row in `docs/PHASE3_TEST_MATRIX.md` that expects a report card should be rewritten. |
| **What retail did** | Children walk off the lot to school in the morning through the pedestrian/car portal; a bad grade sends them to military school with the quoted failure text; both are consequences of the person's `PersonsAge`/grades state, not of a screen. `[A]` on the "no panel" claim being retail-faithful — but the shipped-string evidence is strong and I mark it `[V-]`. |
| **Effort** | S to confirm by playtest; 0 to build |
| **Owner** | V (engine) / QA (human) |
| **Recorded?** | Partly — `ADDON_PROGRESSION_PLAN.md` §12 lists "child/school/military school" as disassembly-verified. **Not** recorded: that there is deliberately no report-card UI, and the "rewrite the QA row" correction. |

### 3.3 Relationships and family

| | |
|---|---|
| **Feature** | Relationship panel: friend / family / all / famous tabs, drag-to-edit, phone call from the panel |
| **Current state** | **Partial.** Data: `Neighbour.Relationships` as `Dictionary<int, List<short>>` read **and written** (`tso.files/.../NBRS.cs:116,159-173,211-221`). Engine: `VMRelationship` / `VMOldRelationship` (opcodes 24/26) both registered (`VMContext.cs:244,252`) — 329 + 2 820 shipped sites. `set_to_next SearchType 12 FamilyMember` implemented (`VMSetToNext.cs:124-128,151-162`). |
| | **Gap A — the friend and family tabs do not filter.** `UIRelationshipSubpanel.cs:97-98` says so in its own comment: *"Only the fame tab (cat 2) narrows the list today; friend (0) and family (1) still show everything"*, and `RelationshipFameFilter.Apply` (`:189-198`) returns the input untouched for every other tab. `[V]` |
| | **Gap B — the bars cannot be dragged.** `UIRelationshipDisplay` (`:201-276`) has no mouse handler; `UIRelationshipSubpanel` has none. Retail's signature interaction — grab a relationship bar and pull it up or down — is absent. `[V]` |
| | **Gap C — no "call this neighbour" from the panel.** `UICallNeighborAlert` exists but is only reached from the phone-plugin dialog (`UILotControl.cs:363`). `[V]` |
| | **Gap D — family tree.** No panel. Retail's was the "family" portrait in the live-mode cluster `[A]`; there is no retail art for it under `cpanel\` beyond the relationship panel, so I rate this **low confidence as a real retail feature**. |
| **What retail did** | Four filtered/sorted lists (`cpanel\Buttons\RelAllSort/RelFamilySort/RelFriendSort/RelStarSort` + `RelBars.bmp`), drag-to-edit, and call-from-panel. `[V]` for the art, `[A]` for the drag gesture. |
| **Effort** | A: S · B: S · C: S · D: M |
| **Owner** | S |
| **Recorded?** | **No.** Not in any of the four audits. |

**Note (positive):** the **Famous** tab (`rel_fame.png`, `UIRelationshipSubpanel.cs:57-60`) is the
one sub-feature the whole Superstar career ladder is player-visible through, and it *is* wired to
`VMPersonDataVariable.TS1FameScore` (`:154-179`). This is the ported `fame-implementation` branch
from `docs/FORK_INGESTION.md` §2.3.

### 3.4 Death and ghosts

| | |
|---|---|
| **Feature** | Sim dies → becomes a ghost → can be kept in an urn; Grim Reaper; "burn to death" |
| **Current state** | **Implemented, script-driven.** `[V]` `VMPersonDataVariable.IsGhost = 68` and `TS1LivingGhost = 84`; `VMEntity.GhostImage` (`Entities/VMEntity.cs:38`) is honoured in the entry-point and position paths (`VMEntity.cs:488,497,541,573,610,1474`; `VMAvatar.cs:1033,1058,1091`; `VMGameObject.cs:247,322,387,392`) so a ghost does not collide, path, or run its `main`. `VMContext.cs:1281,1334` expose ghost-ness to scripts. |
| | `[V]` Retail death is pure script: `Global/Global.iff` 316 `'do grim reaper'` writes person data 4 and person-mode; `Global/CatGlobals.iff` 8357 `'burn to death'` exists. Urn objects ship (`urnstonecelebrity_iff_100`, `urnstonepet_iff_100` in `Downloads`). |
| | **Gap A — no ghost/death UI.** `0 hits` for an obituary or a "sim has died" screen in either tree. I believe retail had none either `[A]`, so this is **closed**. |
| | **Gap B — ghost permanence across lots is unverified.** `Neighbour.PersonData` is 88 shorts written wholesale (`NBRS.cs:141,197-204`), so slot 68 should persist; I could not confirm the `Dead`/ghost write path reaches the NBR without running the game. `[V-]` |
| **Effort** | 0 (closed) / playtest only |
| **Owner** | V |
| **Recorded?** | Yes — `ADDON_PROGRESSION_PLAN.md` §N1 lists death/ghost as disassembles-clean. **Not** recorded: that slot 68 survives a neighbourhood round trip, or the "no obituary UI" closure. |

### 3.5 Motives and needs decay

| | |
|---|---|
| **Feature** | 8 motives (hunger, comfort, hygiene, bladder, energy, fun, social, room) + mood, decaying on the game clock |
| **Current state** | **Implemented, but hard-coded rather than data-driven.** `[V]` `VMTS1MotiveDecay.cs` is selected for TS1 mode (`VMAvatar.cs:245`) and carries its own constant table (`:8-27`). Ticks every two sim-minutes (`:48`), skips `Hidden` avatars, honours the sleeping/asleep split per motive, computes mood as `moodSum / 8` (`:129`). |
| | **Gap A — the decay constants are literals, not data.** `[V]` Compare the TSO path `VMAvatarMotiveDecay.cs:56-84`, which reads `Content.Content.Get().GlobalTuning.EntriesByName["simmotives"]` / `["lotmotives"]`. In TS1 mode `Content.GlobalTuning` is **never assigned** (`tso.content/Content.cs:165` is inside the `else` — the non-TS1 branch), so that path would be null; the TS1 file avoids it by hard-coding. Consequence: a modded/CC tuning table cannot retune decay, and the lot-category motive multipliers (`CategoryNames`/`LotMotives` in the TSO variant) do not exist in TS1 mode at all. |
| | **Gap B — no per-avatar "freeze needs" cheat.** `[V]` `VMAvatarMotiveDecay.cs:107` has `if (avatar.GetPersonData(VMPersonDataVariable.Cheats) > 0) return;`; `VMTS1MotiveDecay` has no such check. The person-data slot 29 (`Cheats`) therefore does nothing in a TS1 session. |
| | **Gap C — the formulas are unverified against retail.** `[V-]` I read the arithmetic; I did not check the constants against a retail table (there is no readable one in the install). The `FracMul`/`ToFixed1000` 1/1000ths accumulator and the `MotiveFractions` persistence look right, and `VMTS1MotiveDecay.SerializeInto/Deserialize` (`:142-154`) does persist the fractions — good. |
| | **Gap D — the motive bar colours.** `[V]` `cpanel\Greenbars.BMP` / `Redbars.bmp` ship; the motive subpanel is present. **Closed.** |
| **What retail did** | Same eight motives; decay constants shipped in the executable's tuning. `[A]` |
| **Effort** | A: M (needs a retail constant source) · B: XS · C: S (a tuning dump) |
| **Owner** | V |
| **Recorded?** | **No.** Not in any audit. This is the largest "engine is fine but the data path is hard-coded" finding in the base game. |

### 3.6 Socials

| | |
|---|---|
| **Feature** | Interaction pie menu, social submenus, joinable/group interactions, "cheer" |
| **Current state** | **Implemented, with one real gap.** `[V]` `UIPieMenu` renders the `TTA` categories, `GetAction`/`CheckAction` produce the entries, and `VMInteractionQueue` shows the queue. Retrospectively: the engine's `old_relationship` (24) and `relationship` (26) primitives and `change_action_string` (50) are all registered and cover 4 379 shipped sites. |
| | **Gap — group/joinable interactions cannot be initiated from the pie.** `[V]` `TTAB.JoiningIndex` is read (`tso.files/.../TTAB.cs:74,203`) and used *internally* by autonomy (`VMFindBestAction.cs:179,342`) to let several sims share one object, but `VMPieMenuInteraction` has **no group/join flag** (0 hits for `GroupSocial`/`Joinable`/`IsGroup` outside `RemeshPackage.cs`'s mesh grouping) and `UIPieMenu.cs` has no such control. Retail showed a group/join option on joinable interactions. `[A]` |
| | **Gap — "cheer" (group encouragement) is missing.** `[A]`, and I could not find a `cheer` string in the shipped data, so I rate this **low confidence** as a separate retail feature rather than a data-driven social. |
| **Effort** | M |
| **Owner** | S (UI) + V (the flag on `VMPieMenuInteraction`) |
| **Recorded?** | **No.** |

### 3.7 Autonomy / free will

| | |
|---|---|
| **Feature** | Free will: each sim scores every available interaction against its motives, personality and skills and picks the best |
| **Current state** | **Substantially implemented; three named defects.** `[V]` `VMFindBestAction` reads the real retail curves — `TTAB.VisitorAttenuationValues` / `AttenuationValues`, `global.InteractionScore` / `InteractionScoreChild`, `global.HappyWeight` / `HappyWeightChild` (`:123-126`) — applies motive ads, personality multipliers (`:253-268`), distance attenuation (`:280`) and the min-score thresholds `1e-6`/`1e-7` (`:139`). Free will can be switched off in Options and is bypassed correctly for visitors and pets (`:110-116`). Opcode 3 (`find_best_interaction`) and opcode 30 (which this tree decided is `gosub_found_action`, not `syslog` — `VMContext.cs:539`) are registered. |
| | **Gap A — the `HappyWeight` curve is read and then thrown away.** `[V]` `:126` fetches `happyCurve`, then `:134-136` sets `var weights = newStyle ? new float[9] : new float[9]{1,1,1,1,1,1,1,1,1}; //TODO: weights from curves for new` and never uses `happyCurve`. Every motive therefore weighs 1.0 in the "effective motive" calculation, which is exactly the weighting Hot Date onwards added. |
| | **Gap B — `validActions.Add(first)` inside `foreach (var item in pie)`.** `[V]` `:284-289`. For an interaction whose TTAB row expands to several pie entries ("Sit" on a sofa with several slots, "Eat" on a multi-serving plate), only `first` is ever enqueued, and it is enqueued once per sibling. `item` is scored and assigned but discarded. This is a straight bug, not a missing feature. |
| | **Gap C — four declared omissions in the file's own header.** `[V]` `:28` object use-count is deferred, `:29` "only considered if the motive is below that minimum" is `TODO: VERIFY` (though `:246` does implement `if (min != 0 && myMotive > min) continue;`), `:294` "Lockout interactions that have been used before for a few sim hours" is a TODO, `:295` "special logic for socials?" is a TODO. |
| | **Gap D — `VaryByTypes` has four dead personality/skill slots.** `[V]` `:66,70,72,73` map motive-ad slots 13, 17, 19, 21 to `UnusedAndDoNotUse` (cleaning/gardening/music/literacy skills), so any interaction whose ad is modified by one of those four modifiers is scored with a neutral multiplier. |
| **What retail did** | The same algorithm, plus the `HappyWeight` curve, use-count reservation, and interaction lockout. `[V]` for the curve (it is read and discarded), `[A]` for lockout. |
| **Effort** | A: S · B: XS · C: M · D: S |
| **Owner** | V |
| **Recorded?** | **Gap B and Gap A: no.** `ADDON_PROGRESSION_PLAN.md` §O1 mentions opcode 30 only. §N1 lists the opcode-33 fix, not these. |

### 3.8 The build/buy catalogue

| | |
|---|---|
| **Feature** | 30-category catalogue, wall/floor/roof/terrain, per-lot-type subcategories, prices, sell mode |
| **Current state** | **The best-covered area in the game.** `[V]` The catalogue is derived from every OBJD's `FunctionFlags`/`BuildModeType`/`Price`/six per-area sort bytes (`tso.content/TS1/TS1ObjectProvider.cs:92-120`), which is why the retail `Live.iff` catalogue file is never read (0 hits for `Live.iff` anywhere in the tree) and does not need to be. Per-lot-type filtering is real: `CatalogQueryService.GetSubsort` picks `DowntownSort`/`CommunitySort`/`VacationSort`/`StudiotownSort`/`MagictownSort` and the `DTCategories`/`DTIcons` tables cover all five area lot types (`BuyCatalogService.cs:221-339`). Walls: 5 styles × 5 patterns (`UICatalog.cs:165-190`, `BuyCatalogService.cs:345-365`) — a plain wall plus picket/iron/privacy fences and banisters, which matches retail's five `WALm` styles. Floors, wallpapers and roofs are spliced in as special rows (`UICatalog.cs:80-163`). Budget gating is real (`BuildBudget` → `TSOState.Budget`, per `MODERNIZATION_PLAN.md` §4). |
| | **Gap A — no undo/redo.** `[V]` `Undo`/`Redo` exist only in the BHAV editor (`FSO.IDE/.../UIBHAVEditor.cs:30-31`). Retail ships `cpanel\Buttons\Undo.BMP` and `Redo.bmp` for build mode. **M**, **S**. |
| | **Gap B — no per-room roof pitch or pattern.** `[V]` `UIRoofer.cs:14` passes `vm.Context.Architecture.RoofPitch` — one pitch for the whole lot — and the `Control` is a pure apply-then-nothing. Retail has `popuproofpitch.bmp` / `popuproofpattern.bmp` and four `roofflat|medium|shallow|steep.bmp` buttons. **M**, **S** + **V** (per-room pitch needs a `VMNetSetRoofCmd` extension). |
| | **Gap C — no dedicated pool tool.** `[V]` Retail ships `cpanel\Build\PoolToolIcon.bmp` and `PopupPool.BMP`. Simitone has no pool control; a pool is instead a floor style (paint pattern `≥ 65534` → category 5, `UICatalog.cs:117`) with real 3D geometry behind it (`tso.world/Components/Geometry/Modelled3DPool.cs`, pool adjacency in `FloorComponent.cs:61-66`) and pool floors/ceilings in `Content/3D/floor/pool*.obj`. So *pools work*, but the retail dig-a-pool tool does not exist. **S**, **S** (or **accept as a deliberate simplification** — the result is visually equivalent). |
| | **Gap D — no build "hand" cursor tool is missing?** `cpanel\Build\PopupHand.bmp` exists in retail; `UIArchTouchHelper` gives a hand in Simitone. **Closed.** |
| | **Gap E — inventory sort sections.** `[V]` `UIInventorySubpanel.cs:29-59` has three sort buttons (Magic type 7, Ingredient type 8, Other); retail's art is `InventoryGiftSort/MagicSort/OtherSort`. See §3.13 for the hidden-section problem, which is the more serious half. |
| **Recorded?** | §3.8 A–D: **no.** `MODERNIZATION_PLAN.md` §4 marks buy/build `[x]`; the residual items there are the six `$`-instead-of-`§` tool strings. |

### 3.9 Lot lifecycle

| | |
|---|---|
| **Feature** | Load a house, move a family in, evict, bulldoze, rezone, save, travel, resume |
| **Current state** | **Implemented and the most heavily hardened area in the tree.** `[V]` `LotLifecycleService` owns exactly two sequences by design (`ResumeFromMarshal`, `BlueprintReset`); `NeighborhoodService` owns `EvictLot`, `ResetHouse`, `RezoneLot`, `GetLotPurchaseValue`, `StartMoveIn`, `PlayHouse`, `MoveInAndPlay`; `SaveService` owns the two save shapes (home `House{NN}.iff` = FSOV + SIMI + thumbnails, atomically with `.tmp`/`.bak`; travel = `<UserDir>/TravelLots/House{NN}.fsov` marshal-only, `SaveService.cs:35-49`); `TravelSave`/`LotTravel`/`LotPersistence` cover multi-slot travel. The `SimlessForLoad(FAMI, bool)` signature exists specifically to stop the inverted-flag bug that made every lot load simless (`LotLifecycleService.cs:93-104`), and `LotAccounting` classifies mailboxes/controllers/catalog objects so eviction does not destroy them. |
| | **Gap A — the character-IFF write-back uses a bare filename.** `[V]` `SaveService.cs:131,161` call `SaveNeighbourhood(true)` on every save. That reaches `TS1NeighbourProvider.cs:519-531`: `new FileStream(obj.Resource.Name, FileMode.Create)` then `obj.Resource.MainIff.Write(stream)`. `GameObjectResource.Name` is the **reference filename** (`tso.content/WorldObjectProvider.cs:300,307-312`). For a neighbourhood character that is the full `…\Characters\User#####.iff` path (set at `TS1NeighbourProvider.cs:381`), so the common case is correct. For any **FAR-resident** person object it is a bare entry name, and the save drops a loose `.iff` into the process working directory. `AvatarChanged` is only called from `VMTS1PurchasableOutfitHelper.cs:311` and `VMGenericTS1Call.cs:1486`, both of which can fire for NPCs (`maid.iff`, `repairman.iff`, `thief.iff`, `Bones.iff` all ship), so the hazard is reachable. `[V-]` — I did not run it. **XS**, **V**. |
| | **Gap B — no autosave / no "restart lot from blueprint" button in-game.** `[V]` `BlueprintReset` exists and is reachable from the lot panel's "Bulldoze"; there is no separate retail "Restart" affordance and no scheduled save. Retail had neither. **Closed.** |
| | **Gap C — scenario support.** `[U]` `GameData\Scenario.tds` is unread. I cannot say what it holds. |
| **Recorded?** | A: **no** (new). B: closed. C: **no** — not even noted. |

### 3.10 The neighbourhood

| | |
|---|---|
| **Feature** | The map view: pick a house, move in, rezone, bulldoze, edit the family, cycle areas |
| **Current state** | **Partial.** `[V]` `UINeighborhoodSelectionPanel` composites the correct retail background per area with the recovered animation layers (`Nbhd\NScreen.BMP`, `Downtown\DScreen.bmp`, `VIsland\visland.bmp`, `Community\NScreen_unleashed.bmp`, `Studiotown\DScreen.bmp`, `Magicland\DScreen.bmp`) and reads lot hotspots from `LotLocations.iff`. The lot panel (`UIHouseSelectPanel.cs:466-487`) offers bulldoze-or-evict, edit family, rezone — all real. `NeighbourhoodManager` (56 KB) + `UINeighbourhoodManagementPanel` give create/copy/rename/reset-name/delete across the 8 `UserData*` slots, with the `TS1NeighborhoodProvider.ResolveBootstrapSource` ladder so a virgin install bootstraps. |
| | **Gap A — the base-game residential map is unreachable.** `[V]` The panel indexes `Neighborhoods[mode - 1]` (`UINeighborhoodSelectionPanel.cs:260,266`). `NeighborhoodService.NeighbourhoodIdForHouse` (`:111-118`) returns **4** for residential, i.e. index 3, which is `Community\NScreen_unleashed.bmp`. Index 0 — the original `Nbhd\NScreen.BMP` — is only reachable at `mode == 1`, and no code path produces 1. The switcher (`:46,59-62`) only ever offers 2, 3, 5, 7 and 4. This is *deliberate* (see the comment at `NeighborhoodService.cs:98-99`: "Simitone's hybrid mode opens on the unleashed (unzoned) map"), but it is a documented deviation, not an accident, and the base-game map art ships unused. |
| | **Gap B — index 5 is an empty config.** `[V]` `UINeighbourhoodSelectionPanel.cs:130` is a bare `new NeighborhoodViewConfig()` with no `Graphic`; `:260` then refuses `mode == 6` outright. Harmless, but it is a half-wired slot. |
| | **Gap C — no lot filters.** `[V]` Retail ships 11 `NghUI\Filter*.bmp` toolbar buttons (Dog/Cat, Food, Gardening, Lodging, Magic, Model, Movie, Music, Recreation, Rides, Shopping, SmallAnimal, Spa) plus `filter_toolbar.bmp` and two per-area variants. `0 hits` for any of these in either tree. `[A]` that they filtered which lots were clickable — the name and placement make that near-certain. **L**, **S**. |
| | **Gap D — no add-lot / delete-lot.** `[V]` `0 hits` for `AddLot`/`NewLot`/`CreateLot`; `NeighborhoodService`'s public surface has no lot-creation entry point. Retail's `Nbhd\` art has no obvious "new lot" button, and `0 hits` for "add lot"/"new lot"/"empty lot" in any shipped string, so **I cannot confirm retail had it** `[U]`. **M if it did.** |
| | **Gap E — no Next/Previous area cycle, no neighbourhood phone, no add/edit/delete person, no import/export, no Inet button.** `[V]` retail art: `NghUI\Next.bmp`, `Previous.bmp`, `Current.bmp`, `Exit.bmp`, `Return.bmp`; `Nbhd\NeighborhoodPhone.bmp`, `Nbhd\AddPersonBtn.bmp`, `Nbhd\DeletePersonBtn.bmp`, `Nbhd\EditPersonBtn.bmp`, `Nbhd\Import.bmp`, `Nbhd\InetDialogIcon.bmp`, `Nbhd\HotSpotPopupBkg.bmp`, `NghUI\InetBtn.bmp`, `cpanel\PopupExportHTML.bmp`. `0 hits` for `PhoneBook`/`ExportHtml`/`Import` as features (all 4 `Import` hits are prose comments). The Inet button is for EA's dead online service — **permanently out of scope**. |
| | **Gap F — the hot-spot popup.** `[V]` retail `Nbhd\HotSpotPopupBkg.bmp`; Simitone's `UIHouseSelectPanel` is a flat panel with no hot-spot sub-popup. `[A]`. |
| **Recorded?** | A–F: **no.** `ADDON_PROGRESSION_PLAN.md` §10/§12 closed the *neighbourhood management page* and the *bootstrap*; neither touches the map view itself. |

### 3.11 Create-a-Sim

| | |
|---|---|
| **Current state** | **Implemented — treat as done.** `[V]` `TS1CASScreen` + `UISimCASPanel` + `UIFamilyCASPanel` + `CasEdit` cover family create / family delete / **edit an existing family in place** with name, bio, gender, child-adult, 3 skin tones, 5 personality bars, and **body and head appearance carousels** (`TS1CASScreen.cs:425,447,453`). Validation (`CasValidation`: family cap 8, name sanitation, 32-char truncation), character-IFF backup into `Characters/CASBackups/` before `PrepareTemplatePerson` repoints the registry, and removal of the superseded file. Retail art `Nbhd\CreateACharBack.BMP`, `DesignChar*Btn.bmp`, `DsgnFamBkg.bmp` is all matched. |
| **Minor gaps** | `[V]` (a) CAS never writes **generous** (person data 4): `CasEdit.cs:39-40` says *"offset 4 (generous) is deliberately absent: the generator never writes it"*, so a CAS-created sim starts at 0 and only accrues it if a script does — and 20 shipped trees do write slot 4 (verified: `pdw 4` over all 11 fars returns `Global.iff` 316 `'do grim reaper'`, `ExpansionPack\Telescope.iff` 4102, `ChemistrySet.iff` 4139/4140, and every `init traits`/`init NPC`). (b) No standalone "design a sim from scratch" entry point — Simitone is family-first. Retail's `Nbhd\` art implies both existed. (c) No age-in-days control (`MODERNIZATION_PLAN.md` §4 already calls clothing and exact age best-effort). |
| **Effort** | a: XS · b: M · c: S |
| **Owner** | S |
| **Recorded?** | (a)–(c): **no**. |

### 3.12 Motives panel / plumbbob / interaction queue

`[V]` All present and wired: `UIMotiveSubpanel`, `UIInteractionQueue` + `UIInteraction` (click to re-order), `PlumbbobMotive` (colour reflects the lowest watched motive), `VMSetBalloonHeadline` → `UIHeadlineRenderer` with 1 997 shipped sites. **No gap found.** The one live item is `ADDON_PROGRESSION_PLAN.md` §13 item 2 (plumbbob-on-hover exposure), which is a design question, not a gap.

### 3.13 Inventory

| | |
|---|---|
| **Current state** | **Partial.** `[V]` `NGBH` reads *and writes* the per-neighbour token inventory (`NGBH.cs:39-52,69-81`), which is what makes the Vacation score, souvenirs, prize tokens, magic ingredients and Downtown time all persist. The panel reads it (`UIInventorySubpanel.cs:90`) with three sort buttons. |
| | **Gap A — two token sections are permanently invisible.** `[V]` `UIInventorySubpanel.cs:33-37`: `HiddenCats = { 2, 6, 7, 8 }` with the comment *"section 2 is hidden. guids 7 and 8 seem to contain downtown time in count."* 7 and 8 are unhidden by their own buttons (Magic, Ingredient); **2 and 6 have no button**. Type 6 is the Vacation section — `REMAINING_ENGINE_PLAN.md` §7 records that `NGBH` holds *"8 magic tokens, 6 vacation tokens, hotel key, prize tokens"*, and the Vacation score token itself is a **type 6** item (`REMAINING_ENGINE_PLAN.md` §2.2, GUID `0x842A8C3B`). So a player who wins a prize or takes a souvenir **never sees it in the inventory panel**, even though the whole Vacation chain works. The type→content mapping is inferred from the docs, not dumped from a real `Neighborhood.iff` — my chunk-walk attempt on that file did not terminate cleanly `[U]`. **XS**, **S**. |
| | **Gap B — the "Gift" sort is the "Ingredient" sort.** `[V]` `IngButton` targets `ChangeCat(8)`. Whether the retail type-8 section is called "gift" or "ingredient" I could not determine from the data. **Closed as a naming question.** |
| **Recorded?** | Gap A: **no** — and it is the visible face of a feature the plans call "already implemented", so it is worth writing down. |

### 3.14 Meta features

| Feature | State | Evidence / what retail did | Effort | Owner | Recorded? |
|---|---|---|---|---|---|
| **Tutorial** | **Absent** | `[V]` `opcode 34` — the tutorial "instruct" primitive, operand `[6400, 0/1, on/off, 0]` — has **58 call sites**, every one of them in `Objects/HelpSystem.iff` or `Objects/Tutorial.iff`, and is **not registered** (`VMContext.cs` registers 0–9, 11–32, 35–51, 62, 63, 65, 67). `Objects/Tutorial.iff` OBJD `0x41A7` is `Global=0`, so nothing spawns it. `Objects/HelpSystem.iff` has the tutorial trees (`b4116 'tutorial - Buy Mode - More Fun'`, `b4123 'tutorial - Job Panal'`, `b4132 'tutorial - Buy Phone'`, `b4135 'tutorial - Sell an Object'`, `b4115 'reset lot'`, `b4157 'load tree'`). Retail had a first-run tour plus an on-demand Help button (`cpanel\Buttons\HelpButton.bmp`) and a "Reset Tutorial" option (`cpanel\PopupResetTutorial.bmp`, `cpanel\Buttons\opttutreset.BMP`) — `[V]` for the art, `[A]` for the shape. | L (two primitives + a help system + a first-run flow) | V | **No** — `VMGenericTS1Call.cs:1591-1605` explicitly declines to implement the three tutorial *flags* and calls the tutorial a non-goal, but nothing records that the tutorial *content* (58 sites) is also unrunnable. |
| **opcode 10** | **Absent, harmless** | `[V]` 13 sites: `Global/Global.iff` 274 `'Move forward n tiles (n)' [3][4]`, `Objects/HelpSystem.iff` 4115 `[0]`, 4157 `[2]`. Two are the tutorial, one is a global helper. Not registered. | XS | V | **Yes** — `REMAINING_ENGINE_PLAN.md` §1.2, correctly dismissed as "no". I agree, with the note that 11 of 13 sites are tutorial-only. |
| **Credits** | **Done** | `[V]` `UICreditsPanel` + `CreditsData` read `GameData\Credits.iff` (`CreditsData.cs:113`). | — | — | Yes (Waves B1/G4). |
| **Options** | **Partial** | `[V]` Three sub-pages exist (`UIOptionsSubpanels.cs:29` graphics, `:142` general, `:278` sound). **Sound is an empty placeholder** (`:272-287`: *"Intentionally empty"*) while `GlobalSettings` carries `FXVolume`/`MusicVolume`/`VoxVolume`/`AmbienceVolume` (`FSO.UI/GlobalSettings.cs:148-151`) and `SimitoneGame.cs:183-186` applies them **once at startup**. The TSO options panel with the four sliders (`tso.client/UI/Panels/UIOptions.cs:374-398`) is not used by Simitone. | S | S | **No** — `MODERNIZATION_PLAN.md` §4 records an in-game *Settings* panel for AA/lighting/shaders/transitions/edge-scroll and notes FreeSO's options dialog is unusable, but nobody noticed the sound page is empty. |
| **Options checkboxes** | **Partial** | `[V]` Retail ships 16 `cpanel\PopupOpt*.bmp` (AntiAlias, AutoCenter, AutoSnap, CharDetail, EdgeScroll, FreeWill, Lighting, LivePIP, Music, QuickTips, SFX, Shadows, SimInBack, TerrainDetail, TransUI, Vox). Simitone exposes 6 in total and `0 hits` for `AutoSnap`/`AutoCenter`/`CharDetail`/`TransUI`/`QuickTips`/`SimInBackground` in either tree; `GlobalSettings` has no such properties. Free will ✓, edge scroll ✓, weather (a Simitone addition). | S each, M for a coherent pass | S | **No** |
| **Cheats** | **Partial** | `[V]` `UICheatTextbox.cs:24-32` defines 5 keywords (`moveobjects`, `motherlode`, `klapaucius`, `rosebud`, `giveMoney`) plus a non-cheat `weather <type> [intensity]` parser. `VMCheatContext.cs:12-16` has exactly 2 handlers: `Budget` and `MoveObjects`. Retail's list (`[A]`, unverified — see §0.2) is ~14, including `setallmotives`/`allmotives`, `killskills`/`killtraits`, `sethours`/`setworks`/`setschool`/`sethistory`, `invisible on|off`, `unlock`/`lock`, `noexit`, `expand`, `exit`. The cheat box is bound to `Ctrl+Alt+Shift+C` (`:74-78`) and repeats via `!`/`;`. | M | S (+V for any cheat that needs an engine hook) | **No** — `ADDON_PROGRESSION_PLAN.md` §12 does not mention cheats at all. |
| **Save format** | **Done, with a caveat** | `[V]` Two shapes, both documented in `SaveService.cs:35-49`: home = `House{NN}.iff` with FSOV (the `VMMarshal`) + SIMI + both PNG thumbnails, committed through `AtomicFileCommit` (`.tmp` staged, `.bak` kept, then replace); travel = `<UserDir>/TravelLots/House{NN}.fsov` marshal-only, family-stamped. Retail `.iff` lots load through `VMTS1ActivatorNew.LoadFromIff` reading `HOUS`/`OBJT`/`OBJM`/`SIMI` (`:416-499`), so **retail saves open**. `NBRS`/`NGBH`/`FAMI`/`TATT`/`HOUS`/`SIMI`/`FSOV` all round-trip. `SaveCompatTests`, `SaveServiceTests`, `TravelSaveTests`, `LotPersistenceTests` pin it. Caveat: the Simitone-written `FSOV` is **not** retail-readable, which the code says out loud (`TS1NeighbourProvider.cs:104-105`). | — | — | Yes (`MODERNIZATION_PLAN.md` §4, `ADDON_PROGRESSION_PLAN.md` §10). |
| **`Live.iff` / `Scenario.tds` / `Ranger*.iff` / `log.cht` / `stalk.ini`** | **Unread** | `[V]` 0 references in the tree. `Build.iff` *is* read (`WorldWallProvider.cs:185`, `WorldFloorProvider.cs:112`) for wall and floor styles. `Live.iff` is not needed because the catalogue is derived from OBJDs. `Ranger*.iff` and `Scenario.tds` are unidentified `[U]`. | investigation first | F | **No** |

---

## 4. Expansions

### 4.1 Downtown — `ExpansionPack.far`

| | |
|---|---|
| **State** | **Best-covered expansion. No new gap found.** `[V]` The Downtown map, its `DScreen.bmp` + `dscreen00-04` sea layer (`NeighbourhoodAnimations.DowntownWavePosition/DowntownFrames`), its own catalogue tables (`BuyCatalogService.cs:221-243`), its own "time" lot and the auto-follow invite/take-home path (`Utils/AutoFollow.cs`, 36 KB + 98 tests) are all present. |
| | `[V]` **Correction to an earlier finding, confirmed:** there is no Downtown car sprite to position. `ADDON_PROGRESSION_PLAN.md` §N3 got this right and the tree documents why (`UINeighbourhoodSelectionPanel.cs:85-88`). |
| | `[V]` The one visible artefact of Downtown that *is* missing is a row in the player's **inventory** — the Downtown "time" tokens are sections 7/8, which `UIInventorySubpanel` exposes as Magic/Ingredient buttons with the comment *"guids 7 and 8 seem to contain downtown time in count"* (`:35-36`). i.e. the Downtown time is filed under the Makin' Magic button. **Cosmetic, S, S.** |
| **Recorded?** | Yes (§N1, §N2, §N3, §12). |

### 4.2 Hot Date — `ExpansionPack2.far`

This is where the largest single finding is.

| | |
|---|---|
| **Feature** | The EOD minigames — Dance Dance Revolution, Blackjack, the dance floor, the DJ station, the costume trunk, the bulletin board, the signs |
| **Current state** | **Absent, and structurally so.** `[V]` FreeSO ships a complete 30-handler EOD stack: `VMEODServer.IDToHandler` (`NetPlay/EODs/VMEODServer.cs:27-64`) maps 23 TSO plugin ids, 6 FreeSO-specific ones and 1 archive one, `VMEODHost` is constructed at `VM.cs:237` **when the driver is a `VMServerDriver`** — and Simitone does use one (`TS1GameScreen.cs:815`, `TS1CASScreen.cs:1933`), so `EODHost` is live. The client has 34 EOD panels under `tso.client/UI/Panels/EODs/`. |
| | **`[V]` and yet none of it can be reached from TS1 data, for two independent reasons:** |
| | 1. **Opcode 62 (`invoke_plugin`, `VMContext.cs:429`) has exactly 0 call sites** across all 11 retail `.far`s. I measured this directly (`op 62` over every far: **0**). `VMInvokePlugin` is the only way a script opens an EOD connection. |
| | 2. **No EOD semi-global IFF ships anywhere.** The complete set of semiglobals in the install is 24 files, all `*Globals.iff` inside `GameData\Global\Global.far` (`ArtGlobals`, `CarGlobals`, `CatGlobals`, `CounterGlobals`, `DogGlobals`, `DoorGlobals`, `DragonGlobals`, `FameObjectGlobals`, `FunHouseGlobals`, `Global`, `LevelGlobals`, `LightGlobals`, `PersonGlobals`, `PetSitGlobals`, `PhoneGlobals`, `RentalClerkGlobals`, `RentalShackGlobals`, `SalesClerkGlobals`, `SocialGlobals`, `SofaGlobals`, `VacationCarnieGlobals`, `VacationDirectorGlobals`, `VacationMascotGlobals`, `WaiterGlobals`). There is no `EODWarGame.iff`, no `SignsGlobals.iff`, no `TrunkGlobals.iff`, no `BulletinGlobals.iff`, no `DJStationGlobals.iff`. |
| | 3. And no DDR object: searching every far for `Dance`/`DDR`/`Blackjack`/`GameMachine` returns only `ExpansionPack2\DanceFloor.iff` (a plain 4-piece dance floor with **no** semiglobal and `global=0`), `DanceCage.iff`, `ExpansionPack3\HDSI_Dance.iff` (a social interaction), and a lot of skins. |
| **What retail did** | Hot Date shipped EOD minigames driven by those semiglobals `[A]`. The Complete Collection install in front of us **does not contain them** `[V]` — so this is not a Simitone regression, it is either a data gap in *this* install or a hard-coded-executable feature. |
| **Effort** | S to *document* as unrecoverable; L if the data is ever found; XL if it has to be re-authored |
| **Owner** | V (engine) + F (data question) |
| **Recorded?** | **No.** This is the biggest thing in the document that no plan mentions. |
| **What *does* work from Hot Date** | `[V]` The pack's non-EOD content is fine: `SocialGlobals.iff` (group socials), `FunHouseGlobals.iff` (House Party fun-house objects), the dance floor as an ordinary object, the 20 interest person-data slots (`VMPersonDataVariable.cs:52-59`, Hot Date's 20 interests are person data 46–53) and the dance/social `.cfp` animations under `ExpansionShared\Animations\`. |
| **Gap — the Interests subpanel is missing.** `[V]` `cpanel\Buttons\Interest.bmp` plus **20** `InterestXxx.bmp` buttons (Aliens, Charisma, Exercise, Food, Health, Music, Outdoors, Parties, Pets, Politics, Romance, School, Sports, Style, The60s, Toys, Travel, Violence, Weather) and `cpanel\InterestBars.BMP` + `cpanel\Backgrounds\InterestBarsBackground.BMP` are all unused; `0 hits` for `Interest` in either tree's UI. The *data* is modelled (slots 46–53) and the retail scripts read and write it, so the game plays correctly — the player simply cannot see their interests or toggle them. **L**, **S**. |
| **Gap — group socials cannot be started.** See §3.6. `FunHouseGlobals.iff` ships and is called by name; the joining flag is not surfaced. **M**, S+V. |
| **Gap — the radio.** `[V]` `cpanel\Buttons\NewRadio.bmp`, `NewRadioSelected.bmp`, `AMPM.BMP`, `cpanel\Backgrounds\phonebookbkg.bmp`, `phoneicon.bmp` are unused; `0 hits` for `PhoneBook`. The `Music/` folder in the install is not read by any code I could find `[U]`. The neighbourhood map *does* carry per-area music (`"station_dtnhood"`, `"station_vacation"`, `"station_superstar"`, `"music_magictown"`) so the soundtrack works in the hood; in-lot radio object behaviour is unverified. **M**, S. |

### 4.3 Unleashed Pets — `ExpansionPack3.far`

| | |
|---|---|
| **State** | **Script-complete, and the pet specifics are the best-documented thing in the tree.** `[V]` Pet shows and pet training were disassembled in `REMAINING_ENGINE_PLAN.md` §4.1–4.3 and are script-driven from `Control-PetShow.iff`, `PetPedestal.iff` and person data 10/11/12; the documented "pets cannot randomly starve" finding is implemented in `VMTS1PetAutonomyHelper` and referenced from `VMFindBestAction.cs:128-170`. `Objects/MailBox.iff` 4126 `'Interaction - Debug - Buy A Pet'` and `Objects/Baby.iff` 4112 `'create child'` show the object side. Opcode 19 (`make_new_character`, `VMTS1MakeNewCharacter`) is implemented. |
| | `[V]` **The one place the base game also has pets** (`CatGlobals.iff`, `DogGlobals.iff`, `PetSitGlobals.iff`, `Objects/Baby.iff`) is fully scripted and needs no new engine work. |
| **Gaps found** | `[V]` (a) No pet-specific UI — but retail had none either `[A]`, so **closed**. (b) `VMFindBestAction.cs:66-73` maps pet-skill motive-ad slots 13/17/19/21 to `UnusedAndDoNotUse`; if a pet interaction advertises itself on a cleaning/gardening/music/literacy modifier its score is wrong (same defect as §3.7 D). (c) The **Community (Unleashed) neighbourhood map is Simitone's default "residential" map** (§3.10 A) — a deliberate hybrid choice, but it means the base-game residential map never appears. |
| **Recorded?** | Yes (§4.1–4.3, §N1, §12). The pet-skill modifier defect is **not**. |

### 4.4 Superstar / Studio Town — `ExpansionPack6.far`

| | |
|---|---|
| **State** | **Mechanics complete, UI almost entirely absent.** `[V]` The fame ladder exists as `VMTS1FameHelper` (10 half-star rungs), driven by TS1 global calls 34/36 (`VMGenericTS1Call.cs:333,339,1186-1209`), writing `TS1FameScore` (80), `TS1FameStarPower` (81) and `TS1FameStarHighWatermark` (82). The studio map + its idle-car animation layer are wired. The fame objects (`Global/FameObjectGlobals.iff` 8192 `'Calculate and Pay Sim'`, 8203 `'Get Award?'`) and the obsessed fan are script-complete (`REMAINING_ENGINE_PLAN.md` §3.3, §3.5). |
| **Gap A — no Fame subpanel.** `[V]` Retail ships `cpanel\FameSubBars.bmp`, `cpanel\Backgrounds\FameSubBarsOff.bmp`, `cpanel\Backgrounds\FameIconMultiPopup.bmp`, `cpanel\Buttons\FameFriendSmiley.bmp`, `cpanel\Buttons\FameIconMultiButton.BMP`, `cpanel\Buttons\littleStar.bmp` — a five-star display plus a points readout. Simitone has **only** the `rel_fame.png` *relationship filter tab* (`UIRelationshipSubpanel.cs:57-60`). `0 hits` for `StarPower` in the client. The player can become famous and cannot see their rating. **M**, **S**. |
| **Gap B — the fame ladder is community data, not retail data.** `[V-]` `VMTS1FameHelper.cs:22-31` says so in its own words: the table is *"taken from the career table the community compiled out of the retail game"*, and the base game's acting-career thresholds are explicitly not modelled (`:33-39`). So the ladder is plausible but **not verified against the shipped data**. It is the single most load-bearing unverifiable constant in the whole expansion. **S** to verify (needs a retail dump or a community cross-check), **S**. |
| **Gap C — the Superstar social "Worship" / paparazzi / media-mobile set.** `[V]` `ExpansionPack6` ships `NPC_Dancer_*` (8 files), `NPC_Dancer_Clown.iff`, and the backup-dancer skins. These are lot NPCs the scripts place, so they need no engine work `[V-]`. I found no missing primitive. **Closed.** |
| **Gap D — "studio tours at home" is server-side.** Already recorded as blocked (`REMAINING_ENGINE_PLAN.md` §3.4, `ADDON_PROGRESSION_PLAN.md` §12). Not re-litigated here. |
| **Recorded?** | Mechanics yes (§12, G1). **Gap A (the fame panel) and Gap B (the ladder's provenance): no.** |

### 4.5 Makin' Magic — `ExpansionPack7.far`

| | |
|---|---|
| **State** | **Mechanics complete; UI is three-quarters built.** `[V]` Call 38 `GetTokensFromString` is implemented (`VMTS1PresentationHelper.TokenListRequest`/`ReadTokenList`/`TokenizeTokenList`) and call 3 `PullDownTaxiDialog` is implemented (`VMTS1TaxiDialogHelper`, `VMDialogType.TS1TaxiLeave = 16`). The family **spellbook and cookbook** are a real paged panel built from `GameData\magic.iff` (25 spell titles, 9 recipe titles) lined up against the NGBH token inventory, with owned entries marked (`MakingMagicTables`, `MakingMagic`, `UIMakingMagicAlert.cs:22-57`). The magic quest controller chain is script-complete (`REMAINING_ENGINE_PLAN.md` §5). |
| **Gap A — the cauldron ingredient list renders as a literal.** `[V-]` Already recorded as `ADDON_PROGRESSION_PLAN.md` §13 item 4: the shipped cauldron dialog uses the TS1 `$TokenNameLocal:N$` substitution token, which `VMDialogHandler` does not implement, so the dialog shows `$TokenNameLocal:N$` instead of the ingredients. Wave Q4 owns it. **S**, F. |
| **Gap B — no scrapbook.** `[V]` Retail ships `cpanel\Buttons\scrapdelete/scrapdone/scrapend/scraphome/scrapleft/scrapright.bmp` and `cpanel\Backgrounds\scrapback.bmp` — a six-button navigable scrapbook page for browsing collected spell parts. `0 hits` for `Scrap` in the tree. `[A]` on what it displayed. **M**, **S**. |
| **Gap C — no quest log.** `[V]` The quest controller (`0xC953333E`) is script-only; no panel. Retail shipped no obvious quest art, so **closed** `[U]`. |
| **Recorded?** | Gap A yes (§13 item 4). **Gaps B and C: no.** |

### 4.6 Vacation — `ExpansionPack4.far`

| | |
|---|---|
| **State** | **Mechanics fully implemented, per a disassembly I accept.** `REMAINING_ENGINE_PLAN.md` §2 proves the score controller (`0x9CC72382`), the `NGBH` type-6 token persistence, the three souvenir paths, and the entire front-desk hotel-booking chain including the `§66/§72/§62` pricing and the "Count Beds ≤ 2 ⇒ refuse" rule. The V-VAC-1..6 QA rows are the right shape. |
| **Gap A — the souvenirs and prizes are invisible in the inventory panel.** See §3.13. `[V]` for the code, `[A]`-flavoured for the type→content mapping. **This is the one place where a "fully implemented" verdict produces a visibly incomplete game.** |
| **Gap B — no rental-shack / bed-assignment UI.** `[V]` `REMAINING_ENGINE_PLAN.md` §2.6 established that rooming is emergent (first-claimed bed + a gender-preference guard) and §2.6's second half that the Rental Shack is pure script. I re-read the citation and agree. **Closed — the QA row should be deleted, not built.** |
| **Recorded?** | Mechanics yes. **Gap A: no.** Gap B closed in §2.6 but the *matrix row* rewrite is still an open item there. |

### 4.7 House Party (shared, `ExpansionShared.far`)

| | |
|---|---|
| **State** | **Script-complete; no UI gap found.** `[V]` `ExpansionShared\Sound\HouseParty.hot/.hsm/.HIT` and `HotDate.hot` ship, so both packs' sound is present. `Global/FunHouseGlobals.iff` ships (the "Fun House" object group) and is called by name. `ExpansionShared/HDSI_*.iff` are the social-interaction objects (`HDSI_Nag.iff` 4349, `HDSI_Dance.iff`, `HDSI_Souvenir.iff` `0x2F1DDC9C`). `Objects/NPCPartyCrasher.iff` / `NPCPartyCrasher2.iff` ship. The party NPCs and the party interactions need no engine work. |
| **Gaps found** | `[V]` (a) No **guest list** — retail's `HDSI` interactions gate on it, and `Objects/DollHouse.iff` 4103 `'Play Test'` uses `test_sim_interacting_with` (opcode 37, registered). I could not find retail guest-list art, so **closed** `[U]`. (b) No **party score** panel — again no art, **closed** `[U]`. (c) **Group socials cannot be initiated** (§3.6, §4.2) — this is where House Party actually bites, because a party *is* a group interaction. |
| **Recorded?** | The House Party / Hot Date overlap is **not** broken out anywhere in the four audits; §12 lumps it into "disassembly-verified". §4.2's group-interaction gap is new. |

---

## 5. Cross-cutting engine findings

These are not "features" but they are what a future wave will otherwise rediscover.

| # | Finding | Evidence | State | Effort | Owner |
|---|---|---|---|---|---|
| E1 | **Opcode coverage is complete except the tutorial.** `[V]` Full `opcensus` over all 11 fars (~230 k instructions): 0–9, 11–32, 35–51, 62, 63, 65, 67 all used and registered; **10** (13 sites, 11 tutorial), **34** (58 sites, all tutorial), and 33 (2 sites, now implemented) are the only gaps. Opcodes 38–40, 52–61, 64, 66, 68–99 have **zero** sites. | `opcensus` | confirmed | — | — |
| E2 | **`invoke_plugin` is dead for TS1.** See §4.2. | `op 62` = 0 | confirmed | — | — |
| E3 | **`VMMemory.SetVariable` throws for 11 scopes that retail never writes.** `[V]` Scopes 16 `StackObjectSlot`, 20 `MySlot`, 23 `RoomByTemp0`, 24 `NeighborInStackObject`, 32 `NeighborPersonData`, 33 `JobData`, 34 `NeighborhoodData`, 42 `TempXL`, 46/47 `MyList`/`StackObjectList`, 54 `FeatureEnableLevel` all `throw new VMSimanticsException("Not implemented...")` (`VMMemory.cs:531,543,553,556,582,585,588,613,622,625,648`). I scanned every `expression` (opcode 2) operand in all 11 fars for an assignment to any of them — **0 real writes** (the tool's own `WRITE` heuristic over-reports, because it counts comparison operators 14/15/16 as writes; filtering on the true assignment opcodes `{3,4,5,6,7,9,10,11,12,13,17,18,19,20}` gives zero). So the throwing paths are **unreachable from shipped data** — latent, not live. | scope scan | verified, no action needed | XS (clamp) if desired | V |
| E4 | **`set_to_next SearchType 11 ClosestHouse` is a stub.** `[V]` `VMSetToNext.cs:121-123` returns `GOTO_FALSE` with a `throw new VMSimanticsException("Not implemented!")` on the next line — dead code after a `return`. I did **not** count its call sites; it is a one-line risk. | code | partial | XS | V |
| E5 | **`get_variable` scope 34 `NeighborhoodData` returns 0.** `[V]` `VMMemory.cs:145-147` — with the comment *"tutorial values only"*, which ties it to the tutorial gap. | code | consistent with §3.14 | — | — |
| E6 | **The `NBRS` person-data length is a latent trap that has already been clamped once.** `[V]` `OBJM.cs:277` reads 0x50 shorts for lot person data ≤ v0x45 and 0x100 above; `NBRS.cs:141` reads a fixed **88** shorts for neighbourhood person data. Retail's max index is 87 (verified by the plan's `pdrange` census). `VMMemory.cs:137-139` now clamps. | code + prior census | closed | — | — |

---

## 6. Data shipped by retail that this tree does not read

| File | Size | Referenced? | Notes |
|---|---|---|---|
| `GameData\Ranger.iff` | 643 B | **no** (`0 hits` for `Ranger`) | two obfuscated `STR#` tables. `[U]` unidentified. |
| `GameData\Ranger2..8.iff` | 7 338 B each | **no** | one per neighbourhood slot. `[U]` |
| `GameData\RangerD.iff` | 7 338 B | **no** | Deluxe variant. `[U]` |
| `GameData\Scenario.tds` | 4 096 B | **no** | `[U]`. |
| `GameData\log.cht` | 15 B | **no** | contains `sim_log begin`. |
| `GameData\stalk.ini` | 12 B | **no** | contains `320 / 240 / 30`. |
| `GameData\Live.iff` | 1 168 691 B | **no** | retail buy catalogue. **Not needed** — the catalogue is derived from OBJD `FunctionFlags`/`BuildModeType`/`Price`/sort bytes (`TS1ObjectProvider.cs:92-120`). |
| `GameData\Build.iff` | 687 194 B | **yes** | wall + floor styles (`WorldWallProvider.cs:185`, `WorldFloorProvider.cs:112`). |
| `GameData\Credits.iff` | 986 713 B | **yes** | `CreditsData.cs:113`. |
| `GameData\behavior.iff` | 81 913 B | **yes** | install sentinel only (`TS1InstallSelection.cs:151`). It is *not* loaded as a tuning source — the whole behaviour graph ships inside the `.far`s. |
| `Music\*.ogg` | — | **no** found | `[U]` — I did not exhaustively grep for a music loader. |

**Recommendation:** one 1-hour read-only spike to identify `Ranger*.iff` and `Scenario.tds`. If
`Ranger` turns out to be a per-neighbourhood setting/progress table, the missing tutorial state
storage (`VMGenericTS1Call.cs:1591-1605`, calls 0/8/9) may have a home after all, which would
close a third of §3.14's first row.

---

## 7. What the four existing audits already record

Cross-reference so the next wave does not re-derive any of this. **"Recorded" means a reader of
that document would learn the fact.**

| Fact | Where it is already recorded |
|---|---|
| TS1 call surface is complete except call 3 | `REMAINING_ENGINE_PLAN.md` §1.1 |
| Opcode surface: only 10/33/34 unregistered | `REMAINING_ENGINE_PLAN.md` §1.2 |
| Call 3 `PullDownTaxiDialog` implemented | `ADDON_PROGRESSION_PLAN.md` §M1 |
| Call 38 `GetTokensFromString` implemented | §L1 |
| Opcode 33 `find 5 worst motives` implemented | §N1 |
| Vacation Score / souvenirs / hotel booking work | `REMAINING_ENGINE_PLAN.md` §2 |
| Studio Town fame objects / obsessed fan work | §3 |
| Unleashed pet shows / training work | §4 |
| Makin' Magic quests work; "pyrite" is not a thing | §5 |
| No roommate/rental UI is needed (script-only) | §2.6 |
| Career / school / death / relationships / pets / MagiCoin "disassemble clean" | `ADDON_PROGRESSION_PLAN.md` §N1, §12 |
| Neighbourhood management page + bootstrap | §10, §11 Wave J |
| Auto-follow (Downtown) implemented and wired | §M3, §N2 |
| Spellbook/cookbook, Picture-in-Picture, localisation | §G2/G3/M2/P1/P2 |
| Buy/build, lot lifecycle, save format, CAS complete | `MODERNIZATION_PLAN.md` §4 |
| No Downtown car sprite exists | `ADDON_PROGRESSION_PLAN.md` §N3 |
| `NeighbourPersonData` clamp; `push_interaction` null guard | `REMAINING_ENGINE_PLAN.md` §6 items 3/5 |
| Remaining human playtest matrix | `REMAINING_ENGINE_PLAN.md` §6.1, `PHASE3_TEST_MATRIX.md` §11 |

**And, explicitly not recorded anywhere:** §1's items 1–14, §3.7 A–D, §3.13, §3.14 (all rows
except opcode 10 and the three tutorial flags), §4.2 (EOD + Interests + radio), §4.4 A/B,
§4.5 B, §4.6 A, §5 E3–E6, §6.

---

## 8. Suggested order of work

Ordered by (player-visible value) ÷ (risk), on the same basis `REMAINING_ENGINE_PLAN.md` §6 used.

1. **Fix `VMFindBestAction.cs:288`** (`Add(first)` → `Add(item)`). XS, V, and it is a live bug in the
   single most-executed engine path in the game. **Do this first.**
2. **Apply the `HappyWeight` curve** (`VMFindBestAction.cs:134-136`). S, V. Autonomy currently
   ignores the motive weighting Hot Date introduced; this is the highest-value autonomy fix
   available.
3. **Document the EOD situation** (§4.2) as a permanent data-level block, so nobody re-derives the
   34 EOD panels. S, docs.
4. **Options ▸ Sound volume sliders.** S, S. `GlobalSettings` already has the four properties and
   `HITVM.SetMasterVolume` already exists; only the rows and the save are missing.
5. **Fame subpanel** (§4.4 A). M, S — Superstar is otherwise invisible, and this is the whole
   expansion's player-facing surface.
6. **Vacation inventory sections** (§3.13 A). XS, S. Souvenirs and prizes already work; they just
   cannot be seen. Cheapest visible win on the list.
7. **The dead `CareerButton`** (§3.1). M, S.
8. **Relationship tab filters + drag-to-edit** (§3.3 A/B). S each, S.
9. **Undo/redo in build mode** (§3.8 A). M, S. Needs a command-stack layer; check with the
   `VMNet*` command set before sizing.
10. **Per-room roof pitch** (§3.8 B). M, S + V.
11. **Interests subpanel** (§4.2). L, S — biggest single player-facing addition; only worth it if
    Hot Date is a target.
12. **Neighbourhood lot filters** (§3.10 C). L, S — speculative until someone says the map view is
    the problem.
13. **The `Ranger*.iff` / `Scenario.tds` identification spike** (§6). ~1 h, F. Cheap, and it might
    give the tutorial somewhere to store its flags.

Do **not** build: the tutorial (§3.14, L, and it needs a first-run flow plus a help system);
the Inet button (EA's dead service); the EOD minigames (§4.2, no data).

---

## 9. Confidence ledger

**Verified in this session, no caveats (§1, §3.6, §3.7, §3.8, §3.10, §3.13, §3.14, §4, §5, §6).**
Every claim above has a `file:line` or a disassembly count in it.

**Verified with a named inference.**

* §3.13 / §4.6 A — the `NGBH` inventory *type* → *content* mapping is taken from
  `REMAINING_ENGINE_PLAN.md` §7, not dumped from a real `Neighborhood.iff` (my chunk-walk on that
  file did not terminate cleanly). The **code** claim (`HiddenCats` hides 2 and 6) is verified; the
  **"6 is the vacation section"** claim is inherited.
* §3.4 B — ghost persistence to the NBR is code-plausible but unobserved.
* §3.14 (cheats) — Simitone's 5 keywords are verified; retail's ~14 are `[A]`.

**Assumed from published descriptions of retail TS1 `[A]`.** Every UI-gesture claim: drag-to-edit
relationships (§3.3 B), group interactions (§3.6), work-schedule editing (§3.1), the
Neighbourhood filter semantics (§3.10 C), the add-lot feature (§3.10 D), the guest list and party
score (§4.7), the scrapbook contents (§4.5 B), retail's cheat list (§3.14). Where I had retail art
I have said so; where I had only a name I have said that too.

**Unverifiable from this install `[U]`.** `Ranger*.iff` (9), `Scenario.tds`, `log.cht`,
`stalk.ini`, the `Music\` folder's consumption, whether retail had an obituary / guest-list /
party-score / quest-log screen, and the entire hard-coded content of `Sims.exe` (§0.2).

**Not independently re-verified.** I took these at their authors' word and did not redo the
instruction-level work: the full career chain, the school chain, the pet-show chain, the
Studio Town arrival chain, the Vacation arrival chain. They are marked as such above. This
document's engine findings are all *my own* measurements; its "already implemented" verdicts are
inherited.

---

## Corrections from Wave T (2026-09-28)

Several rows above were **closed or disproved** by later work. These override them:

- **§3.8 Gap B "per-room roof pitch" — CLOSED as a non-feature.** TS1 never had it. A census of all
  86 shipped lot files finds no roof array (23 distinct `Arry` pairs, none roof); `HOUS` carries one
  `RoofName` texture string; rooms are not stored at all (they are flood-filled from wall tiles by
  `VMRoomMap.GenerateMap`); `VMArchitectureCommandType` has **no roof command**; `VMNetSetRoofCmd` is
  a whole-lot `(pitch, style)`; and retail's own `Res_CPanel.h` lists a lot-global roof level
  chooser (`kBldSbTlRoofShallow/Medium/Steep/Flat`) exactly like the terrain tool's. Simitone's one
  global pitch is faithful. (`Simitone.Tests/Rendering/RoofPitchScopeTests.cs` pins this.)
- **§1 item 3 (`VMFindBestAction` enqueues only the first option) — FIXED** (Wave R1).
- **§1 item 4 (`HappyWeight` discarded) — FIXED** (Wave R1; the retail table is 7 curves, not 9).
- **Careers / relationship / inventory / House / Interests / Fame subpanels — largely DONE**
  (Wave R3): relationship filtering + draggable bars, the career page, inventory types 2/6 and the
  MagiCoin balance, and new House/Interests/Fame panels. House chores and Interests toggling remain
  partial (no client read path).
- **Options > Sound, cheats, freeze-needs — DONE** (Wave R5). Build/buy undo is still open and must
  be a *net* operation, not a placement undo.
- **Tutorial / Help — DONE** (Wave R6, opcode 34). Opcode 10 is now registered too (Wave S4); the
  third object is `ExpansionShared/HDHelpSystem.iff`, and its sites were already live.
- **Neighbourhood view filters / Next-Previous / residential map — DONE** (Wave S3).
- **Undo/redo (build/buy)** and the **`freeze-needs` UI** are the notable live rows still open.
