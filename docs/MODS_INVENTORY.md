# Mods inventory — `D:\Program Files\Sims\Downloads` (read-only)

A disassembly-backed inventory of the 70 retail `.far` download lots in the TS1 install, and an
extraction of the **mechanics** they implement, ranked as candidates for native Simitone features.

Read-only investigation. Nothing outside this file was created or modified. No UI automation, no
mouse/keyboard/window automation, no game launch: every finding below is a static read of the retail
install at `D:\Program Files\Sims` plus a read of `FreeSO/TSOClient/**`.

---

## 0. Method and tooling

Same disassembler as `docs/REMAINING_ENGINE_PLAN.md` §0, unchanged
(`%LOCALAPPDATA%\Temp\opencode\bhavscan2`). Commands used: `ls`, `objids`, `objd`, `names`, `chunks`,
`ops`, `census`, `opcensus`, `sub`, `gcall`, `lits`, `bcon`, `dis`, `disraw`, `findobj`, `guid`,
`simi`, plus a per-file `op 29` scan. Conventions are §0/§0.1 of that plan and are re-used verbatim
(short pointers, scope table, `set_to_next` operand layout, `Tuning[]` decode).

### 0.1 Decode facts re-used / newly pinned here

Everything in `REMAINING_ENGINE_PLAN.md` §0.1 holds. Three things this pass added, all verified with
`disraw` (operand bytes read as bytes, never as shorts):

| fact | evidence |
|---|---|
| **Opcode ≥ 256 is a tree call.** `VMEntity.GetRoutineWithOwner` (`VMEntity.cs:661-683`): `< 4096` → `Global.iff`, `4096…8191` → the object's own BHAVs, `≥ 8192` → the semi-global `.iff` named by the object's `GLOB` chunk. So a mod's `GLOBAL 400` call is `Global.iff` BHAV 400, and its `GLOBAL 4105` call is its *own* BHAV 4105. | disassembly + `VMEntity.cs` |
| **`Tuning[N]` is `tableID = (N >> 7)`, `keyID = (N & 0x7F)`, plus a per-scope `TableIDOffsets` of `{4096, 8196… }` = `{4096, 8192, 256}`** (`VMMemory.cs:369-426`, `TableIDOffsets` at `:109`). Local tuning therefore lands on `BCON`/`OTF` chunk `4096 + (N>>7)`. | `SCDrinks` `Tuning[0..7]` → its own `BCON 0x1000` keys 0…7 = 0…7; `Tuning[131]` → `BCON 0x1001[3] = 3` |
| **`set_motive_deltas` (op 29) operand is `[DeltaOwner][MaxOwner][Motive][Flags][Delta:i16][Max:i16]`** — not four shorts. `Flags & 1` = clear, `& 2` = once. | `CopedFlowers` `[FF,06,05,01,…]` → DeltaOwner 255, MaxOwner Global, **Motive 5 (Energy)**, Flags 1 (clear) |
| **`relationship` (op 26) is used by the mods as a general per-object↔per-sim attribute store**, not only as a relationship. `VMRelationship` keeps a `List<short>` per pair, so `RelVar` > 0 is a free scratch channel. | `GuineaPig` 4132 `[00,00,01,00,19,00,01,00]` get / `[00,00,05,00,19,00,01,00]` set, repeatedly, on slot 0 |
| **`run_tree_by_name` (op 28) resolves a tree *by name*** out of a string table: `[StringTable:u16][StringScope][Unused][StringID][Destination]`, name = `GetString(StringID-1)` (`VMRunTreeByName.cs:63-92`). `Global.iff` **STR 0x012F is a 123-entry "verb on the stack object" table** (`setup`, `set conversation type`, `do group talk`, `Deliver Mail`, `adopt neighbor`, `visitor do knock`, …). | `Global.iff` 400 `[2F,01,00,07,02,…]`, `STR 0x012F` dump |

### 0.2 One data-model conflict found immediately (read this before §4)

`VM.cs:233-236`:

```csharp
GlobalState = new short[38];
GlobalState[20] = 255; //Game Edition. Basically, what "expansion packs" are running. Let's just say all of them.
GlobalState[25] = 4;   //as seen in EA-Land edith's simulator globals, this needs to be set for people to do their idle interactions.
GlobalState[17] = 4;   //Runtime Code Version
```

`Global[20]` is a **16-bit flag word**, and Simitone pre-sets *all sixteen bits*. At least **six
download objects use `Global[20]` as their own 1-bit-per-feature switch**, and every one of their
gates therefore reads "on" from lot load and every `setFlag`/`clearFlag` they perform is a no-op in
effect. Evidence: `CCPlugin` 4141 `[0] Global[20] flagSet 1`; `SCDrinks` 4124 `[20] Global[20] flagSet 3`;
`NoPetSign` 4097 `[0] Global[20] flagSet 6`; `ToiletUrinal_SP` 4100 `[1] Global[20] flagSet 2`;
`BookcaseMagic` 4130 `[0] Global[20] flagSet 8`; `UnSnacker`/`SCController` gate on it too.
Mitigation is available: globals **do** round-trip (`VMTS1LotState.cs:96`
`SimulationInfo.GlobalData = vm.GlobalState`), so the fix is a reserved-bit policy, not a save-format
change — see §5.1.

---

## 1. What is actually in the install

| | |
|---|---|
| download folders | **70**, each `<Name>.far` + `<Name>.ndx`/`.hot`/`.hsm`/`.hdb` |
| `.iff` entries carrying an `OBJD` | **87** (some lots ship several objects) |
| `BHAV` trees | **2 278** |
| `BHAV` instructions | **29 931** |
| calls into `Global.iff` trees (opcode ≥ 256, resolved name) | **565** |
| private subtree calls (opcode 4096…8191) | **975** |
| semi-global calls (≥ 8192) | 4 (`8244`, `8298` — `LightGlobals`/`DoorGlobals`) |
| sentinel "no such tree" calls | `0x7FFF` × 5, `0xFFFF` × 4 — already handled, see §5.4 |
| `generic_sims_call` (opcode 1) sites | **54**, using only **7** distinct call ids |
| raw opcodes below 256 used | 39, **all** inside the set `VMContext.cs` registers |
| lot-global controllers (`OBJD.Global = 1`) | **3** |
| `Person`-type objects | 9 — Santa Claus, 2 × Clown Cleanser, Grim Reaper Ditcher, 4 × Superstar, Cajun Vendor. (`SantaFX.iff` is `type=Normal`, not a person.) |

### 1.1 The three lot-global controllers

`VMTS1ActivatorNew` spawns any `OBJD` with `Global = 1` once per lot. All three are
`ObjectType = SimType` and are the cleanest template in the whole install for a *hidden background
service*:

| GUID | OBJD label | what its `main` does |
|---|---|---|
| `0x85E0C27F` | `Phone - Clown Catchers Plugin` | registers a service into the touch-tone phone's pie menu, gated on `Global[20] bit 1` |
| `0x7B3CCC04` | `NPC Santa - Controller` | hourly poll (`Global 366 'idle hours'`) of tree/fireplace/occupancy conditions; spawns Santa once per lot-day |
| `0x31437EA7` | `UnSnacker` | hourly sweep of every object on the lot, clearing the "unusable" flag (`StackObj[8] clearFlag 6`) from any object with an empty slot 29. `TTAB = 0xFFFF`, i.e. **no pie menu at all** |

### 1.2 `generic_sims_call` census over all 70 lots

| call | name | sites | state | where |
|---|---|---|---|---|
| 2 | `SetActionIconToStackObject` | 9 | impl | birthdaycake / ChristmasCookies / Shrimp `eat meal` |
| 4 | `AddToFamily` | 2 | impl | `RoseInteraction` `do move in` |
| 5 | `CombineAssetsOfFamilyInTemp0` | 1 | impl | `RoseInteraction` `do move in` |
| 10 | `DisableBuildBuy` | 3 | impl | CC M/F, Santa |
| 11 | `EnableBuildBuy` | 3 | impl | CC M/F, Santa |
| 14 | `HouseRadioStationEqualsTemp0` | 3 | impl | DormStereo `SubSwitch`, JukeBox `SubSwitch` / `Turn On Specific` |
| 15 | `MyRoutingFootprintEqualsTemp0` | 33 | documented no-op | `BedMMDownload` `get in` / `Sleep Single` |

Separately, opcode **37** `test_sim_interacting_with` (not a call) has one site, in
`CCPlugin.iff` 4115 `find service gender`.

**Conclusion that matters for planning: not one of these 70 lots needs a TS1 call that Simitone does
not already implement.** Every novel mechanic below is expressible with registered opcodes and
`Global.iff` trees. The gaps are data-model and UI, not VM surface.

---

## 2. Full catalogue

`G` = semi-global `.iff`; `Ctrl` = `OBJD.Global = 1`; trees = `BHAV` count.

### 2.1 Balloon / party / celebration

