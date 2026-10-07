# Phase 3 — Per-lot-type manual test matrix

Status: harness for the human-in-the-loop pass that cannot be scripted. The automated
headless tests live in `Client/Simitone/Simitone.Tests` (683 at the time of writing); this
document covers the parts that need a running game and a real TS1 install.

How to run: build `Simitone_Windows` (Release) and launch
`Client\Simitone\Simitone.Windows\bin\Release\net10.0-windows\Simitone.exe`
with `-path:"<your Sims install>"`. `Documents\Simitone\logs\simitone.log` records warnings and
crashes; check it after each row.

Legend: **PASS** / **FAIL** / **N/A**. Record the build date so results are attributable.

---

## 1. Boot / neighbourhood

| # | Steps | Expected | Result |
|---|---|---|---|
| 1.1 | Launch with no config file present | Boots to the neighbourhood; base `config.ini` written to `Documents\Simitone\` (not `Content\`) | |
| 1.2 | Launch with the default neighbourhood view | Nessie/cars/blimp absent here; no warning in the log | |
| 1.3 | Open Vacation Island (mode 3) | Waves + port + trees render; Nessie visible in the ocean, bobbing | |
| 1.4 | Open StudioTown (mode 5) | Top layer renders; car sprite visible in the lot | |
| 1.5 | Open MagicTown (mode 7) | Windmill/waves render; blimp visible over the meadow | |
| 1.6 | Resize the window on each screen | Layout re-centres; no `NullReferenceException` in the log | |

> Positions for 1.3–1.5 are informed guesses (`Utils/NeighbourhoodAnimations`); if one is
> wrong, nudge the single `Vector2` constant and rebuild (see the XML docs there).

## 2. Home lot: save / load

| # | Steps | Expected | Result |
|---|---|---|---|
| 2.1 | Enter a family's home lot | Lot loads; family present; budget shown | |
| 2.2 | Buy an object, place it, save, return to neighbourhood, re-enter | Object persists; budget persisted | |
| 2.3 | Build a wall/floor, buy furniture, save, quit to desktop, relaunch, re-enter | Everything persists (FSOV marshal + `House{NN}.iff` round-trip) | |
| 2.4 | After 2.3, inspect `UserData\Houses\` | `House{NN}.iff` updated; `.bak` is the previous save; no `.tmp` left | |
| 2.5 | Save, then deliberately corrupt the `.iff` (truncate it), relaunch | Lot fails gracefully (load error dialog); `.bak` still present | |

## 3. Travel / vacation persistence

| # | Steps | Expected | Result |
|---|---|---|---|
| 3.1 | From home, travel to Downtown | Downtown loads from its shipped template, not the home lot | |
| 3.2 | In Downtown, buy/move something, return home, return to Downtown | Downtown shows the change (in-session `SavedLots`) | |
| 3.3 | In Downtown, press Save | Save succeeds (no "cannot save" dialog); `Documents\Simitone\TravelLots\House{NN}.fsov` created | |
| 3.4 | With 3.3 saved, quit to desktop, relaunch, travel Downtown again as the same family | Downtown restores the saved state | |
| 3.5 | Repeat 3.4 as a *different* family | The other family's slot is ignored (warning logged); shipped template loads | |
| 3.6 | Home → Downtown → Vacation → home → Downtown → Vacation | Every leg restores its own state; no money/objects lost | |
| 3.7 | Inspect `UserData\Houses\House{NN}.iff` for a Downtown/Vacation lot after 3.3 | **Unchanged** (templates must stay pristine) | |
| 3.8 | Corrupt a `TravelLots\House{NN}.fsov` (truncate it) and travel there | Warning logged; shipped template loads; no crash | |

## 4. Move-in / move-out accounting

| # | Steps | Expected | Result |
|---|---|---|---|
| 4.1 | Note a family's budget; move them into a lot; evict them from the neighbourhood | Budget after eviction = before move-in ± any in-lot spending (no money vanishes) | |
| 4.2 | Move the same family between two lots a few times | Budget stays conserved each time | |
| 4.3 | Magic Town lot (90–92): select a family with MagiCoins | MagiCoin count/price shown is non-zero and correct | |
| 4.4 | Evict a family, then relaunch | The eviction and refund persisted | |

## 5. CAS — create / delete / edit

| # | Steps | Expected | Result |
|---|---|---|---|
| 5.1 | Create a family (name + ≥1 sim), accept, move in | Family appears; named correctly | |
| 5.2 | Try to accept a family with a blank name or > 8 sims | Accept disabled / extra sim refused; nothing corrupts | |
| 5.3 | Delete an unplaced family | Confirm dialog is localised; family removed; relaunch keeps it removed | |
| 5.4 | From a house with a family, choose **Edit Family** | CAS opens with that family's sims loaded (names, bodies, heads, personality) | |
| 5.5 | Change one sim's name/body/personality, accept | Change appears on the lot; other family members unchanged | |
| 5.6 | Before 5.5, inspect `UserData\Characters\` and `Characters\CASBackups\` | The edited sim's previous `User#####.iff` is backed up | |
| 5.7 | Edit a family, add a sim, accept | New sim is in the family; family size ≤ 8 | |
| 5.8 | Edit a family, open every sim, accept without changing anything | No crash; family still loads; note any clothing/appearance drift (known best-effort) | |
| 5.9 | Edit a family and cancel/back out | Confirm dialog; nothing written | |