| download → entry | OBJD label | G | trees | what it is |
|---|---|---|---|---|
| `BalloonArch/BalloonArch.iff` | `Balloon Arch` | | 22 | multi-tile arch that recomputes its own sprite from a 4-direction neighbour bitmask, incl. diagonal-only joins (§4.14) |
| `PartyBalloons/PartyBalloons.iff` | `Balloons - Party` | | 17 | **invites up to 5 random neighbours over** (§4.2); child-count scaling; neighbour mood motives |
| `CClock/CuckooClock.iff` | `Clock - Cuckoo` | | 8 | hour-driven chime FSM (0/6/12/18), interest-gated reaction that can *remove* Fun, and a room-wide wake-up (§4.11, §4.16) |
| `FenceParty/FenceParty.iff` | `Fence - Party Lights` | | 13 | same adjacency system as the arch, with per-column dynamic-sprite lighting |
| `JackOLantern/jackolantern.iff` | `Jack-O-Lantern` | | 16 | `Dispose` → composter/incinerator handoff; `grab pumpkin`; `ComeAndSeeMe` |
| `Christmas2000/SCController.iff` | `NPC Santa - Controller` | Ctrl | 10 | §4.3 |
| `Christmas2000/SantaClaus.iff` | `NPC Santa Claus` | `PersonGlobals` | 20 | appear/disappear FX, `do rings`, `ho ho ho`, `Create Presents` weighted loot table, build/buy lock, `eat cookies` |
| `Christmas2000/SantaFX.iff` | `NPC Santa - FX` | | 2 | pure effect object |
| `Christmas2000/ChristmasPresent.iff` | `NPC Santa Present` | | 30 | gift object; `Give Gift`, `create bad gift` (3 joke objects), self-cleaning |
| `Christmas2000/ChristmasCookies.iff` | `Christmas Cookies` | | 100 | copy of the birthday-cake group-meal engine with a Santa gate |
| `Christmas2001/Christmas2001.iff` | `Christmas 2001 - Mistletoe` | `LightGlobals` | 7 | mistletoe: `Stand Below` + `Join` pushes a paired social onto whoever is beneath it (§4.20) |
| `BirthdayCake/birthdaycake.iff` | `Birthday Cake - Cake` | | 101 | group meal: `Serve`, `call everyone to come watch`, `Blow out candles`, per-plate objects, `Am I the Tragic Clown?` branch |
| `Turkey/turkey.iff` | `Food - Turkey` | | 101 | same group-meal engine + `Carve and Serve Turkey` (skill-gated) + `call everyone to come see turkey` |

### 2.2 Food counters, vending, consumables

| download → entry | OBJD label | G | trees | what it is |
|---|---|---|---|---|
| `SCDrinks/SCDrinks.iff` | `Vending Machine - SimCity Drinks` | | 33 | 7 flavoured sodas, each with its own motive trade-off, a malfunction state, and a kick that can electrocute you (§4.6) |
| `ShrimpCartReal/ShrimpCart.iff` | `Food Counter - Cajun` | | 50 | vendor counter with 5 dish types, a real queue (`Wait for …`, `Just Receive …`), an NPC attendant, and away-mode charging |
| `ShrimpCartReal/Shrimp.iff` | `Food - Shrimp` | | 53 | the food object: eat standing/sitting, `Drink Coffee`, spoil loop |
| `ShrimpCartReal/NPC_CajunVendor_drk.iff` | `NPC - Unleashed - Cajun Vendor - dark` | `PersonGlobals` | 9 | vendor NPC with its own idle loop, table-cleaning chore, and shrimp-stock check |

### 2.3 Games and minigames

| download → entry | OBJD label | G | trees | what it is |
|---|---|---|---|---|
| `TableSoccer/TableSoccer.iff` | `Table Soccer` | | 52 | 2–4 player, two teams, side-picking, ball on a dynamic sprite, relationship bump with every player, size-scaled away-mode fee (§4.10, §4.18) |
| `dart_board/dart_board.iff` | `Dartboard` | | 24 | 2-player turn-based throws, hit/miss decision trees, per-throw dynamic sprites, group talk while playing (§4.9) |
| `HungryHamster/HungryHamster.iff` | `Game - Hungry Hamster - A2` | | 15 | one seed pistol, tag-driven feed/clean/water outcomes, deviant-tag branch, prize tokens, autofollow join (§4.9, §4.7) |
| `SlotMach/slotmachine.iff` | `Slot Machine` | | 13 | $10/$100 bets, animated win/lose, pulls the nearest free machine over via autofollow, away charge |
| `Fountain3x3Deluxe2/Fountain3x3Deluxe2.iff` | `Fountain - African` | | 17 | `Play` (splash), `Make Wish` (Fun, fun-object refusal gate), `wash hands` (+Hygiene), and a **stool conjured for children** (§4.15) |
| `Fountain2x2Deluxe2/Fountain2x2Deluxe2.iff` | `Fountain - Japanese Dragon` | | 8 | same family, 2×2 |
| `FunHouseTrack4x1`, `FunHouseTrackLoop` | `Fun House - Track - …` | `FunHouseGlobals` | 4 + 4 | pure multi-tile connector with looping track audio |

### 2.4 Living creatures and NPCs

| download → entry | OBJD label | G | trees | what it is |
|---|---|---|---|---|
| `GuineaPg/GuineaPig.iff` | `Guinea Pig - Cage` | | 49 | **a full pet simulation**: hunger, dirt, mild/severe sickness with decay, contagion to nearby sims, death, autonomous play, feed/clean/join, bad-guy state (§4.13) |
| `HPPottyPack/ToiletStall_SP.iff` | `Toilet - Stall` | | 31 | clog → flood, unclog, repair, stall door, wash-hands; `Privacy` hooks (§4.8, §4.21) |
| `HPPottyPack/ToiletUrinal_SP.iff` | `Toilet - Urinal` | | 15 | gender-segregated urinal that walks over and "shoos" women out of the room (§4.21) |
| `HPPottyPack/ToiletPortable.iff` | `Toilet - Portable` | | 22 | same without the stall door |
| `CCPlugin/CCPhonePlugin.iff` | `Phone - Clown Catchers Plugin` | Ctrl | 35 | phone-callable service that spawns a scheduled NPC, gets paid, and rips paintings off the walls (§4.1) |
| `CCPlugin/CCFemale.iff`, `CCMale.iff` | `NPC Clown Cleanser – Female/Male` | `PersonGlobals` | 7 + 7 | the spawned NPC |
| `CCPlugin/CCfx.iff` | `Clown Cleanser FX` | | 2 | effect object |
| `JackOLantern/GrimReaperDBD.iff` | `NPC Grim Reaper - Doorbell Ditcher` | `PersonGlobals` | 8 | a Grim Reaper variant that rings the bell and leaves when ignored |
| `NoPetSign/NoPetSign.iff` | `Download - No Pets Sign` | | 4 | a sign that pets can be told to leave through: walk to the exit slot, despawn, thought bubble (§4.4) |
| `NPC_FreddiePrinzeJr`, `NPC_JonBonJovi`, `NPC_RichieSambora`, `NPC_SarahMcLachlan` | `NPC - Superstar – …` | `PersonGlobals` | 7 × 4 | four celebrity NPCs: `Interaction - For Autograph TEST`, `init traits`, `Find a random Table to Clean`, `CT - Cost` |
| `Moose/MooseHead.iff` | `Moose Head` | | 6 | wall ornament whose face is the **average mood of everyone on the lot** (§4.17) |

### 2.5 Social / conversational

| download → entry | OBJD label | G | trees | what it is |
|---|---|---|---|---|
| `FlowersRoses/RoseInteraction.iff` | `Flowers - Vase - Roses Interaction` | | **189** | **a 76-interaction socials menu** built entirely out of BHAVs, including propose → move-in → kids, fire, arrest, deliver-pizza, jealousy (§4.4) |
| `FlowersRoses/FlowersRoses.iff` | `Flowers - Vase - Roses` | | 18 | the vase: give-a-flower, gender-preference matching, room impact, romantic value |
| `FlowersRoses/trashflower.iff` | `Flower Trash` | | 23 | the discarded-flower trash object |
| `BedMMDownload/BedMMDownload.iff` | `Bed - Magic - Download- bottom left` | | 72 | 4-piece bed with sleep-relationship accrual, tuck-in, bed privacy, and a **dream system** (§4.8, §4.12) |
| `DormStereo/DormStereo.iff` | `Stereo - Dorm` | | 64 | music controller registration, 5 station genres, `Dance At Stereo Together` + `Join Dance`, `Play Next Song`, break/repair |
| `Jukebox/JukeBox.iff` | `Stereo - Jukebox` | | 63 | same music-controller + dance engine with 8 genres, charged usage, random lights |
| `ReclinerAOL/ReclinerAOL.iff` | `Recliner - Wicker` | | 24 | `Nap`, `Relax`, and the clearest small example of the group-talk handshake (§4.5) |
| `SofasAOL`, `SofaLoveSeatAOL` | `Sofa - Wicker AOL`, `Sofa - Loveseat - Pressed Metal` | | 34 + 34 | `Nap` with motive deltas and a nap-failure message; `Relax` on a sofa drives group talk |
| `PicnicUmbrellas/PicnicUmbrella1..3.iff` | `Picnic Basket - Umbrella - Template` | | 79 × 3 | two-sided picnic with a **state-driven pie-menu** (§4.22), sunbathing, toast, rub-oil, and a jealous partner |
| `ChairDiningAOL`, `ChairDining2AOL` | `Chair - Dining – Wicker / Pressed Metal` | | 19 + 19 | sit comfort accrual, `ComeAndSeeMe`, scoot sound |
| `DormChairLiving/DormChairLiving.iff` | `Chair - Living Room - Dorm` | | 14 | `CT - Relax` |
| `DormLampDesk/DormLampDesk.iff` | `Lamp - Table - Dorm` | `LightGlobals` | 10 | price-banded room score |
| `StairSweeping/StairSweeping.iff` | `Stair - Sweeping` | `DoorGlobals` | 29 | a `Portal`-type object with its own wall-change handling and a dog-aware traversal path |
| `UnSnacker/unsnacker.iff` | `UnSnacker` | Ctrl | 2 | hidden lot-global "make unusable snacks usable" service (§4.12) |