## 6. Buy / build

| # | Steps | Expected | Result |
|---|---|---|---|
| 6.1 | Open Buy on a residential lot | Prices show `§<n>`; items you cannot afford are dimmed | |
| 6.2 | Hover/select an item | Query panel shows the correct price | |
| 6.3 | With a low budget, drag out an expensive wall | The tool shows it as unaffordable rather than silently accepting then refunding | |
| 6.4 | Build wall / floor / wallpaper / roof / terrain on each lot type | Tools work; costs are debited from the family budget (`§`, TS1) | |
| 6.5 | Open Buy/Build twice on each lot type (DT/Community/Vacation/Studio/Magic/Build) | No duplicated "other"/"all" buttons (the old in-place mutation) | |
| 6.6 | Roof subcategory | Rows have names (not blank) and selecting a roof applies it | |

## 7. Input

| # | Steps | Expected | Result |
|---|---|---|---|
| 7.1 | Mouse wheel zoom in/out, then use the desktop zoom buttons | All zoom through one path; buttons reflect the current level | |
| 7.2 | Launch with `-touch`, then tap vs drag | Tap threshold feels the same at 100% and (if available) 150/200% DPI | |
| 7.3 | Touch: two-finger rotate | Rotation graphic appears and fades out | |
| 7.4 | Touch: arch tools | Crosshair follows the pointer; the click button sits with the others | |

## 8. In-game settings

| # | Steps | Expected | Result |
|---|---|---|---|
| 8.1 | Options → Settings; change Anti-Alias, apply | Change applies live; no crash | |
| 8.2 | Change Lighting, apply | Lighting changes live | |
| 8.3 | Change Shaders / Transitions / Edge Scroll, apply | Shaders live; Edge Scroll live; Transitions noted as 3D-only | |
| 8.4 | Quit to desktop, relaunch | The saved settings are still in effect (not overwritten by startup) | |
| 8.5 | Revert on the panel before Apply | The staged changes are discarded | |

## 9. Save-compatibility fixtures

| # | Steps | Expected | Result |
|---|---|---|---|
| 9.1 | Load a lot saved by the pre-modernization build (if available) | Loads; no SV mismatch | |
| 9.2 | Save with the current build, then load with an older Simitone build | Older build either loads it or fails cleanly; `.bak` preserves the previous save | |

## 10. Addon content (3D remesh pack)