### 2.6 Plants, art, and surface objects

| download → entry | OBJD label | G | trees | what it is |
|---|---|---|---|---|
| `PlantCarnivore/PlantCarnivore.iff` | `Plant - Table - Carnivore` | | 24 | `Interaction - Dragon Eat` **eats the plant** and despawns it; `Stomp on` clears the plant's Energy boost; full water/wilt/dead/replant lifecycle; `Open Mouth` idle |
| `CopedFlowers/FlowersCoped.iff` | `Flowers - Outdoor - Coped - White` | | 23 | same + `Stomp on`, `Replant`, `find another plant to water`, `Force Gardener` |
| `Plants2/Plants2.iff` | `Plant - Floor - Palm Tree` | | 21 | same lifecycle without the carnivore behaviour |
| `DormBookcase/DormBookcase.iff` | `Bookshelf - Dorm` | | 29 | `Fun Object Test` + `Exit Skill Object`: the retail skill-object registration path, wired to read/study/cookbook |
| `BookshelfMagic/BookcaseMagic.iff` | `Bookshelf - Magic` | | 40 | the same bookcase with a **shop dialog** and a **MagiCoin currency** in the sim's inventory (§4.7, §4.23) |
| `DormSculpture/DormSculpture.iff` | `Sculptures - Dorm` | `ArtGlobals` | 9 | `View`, kinetic variant, per-piece room impact |
| `SculpturesDeluxe2/SculpturesDeluxe2.iff` | `Sculptures - African Vase` | `ArtGlobals` | 17 | multi-piece sculpture with per-piece init and on/off lighting |
| `PaintingsDeluxe2/PaintingsDeluxe2.iff` | `Painting - African Mask` | `ArtGlobals` | 16 | value + room-impact + fun motive, adjacency check |
| `Painting1AOL`, `Painting2AOL` | `Painting - Pastel Beach`, `Painting - Seascape Shadowbox` | | 11 + 11 | same, cheaper |
| `DormPainting/DormPainting.iff` | `Painting - Dorm` | `ArtGlobals` | 3 | two-piece wall art |
| `Topiaries/Topiaries.iff` | `Topiaries - Dolphin` | | 3 | pure decoration |
| `ColumnFrench/ColumnFrench.iff` | `Column - French` | | 4 | cleanable decoration |
| `Moose/MooseHead.iff` | `Moose Head` | | 6 | see §2.4 |
| `TableEndMMDownload`, `TablesEndDeluxe2`, `TablesCoffeeDeluxe2`, `TableDiningAOL`, `TableDining2AOL` | tables | | 16/15/12/20/20 | surface objects with `food slot test`, chair pairing, `generic place on surface`, `update function scores` |
| `WallLite`, `LampsWallDeluxe2`, `LampsCeilingDeluxe2`, `LampGardenUnleashed` | wall/ceiling/garden lamps | `LightGlobals` | 4/3/3/3 | lighting only |

### 2.7 Lighting with real behaviour

`LampWallAOL`, `LampWallAOLSconce`, `LampWallBar`, `LampAOLCandle`, `LampHulaUke` (27/27/27/35/19 trees).
All five share: turn on/off, **burn-out with a price-banded escalating repair cost**, a `Fix bulb`
interaction that can **electrocute** the repairer, an occupancy-aware auto-off, and — for
`LampHulaUke` / `CuckooClock` / `MooseHead` — a **preference-gated reaction animation**. §4.5, §4.8,
§4.16.

### 2.8 Trash

`TrashPack/BlackTrash.iff` (`Trash Can Inside`) and `TrashElephantFoot1.iff` (`Trash Can Inside clone`),
21 trees each: `Empty trash` spawns a **real trash-pile object on the floor with a size parameter**
via `Global 343 'Create Trash Pile By Size'`, plus `toss trash outside`, `flip trashcan`, `Force Full`.

---

## 3. Engine surface the downloads actually use

Everything below is inside the surface Simitone already registers, which is the single most important
finding of this pass.

**Opcodes < 256 (39 distinct, all registered in `VMContext.cs`):**
1 `generic_sims_call` · 2 `expression` · 4 `grab` · 6 `change_suit_or_accessory` · 7 `refresh` ·
8 `random_number` · 11 `get_distance_to` · 12 `get_direction_to` · 13 `push_interaction` ·
14 `find_best_object_for_function` · 15 `breakpoint` · 16 `find_location_for` ·
18 `remove_object_instance` · 20 `run_functional_tree` · 21 `show_string` · 22 `look_towards` ·
23 `play_sound` · 24 `old_relationship` · 25 `budget` · 26 `relationship` · 27 `goto_relative` ·
28 `run_tree_by_name` · 29 `set_motive_deltas` · 31 `set_to_next` · 32 `test_object_type` ·
35 `special_effect` · 36 `dialog_private` · 37 `test_sim_interacting_with` · 41 `set_balloon_headline` ·
42 `create_object_instance` · 43 `drop_onto` · 44 `animate` · 45 `goto_routing_slot` · 46 `snap` ·
47 `reach` · 48 `stop_all_sounds` · 49 `stackobj_notify_out_of_idle` · 50 `change_action_string` ·
51 `manage_inventory`.

**`Global.iff` trees the mods lean on (name → count, all resolved):**

| tree | name | n | role |
|---|---|---|---|
| 285 | `Fun Object Test` | 13 | the retail fun/skill-object entry gate |
| 405 / 415 | `Exit fun object` / `Exit Skill Object` | 8 / 2 | session teardown, with a reason bubble |
| 408 | `Refuse Fun Object?` | 8 | the "I'm not in the mood" refusal (8 objects incl. every painting) |
| 286 | `show skill progress` | 2 | skill progress UI |
| 272 / 273 | `Standard entry` / `Standard Exit` | 36 / 36 | routing handshake |
| 280 / 281 | `idle` / `Wait For Notify` | 70 / 32 | idle + push hand-off |
| 302 / 343 / 347 / 350 | `Clean` / `Create Trash Pile By Size` / `dirty inc` / `repair finished` | 10 / 3 / 6 / 1 | cleaning, trash, wear-and-tear |
| 324 / 332 / 333 | autofollow: `Get Stack Object's …`, `Push Interaction on My …`, `Get My … in Temp 0` | 1 / 8 / 4 | **autofollow** (see §4.5) |
| 334 / 463 | `Charge for Downtown` / `Charge for Away from Home` | 5 / 2 | away-mode fees, party-size scaled |
| 306 / 307 / 369 | `Check Schedule` / `Schedule` / `Schedule with param` | 5 / 3 / 4 | background polling + NPC scheduling |
| 366 / 367 / 370 / 425 | `idle hours` / `idle minutes` / `animated idle for` (+ its 1.5 variant) | 11 / 2 / 17 | background ticks and animated idles |
| 368 / 374 / 419 | `Leave` / `set to leave` / `Wander` | 2 / 1 / 1 | NPC exit |
| 371 / 372 | `Start Jealousy` / `End Jealousy` | 1 / 1 | romance/jealousy state |
| 383 / 384 | `Create Stool` / `Remove Stool` | 1 / 1 | child reachability |
| 385 | `NPC Get Paid` | 1 | service NPC payout |
| 392 / 393 / 394 / 396 | `do electrocution` / `kill person for good` / `create flood under me` / `create flood in front of object` | 4 / 1 / 1 / 1 | hazard + flood |
| 398 | `route failure feedback` | 5 | graceful routing failure |
| 288 | `In Use?` | 3 | occupancy test (toilets, shrimp cart, coped flowers) |
| 314 | `Is Stack Object held by person?` | 2 | "is anyone carrying this" (SlotMach, UnSnacker) |
| 321 / 363 | `Is lot a Residential Lot?` / `Am I a Child?` | 17 / 11 | the two cheapest predicates in the whole set |
| 399 / 413 | `wake everyone up in stack objects room` / `… with balloon` | 4 / 1 | sleep interruption |
| 400 / 401 / 402 / 403 | `group talk create/do/add person/exit person` | 9 / 10 / 10 / 10 | **group conversation** |
| 406 | `drop object from my slot to floor` | 6 | dropping carried objects |
| 416 | `Relationship Impact for Join` | 1 | joining a game is socially weighted |
| 421 / 426 / 427 / 442 | `Return in Temp 0 & 1` (+ its multi-tile variant) / `Get Available Family Kid` / `… Adult` | 2 / 1 / 1 / 1 | family- and geometry-aware minigames |
| 429 / 438 | `Turn Off All Dyn Sprites` / `Set Multi-Tile Wall Placement Flags` | 2 / 3 | multi-tile plumbing |
| 435 | `Prize Token - Add - 1.5` | 1 | minigame prize economy |
| 452 | `Allow in Locations only` | 2 | venue restriction |
| 497 / 504 / 505 / 506 | `Magic - Find/Add/Remove Token ( Invisible )` | 1 each | invisible inventory tokens |