| # | Steps | Expected | Result |
|---|---|---|---|
| 10.1 | Build Release with `FSO.Remeshes-main/` present | Log shows `packaged 3250 files (2 conflicts resolved)`; `Content\MeshReplace\` has ~3,253 files | |
| 10.2 | Enter a lot in 3D mode (`-3d`), look at a remeshed object (e.g. a Christmas tree, a door) | The hand-authored mesh is used, not the auto-generated one | |
| 10.3 | Build with `-p:RemeshPackDir=` | Target is skipped; the game still runs with generated meshes | |
| 10.4 | Inspect the Release output size | ~562 MB (server stack + non-Windows natives trimmed) | |
| 10.5 | Launch with `-ide` after a trimmed build | Fails to start; rebuild with `-p:TrimSingleplayerOutput=false` fixes it (documented) | |

## 11. Progression (issue #8 QA)

The roadmap's QA bullets, one row each. This section exists to turn *believed* into
*observed*: most of these cannot be scripted, and several are known to be missing, so **a
FAIL here is a work item, not a surprise** — file the failing rows against
`docs/ADDON_PROGRESSION_PLAN.md` §4.3–4.9, which is where the owning side (**[S]** Simitone,
**[F]** FreeSO, **[V]** verify-only) is recorded.

Owner column key: **[S]** Simitone-side, **[F]** FreeSO-side, **[V]** believed working, unproven.

### 11.1 Core progression

| # | Steps | Expected | Owner | Result |
|---|---|---|---|---|
| 11.1.1 | Select a sim, open the job subpanel, send them to work; repeat on every track the install offers (all 9 teen/adult tracks, 8 childhood tracks) | `UIJobSubpanel` shows the six skill captions from TS1 table `136` with its English fallbacks, and "unemployed" is id 7; promotion and demotion both move the skill bars; a failed shift fires the fail sound and the career does not silently skip a rung | [F]/[S] | |
| 11.1.2 | Age a sim to a child; send them to the school lot, then the military school lot | Child sims walk with the child animation set and scale (`TS1ScalingSim`, 85); school day advances the school skill and the day counter; the lot controller's school bell ends the day and sends them home; returning to the adult age on schedule is the one thing that is **not** implemented — see 11.1.3 | [F] | |
| 11.1.3 | Force a child sim past the age-up threshold (cheat box, or edit the `NBR` `PersonsAge` byte and reload the lot) | `ChildToAdult` (39) is implemented as an **in-place** transform (`VMGenericTS1Call.TransformToAdult` + `VMTS1BodyTransformHelper`): the sim keeps their GUID, `user_NNNNN`, `CTSS`/body-string name, `FAMI` slot and every relationship, and only the body moves. Watch for, in order: (a) an adult skeleton at full size with a new head and body, same skin tone and same sex; (b) the sim walking with the **adult** walk set and no child scale; (c) the sim's clothes, handgroup and swimwear/formalwear all resolving (a missing mesh is the "f100mafit_original" naming, not a random texture); (d) **save, quit, reload** - the NBR's `PersonsAge`/`SkinColor`/`Gender`/`TS1ScalingSim` must have been mirrored or the transform is undone on the next lot load; (e) calling 39 again is a no-op, not a re-roll. Repeat 11.1.3 for a dog/cat with `PetToAdult` (40) and check the pet becomes a *human* of the same sex and skin (person data 65: 8/9/16/17 -> 0/1). Both need a lot-script playtest: which sim retail applies them to, and whether the surrounding script expects anything else, are only answerable by running the retail script | [F]/[V] | |
| 11.1.4 | Walk two sims into each other, then phone, then romance, then propose, then move in together, then have a baby, then let the baby grow | The whole chain works in order: the relationship subpanel's two tabs and the `RelationshipFameFilter` sorting are populated; a proposal adds the second sim to the family (call 4 `AddToFamily`); a baby arrives as a child of the family; the child's `NBR` is written. Watch for the age-up break at 11.1.3 — the chain "passes" up to a baby only | [F]/[S] | |
| 11.1.5 | Kill an adult sim (cheat box / the deadly plant); separately a child sim; separately a pet | An adult death shows the ghost and the family's roster keeps the dead neighbour listed; a child death does not remove the child from the family or corrupt the `NBR`; a pet death likewise. The ghost must be houseable and its portrait must still load | [F]/[S] | |

### 11.2 Downtown

| # | Steps | Expected | Owner | Result |
|---|---|---|---|---|
| 11.2.1 | Cook a meal on a restaurant lot, serve it, eat, leave a dirty plate, then return the next day with a different family | The full lifecycle holds across visits: the chef sims return to their scripted day, orders are produced, and dirty dishes clear overnight. Shipped as working upstream — this row is the regression guard, not a discovery | [V] | |
| 11.2.2 | Open Buy/Build on a Downtown lot, buy an outfit from the purchasable-clothing section, put it on the sim, change into it, save and reload | The outfit is applied through `VMTS1PurchasableOutfitHelper` (`SetSuit`/`GetSuitIndex`/`CountStackObjectSuits`) and survives a lot save + reload; the suit is charged once, not per change | [V] | |
| 11.2.3 | Hover a Downtown sim and use the auto-follow panel: **invite to Downtown** from home, then **take home** from Downtown | `Utils/AutoFollow` queues the request in the person's inventory (type 2, slots 10/11), sets `TS1AutoFollow` person data to the neighbour id, and issues the matching `VMGenericTS1CallMode` (`SpawnDowntownDateOfPersonInTemp0` / `SpawnTakeBackHomeDataOfPersonInTemp0`); the sim arrives at the Downtown date, and **Take Home** clears the standing request (`AutoFollow.ClearRequest`) rather than leaving the sim stranded. Refusal reasons (self / pet / ghost / off-lot / already travelling / already pending) must each be reported in the panel status, not silently ignored | [S]/[F] | |
| 11.2.4 | Look at each Downtown car sprite from the neighbourhood camera; walk the car lot; buy a car | The cars render and are named per-lot (StudioTown's is `NeighbourhoodAnimations.StudioCarPosition`; Downtown's are separately named constants, not the Studio ones). Expect the Downtown cars to be **missing or mispositioned** — the StudioTown overlay was ported, Downtown's were not. Try buying one: there is no Downtown car-purchase path wired | [S] | |

### 11.3 Vacation Island

| # | Steps | Expected | Owner | Result |
|---|---|---|---|---|
| 11.3.1 | On a vacation lot, press Save; quit to desktop; relaunch; travel back as the **same** family, then as a different one | A slot is written to `<UserDir>/TravelLots/House{NN}.fsov` (`TravelSave.SlotFolderName` / `SlotExtension` / `Magic` = `SLTT`) and restores via `TS1GameScreen.TryRestoreTravelSlot` → `SaveService` → `LotTravel.TryRestore`; the other family's slot is refused and the template loads with a logged warning. The shipped template `House{NN}.iff` must stay byte-identical (§3.7) — `LotPersistence.IsPersistableLot` is what guarantees it | [S] | |
| 11.3.2 | Visit a vacation lot that already holds a saved slot, as a family that has never been there; then visit as the owner family with the slot deliberately overwritten by a second family | The foreign slot is ignored (header names the family) and the shipped template loads; no leftover Sims, NPCs or bought objects from the previous visit appear. **Expect a leak** — revisit cleanup is a semantic the travel slot does not implement, it only restores the marshal | [F]/[S] | |
| 11.3.3 | Rent each of the three Vacation rentals in turn: the igloo, the snowman kit, the picnic basket | Each becomes the family's temporary home, the family's neighbourhood lot pointer is redirected, and returning home releases it. There is **no rental UI in Simitone** — the panels to drive these do not exist, so this row is expected to FAIL until a rental screen lands | [F]/[S] | |
| 11.3.4 | Complete vacation activities and watch for the "Vacation Score" and its prize | A Vacation Score accumulates and a prize is awarded at the threshold. No Simitone HUD shows it and the engine symbols are absent from the pinned tree (0 hits for `VacationScore`) — expect FAIL, and file as the missing score/prize presentation | [F]/[S] | |
| 11.3.5 | Buy a souvenir on Vacation, save, revisit, and check the family's inventory | The souvenir is owned and persists. No Simitone surface for it; check the `NBR`/inventory rather than a UI, and note the single `Souvenir` hit in the engine tree as engine-side only | [F] | |
| 11.3.6 | Check a hotel room into (book it), then have a **visitor** share the room as a roommate; then check out | A room is booked, the visitor↔roommate assignment takes effect, and both are released on checkout. Nothing in Simitone drives this — the hotel/roommate chain is engine-side and has no UI here, so this is a documented FAIL until the panel exists | [F] | |

### 11.4 Unleashed

| # | Steps | Expected | Owner | Result |
|---|---|---|---|---|
| 11.4.1 | Buy a pet in CAS ("Adopt a Pet") or in Buy mode; add it to the family; save, reload, evict and re-move-in | `CreatePurchasedPetsNearOwner` (32) spawns the pet next to its owner and the family takes it; the pet persists in `FAMI.FamilyGUIDs` and on the lot. Note that call 32 currently only runs `SelectWholeFamily`/`VerifyFamily` — **verify the pet is actually created** and treat a no-op as a FAIL | [F] | |
| 11.4.2 | Leave a pet unattended across a full in-game day; repeat three times | A pet never starves or dies from random motive decay; its needs are driven by the Unleashed pet scripts, not by the generic decay sweep. This is the specific bug the roadmap names — run it for at least 3 in-game days to see it | [F] | |
| 11.4.3 | Enter a pet in a pet show, then send it to training; check the skill bars and the show result | The show runs, a result is recorded, and training advances the pet's skill bars. No Simitone show/training UI exists (0 hits for `PetShow`) — the engine must do all of it via lot scripts, so if it does not, the row is a documented FAIL | [F] | |
| 11.4.4 | Put a pet-sim in a pet object (basket, bowl, scratching post), save, leave the lot, return, then release it | The pet enters, sleeps, exits on its own, and is not duplicated or lost across the save/load. The object-pet lifecycle is object-script driven; the failure to watch for is a **second** copy of the pet appearing after reload | [F] | |
| 11.4.5 | Rent a boat on Vacation and sail it; repeat with a second boat | A boat can be rented, spawned, driven and returned. No Simitone boat-rental surface (0 hits for `BoatRent`) — expect FAIL; the engine's own object must be the only path, so verify it spawns at all | [F] | |

### 11.5 StudioTown

| # | Steps | Expected | Owner | Result |
|---|---|---|---|---|
| 11.5.1 | Take a sim through the StudioTown career: build fame, then let it decay | The lot scripts write the fame **point total** (`TS1FameScore`, person-data 80) and calls 34 `PromoteFameIfNeeded` / 36 `DemoteFameIfNeeded` convert it to Star Power (81) and the high watermark (82) via `VMTS1FameHelper.Promote`/`Demote`/`RaiseWatermark`; the triple is mirrored into the `NBR`, and Simitone's relationship panel **fame tab** (`RelationshipFameFilter`, `MinCelebrityFame` = 1) then lists the sim. Also check `UIRelationshipSubpanel` stops logging `Relationship fame tab is empty` | [F]/[S] | |
| 11.5.2 | Use each of the three Fame special objects: skydiving, the aquarium, the satellite dish | Each is usable and drives its own Fame outcome. No Simitone-side handling exists for any of them (0 hits for `Skydiv`/`Aquarium`/`Satellite`) — they are pure lot scripts, so a FAIL means the scripts cannot reach the fame calls, which is the real finding | [F] | |
| 11.5.3 | Build an **obsessed fan**; have them visit the sim's **home** lot, then take a studio tour at the sim's **home** | The fan spawns, travels to the home lot, and the tour runs. Nothing in either tree implements the obsessed-fan travel or the home tour (0 hits for `ObsessedFan`) — expect FAIL; note the free-standing engine hooks (`TakeTaxiHook`, call 35, is a `break` with a "not sure where this one is called" comment, which is the closest thing) | [F] | |

### 11.6 Makin' Magic

| # | Steps | Expected | Owner | Result |
|---|---|---|---|---|
| 11.6.1 | Try to move a family into each Magic Town lot (90, 91, 92) with (a) enough § but too few MagiCoins, (b) enough MagiCoins but too few §, (c) enough of both | `UIHouseSelectPanel` reads the **family's** MagiCoins via `neigh.GetMagicoinsForFamily(moveIn ?? family)` and the lot's required count from table `134`, and shows the right one of the four captions (can afford / missing simoleons / missing magicoins / missing both) with **Enter House** disabled in the three failure cases. The family — not the lot — is the MagiCoin holder; getting that wrong makes every magic lot read "no magicoins", which is the regression this row guards | [S] | |
| 11.6.2 | Start a quest on a Magic Town lot; run it to completion; then fail one deliberately | The quest advances stage by stage, rewards on completion, and a failure resets or ends it as the script says. All engine-side via lot scripts — no Simitone surface. A quest that soft-locks (no reward, no reset) is the failure to record | [F] | |
| 11.6.3 | Open Buy/Build on a Magic Town lot; buy magic-usable furniture and a purchasable item; save and reload | Purchasable items are placed, charged once, and persist. Uses the same `SetSuit`/purchasables path as 11.2.2 plus the Magic Town catalogue; check the § **and** MagiCoin balances stay consistent across a save/load | [V] | |
| 11.6.4 | On a Magic Town lot, have a sim read the **spellbook** (`SpellBook.iff` → "Interaction - Read") | `UILotControl` routes dialog type 12 (`TS1Spellbook`) to `UIMakingMagicAlert`/`UIMakingMagicPanel`; the panel lists the real 25 spell titles read from `GameData/magic.iff` by `MakingMagicTables` (label "Spell Titles"), filters `IsPlaceholderTitle` entries out, pages at 5 per page, captions itself from "Magic Book Dialog Strings" with the family name substituted, and the chosen entry answers through `VMNetDialogResponseCmd` (`ResponseAccepted`/`ResponseTextFor`). The engine's `VMGenericTS1Call` **does** now have `FamilySpellsIntoController` (43): it copies every spell token (neighbourhood-inventory type 7) any member of the active family holds into the caller's own inventory, which is the only inventory the panel can read - so a charm learnt by one family member must show up for the rest. The copy is additive and deduplicated, so a repeat call must not add a second row. Still open: the answer itself only unblocks the thread; no cast is performed from the panel | [S]/[F] | |
| 11.6.5 | Repeat 11.6.4 with the **cookbook** (`Cookbook.iff`) | Same path with dialog type 15 (`TS1Cookbook`) and the recipe title table ("Nectar Bar/ Bakers Oven Titles"): 9 usable recipes after the "not used" placeholder, listed with their descriptions ("Nectar/Bakers Oven Description"). Same engine-side answer gap as 11.6.4 | [S]/[F] | |
| 11.6.6 | Leave a pyrite (npc) on a Magic Town lot and let it act for a full day with no player input | The pyrite picks its own goals from the magic scripts and completes them without the player directing it, instead of freezing or idling. Nothing in either tree names pyrite autonomy (0 hits for `Pyrite`) — the behaviour is entirely in the shipped lot scripts, so a pyrite that stands still for a whole day is a documented FAIL and the highest-value Unleashed/Magic gap to file | [F] | |

#### 11.8 Corrections from the 2026-09-27/28 disassembly audits (read before the rows above)

The rows above were written **before** the disassembly audits in `docs/REMAINING_ENGINE_PLAN.md`,
and several of their "expect FAIL / no UI exists" claims are wrong. These override them — do
**not** file the following as engine gaps:

| Row | Correction |
|---|---|
| 11.1.2 Child / school | Was genuinely broken: opcode **33 `find 5 worst motives`** was unregistered, so `PedPortal.iff` 4107 selected no need and the walk-to-school controller did nothing. **Fixed** (`VMFind5WorstMotives` + `VMTS1MotiveHelper`). The row now really tests the school day. |
| 11.2.3 Auto-follow | The path was already wired; the **only** real defect was that `ClearRequest` was never called, so a sim could hold both directions and the panel greyed out **both** buttons. **Fixed** — queuing one direction withdraws the other. |
| 11.2.4 Downtown cars | **There is no Downtown car overlay to fix.** The install ships **no car art under `Downtown\`** — the traffic is painted *into* `DScreen.bmp`, and the only animated Downtown layer is the sea/jetty (`dscreen00-04`, stopping at 4; `dscreen05` is a blank plate). The implemented overlay is that wave, plus a per-view frame filter. A "FAIL" here means something else. |
| 11.3.3 Vacation rentals | **No rental UI is needed and none is missing.** Igloo/snowman/picnic are pure lot-script content (`RentalShackGlobals`, `RentalClerkGlobals`); the row tests the shipped script. |
| 11.3.4 Vacation Score | **Fully implemented engine-side.** The score lives in attribute 5 of the hidden controller `VacationPedMarker.iff` GUID `0x9CC72382`, accrues from the family's average Fun motive via `Tuning[256..261] = [40,40,+6,+3,−2,−2]`, and persists in the `NGBH` inventory (type 6 GUID `0x842A8C3B`). **No Simitone HUD exists because retail had none.** |
| 11.3.5 Souvenirs | Implemented (director gift, check-to-give, treasure hunt, end-of-trip mood spawns); the souvenir threshold is a score of **150**. |
| 11.3.6 Hotel / roommate | Booking is `FrontDesks.iff` 4108 (prices §66/§72/§62 × party, Hotel Key `0x3AC022D6`; it **refuses outright** when `Count Beds` ≤ 2 — the "check in anyway?" offer is dead code). **"Roommate switching" is not a feature**: no roommate object or string exists anywhere in the install; rooming is emergent bed/gender-preference script. Do not expect a room UI. |
| 11.4.3 Pet shows / training | **Fully implemented engine-side** (zero unimplemented calls). Training is pet person-data 10/11/12 written by `SocialPetSolo`/`PetCommandSocials`/`PetSocials*`; the show scores against `Tuning[0..2] = [1500, 2250, 75]`. **Gameplay → Skills must be on** or the scores never rise and the show is unwinnable (retail-faithful — do not "fix"). |
| 11.4.5 Boat rental | Implemented (`RentalShackGlobals` 8204/8210, `RentalClerkGlobals`). No Simitone surface is expected. |
| 11.5.2 Fame special objects | **No unimplemented calls** (satellite dish and aquarium: zero; skydiving: call 6, implemented). No Simitone handling is expected. |
| 11.5.3 Obsessed fan | The generator `0x94400258` is **`Global=1`, so it is already auto-spawned** on every lot. The *celebrity-visits-your-home* event was most plausibly online-server-driven — it is a scope item, not an engine gap. |
| 11.6.4 / 11.6.5 Spellbook / Cookbook | Implemented: the panel reads the real 25 spell / 9 recipe titles from `GameData/magic.iff`, and **call 38 `GetTokensFromString` is now implemented** (the register contract is `Temp0` = an `STR` *chunk id*, `Temp1` = the index — not a literal id). One residual: the `[4] Temps[0] = Literal[1]` anomaly in `MagicMasterSpells.iff` 4098/4104 — if **every** combination reports "this combination does not exist", that instruction is the cause (see `REMAINING_ENGINE_PLAN.md` §5.1, V-MAG-2). |
| 11.6.6 Pyrite autonomy | **No "pyrite" exists** in any `TTA`/`STR`/`TTAB`, OBJD label or BHAV literal across all 11 `.far`s. Re-scoped to the Magic Growth scripts (blocked only by call 3, now implemented) and the quest controller. Do not file a pyrite row. |

Also landed since these rows were written: **call 3 `PullDownTaxiDialog`** (the last missing TS1
global call; now a real departure prompt), the `push_interaction` null guard, the
`NeighborPersonData` clamp, and the `0x7FFF`/`0xFFFF` sub-routine sentinel guard. The precise
per-target observation rows are `docs/REMAINING_ENGINE_PLAN.md` §6.1 (V-VAC-*, V-SS-*, V-PET-*,
V-MAG-*); use those in preference to the coarser rows above where they overlap.

### 11.7 Custom content

| # | Steps | Expected | Owner | Result |
|---|---|---|---|---|
| 11.7.1 | Drop in a CC **object** as a `.iff` + `.far` pair with custom animations and custom sounds, and place it on a lot | The object loads, its custom anims and sounds play, and a **bad** anim/sound reference degrades to a logged warning rather than a crash or a hang (the defensive frame loading and animation-failure reporting that shipped with the Wave A2/A4 work). Test one deliberately broken `.far` frame index too | [S]/[F] | |
| 11.7.2 | Drop in a **skin** CC (a body/head/handgroup set) and start a new sim in it; also edit an existing sim into it | The skin appears in the skin-tone picker, a new sim created in it looks right in 2D and 3D, and an existing sim is edited into it without corrupting the `User#####.iff` backup (`Characters\CASBackups\`) | [S] | |

---

Progression section added for the Wave H item recorded in
`docs/ADDON_PROGRESSION_PLAN.md` §11. Counts: **31 rows** — core 5, Downtown 4, Vacation 6,
Unleashed 5, StudioTown 3, Makin' Magic 6, CC 2. Every Result cell is deliberately empty:
these are the rows that turn into work items.

---

When a row fails, capture: the log tail, whether a `.bak`/`.tmp` was left behind, and the
lot type / neighbourhood. Then file it against the relevant section of
`docs/MODERNIZATION_PLAN.md` §4.