**`Global.iff` STR 0x012F (123 entries)** is the shared "verb" table reached by
`run_tree_by_name` with `StringScope = 1`. Of the 206 `run_tree_by_name` sites across the 70 lots,
**80 are global-scope**, concentrated in 16 objects (`RoseInteraction` 27, `ChristmasCookies` 25,
`birthdaycake` 5, `turkey` 5, `FlowersRoses` 3, `ToiletUrinal_SP` 3, `Christmas2001` 2,
`GrimReaperDBD` 2, and one each in seven others). The verbs they reach for are
`setup`, `set conversation type`, `do group talk`, `Deliver Mail`, `adopt neighbor`,
`visitor do knock`, and the `Global 0x012F[551]` entry that the `help display` trees use.

**`Objects.far / HelpSystem.iff`** (`GUID 0x8963D7C1`, `Global = 1`, 75 trees) is the target of
`Global 297/309/310 'help display - …'`: `Temps[0] = Tuning[17580…17586]` (a help-string id),
`Temps[1] = 700` (duration ms), then `run_tree_by_name` on it. **Six** download objects raise help
bubbles this way — `CopedFlowers`, `FlowersRoses`, `GuineaPg`, `HPPottyPack` (broken object),
`PlantCarnivore`, `Plants2` — and the retail `do electrocution` tree does too.
**This is a ready-made, data-driven advice/notification channel.**

---

## 4. Useful mechanics — the list

Each item: **what** it does, **how** (tree + opcode evidence), **what Simitone would need**, and
whether that support **already exists**.

Status key: **Yes** = fully data-driven, works as shipped ·
**Partial** = the path exists but a specific engine behaviour is off or an input is missing ·
**No** = the behaviour lives entirely in BHAV data and would need new native code to be *exposed*
(it would still run as a mod object).

### 4.1 A phone-callable service that spawns a scheduled, paid NPC — **Yes**

* **What.** The player picks "Be Cleansed" from the touch-tone phone. The lot checks the Tragic Clown
  is present, spawns a Clown Cleanser NPC, schedules him, charges §500 up front
  (`budget [1795,500,1,0]`), and he rips the paintings off the walls before leaving.
* **How.** `CCPhonePlugin.iff` 4141 `CT - Phone Plugin Menu` is *itself* an entry on the
  touch-tone phone's menu, and it renames itself in place. It gates on `Global[20] bit 1`, then:
  `[5]` `set_to_next` with `SearchType = 4` (by GUID) and GUID `0x60344A9B` = **`NPC Tragic Clown`**
  (`ExpansionPack/TragicClown.iff`) — the service is only offered while a Tragic Clown is on the lot;
  `[10]`/`[18]` use `SearchType = 7` (neighbour-of-type) with the Clown Cleanser GUIDs
  `0x59255DCB` / `0xB030C4F0`, and `[13]`/`[21]` repeat them with `SearchType = 4`, to check that a
  cleanser is not already here; then `[4]` `change_action_string [302,0,1,0]` renames the running
  phone interaction from "Answer" to "Be Cleansed". 4134 `CT - Plugin Call` then rolls a 1–3 outcome
  and `dialog_private`s every line.
  `CC - Get Paid` uses `Global 385 'NPC Get Paid'`; `CC - kill painting` (4118, 31 instructions) does
  `set_to_next` over wall art and `remove_object_instance`.
* **Need.** Nothing new. Person-object `create_object_instance` (with `UseNeighbor` →
  `InheritNeighbor`), `budget`, `dialog_private`, `change_action_string` and `set_to_next` search
  types 4/7 are all implemented.
* **Note.** A `Global = 1` controller *renaming another object's in-flight pie-menu entry* is a
  clean, general "add a menu entry to an existing object" pattern — and it needs no host object to
  know the mod exists. That is arguably more useful than the service itself.

### 4.2 "Invite random neighbours over" — **Yes**

* **What.** Up to five randomly chosen neighbours are scheduled to visit the lot, filtered by age,
  own work hours, household identity, and whether they are already here.
* **How.** `PartyBalloons.iff` 4110 `need neighbors?` counts on-lot sims whose
  `StackPersonData[32] == Tuning[16897]` (= global `BCON 0x104[1] = 1`, person type *visitor*) up to 5.
  4104 `bring a neighbor` iterates **neighbour ids** with `set_to_next` `SearchType = 5`
  (`Flags 0x85`), then per neighbour calls 4105 `can this person visit?`
  (`NeighborInStack[2]` age ≥ 18, `MyAttrs[1]` not blocked, `NeighborInStack[6]` not equal to
  `NeighborhoodData[2]`/`Global[10]`), 4106 `is person at work?` (compares
  `NeighborPersonData[56]/[57]` against the *player's* `JobData[12]/[13]`, handling midnight wrap),
  4108 `is neighbor already over?` (`StackPersonData[31] == candidate`), and finally
  `Global 369 'Schedule with param'` with `Temps[0] = Tuning[16385]` (= 1). The same five trees are
  copy-pasted verbatim into `turkey.iff` and `birthdaycake.iff`.
* **Need.** Nothing. `set_to_next` search type 5, `NeighborInStack` (scope 24), `NeighborPersonData`
  (scope 32), `JobData` (scope 33) are all present — and `VMMemory.cs:132-140` already clamps
  scope 32 into the 88-short neighbour array.
* **Caveat.** This is the cleanest example in the install of a mechanic that *only* needs
  `NeighborInStack` to be meaningful. Worth a playtest: it is the one path that reads neighbourhood
  data heavily.

### 4.3 A one-per-day scripted visitor driven by a lot-global controller — **Yes**

* **What.** On any lot with a Christmas tree, a table of cookies and at least one awake adult, Santa
  appears once per in-game day, does his routine, drops presents, eats the cookies and leaves.
* **How.** `SCController.iff` (`Global = 1`) 4104 `main - controller` calls
  `Global 366 'idle hours'`, checks `Global[7] == MyAttrs[4]` (already visited today), and calls
  4097 `Generate Santa`, which does `create_object_instance` of `0x7B6583CF` (`SantaClaus.iff`),
  `find_location_for [0,1,0,0]`, `remove_object_instance` of the *previous* Santa, then
  `MyAttrs[4] = Global[7]`. The NPC's own 4096 `Main` alternates
  `call 10 DisableBuildBuy` / `call 11 EnableBuildBuy`, plays `Global 4109 'appear effects'` and
  `special_effect [10,513,9,0]`, and calls 4108 `Create Presents` — a weighted loot table
  (`random_number 0..100` against thresholds 98/96/94/92/80/62/40) that spawns one of seven present
  objects and places each with `find_location_for [0,5,0,0]`.
* **Need.** Nothing. `Global = 1` controller spawn is `VMTS1ActivatorNew`; both build/buy calls are
  implemented and signal `VMEventType.TS1BuildBuyChange`.
* **Native-interest.** This is the template for "seasonal/event content that runs itself".

### 4.4 A 76-interaction socials menu authored entirely in BHAV — **Yes (as data) / No (as native)**

* **What.** `FlowersRoses/RoseInteraction.iff` is **189 BHAVs** behind a **76-entry pie menu**:
  Talk, Join Conversation, Give/Receive Flower, Flirt/Be Flirted With, Kiss/Be Kissed, Tell Joke,
  Compliment, Hit, Insult, Join Fight, Swear, Call Over, Ask to Leave, Hug, Give Gift, Slap, Tease,
  Scare, Dance, Brag, Tickle, Cheer Up, Back Rub, Entertain, Greet, **Propose**, **Move In**,
  Watch Wedding, Apologize, Deliver Pizza, **Fire**, **Collect Kids**, Arrest.
* **How.** Topic selection (`select topic I like.` 4099, 39 instructions) → per-topic
  `Person A`/`Person B` trees → `adjust my relationship to speaker/listener` (4137/4138, 62
  instructions each) → `Global 371/372 'Start/End Jealousy'` for the romance branch →
  `do move in` (4241) which walks the object list, filters on `NeighborPersonData[61]`
  (family number) and `[58]` (age), and then calls **`call 4 AddToFamily`** and
  **`call 5 CombineAssetsOfFamilyInTemp0`** — i.e. it really does move a neighbour into your
  household — then `Global 307 'Schedule'`. `have baby` (4225) and `Collect Kids` (4272/4273) hang
  off it.
* **Need.** The two TS1 calls are the only "new" surface and both are implemented. Everything else
  is `expression`, `relationship`, `animate`, `dialog_private`, `push_interaction`,
  `run_tree_by_name`.
* **Native-interest.** This is the single best proof in the install that a *content* team can
  author a whole social layer with no engine work. If Simitone wants first-class group socials
  beyond what `HDSI.iff` provides, this file is a working reference implementation.

### 4.5 Autofollow, and pushing an interaction onto your companion — **Yes**

* **What.** One sim is the "autofollow"; any other object can pull them across the lot, find the
  nearest free instance of itself, and queue an interaction on them.
* **How.** `Global 333 'Get My Autofollow Sim in Temp 0'` reads `StackPersonData[77]`, searches
  avatars with `set_to_next` `SearchType = 1`, negates the id and compares with the anchor object.
  `Global 332 'Push Interaction on My Autofollow'` is a 21-way `switch` on `Params[0]` mapping to 21
  HDSI verbs. Objects then do the search themselves: `ReclinerAOL` 4114 and `slotmachine` 4108 both
  call `Global 333`, then `set_to_next` over slot machines/recliners keeping the nearest by
  `get_distance_to [0,769,11,0]`, then `Global 332` with the target id. `HungryHamster` 4110 walks
  the multi-tile parts of its own game with `SearchType = 3` and pushes param 1. `TableSoccer` 4139
  `Pick a side` pulls the companion onto a team.
* **Need.** `VMPersonDataVariable.TS1AutoFollow = 77` is already named and modelled. The whole
  mechanism is `Global.iff` + `set_to_next` + `push_interaction`.
* **Residual risk.** `VMPushInteraction` does not null-guard `interactionSource`
  (`REMAINING_ENGINE_PLAN.md` §1.3). Autofollow is exactly the path that pushes onto a *third* party,
  so that guard is worth adding before leaning on this.
* **Do not conflate with `Client/Simitone/Simitone.Client/Utils/AutoFollow.cs`.** That helper is the
  *player-facing* Downtown invite / take-home pair (TS1 calls 19 and 20). This section is the
  *in-lot* companion handle — person data 77 plus `Global 332/333` — which objects use to drag a
  second sim somewhere. The two share a name and nothing else; both are worth having.

### 4.6 A consumable that trades needs — **Yes**

* **What.** A vending machine selling seven "Disaster Fizz" flavours. Each flavour has its own
  motive trade-off; drinking repeatedly is diuretic; there is a malfunction state; and kicking the
  machine can electrocute you.
* **How.** `SCDrinks.iff` 4124 `Buy Soda - Effect` (22 instructions) switches on the flavour id
  (`Tuning[0..6]` → its own `BCON 0x1000`) and applies:
  `MyMotives[5] += 15 or 30` (Energy, scaled by whether `Global 4116` succeeded),
  `MyMotives[7] += 10` (Hunger), `MyMotives[9] -= 35 or 20` (Bladder),
  `MyMotives[15] += Tuning[131] = 3` (Fun), `MyMotives[6] += Tuning[133] = 5` (Comfort).
  Personality gates: `MyPersonData[7] > 6` (Neat) and `MyPersonData[20] > 6`
  (`AllowedSocialAndPuppeteering`) pick the bigger values. A bit is latched with
  `Global[20] flagSet 3` so the machine remembers the last flavour. 4106 `Interaction - Kick` is a
  39-instruction state machine that can roll 101/102 → sound → `Global 392 'do electrocution'`.
* **Need.** Nothing. Motive slots 5/6/7/9/15 are the eight TS1 needs FSO already models
  (`VMTS1MotiveHelper.Needs`), and `change_suit_or_accessory` (used by `do electrocution` to swap
  in a scorched outfit) is implemented.
* **Native-interest.** A clean template for "a mod can add a new consumable with its own balance
  table and its own failure mode".

### 4.7 A custom currency living in the sim's inventory — **Yes**

* **What.** MagiCoins: denominations of 1 / 10 / 100, a hard 30 000 cap, family-only access, a
  store/withdraw pair of pie-menu entries with `$Local:1` in the label, and a multi-select shop dialog.
* **How.** `BookcaseMagic.iff` 4130 does `manage_inventory [00,07,03,09,EC,1B,E8,99]` (AddToken,
  type 7 = magic, count from Temp0, GUID `0x99E81BEC` = `ExpansionPack7/TokensMagic.iff`
  *Magic Token - Currency*) to deposit and `manage_inventory [03,07,11,0D,…]` (FindToken) to read the
  balance; 4128 `Interaction - Inventory` is a **166-instruction, 65-local shop** that opens
  `dialog_private [0,11,-32768,512]` — the `0x8000` operand bit is the TS1 *checkbox* dialog flag.
  Pie-menu labels live in `STR 0x012E[6..7]`: `"Store MagiCoins…/Add $Local:1"`,
  `"Get MagiCoins…/Get $Local:1"`. `Global 497/504/505/506` manage the invisible ingredient tokens.
* **Need.** Nothing. `VMTS1InventoryOperations` implements AddToken / RemoveToken /
  RemoveTokenAtIndex / FindToken / SetToNextTokenOfType and auto-creates the `NGBH` inventory.
* **Gap.** The balance is never *displayed* as a number anywhere — `docs/ADDON_PROGRESSION_PLAN.md`'s
  "no Vacation HUD" finding generalises: if Simitone wants a wallet/ledger UI for mod currencies,
  that UI does not exist and is a real feature to build.

### 4.8 Wear-and-tear, escalating repair cost, and skill-weighted electrocution — **Yes**

* **What.** Lamps wear out. The repair bill scales with the lamp's price band, and a Handyman can
  still get electrocuted fixing one.
* **How.** `LampWallAOL.iff` 4127 `Break` sets the state to off and adds
  `Tuning[128/129/130] + Tuning[131]` to `StackObj[7]` (the stack's repair cost), choosing the base by
  `StackAttrs[3] == Tuning[0]/[1]/[2]` (price band), then `StackObj[15] = 100` and `refresh`.
  4103 `Interaction - Fix bulb` computes the outcome as
  `Local[1] = 10; Local[1] -= MyPersonData[12]` (Mechanical skill) `[12] Local[1] /= 3`, then
  `random_number [0,8,139,26]`; a failure goes to `Global 392 'do electrocution'`, which applies a
  scorched outfit via `change_suit_or_accessory` and, if the sim was in their swimwear
  (`MyPersonData[8] == 1`), escalates to `Global 393 'kill person for good'`.
  `CopedFlowers` 4103 `Stomp on` clears the resulting Energy boost with
  `set_motive_deltas […,Motive 5,Flags 1]`.
* **Need.** Nothing for the wear model. **One engine fix** — see §5.2.
* **Native-interest.** "Objects degrade, the bill grows, and a skilled repairer is still gambling"
  is a whole gameplay loop that only needs the correct `set_motive_deltas` clear.

### 4.9 A reusable multiplayer-minigame framework — **Yes**

* **What.** Three of the 70 lots (table soccer, dart board, hungry hamster) independently converge
  on the same architecture.
* **How (the shared shape).**
  * `TTAB` = exactly two entries, `"Play"` and `"Join"`.
  * Player slots in `StackAttrs[0..3]`, turn/phase in further `StackAttrs`.
  * `Global 281 'Wait For Notify'` + `StackAttrs[x] == MyObj[11]` to hand over between players.
  * `Global 400/401/402/403` group talk so the players talk while they play.
  * `Global 332/333` autofollow to drag a companion into a team.
  * `set_to_next` `SearchType = 3` (multi-tile parts) to walk players to their own position.
  * `DynSprite[0]` writes to animate a shared prop (the ball).
  * `Global 285/405/415` for the fun/skill-object session, `Global 408` for the refusal.
  * `Global 435 'Prize Token - Add'` and `Global 421/426/427` for rewards and family-aware roles.
  * `TableSoccer` 4118 `Add Social` writes relationship slot 0 between the table and **every one of
    the four players** — the "you get closer to everyone you played with" model, in six lines.
* **Need.** Nothing.
* **Native-interest.** Three independent implementations of the same pattern is strong evidence it
  should be a *framework* in Simitone (a `Minigame` base class: slots, turn arbitration, join/exit,
  reward hook) rather than three bespoke objects.

### 4.10 Group conversation, driven from a mod — **Yes**

* **What.** Sims sitting on a sofa, playing darts, or eating a group meal hold a real multi-party
  conversation with turn-taking.
* **How.** `Global 400 'group talk create'` `create_object_instance` of
  **`ExpansionShared/HDSI.iff` = `Social Interactions - Hot Date`** (GUID `0xFD4FB8E4`) out of world,
  then `run_tree_by_name` on it for `set person to init with` / `set conversation type` /
  `do group talk` (HDSI 4151 `do group talk`, 81 instructions). `Global 401/402/403` do/join/leave
  and are themselves thin wrappers over the same object. The canonical small caller is
  `ReclinerAOL.iff` 4115-4118, six instructions each.
* **Need.** Nothing. `HDSI.iff` ships in `ExpansionShared.far`; the group-talk handle is
  `MyPersonData[77]`, already named `TS1AutoFollow`. `grep -r GroupTalk FreeSO/TSOClient` returns
  **zero hits**, which is correct and reassuring: the whole system is data, not code.
* **Caveat worth a playtest.** `Global 401` writes `Local[1] = Local[0]` before
  `Temps[1] = Local[0]`, and `Global 400` loops `[5] → [1] → [6]` — an unusual control shape. It is
  retail data, so it presumably works, but it is the one piece of this list with zero test coverage
  in the existing docs.

### 4.11 An object that wakes an entire room — **Yes**

* **What.** The cuckoo clock chimes on the hour; at midnight it wakes everyone in the room, one at a
  time, with an optional thought balloon.
* **How.** `CuckooClock.iff` 4098 `process` runs an hour FSM off `Global[0]`, sets
  `MyAttrs[2] = 1`, plays 6 or 12 chimes with a 5-tick gap, then at instruction `[27]` calls
  `Global 399 'wake everyone up in stack objects room'` with `Params = (0, 1)`. `Global 399` itself
  checks `Global 321 'Is lot a Residential Lot?'`, filters
  `StackPersonData[58] >= 18` and `StackPersonData[65] < 2` (not a child), and raises
  `set_balloon_headline [1,1794,150,257]`. `GuineaPig` 4131 uses the balloon variant (413).
* **Need.** Nothing. `VMRuntimeHeadline` / `HeadlineRenderer` are implemented.
* **Native-interest.** "Noise events that interrupt a room" is a general system (alarm clock,
  argument next door, child crying) that only exists here because one download needed it.

### 4.12 A hidden, menu-less, lot-global background service — **Yes**

* **What.** `UnSnacker` ("Makes unusable snacks usable") — a `Global = 1` `SimType` object with
  `TTAB = 0xFFFF`, so it never appears in a buy menu, which idles hourly and clears the
  "unusable" flag from any flagged object whose slot 29 is empty.
* **How.** `unsnacker.iff` 4106 `main`: `Global 366 'idle hours'`, then
  `set_to_next [GUID 0, Flags 0x84]` (all objects), `StackObj[8] flagSet 6`, `Global 314
  'Is Stack Object held by person?'`, `StackObj[29] < 0`, then `StackObj[8] clearFlag 6`.
* **Need.** Nothing. `VMTS1ActivatorNew` spawns `Global = 1` objects; `VM.cs:235` even notes
  `GlobalState[25] = 4` is needed "for people to do their idle interactions".
* **Native-interest.** This is the cheapest possible native feature: a housekeeping pass. A native
  version could reset one-shot state, re-home orphans, expire stale buffs, etc. **But** note the
  `Global[20]` collision — this object's gate is unreadable for the same reason as §0.2.

### 4.13 A living pet with its own needs, illness and contagion — **Yes**

* **What.** The guinea pig gets hungry and dirty, can fall mildly or severely ill, **infects nearby
  sims**, can die, plays autonomously when a sim is nearby, and can be fed, cleaned and joined.
* **How.** `GuineaPig.iff` (49 trees) is a full state machine: 4110 `inc - hunger`,
  4107 `inc - dirt`, 4132 `process - sickness update` (35 instructions, decaying sickness with
  `random_number` severity rolls), 4131 `spread to Sims around me - sickness` (15 instructions:
  iterate avatars within `get_distance_to [0,6401,0,0]`, require `StackMotives[11] == 0` (awake) and
  `Temps[0] < 4` cooldowns, then write the sim's own exposure counter), 4129 `Interaction - Die -
  Sickness`, 4130 `create sickness`, 4143 `Interaction - Autonomous Play`, 4137 `choose state - cage`
  dispatching to `simulate - eating / sleeping / drinking / rustle / looking / on wheel / dead`.
  Per-pair state is stored in the **relationship matrix** (op 26, slots 0 and above) between the
  cage and each sim — see §5.3.
* **Need.** Nothing. `relationship` is a per-pair `List<short>`, so a mod can keep arbitrary
  per-sim state on a pet with no engine support.
* **Caveat.** `GuineaPig` 4132 `[20]` and `MooseHead` 4099 `[5]` read `StackMotives[3]`. Slot 3 is
  **not** one of the eight TS1 needs FSO models (`VMTS1MotiveHelper.Needs` = 5, 6, 7, 8, 9, 13, 14, 15)
  and is not written in a TS1 lot, so it will read 0. For `MooseHead` that means the "average mood"
  is always 0 and the ornament shows one fixed face; for `GuineaPig` it means the sickness-decay
  condition is always true. See §5.5.

### 4.14 Procedural multi-tile auto-tiling from a neighbour bitmask — **Yes**

* **What.** The balloon arch (and the party-light fence) work out which sprite to draw from which of
  their neighbours are also arches/fences — including diagonal-only connections.
* **How.** `BalloonArch.iff` 4114 `set adj` (41 instructions) uses `get_distance_to` /
  `get_direction_to` to set bits 0/2/4/6 of `MyAttrs[1]`; 4113 `update just me` (41 instructions)
  then switches on the resulting mask and picks `Tuning[384]`…`Tuning[388]` — the masks it tests are
  exactly `0, 1, 4, 16, 64, 17, 68, 5, 20, 80, 65, 21, 84, 81, 85`, i.e. all four orthogonal bits
  plus the four diagonals. It also re-`refresh`es every box. 4105 `update function scores` clears
  `MyObj[38]`/`MyObj[61]` and sets `MyObj[8] flagSet 9` so the arch stops offering surface/sim
  placement.
* **Need.** Nothing.
* **Native-interest.** A generic "auto-tiling connector object" (fences, hedges, paths, railings)
  is a legitimate Simitone object-family feature and this is a complete worked example.

### 4.15 Reachability: conjuring a stool so a child can use a fountain — **Yes**

* **What.** A child who wants to wash their hands at, or make a wish at, a fountain gets a stool
  created and removed again afterwards.
* **How.** `Fountain3x3Deluxe2.iff` 4111 `wash hands`: `Global 363 'Am I a Child?'` →
  `Global 383 'Create Stool'` → use → `Global 384 'Remove Stool'`. `Global 383` is five
  instructions: `create_object_instance` of the stool, `run_tree_by_name` on it, and
  `relationship [256,4,7,1]` to bump a relationship counter on the new object.
* **Need.** Nothing.
* **Native-interest.** A general "this sim cannot use this thing yet" affordance — the Simitone
  equivalent would be a per-object *accessibility provider* so a child can reach a sink, a
  fountain, a tall shelf, a piano.

### 4.16 Objects that react to a sim's tastes, and can make them *worse* — **Yes**

* **What.** The cuckoo clock and the hula-ukulele lamp pick one of four reactions from the viewer's
  interest profile; the clock will *subtract* Fun from a sim who already has plenty.
* **How.** `CuckooClock.iff` 4103 `react - view interaction` (19 instructions):
  `Local[0] = StackDef[70] + MyPersonData[54]` (the sim's *Sixties* interest), `&= 3`, and the four
  branches do `animate [1..4]` + `play_sound`; branch `[15]` is
  `MyMotives[15] += Tuning[1]` and branch `[16]` is `MyMotives[15] -= Tuning[1]`, chosen by
  `MyMotives[15] < Tuning[2]`. `LampHulaUke.iff` 4117 is the same shape.
* **Need.** Nothing. `MyPersonData[54]` = `Interest_SixtiesSchool` is already named.
* **Native-interest.** "Objects have opinions" — a sim-neutral, data-driven source of personality
  expression and of *negative* feedback loops.

### 4.17 An ornament that displays the household's aggregate mood — **Partial**

* **What.** The moose head's expression is the average of some motive across everyone on the lot,
  recomputed hourly, and it makes a noise when it changes.
* **How.** `MooseHead.iff` 4099 `process mood` (29 instructions): iterate avatars with
  `set_to_next` `SearchType = 1`, filter `StackPersonData[32] == Tuning[16898]`, accumulate
  `StackMotives[3]`, divide by the count, compare against `Tuning[3]/[4]/[5]`, then
  `Global 277 'set multi-object graphic'` 0…3 and `play_sound`.
* **Need.** The mechanism works. **The input does not** — see §4.13 / §5.5: motive slot 3 is not a
  TS1 need. A native version should aggregate the eight *real* needs, and could do considerably
  better (e.g. show the worst need, not an average).
* **Status.** **Partial** — the only mechanic in this list whose *output* is wrong on Simitone today.

### 4.18 Away-mode fees scaled by party size — **Yes**

* **What.** A group object charges more when it is used away from home, and charges more still when
  you bring a companion.
* **How.** `TableSoccer.iff` 4141 `Charge`: `Global 321 'Is lot a Residential Lot?'` →
  `Global 463 'Charge for Away from Home 1.5 - 1.6'` with param 5 (solo) or, when
  `MyPersonData[77] != 0`, param 10. `dart_board` and `Fountain3x3` use `Global 334 'Charge for
  Downtown'`; `HungryHamster` and `Jukebox` use both.
* **Need.** Nothing.

### 4.19 Gender-segregated privacy that physically evicts people — **Yes**

* **What.** A female sim who wanders into a room with a man using the urinal is walked up to and
  shooed out.
* **How.** `ToiletUrinal_SP.iff` 4100 `Privacy - shoo women` (39 instructions): iterate avatars with
  `set_to_next` `SearchType = 1`, and for each *female* who is not the user,
  `look_towards [2,…]`, `animate [3,0,3,0]`, `play_sound`, and
  `run_tree_by_name [303,1,534,0]` → the HDSI `STR 0x012F` verb that sends them away, with a
  courtesy cap of `Tuning[17280]` shoo attempts. `ToiletStall` uses `Global 294 'privacy - ask Sims
  to Shoo'`. `HPPottyPack` also uses `Global 362 'wash hands if neat'`.
* **Need.** Nothing. Gender is `MyPersonData[65]`.
* **Native-interest.** Object-driven *social* eviction is a general mechanic (changing rooms, taking
  a bath, a private meeting) that no retail object uses.

### 4.20 Object-mediated pairing (mistletoe) — **Yes**

* **What.** Stand under the mistletoe with someone and the object pushes a *pair* of socials onto
  both of you.
* **How.** `Christmas2001.iff` 4098 `Join`: find a second sim with `set_to_next`, create a
  short-lived pairing object (`create_object_instance` ×2, gated on `Global[20] flagSet 3`),
  `run_tree_by_name` on it, then `push_interaction [640,2816,1,0]` on sim A and
  `push_interaction [641,3840,3,0]` on sim B.
* **Need.** Nothing.
* **Native-interest.** The object is the *conversation initiator* — it hands a bespoke pair of
  interactions to two named sims. That is a general "object proposes a social" API.

### 4.21 Clog → flood → unclog → repair, as a plumbing failure chain — **Yes**

* **What.** The stall accumulates a clog counter, can be flushed (which floods the floor if it is
  badly clogged), has a separate "unclog" interaction, and a repair function.
* **How.** `ToiletStall_SP.iff` 4117 `Maybe Clog` compares `StackAttrs[1]` (fill) against
  `Tuning[135]`; 4102 `Interaction - Clog` (14 instructions) sets `StackAttrs[2] = 1`,
  `StackObj[15] = 100` and then, if already clogged, calls
  `Global 292 'create multiple floods in front of object'` with `5`. 4113 `flush` (55 instructions)
  branches on `Params[1] == 2` (unclog vs flush) and `StackSlot[0]` occupancy. 4104 `Interaction -
  Clean` and 4125 `Interaction - Unclog` (61 instructions) complete the loop;
  `Global 417 'Exit Repair Function'` and `Global 394/396` cover the rest.
* **Need.** Nothing at the VM level. **But** there is **no flood system in Simitone's SimAntics** —
  `grep -ri flood FreeSO/TSOClient` hits only `VMRoomMap`, `VMArchitectureTools` and the world
  renderer, never a flood object or a flood primitive. `Global 394/396` are
  `create_object_instance` of a flood object, so the *spawn* will work if the flood `.iff` is present,
  but nothing will simulate water. **Status: Partial.**

### 4.22 A pie menu whose labels and verbs change with world state — **Yes**

* **What.** One picnic-basket interaction acts as a whole command menu: the *label* is rewritten at
  runtime depending on who is sitting where, what they are wearing, whether there is food left,
  whether the fireplace is on, and how far away the other person is.
* **How.** `PicnicUmbrella1.iff` 4115 `CT - Object Menu` (30 instructions) picks between
  `change_action_string [302,0,3,0]`, `[302,0,4,0]`, `[302,0,6,0]` based on `StackAttrs[0..3]`,
  `MyPersonData[8] == 2` (outfit), `Global[4] == 0`, `StackObj[39] <= 600`,
  `test_object_type` for a fireplace, and `Global 4174 'Is the a fireplace & is it on?'`.
  4114 `CT - Object Menu Command` then routes the choice to port- or starboard-side occupants.
  `VMChangeActionString` supports both the check-time rename and the live rename of the running
  action.
* **Need.** Nothing. This is the strongest existing argument for a native
  **"stateful interaction"** concept: an interaction whose verb set and label are computed from
  world state, rather than a static `TTAB` menu.

### 4.23 A multi-select shop dialog — **Yes**

* **What.** The magic bookshelf opens a dialog with checkboxes so the sim can choose exactly which
  ingredients to store or retrieve in one action.
* **How.** `BookcaseMagic.iff` 4128 `Interaction - Inventory` (166 instructions, 65 locals) calls
  `dialog_private [0,11,-32768,512]`; the `0x8000` operand is the TS1 *checkbox/response-grid*
  flag, and the results come back in `Temp` registers which the tree then walks with
  `manage_inventory` in a loop.
* **Need.** `VMDialogPrivateStrings` is implemented, but this is the most complex dialog shape in
  the downloads and the one I have **not** verified end-to-end. **Status: Yes (data) / unverified.**

### 4.24 In-game advice / notification bubbles — **Yes**

* **What.** Objects can raise a contextual "you should …" hint at the sim.
* **How.** `Global 297/309/310 'help display - …'` find `Objects/HelpSystem.iff`
  (`GUID 0x8963D7C1`, `Global = 1`, 75 trees) with `set_to_next` `SearchType = 4`, set
  `Temps[0] = Tuning[17580…17586]` (a help-string id) and `Temps[1] = 700` (ms), then
  `run_tree_by_name` on it. Used by `ToiletStall` (broken object), `CopedFlowers` /
  `PlantCarnivore` / `Plants2` (object dirty), `BedMMDownload` (`help - won't sleep together`),
  `birthdaycake` (`display help for prepare` / `for cook`), `do electrocution` (autosnapshot), and
  `SCController`.
* **Need.** Nothing. This is a **ready-made advice channel** that Simitone does not appear to
  surface in any UI — worth confirming before building anything new.

### 4.25 Build/buy lock while a guest is present — **Yes**

* **What.** While Santa (or a service NPC) is on the lot, the player cannot build or buy.
* **How.** `SantaClaus.iff` 4096 `Main` alternates `call 10 DisableBuildBuy` / `call 11
  EnableBuildBuy` around its routine.
* **Need.** Nothing; `VMGenericTS1Call` sets `Architecture.BuildBuyEnabled` and signals
  `VMEventType.TS1BuildBuyChange`.
* **Native-interest.** A "lot state lock" that a guest can impose. It is also the natural hook for
  cut-scenes and for §4.3-style scripted visitors.

### 4.26 Skill / fun-object session protocol — **Yes**

* **What.** Objects register as fun or skill providers, gate on the sim's needs, and exit with a
  *reason* bubble when the sim is done or something else became more urgent.
* **How.** `Global 285 'Fun Object Test'` refuses if `MyMotives[15] < Param` or if Bladder /
  Hunger / Energy / Comfort are below `Tuning[16647…16654]` (different thresholds for players and
  NPCs, via `MyPersonData[32]`). `Global 405 'Exit fun object'` ends the session when Fun is full
  *or* a need bottomed out, and shows which one with
  `set_balloon_headline [0,768|769|772|773,100,256]`. `Global 408 'Refuse Fun Object?'` is the
  "no thanks" behaviour: `animate [80,…]` (arms crossed), a sound, and
  `set_balloon_headline [0,774,100,768]`. `Global 415 'Exit Skill Object'` is the skill equivalent.
  `dart_board`, `Fountain3x3`, `DormBookcase`, `BookcaseMagic`, `DormSculpture`, `DormStereo`,
  `Jukebox`, `Sofa*`, `ReclinerAOL`, `PartyBalloons`, `PicnicUmbrella*` all use this.
* **Need.** Nothing.
* **Native-interest.** The refusal animation + reason bubble is the best "the sim has an opinion"
  affordance in the install, and it is entirely retail data. A native version should extend the
  reason set and make the gate authorable rather than hard-wired to eight needs.

---

## 5. Engine and data-model notes (small, concrete)

### 5.1 `Global[20]` is claimed twice — **needs a decision**

`VM.cs:234` pre-sets `GlobalState[20] = 255`. Six download objects use it as their own feature-flag
word, so all six gates are permanently open and all their `setFlag`/`clearFlag` calls are cosmetic.
Globals **do** persist (`VMTS1LotState.cs:96`), so a reserved-bit policy is enough:

* keep bit 0 (`255` minus a reserved mask) as the engine's "editions present" flag, and
* document bits 1…15 as the **mod feature-flag word**, giving new native content a documented place
  to live instead of every mod inventing its own.

This is a one-line default plus a comment, and it un-breaks six objects.

### 5.2 `set_motive_deltas` with the clear flag ignores the motive

`VMSetMotiveChange.Execute` calls `avatar.ClearMotiveChanges()`, which loops `for (int i = 0; i < 16;
i++)` and clears **all sixteen** motive-rate changes, ignoring the `Motive` byte it just parsed.
Retail evidence: I scanned every `set_motive_deltas` site in the eleven retail `.far`s
(1 629 instructions) and **found zero** with `Flags & 1`, so retail never constrains the semantics.
The only user is `CopedFlowers` 4103 `Stomp on`
(`set_motive_deltas [FF,06,05,01,…]` = clear **Energy**), where a blanket clear would also wipe
unrelated in-flight rate changes. A one-line fix (`ClearMotiveChanges((VMMotive)operand.Motive)`)
is safe and makes the primitive match its operand.

### 5.3 `relationship` is the de-facto object↔sim state store

`VMRelationship` keeps a growable `List<short>` per pair, so `RelVar` > 0 is free scratch storage —
and even `RelVar == 0` is repurposed away from "relationship". `GuineaPig` 4132 uses **rel slot 0** on
the pair (cage → sim) as that sim's germ load and **slot 1** as a running energy drain;
`TableSoccer` 4118 `Add Social` writes **rel slot 0** between the table and each of the four players,
i.e. it stores "friendship with the foosball table" and reads it back as the player's relation to
everyone. Nothing is missing — but it is worth documenting, because a Simitone-native
`ObjectStateTable` would be both cheaper and far more legible than reusing the relationship matrix.

### 5.4 Sentinel subtree calls are already handled — no action

`ChristmasPresent.iff` calls opcode `0x7FFF` (×5) and `0xFFFF` (×4) from a `GLOB`-less object.
`VMThread.ExecuteSubRoutine` has an explicit, commented null-`SemiGlobal` guard for exactly this
(131 such sites over 46 retail objects). Confirmed safe; noted only so nobody re-opens it.

### 5.5 Two downloads read motive slot 3, which TS1 never writes

`GuineaPig` 4132 `[20]` and `MooseHead` 4099 `[5]` read `StackMotives[3]`. FSO's model of the TS1
motive array puts the eight needs at **5, 6, 7, 8, 9, 13, 14, 15** (`VMMotive`; see
`VMTS1MotiveHelper.Needs` and the `PedPortal.iff` 4107 evidence quoted there), and slot 3 is one of
the "never written in a TS1 lot" leftovers. Consequence on Simitone: `MooseHead`'s ornament shows a
single fixed face and `GuineaPig`'s sickness decay condition is always true. Not an engine bug — a
**documentation gap** that should be recorded next to `VMMotive` so future mod support knows which
slots are live.

---

## 6. Explicitly not resolved

* **`HPPottyPack` flooding.** The spawn side is `create_object_instance` and will work; the
  *simulation* side does not exist in Simitone's SimAntics. Whether the flood `.iff` is even
  shipped in this install was not checked.
* **`RoseInteraction` 4241 `do move in`.** `call 4 AddToFamily` and `call 5
  CombineAssetsOfFamilyInTemp0` are implemented, but the tree also hand-rolls family counting over
  `NeighborPersonData[61]` and a `Local[3]` "movers remaining" accumulator whose interaction with
  the real family API is unverified. This is the one item on the list I would not sign off without a
  playtest.
* **`BookcaseMagic` 4128's checkbox dialog.** `VMDialogPrivateStrings` is implemented and the
  operand shape is standard, but the 65-local result-walking loop was not traced to completion.
* **Motive semantics of `MyMotives[3]`** — §5.5. I established that Simitone reads 0; I did not
  establish what retail TS1 put there.
* Whether `Global[20]`'s collision is a regression or has always been this way — no history in this
  tree.

---

## 7. Exact reproduction

```powershell
$BV  = "$env:LOCALAPPDATA\Temp\opencode\bhavscan2\bin\Release\net9.0\bhavscan2.exe"
$DL  = "D:\Program Files\Sims\Downloads"
$S   = "D:\Program Files\Sims"
$FARS = Get-ChildItem $DL -Directory | ForEach-Object { Join-Path $_.FullName ($_.Name + ".far") }

& $BV ls @FARS                       # 87 .iff entries
& $BV objids ""                      # every OBJD in the install; filter to Downloads/
& $BV objd $DL\UnSnacker\UnSnacker.far "unsnacker.iff"   # Global=1, TTAB=0xFFFF
& $BV census @FARS                   # §1.2
& $BV opcensus @FARS                 # §3 opcode surface
foreach ($f in $FARS) { & $BV ops $f "" }   # per-object histograms incl. Global.iff names
foreach ($f in $FARS) { & $BV names $f <entry> }  # BHAV labels per object
& $BV bcon $DL\SCDrinks\SCDrinks.far "SCDrinks.iff"       # Tuning[] -> BCON mapping
& $BV bcon "D:\Program Files\Sims\GameData\Global\Global.far" "Global.iff" 256   # 0x100 -> Tuning 16385
& $BV bcon "D:\Program Files\Sims\GameData\Global\Global.far" "Global.iff" 260   # 0x104 -> Tuning 16897/8
& $BV simi "D:\Program Files\Sims\TemplateCommunity\House50.iff"               # globals 0..37 baked, [20]=0

# §4.1 / §4.2
& $BV dis $DL\CCPlugin\CCPlugin.far "CCPhonePlugin.iff" 4141 0 21
& $BV disraw $DL\CCPlugin\CCPlugin.far "CCPhonePlugin.iff" 4141 5 21
& $BV findobj 59255DCB      # NPC Clown Cleanser - Female
& $BV findobj 60344A9B      # NPC Tragic Clown
& $BV dis $DL\PartyBalloons\PartyBalloons.far "PartyBalloons.iff" 4104 0 12
& $BV dis $DL\PartyBalloons\PartyBalloons.far "PartyBalloons.iff" 4110 0 5
# §4.3
& $BV dis $DL\Christmas2000\Christmas2000.far "SCController.iff" 4097 0 4
& $BV dis $DL\Christmas2000\Christmas2000.far "SantaClaus.iff" 4096 0 19
# §4.5 / §4.10
& $BV dis $DL\ReclinerAOL\ReclinerAOL.far "ReclinerAOL.iff" 4115 0 8
& $BV dis "D:\Program Files\Sims\GameData\Global\Global.far" "Global.iff" 400 0 11
& $BV disraw "D:\Program Files\Sims\GameData\Global\Global.far" "Global.iff" 400 0 11
& $BV findobj FD4FB8E4      # Social Interactions - Hot Date (HDSI.iff)
& $BV lits "D:\Program Files\Sims\GameData\Global\Global.far" "Global.iff"   # STR 0x012F verb table
# §4.6
& $BV dis $DL\SCDrinks\SCDrinks.far "SCDrinks.iff" 4124 0 21
& $BV dis $DL\SCDrinks\SCDrinks.far "SCDrinks.iff" 4106 0 38
# §4.7
& $BV disraw $DL\BookshelfMagic\BookshelfMagic.far "BookcaseMagic.iff" 4130 2 2
& $BV findobj 99E81BEC      # Magic Token - Currency
& $BV lits $DL\BookshelfMagic\BookshelfMagic.far "BookcaseMagic.iff"          # STR 0x012E[6..7]
# §4.8
& $BV dis $DL\LampWallAOL\LampWallAOL.far "LampWallAOL.iff" 4103 0 23
& $BV disraw $DL\LampWallAOL\LampWallAOL.far "LampWallAOL.iff" 4103 0 23
& $BV dis "D:\Program Files\Sims\GameData\Global\Global.far" "Global.iff" 392 0 30
# §4.13 / §5.5
& $BV disraw $DL\GuineaPg\GuineaPg.far "GuineaPig.iff" 4132 3 8
& $BV dis $DL\Moose\Moose.far "MooseHead.iff" 4099 0 28
# §4.14
& $BV dis $DL\BalloonArch\BalloonArch.far "BalloonArch.iff" 4113 0 24
# §4.19 / §4.21
& $BV dis $DL\HPPottyPack\HPPottyPack.far "ToiletUrinal_SP.iff" 4100 0 20
& $BV dis $DL\HPPottyPack\HPPottyPack.far "ToiletStall_SP.iff" 4102 0 13
# §4.24
& $BV dis "D:\Program Files\Sims\GameData\Global\Global.far" "Global.iff" 310 0 6
& $BV findobj 8963D7C1      # Help System
```

## 8. Cross-references

* `docs/REMAINING_ENGINE_PLAN.md` §0 (tooling), §0.1 (decode facts), §1.3 (`push_interaction`
  null guard, person-data bounds) — this document extends both.
* `FreeSO/TSOClient/tso.simantics/VM.cs:233-236` — the `Global[20] = 255` default (§5.1).
* `FreeSO/TSOClient/tso.simantics/Primitives/VMSetMotiveChange.cs:18-21` +
  `Entities/VMAvatar.cs` `ClearMotiveChanges()` — §5.2.
* `FreeSO/TSOClient/tso.simantics/Primitives/VMChangeActionString.cs` — the pie-menu relabelling in
  §4.22.
* `FreeSO/TSOClient/tso.simantics/Utils/VMTS1MotiveHelper.cs` — the eight live TS1 motive slots
  (§5.5).
* `FreeSO/TSOClient/tso.simantics/Model/VMGenericTS1CallMode.cs` — the call names in §1.2.
* `FreeSO/TSOClient/tso.simantics/Primitives/VMTS1InventoryOperations.cs` — the token economy in
  §4.7.
* `FreeSO/TSOClient/tso.simantics/Model/TS1Platform/VMTS1LotState.cs:96` — global persistence.
