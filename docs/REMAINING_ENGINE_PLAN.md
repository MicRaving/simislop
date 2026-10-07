# Remaining expansion engine work — disassembly-backed plan (2026-09-27)

Wave L3. Scope: the four remaining expansion events in `docs/ADDON_PROGRESSION_PLAN.md` §4.4–4.8 —
**Vacation Score + souvenirs + hotel booking**, **StudioTown (obsessed fan / studio tours / fame
objects)**, **Unleashed pet shows + training**, **Makin' Magic quests + "pyrite" autonomy**.

Read-only investigation. Nothing outside this file was modified. No UI automation, no game launch:
every finding below is a static read of the retail install at `D:\Program Files\Sims` plus a read of
`FreeSO/TSOClient/**`.

---

## 0. Method and tooling

The throwaway disassembler from the previous session
(`%LOCALAPPDATA%\Temp\opencode\bhavscan`) reads the retail `.far` cabinets directly. It was
**extended** (a copy, not the original, at `%LOCALAPPDATA%\Temp\opencode\bhavscan2`) with:

| Command | Purpose |
|---|---|
| `census <far…>` | every `generic_sims_call` (opcode 1) site, grouped by call id, annotated with the FreeSO implementation state |
| `opcensus <far…>` | histogram of *raw* opcodes 0–255 vs. the opcodes FreeSO registers in `VMContext.cs` |
| `gcall <far> <entryRegex>` | per-object call census |
| `ops <far> <entryRegex>` | per-object opcode histogram with `Global.iff` tree names resolved (opcodes ≥ 256 are global trees) |
| `dis / disraw <far> <entry> <bhav> [from] [to]` | disassembly; `disraw` prints raw operand bytes (essential — see the decode warnings below) |
| `objd / objids / findobj <…>` | OBJD records with `GUID`, `Global` flag, `UsesFnTable`, and the resolved `OBJf` function table |
| `lits <far> <entry>` | every `TTA`/`STR` string table in an object |
| `bcon <far> <entry>` | `BCON` constant tables and the `Tuning[]` indices they back |
| `guid <hex> <far…>` / `pushaudit <far…>` / `pdrange <far…>` / `tad <far…>` / `pdw <slot> <far…>` / `simi <iff…>` | targeted cross-cabs: GUID references at any operand offset, `push_interaction` target audit, person-data slot census, `TreeAdRange`/param census, person-data writers, lot `SIMI` globals |
| `objtwhere` | proves the `OBJT` object-type catalog is **not** shipped in any `.far` (see §2.6) |

### 0.1 Decode facts (all re-verified against `VMThread.MoveToInstruction`)

* `BHAVInstruction.TruePointer` = low byte, `FalsePointer` = high byte of the `Next` field.
  `255` = `RETURN_FALSE`, `254` = `RETURN_TRUE`, `253` = "continue along the only path available",
  `0` = fall through to the next instruction.
* Opcodes `256…506` are `Global.iff` trees, not primitives.
* **Variable scope byte order in an `expression` (op 2) operand is
  `[LhsData(2)][RhsData(2)][IsSigned(1)][Operator(1)][LhsOwner(1)][RhsOwner(1)]`** — confirmed against
  `VMExpressionOperand.Read`. **Scope 9 is `Parameters`, not `TempByTemp`; scope 42 is `TempXL`; scope
  26 is `Tuning`.** The inherited disassembler's scope table was wrong from index 9 onwards (it
  labelled 9 `TempByTemp` and 26 `Tuning` at the wrong offsets), which silently turned
  `Params[0] == Tuning[0x4880]` into `Temps[0x4880] == Literal[…]` on a first pass here. Every
  expression quoted in this document has been re-decoded with the corrected table.
* **Operand bytes must be read as bytes, not shorts.** Printing a `manage_inventory` /
  `create_object_instance` / `push_interaction` / `set_to_next` operand as four shorts silently
  rotates the fields. Two inventory GUIDs in §2.2 and one in §2.4 were wrong on the first pass because
  of this, and §3.2's car GUIDs were wrong in an earlier draft. `disraw` exists to prevent it.
* `set_to_next` operand = `[GUID(4)][Flags][TargetOwner][Local][TargetData]`, with
  `SearchType = Flags & 0x7F` and bit `0x80` meaning "TargetOwner/TargetData are valid".

---

## 1. Headline findings

### 1.1 The TS1 call surface is **not** the blocker

Full `generic_sims_call` census over `Global.far`, `Objects.far`, `Deluxe.far`,
`ExpansionPack.far` … `ExpansionPack7.far`, `ExpansionShared.far`: **545 call sites**. The only call
ids that are not fully implemented are:

| call | name | sites | state | note |
|---|---|---|---|---|
| 3 | `PullDownTaxiDialog` | **12** | **missing** | needs a dialog type + panel (§4.1) |
| 15 | `MyRoutingFootprintEqualsTemp0` | 203 | documented no-op | 10 sites in Vacation, 29 in Unleashed, 14 in Studio Town, 26 in Magic — all routing/bed/"get in" trees |
| 35 | `TakeTaxiHook` | 2 | documented no-op, **genuinely blocked on data** | both in `PhonePluginStudioLots.iff` (§4.2) |
| 38 | `GetTokensFromString` | 4 | **implemented** (parallel L1 wave, landed during this investigation) | §5.1 independently confirms the register contract; one instruction-level anomaly left to a playtest |
| 0 | `HouseTutorialComplete` | 2 | falls through to `GOTO_TRUE` | **correction**: the note in `VMGenericTS1Call.cs` ("none of 0, 8 or 9 has a single call site") is wrong — `Objects/Tutorial.iff` 4137 `'tutorial - options'` `[6]` and 4148 `'cancel tutorial'` `[11]` call it. Harmless (it is a write-only flag) but the comment should be fixed. |
| 7, 8, 9, 37 | — | **0** | dead calls | confirmed zero over all 11 fars |

**So none of the four targets needs a new TS1 call.** The gaps are (a) one missing dialog, (b) one
blocked data question, (c) verification that the shipped chains actually run in Simitone, and (d) two
object/data-model questions.

### 1.2 The opcode surface is **not** the blocker either

`opcensus` over the same 11 fars (≈230 k instructions) found exactly three opcodes below 256 that
FreeSO does not register (`VMContext.cs` registers 0–9, 11–32, 35–51, 62, 63, 65, 67):

| opcode | sites | where | relevant? |
|---|---|---|---|
| 10 | 6 | `Global.iff` 274 `'Move forward n tiles (n)'` `[3][4]`, `HDHelpSystem.iff` 4115 `[0]`, 4103 `[4]` | no |
| 33 | 1 | `HDSI_Nag.iff` 4349 `'Nag - Friends - Person A'` `[24]` | no |
| 34 | 13 | `HDHelpSystem.iff` 4116/4123 tutorial trees | no |

Opcodes 38/39/40/52–61/64/66/68–99 have **zero** sites in the shipped data.

### 1.3 The three "obvious" suspicions are all **closed**

* **`push_interaction` is safe.** A census of all 2 287 sites: **2 224 set the `ObjectInLocal` flag**
  (`Flags & 2`) and resolve the target from `Locals[target]`; **zero** of those index out of the
  routine's local count. The remaining 63 use the `Args[target]` path and 62 of those have
  `target == 0` — which is how `CarGlobals.iff` 8195 `'go to work'` `[6][9]` and
  `VacationPedMarker.iff` 4112 `'Save Score'` `[7]` are written, and they read fine because the frame's
  `Args` is a 4-element zeroed array. The one thing to keep in mind is that `VMPushInteraction` does
  **not** null-guard `interactionSource`; no shipped site can reach it with a null today, but any new
  code that pushes an interaction with an unresolvable target will NRE. **Recommendation: add the
  null guard** (one line, `FreeSO/TSOClient/tso.simantics/Primitives/VMPushInteraction.cs:42`) as
  cheap insurance.
* **Person-data slots do not overflow.** A census of every `MyPersonData` / `StackPersonData` /
  `NeighborPersonData` expression in the 11 fars: **87 distinct slots, max 87, zero accesses above
  100**. `VMAvatar.GetPersonData` / `SetPersonData` (`VMAvatar.cs:759`, `:850`) throw for indices
  `> 100` and the runtime array is `new short[101]` (`VMAvatar.cs:66`), so retail is inside the
  guard. (A first pass appeared to show `SatelliteDish.iff` writing slot 129; that was a
  short-vs-byte misread — it is `Temps[0] = Tuning[129]`.)
* **Lot global 10 ("house number") is not a gap.** `VMTS1ActivatorNew.LoadFromIff` has the
  `SetGlobalValue(10, HouseNumber)` / `(32)` / `(33)` lines **commented out**
  (`VMTS1ActivatorNew.cs:438–443`, cf. the old `VMTS1Activator.cs:82–87`), but every shipped lot
  template bakes the value into its `SIMI` chunk: `GlobalData[10]` is `40`/`45`/`5`/`70`/`85`/`95`/`25`
  for `House40/45/05/70/85/95/25`. So `UIMainPanel.cs:562`, `TS1GameScreen.cs:577` and
  `SaveService.cs:105` all read a correct house number on stock lots. **No action**, but do not add a
  redundant write. (The one shipped BHAV that reads `Global[10]` — `FrontDesks.iff` 4108 `[49]` — is
  **unreachable**: `[48]` jumps to `[50]`, and nothing else targets `[49]`. See §2.5.)

### 1.4 One data model question that *is* real

`FreeSO/TSOClient/tso.files/Formats/IFF/Chunks/OBJM.cs:277` reads a person object's saved person data
as `0x50` shorts for lot `OBJM` version `≤ 0x45` and `0x100` for `> 0x45`, but
`NBR/NBRS.cs:141` reads a **neighbourhood** `Neighbour.PersonData` as a fixed **88** shorts and
`VMMemory.cs` reads `NeighborPersonData` (scope 32) with `.ElementAt(data)`. Retail never indexes
scope 32 above 87 (verified), so this is safe today — but it is a latent
`ArgumentOutOfRangeException` for any future caller. **Low priority; one-line clamp.**

---

## 2. Target 1 — Vacation Score, souvenirs, hotel booking

**Verdict: the entire chain is already implemented in FreeSO.** The only unimplemented call in the
arrival path is call 3 (§4.1). There is **no** Vacation HUD to build: nothing in the retail data
displays the score as a number — the score's only consumers are the souvenir award and the
end-of-trip souvenir spawn, both of which are scripts.

### 2.1 Where the Vacation Score actually lives

`Global.iff` trees 456/457/458/459 (`'Vacation - Add/Subtract/Get/Save Score'`) all operate on
**attribute 5 of a hidden controller object**, found or created by object GUID:

```
Global.iff 456 'Vacation - Add to Vacation Score 1.5'
  [0]  StackObjID = 0                          T->11
  [11] GLOBAL 446 'Am I in Selected Sims Party?' T->1
  [ 1] set_to_next  GUID=0x9CC72382 SearchType=4(ObjectOfType) -> StackObjID   T->3
  [ 3] StackAttrs[5] += Params[0]              T->4
  [ 4] StackAttrs[5] < 0                      T->5
  [ 5] StackAttrs[5] = 0                      T->9
  [ 9] Temps[0] = StackAttrs[5]               T->10
  [10] GLOBAL 459 'Vacation - Save Score'      T->RETURN_TRUE
      (create_object_instance 0x9CC72382, tileCount 6, at [7] if not found)
```

`findobj 9CC72382` resolves to:

```
ExpansionPack4/VacationPedMarker.iff  OBJD 0x41AA  GUID=0x9CC72382
  label 'Controller - Vacation - Score'  type=Normal  attrs=16  Global=0  UsesFnTable=1
  OBJf: init=0x1006 (4102 'init - score')  main=0x1004 (4100 'Main - Score')  cleanup=0x100F
```

So the object is **not** an auto-spawned controller (OBJD `Global == 0`, and
`VMTS1ActivatorNew.cs:676–687` only spawns `Global == 1` objects). It is created on demand by
`VacationPhonePlugin.iff` 4126 `'Spawn Vacation Score'` (which is itself reached from the pay-booth
plugin's trip-start chain). **That is retail behaviour, not a gap** — but it does mean the score only
exists once a family has booked the island.

### 2.2 The score is *persisted* in the neighbourhood `NGBH` inventory, not in a sim/person field

```
VacationPedMarker.iff 4110 'Interaction - Imbed Score'
  [0] Temps[0] = -1                            T->4
  [4] manage_inventory [01,06,03,09,3B,8C,2A,84]  T->3     ; RemoveToken  type=6 GUID=0x842A8C3B (count from Temp0 = -1 => all)
  [3] Temps[0] = StackAttrs[5]                 T->2
  [2] manage_inventory [00,06,03,09,3B,8C,2A,84]  T->RETURN_TRUE ; AddToken type=6 GUID=0x842A8C3B count=score
```

`GUID 0x842A8C3B` is the OBJD of `ExpansionPack4/VacationScore.iff` (`'Token - Vaction Score'`,
Maxis' typo). The read side is `VacationPhonePlugin.iff` 4126 `[1]`:

```
manage_inventory [04,06,95,0C,3B,8C,2A,84]  ; SetToNextTokenOfType type=6 -> Temps[0] = persisted score
```

`FreeSO/TSOClient/tso.files/Formats/IFF/Chunks/NGBH.cs` already reads **and writes**
`InventoryByID`, and `TS1NeighbourProvider.SaveNeighbourhood` (`:533`) writes the whole
`Neighborhood.iff`. **Persistence works.** No work item.

### 2.3 The score *accrues* from the family's Fun motive, not from prizes

`VacationPedMarker.iff` 4100 `'Main - Score'` calls 4104 `'Process Score'`, which walks the family,
averages their **Fun motive** and adds one of six `Tuning[]` constants to the controller's attribute 5:

```
4104 'Process Score'  (n=29)                                raw bytes
[ 0] StackObjID = Global[3]                                  00,00,03,00,00,05,0A,06   T->24
[24] Local[4] = StackPersonData[61]        (family number)    04,00,3D,00,00,05,19,13   T->1
[ 6] set_to_next  GUID=0 Flags=0x81 -> SearchType 1 (Person) 00,00,00,00,81,0A,00,00   T->25
[25] StackPersonData[61] == Local[4]                          3D,00,04,00,00,02,13,19   T->7
[26] StackPersonData[77] > 0                (has autofollow)  4D,00,00,00,00,00,13,07   T->7
[ 7] StackPersonData[58] < Literal[18]      (age split)       3A,00,12,00,00,01,13,07   T->11
[ 8] Local[1] += StackMotives[3]  (adult fun sum)             01,00,03,00,00,03,19,0F   T->9
[ 9] Local[0] += 1            (adult count)                  00,00,01,00,00,03,19,07   T->6
[11] Local[3] += StackMotives[3]  (child fun sum)             01,00,03,00,00,03,19,0F   T->10
[10] Local[2] += 1            (child count)                  02,00,01,00,00,03,19,07   T->6
[19] Local[1] /= Local[0]      (adult average)                01,00,00,00,00,07,19,19   T->14
[14] Local[1] > Tuning[256]                                    01,00,00,01,00,00,19,1A   T->16
[16] MyAttrs[5] += Tuning[258]                                05,00,02,01,00,03,00,1A   T->13
[20] Local[3] /= Local[2]      (child average)                03,00,02,00,00,07,19,19   T->15
[15] Local[3] > Tuning[257]                                    03,00,01,01,00,00,19,1A   T->17
[17] MyAttrs[5] += Tuning[259]                                05,00,03,01,00,03,00,1A   T->18
[21] MyAttrs[5] += Tuning[260]                                05,00,04,01,00,03,00,1A   T->13
[22] MyAttrs[5] += Tuning[261]                                05,00,05,01,00,03,00,1A   T->18
[18] GLOBAL 4112 'Save Score'                                                     T->RETURN_TRUE
```

with

```
bcon ExpansionPack4/VacationPedMarker.iff 0x1002 = [40, 40, 6, 3, 65534(-2), 65534(-2)]
  Tuning[256]=40  Tuning[257]=40  Tuning[258]=+6  Tuning[259]=+3  Tuning[260]=-2  Tuning[261]=-2
```

i.e. adults' average Fun is compared against 40 and children's against 40, and the score moves by
+6 / +3 / −2 / −2 depending on which comparison passes. `Tuning[]` resolution
(`VMMemory.cs:363 GetTuningVariable` → `WorldObjectProvider.cs:263 TuningCache`, built from `BCON`
chunks of the object's own resource) is implemented, and `BCON 0x1002` **is** in
`VacationPedMarker.iff`. Prize tokens add +1 each via `Global.iff` 435 `'Prize Token - Add'`
`[5] GLOBAL 456 args(1,…)`. **No work item.**

### 2.4 Souvenirs — three separate script paths, all implemented

| Source | Tree | What it does | Calls |
|---|---|---|---|
| Vacation Director gift | `Global/VacationDirectorGlobals.iff` 8205 `'Interaction - Give Souvenir - TEST'` (`StackAttrs[6] == 0`) → 8206 `'Interaction - Give Souvenir'` | `create_object_instance GUID=0x2F1DDC9C` then `run_tree_by_name` + `push_interaction` 28/27 | **none** |
| Director "check" | `ExpansionPack4/NPC_Vacation_Controller.iff` 4108 `'Check to Give Souvenir'` → 4107 `'Do Director Give Souvenir'` | finds/creates the score controller (GUID `0x9CC72382`, `StackAttrs[6] = Global[9]` = family number), reads the score into `Local[1]`, and only then `[2] Local[1] > Tuning[3]` — and `bcon NPC_Vacation_Controller.iff 0x1000 = [10, 3, 6, 150]`, so **the souvenir threshold is a score of 150**. 4107 then gifts a second sim too (`push_interaction` 59) | **none** |
| Treasure hunt | `ExpansionPack4/TreasureHunt.iff` 4106 `'Sub - Create Souvenir'`, 4112 `'Hunt - Put Souvenir in Inventory'`, 4113 `'Sub - Grab Souvenir'` | `STR 0x0100[0] "Souvenir Type"` / `[5] "Souvenir ID"` drive the pick | — |
| End of trip | `VacationPhonePlugin.iff` 4104 `'Interaction - Spawn Vacation Purchases'` → 4106/4107/4115 `'Spawn Bad/Good/Indifferent Mood Souvenirs'` | mood-banded souvenir spawn | none of 4104/4106/4107/4115 |

The object the director creates is a **social interaction object**:
`findobj 2F1DDC9C` → `ExpansionShared/HDSI_Souvenir.iff` OBJD `0x41A7`, label
`'Social - Give Souvenir'`, semi-global `SocialGlobals`. Souvenir *purchases* use
`manage_inventory` with a `"Hey! Your bags are already bulging with Souvenirs of this type…"`
cap (STR `0x012D[17]` of `Souvenirs.iff` / `prizeboothVacation.iff` / `FishingPier.iff`) — again
`NGBH` inventory. **No work item.**

### 2.5 Hotel booking — `FrontDesks.iff`, entirely implemented

`ExpansionPack4/FrontDesks.iff` 4108 `'Check In'` (54 instructions). **Live path:**
`[0]→[13]→[3]→[30]→[31]→[6]→[7]→[18]→[5]→[33]→[40]→[38]→[39]→[32]→[45]→[35]→[36]→[26]→[25]→[27]→[29]`,
with two early-outs at `[48]→[50]` and `[46]→[47]`. Decoded:

```
[ 0] StackAttrs[1] = Tuning[256]                                 01,00,00,01,00,05,01,1A  T->13
[13] StackAttrs[0] = Tuning[256]                                 00,00,00,01,00,05,01,1A  T->3
[ 3] Local[0] = StackObjID                                       00,00,00,00,00,05,19,0A  T->30
[ 4] goto_relative  (-508, -512)                                 00,00,04,FE,... / 00,00,00,FE,...
[ 1] manage_inventory FindToken type=0 GUID=0x3AC022D6            03,00,11,0D,D6,22,C0,3A  T->48
        Flags 0x11 -> count into Temps[0], found index into Temps[1]
[48] Temps[0] > Literal[0]     (do we already hold a key?)        00,00,00,00,00,00,08,07  T->50 / F->[0]
[50] dialog_private Message=3 Type=1(Yes/No) Title=10            00,00,03,00,00,01,0A,00  T->2
        STR 0x012D[2] "Our database shows that you are already registered to stay at another hotel.
                       Do you want to check in here instead? You will lose your privileges at the
                       other hotel."
[ 2] GLOBAL 4123 'Count Beds on Lot'                                              T->42
[42] Local[3] = Temps[0]                                             03,00,00,00,00,05,19,08  T->46
[46] Local[3] == Literal[2]                                          03,00,00,00,00,02,19,07  T->47
[47] dialog_private Message=6 Type=0(Message) Title=10             00,00,06,00,00,00,0A,00  T->53
        STR 0x012D[5] "I'm sorry, our hotel is still under construction and we have not added many
                       beds yet, so there is nowhere to sleep. Try another hotel, or rent a tent or
                       igloo if you are desperate."
[53] StackAttrs[0] = Tuning[259]                                            T->RETURN_FALSE
[30] GLOBAL 4114 'CT - Get my Manager in Temp 0'                              T->31
[31] MyObj[11] == Temps[0]  (is the clerk here?)                    0B,00,00,00,00,02,03,08  T->6
[ 7] StackAttrs[1] = Tuning[257]                                 00,00,01,01,00,05,01,1A  T->8
[ 8] StackAttrs[0] == Tuning[257]                                01,00,01,01,00,02,01,1A  T->11 / F->[5]
[ 5] animate                                                     02,00,01,00,00,20,00,00  T->33
[33] StackObjID = Local[0]                                       00,00,00,00,00,05,0A,19  T->40
[40] Local[2] = StackAttrs[2]      (this desk tier's nightly price) 02,00,02,00,00,05,19,01 T->38
[38] GLOBAL 443 'count my (non-pet) family members - 1.5'                          T->39
[39] Local[2] *= Temps[0]          (price x party size)         02,00,00,00,00,06,19,08  T->32
[32] budget  amount = Local[2], Flags = 0 (charge, not JustTest) 03,19,02,00,00,00,00,00  T->45
        VMTransferFundsOperand: OldAmountOwner=Normal, AmountOwner=Local(25), AmountData=2
[45] dialog_private Message=2 Type=0(Message) Title=10         00,00,02,00,00,00,0A,00  T->35
        STR 0x012D[1] "Thanks for choosing to stay with us! We value your patronage…"
[35] MyPersonData[1] = Local[2]   (pop the cash over the sim's head) 01,00,02,00,00,05,12,19 T->36
[36] play_sound 300                                              2C,01,00,00,...        T->26
[26] StackObjID = MyObj[11]                                      00,00,0B,00,00,05,0A,03  T->25
[25] set_to_next  GUID=0 Flags=0x8C -> SearchType 12 (FamilyMember) 00,00,00,00,8C,0A,00,00 T->27
[27] StackObjID == MyObj[11]  (wrapped round to me?)            00,00,0B,00,00,02,0A,03  T->29 / F->[25]
[29] GLOBAL 4116 'Add Key to my Inventory'                                                 T->RETURN_TRUE
```

* `bcon FrontDesks.iff 0x1000 = [66, 72, 62, 40, 40, 40]` — so the three desk tiers price a room at
  **§66 / §72 / §62** per sim-night (`Tuning[256/257/258]`, held in the desk's attribute 2), and
  `Tuning[259]=40` is the "checked in" state written by `init common`. `4121 'Set Cost'` picks the
  tier with `test_object_type` against the two other desk GUIDs (`C349C14C` and `66131BA8`).
* The **Hotel Key** is inventory item `type 0, GUID 0x3AC022D6`, added by 4116, removed by 4124
  (one key per sim-night) and by 4120 / `VacationPhonePlugin` 4129 on checkout/departure.
* `4123 'Count Beds on Lot'` uses `set_to_next` **SearchType 6** (objects whose catalogue category ==
  `Args[0]`, where `Args[0]` was just set to `Tuning[0x4304]` = `Global.iff` `OTF 0x0106` key 4) —
  implemented (`VMSetToNext.cs` case `ObjectWithCategoryEqualToSP0`).
* `dialog_private` reads `STR 301` (`0x012D`) out of the object's own resource, so every line above is
  already localised data. `VMDialogPrivateStrings` handles types 0/1/2 generically.
* **Dead code in the shipped tree** (nothing targets them): `[14]` `push_interaction` 0,
  `[28]` `push_interaction` 1, `[41]` `GLOBAL 443` + `[43]` `Local[0]-- > Temps[0]` +
  `[44]` `dialog_private` Message=1 (`STR 0x012D[0]` "…we don't have enough beds to meet your
  party's sleep needs. Would you like to check in anyway?"), and `[49] Temps[0] == Global[10]`.
  The "not enough beds" *offer* is therefore unreachable in retail — the desk simply refuses at
  `[46]`/`[47]` (`STR 0x012D[5]`) when `Count Beds` returns 2 or fewer. Worth knowing before anyone
  writes a QA row that expects the offer dialog. `STR 0x012D[0]`, `[3]`, `[4]` and `[7]` are used by
  the sibling trees 4117/4118/4119/4122 (`'Check In TEST'`, `'Check Out TEST'`, `'Check Out'`,
  `'no money failure'`).

**No work item.** The one QA obligation is that the pay booth / phone plugin path must be exercised,
because call 26 (arrival) and the score spawn both hang off it.

### 2.6 "Visitor / roommate switching"

There is **no roommate object and no roommate string anywhere in the install** — searched every
`TTA`/`STR`/`TTAB` string and every OBJD label in all 11 fars for `roommate`, `room assignment`,
`change room`, `bed assignment`: zero hits. In retail, rooming is emergent:

* who sleeps where is decided by whichever bed a sim claims first (`ExpansionPack4/beds3.iff`
  `'Bed Double Lodge'` 4110 `'get in'`, 4134 `'Sleep Single'` — the trees that carry the 10 call-15
  sites in this pack);
* the *gender preference* guard is `VacationPedMarker.iff` 4123 `'Check against Gender Preference'`,
  reached from 4128 `'Main - Family Spawn'` `[20]` (`StackPersonData[34] = 1` = walk) and 4138
  `'Auto-Checkin TEST'`;
* capacity is the `'Count Beds on Lot'` dialog above.

**Conclusion: "visitor-roommate switching" is not a feature to implement; it is the bed/gender-preference
script chain.** The matrix row in `docs/PHASE3_TEST_MATRIX.md` claiming a missing rental/room UI
should be rewritten accordingly (same correction the plan already made for "rentals are pure
lot-script content").

The other sense of "switching" — the **Rental Shack** (`Global/RentalClerkGlobals.iff` 8284
`'Move In'`, 8286 `'begin visiting'`, 8320 `'process visitors'`, `Global.iff` 453/454
`'Find Nearest Rental Shack'`, 463 `'Charge for Away from Home'`, 477 `'Is Away From Home Family Full'`)
— is also pure script (`ExpansionPack4/RentalShackGlobals.iff` 8192–8268 are all
get/give-an-item interactions). No unimplemented call.

### 2.7 Object-type catalog: no data is missing

`objtwhere` over all 11 fars finds **zero** `OBJT` chunks, so the "which object ids are on this lot"
question cannot be answered from a shipped catalog — FSO synthesises `TypeID`s from the OBJD order
(`ITSOObjectCatalog`). This is *why* §2.1/§2.2 had to be resolved by scanning GUID references in
BHAV operands instead. Not a gap; recorded so the next person does not re-derive it.

---

## 3. Target 2 — StudioTown: obsessed fan, studio tours, fame objects

**Verdict: confirmed — the prior session's finding holds. The fame special objects make no
unimplemented calls at all. The arrival chain's only unimplemented call is call 3.**

### 3.1 The arrival chain, end to end

`ExpansionPack6/PedMarkersStudio.iff` OBJD `0x41A8` `'Ped Marker - Studio Middle'`, main
`OBJf 0x100E` = BHAV 4110 `'main - middle ped marker'`:

```
[ 0] GLOBAL 280 'idle' 60 ticks                  3C,00,00,00,...            T->5
[ 5] Local[0] = MyObj[11]                        00,00,0B,00,00,05,19,03    T->6
[ 6] MyAttrs[3]-- > Literal[0]   (consume "guest arriving") 03,00,00,00,00,10,00,07  T->1 / F->[0]
[ 1] call 18 BuildTheDowntownSimAndPlaceObjIDInTemp0      12,00,00,00,...         T->10
[10] Local[1] = Temps[0]                        01,00,00,00,00,05,19,08    T->4
[ 4] Temps[0] > Literal[0]                      00,00,00,00,00,00,08,07    T->12
[12] GLOBAL 4119 'Generate Cab'                                              T->8
[ 8] MyAttrs[3] = Literal[0]                     03,00,00,00,00,05,00,07    T->3
[ 3] StackObjID = Local[1]                       00,00,01,00,00,05,0A,19    T->2
[ 2] find_location_for                                                          T->9
[ 9] Temps[0] = Local[1]                        00,00,01,00,00,05,08,19    T->14
[14] special_effect  effect=0x0201 sound=0x0049  00,00,01,02,49,00,00,00    T->11
[11] StackPersonData[34] = Literal[1]  (walk)    22,00,01,00,00,05,13,07    T->16
[16] Temps[0] = Local[1]                        00,00,01,00,00,05,08,19    T->13
[13] call 21 SpawnInventorySimDataEffects       15,00,00,00,...            T->18
[18] Temps[0] = Local[1]                        00,00,01,00,00,05,08,19    T->15
[15] call 3  PullDownTaxiDialog   ** MISSING ** 03,00,00,00,...            T->17
[17] GLOBAL 4124 'Clear Studio Token'                                       T->6
```

* **call 18** (`VMGenericTS1Call.cs:158`) creates `GameState.DowntownSimGUID` as a `VMAvatar`, sets
  global 3 to it and activates the family — implemented. `DowntownSimGUID` was set by call 17
  `ChangeToLotInTemp0` to `context.Caller.Object.OBJ.GUID`, i.e. the phone plugin object *is* the
  visitor generator. Correct retail structure.
* **call 3** is the only gap, and here **both** answers are harmless: `T->17` (continue the idle loop)
  and `F->0` (fall through to `[16]`, which re-reads Temp0 and re-enters call 3). Unlike the Vacation
  marker there is no self-looping breakpoint on the false branch, so a future implementation cannot
  wedge this tree.
* **4124 `'Clear Studio Token'`** = `manage_inventory RemoveToken type=6 GUID=0xFC44000D` — the Studio
  arrival token is consumed here, and the *return* trip's token is validated by
  `PhonePluginStudioLots.iff` 4124 `'Chance Card w/Check'`.

### 3.2 The arrival car is chosen from Fame, and that data now exists

`PedMarkersStudio.iff` 4113 `'Make Car based on Fame'` (not 4124 — the prior note in
`VMGenericTS1Call.cs` mislabels this tree; the file's own label list is authoritative):

```
[ 0] Local[0] = StackObjID
[ 1] StackObjID = Global[3]                        (the travelling star)
[ 2] StackPersonData[56]-- > Literal[0]  (has a job)            T->13 / F->[3]
[13] StackObjID = Local[0]                                             T->3
[ 3] create_object_instance GUID=0xF408F4DB  (the default/plain car)   T->11
[ 4] StackPersonData[81] <  Literal[2]    (TS1FameStarPower)   T->14 -> [14] -> [7]
[ 5] StackPersonData[81] <  Literal[6]                          T->15 -> [15] -> [8]
[ 6] StackPersonData[81] <  Literal[10]                         T->16 -> [16] -> [9]
[ 7] create_object_instance GUID=0x5326FC22
[ 8] create_object_instance GUID=0xD5A8B9A6
[ 9] create_object_instance GUID=0x105BCC90
[10] create_object_instance GUID=0xADADCF4C   (star power 10+, the top tier)
[11] Temps[0] = StackObjID                                          T->RETURN_TRUE
```

So: **star power `< 2` → `0x5326FC22`, `< 6` → `0xD5A8B9A6`, `< 10` → `0x105BCC90`, otherwise
`0xADADCF4C`**. The "has a job" test at `[2]` is a **no-op in retail**: both its branches
(`T->13` and `F->[3]`) end up creating the same plain car `0xF408F4DB`, and instruction `[12]`
(`StackPersonData[80] == 0`, the "no fame at all" test) is dead — nothing targets it. So a sim with no
star power gets `0x5326FC22`, not the plain car.

`TS1FameScore` (80) / `TS1FameStarPower` (81) / `TS1FameStarHighWatermark` (82) are written by the
Wave-G1 `VMTS1FameHelper`, and calls 34/36 (`PromoteFameIfNeeded` / `DemoteFameIfNeeded`) have call
sites here: `ControllerStudioLot.iff` 4128 `'Promotion Check'` `[7]` and
`PhonePluginStudioLots.iff` 4125 `'Promotion Check'` `[1]`. **This part is done.** QA only.

### 3.3 The obsessed fan — generator is auto-spawned, chain is pure script

```
ExpansionPack6/NPC_Superstar_ObsFanController.iff  OBJD 0x41A7  GUID=0x94400258
  'NPC Obsessed Fan - Generator'  type=SimType  Global=1   main=0x1000 (4096)
```

`Global == 1` ⇒ it **is** in `WorldObjects.ControllerObjects` and `VMTS1ActivatorNew.cs:678` spawns it
on every lot load, including lots vanilla never opened. Its trees:

* 4102 `'Create Obsessed Fan?'` (36 instr.) — gates on `Global 320 'Is lot a Downtown Lot?'`,
  `321 'Is lot a Residential Lot?'`, `469 'Is Lot A Community Lot?'`, `479 'Is lot a Studio Lot?'`,
  schedules via `306 'Check Schedule'` / `381 'Schedule with Hour'` / `366 'idle hours'`, then
  `create_object_instance` + `remove_object_instance`.
* 4101 `'create obsessed fan'` — spawns the NPC; `NPC_Superstar_ObsessedFan.iff` supplies
  `PersonGlobals` and its only calls are 10/11 (`Disable/EnableBuildBuy`) in 4107 `'Steal Award'`.

**Opcode census for the whole file:** 2, 8, 15, 16, 18, 26, 31, 42, 271, 280, 306, 320, 321, 355, 366,
381, 469, 479, 4101, 4102 — all implemented. **No work item.**

The fan's appearance data is present: `ExpansionPack6/Skins/csuperstarma_obsessed_fan.cmx.bcf` +
`superstarmafat_obsessed_fan.cmx.bcf` + the matching `xskin-*` sets, and
`TemplateNPCs/SSCelebGeneric.iff` (a `PersonGlobals` person object, OBJD `0x0080`, 5 trees, one call —
25 `SaveSimPersistentData`, implemented) is the Studio Town visitor template.

### 3.4 "Studio tours visiting the home lot" — partially unresolvable

`ControllerStudioLot.iff` contains six controllers, **all `Global == 1`** (so all six spawn
everywhere):

| OBJD | GUID | label | main |
|---|---|---|---|
| 0x41AB | 0x91D0C8CD | `Controller - Studio Traffic` | 4097 `'main - traffic'` |
| 0x41AD | 0x8EFA9A92 | `Controller - Studio Build/Buy` | 4161 |
| 0x41B0 | 0x6A75FC81 | `Controller - Celebrity Spawn` | 4098 `'main - Celebrities'` |
| 0x41B1 | 0x925C17A2 | `Controller - Studio NPC` | 4104 |
| 0x41B2 | 0x20E32C3C | `Controller - Home Fame Decay` | 4108 |
| 0x41B3 | 0xC2AF7E1C | `Controller - Insanity` | 4126 |

`'main - Celebrities'` (4098) gates on `Global 480 'Allow on Studio Lots Only'` → `479 'Is lot a
Studio Lot?'`; on a residential lot the test is false and the tree falls through to `idle 1` and
retries `[1]`. **So the shipped celebrity-spawn controller only fires on studio lots.** I found **no
shipped tree that spawns a visiting celebrity on a residential lot**, and no `generic_sims_call`
anywhere in the pack that is residential-lot-specific apart from the ped markers and the phone
plugin. Combined with §1.1 (call 3 being the only unimplemented call) the honest reading is:

* everything the retail *data* can express is implemented;
* the "celebrity shows up at your house" event in retail was most plausibly driven by the **online
  server** picking a visitor, which is exactly the class of thing the roadmap already lists as
  server-side (see `ADDON_PROGRESSION_PLAN.md` §4.4–4.8, "Server-side work" column).

That makes it a **QA / scope** item, not an engine item. Do not build a "celebrity visit" event.

### 3.5 The fame special objects — confirmed clean

`gcall` over the three objects:

| object | calls |
|---|---|
| `ExpansionPack6/SatelliteDish.iff` (semi `ArtGlobals`, 4 views, `TTAB 0x0081…0x0084`) | **none** |
| `ExpansionPack6/aquariumfame.iff` `'Aquarium - Fame'` | **none** |
| `ExpansionPack6/SkydivingSimulator.iff` `'SkyDiving Simulator - Part A3'` | 6 `RemoveFromFamily` ×1 (implemented) at 4097 `'Interaction - Enter Simulator'` `[133]` |

Their test trees are trivial:
`SatelliteDish.iff` 4098 / `aquariumfame.iff` 4098 both = `Params[0] == Literal[1]` →
`Temps[0] = Tuning[129]` → `Global 285 'old Wait for Notify'`. `SkydivingSimulator.iff` 4098 =
`Global 288 'Is Stack Object My Peer( Fame )?'` → `Global 321 'Is lot a Residential Lot?'` →
`budget` (a token price) → `change_action_string`.
**No work item.**

---

## 4. Target 3 — Unleashed pet shows and training

**Verdict: zero unimplemented calls and zero unimplemented opcodes. This is entirely a QA item, plus
two documentation corrections.**

### 4.1 Pet shows

```
ExpansionPack5/Control-PetShow.iff  OBJD 0x41A7  GUID=0xC1BA4467
  'Controller - Unleashed - Pet Show'  type=SimType  attrs=12  Global=1
  OBJf: init=0x100A (4106)  main=0x1000 (4096)  load=0x1001 (4097)
```

`Global == 1` ⇒ auto-spawned by `VMTS1ActivatorNew`. 14 trees (4096 `'main'`, 4097 `'load tree'`,
4098 `'Interaction - Activate Head Judge'`, 4099 `'Interaction - Listen to Head Judge'`, 4100
`'Interaction - Pet Judge'`, 4101 `'init common'`, 4102 `'Interaction - Pet Actions'`, 4103
`'Make Reward'`, 4104 `'Family Dog On Lot?'`, 4105 `'Family Cat on Lot?'`, 4106 `'init template'`,
4107 `'Autonomous Pet Actions'`, 4108 `'CT - Generate Pet Tokens'`, 4109 `'Judge Sound'`).

**`generic_sims_call` census for this object: zero sites.** Opcode census: 2, 7, 8, 13, 15, 16, 18,
22, 23, 25, 26, 27, 28, 31, 32, 36, 42, 44, 45, 46, 51 + globals 271/280/366/469/470/4101/4103/4104/
4105/4108/4109 — **all implemented**. Same for `PetPedestal.iff` (15 trees, zero calls) and
`NPC_Unleashed_Trainer.iff` (zero calls).

Scoring — `Control-PetShow.iff` 4103 `'Make Reward'`, which reads the pet's three skill slots and
writes the pedestal's attribute 5 (the award level):

```
[ 0] Local[3] = StackObjID                       03,00,00,00,00,05,19,0A   T->1
[ 1] Local[0] = StackAttrs[1]                     00,00,01,00,00,05,19,01   T->12
[12] Local[0] = StackObjID                        00,00,00,00,00,05,0A,19   T->2
[ 2] Local[1] = StackAttrs[4]                     01,00,04,00,00,05,19,01   T->3
[ 3] Local[2] = Literal[0]                        02,00,00,00,00,05,19,07   T->13
[13] Local[4] = Literal[0]                        04,00,00,00,00,05,19,07   T->4
[ 4] StackObjID = Local[0]                        00,00,01,00,00,05,0A,19   T->5
[ 5] Local[2] += StackPersonData[10]              02,00,0A,00,00,03,19,13   T->6
[ 6] Local[2] += StackPersonData[11]              02,00,0B,00,00,03,19,13   T->7
[ 7] Local[2] += StackPersonData[12]              02,00,0C,00,00,03,19,13   T->8
[ 8] Local[2] > Tuning[1]                         02,00,01,00,00,00,19,1A   T->14
[14] StackMotives[3] > Tuning[2]                  03,00,02,00,00,00,0F,1A   T->17
[17] StackAttrs[0] = Local[0]                     00,00,00,00,00,05,0A,19   T->10
[10] StackAttrs[5] = Literal[4]   <- blue ribbon 05,00,04,00,00,05,01,07   T->RETURN_TRUE
[ 9] Local[2] > Tuning[0]                         02,00,00,00,00,00,19,1A   T->19
[19] StackAttrs[0] = Local[0]                     ...                     T->15
[15] StackAttrs[5] = Literal[2]   <- second       05,00,02,00,00,05,01,07   T->RETURN_TRUE
[20] StackAttrs[0] = Local[0] -> [16] StackAttrs[5] = Literal[1]   <- third
[18] StackAttrs[0] = Local[0] -> [11] StackAttrs[5] = Literal[3]   <- booby
```

with `bcon Control-PetShow.iff 0x1000 = [1500, 2250, 75]`, i.e.
`Tuning[0]=1500` (second place), `Tuning[1]=2250` (blue ribbon, also requiring the pet's
`StackMotives[3]` Fun above `Tuning[2]=75`), `Tuning[2]=75`. The ribbons themselves are handed over by
`PetPedestal.iff` 4108 `'Make Reward'`:

```
[16] manage_inventory [00,00,01,29,2D,43,80,D8]  ; AddToken type=0 GUID=0xD880432D
[19] manage_inventory [00,05,01,29,95,38,1E,D8]  ; AddToken type=5 GUID=0xD81E3895
```

`findobj`: `0xD880432D` = `ExpansionPack5/PetAwards.iff` `'Pet - Award - First Prize'`,
`0xD81E3895` = `'Pet - Award - Second Prize'`. Both are `NGBH` inventory items, so a winner's ribbons
persist and `PetAwardCabinet.iff` / `collar_*_ribbon.iff` display them.

### 4.2 "Training" — it *is* person data 10/11/12, and here is who writes it

A whole-install scan for `MyPersonData`/`StackPersonData` writes to slots 10, 11, 12 gives the
authors (counts are per file):

| slot | writers |
|---|---|
| 10 | **`ExpansionShared/SocialPetSolo.iff` (4)**, plus 1 line in every NPC's `init tree` (`PersonGlobals.iff` 8358 `'Init NPC Jobs'`, `RentalClerkGlobals.iff` 8358, and ~55 NPC objects) |
| 11 | **`ExpansionShared/PetCommandSocials.iff` (3)**, `PetSocialsCat.iff` (1), `PetSocialsDog.iff` (1), `ExpansionPack5/PetParrot.iff` (2), `Global/DogGlobals.iff` (1), `Global/CatGlobals.iff` (1), `Objects/CarPortal.iff` (25) |
| 12 | **`ExpansionShared/PetSocialsDog.iff` (7)**, `PetSocialsCat.iff` (5), `PetCommandSocials.iff`, `Global/DogGlobals.iff` (1), `Global/CatGlobals.iff` (1) |

So pet training = the pet socials / pet command socials in `ExpansionShared` plus the parrot, all of
which are shipped and loadable, writing the pet's three skill slots through
`VMAvatar.SetPersonData`. That path is implemented **and persisted** (`NBRS.cs:141` reads/writes 88
person-data shorts, and slots 10–12 are inside that).

Two consequences worth a matrix row:

1. **`VMAvatar.SetPersonData` scales skill growth by `SkillGameplayMul(Thread.Context.VM)`**
   (`VMAvatar.cs:919–933`). With Gameplay → Skills off (or set to 0) the pet's scores never rise and
   the pet show is permanently un-winnable. That is retail-faithful, but it is the single most likely
   "pet show is broken" report.
2. There is **no skill cap for pets** in the shipped data (the score tree compares a raw sum against
   `Tuning[0]=1500` / `Tuning[1]=2250`, and pets have no skill lock), so a long-trained pet reaches
   the blue ribbon correctly.

**No work item beyond a matrix row.**

### 4.3 The one real risk in this target: `.cfp` skill animations

The whole pet-show choreography is authored as `xskill-*.cfp` files (e.g.
`xskill-a2o-petshow-judge-inspect-good.cfp`, `xskill-k2o-petshow-pet-start-bad.cfp`) — 60+ of them in
`ExpansionPack5/Animations`. FSO has a `CFPCodec` (`tso.content/Codecs/CFPCodec.cs`), registers
`.cfp` in `SmartCodec`, and `TS1BCFProvider` merges a BCF with its CFP
(`TS1BCFProvider.cs:185–190`). So the animations load. **Verify once** that a `.cfp` actually plays
(the `FailedContentService` report is the headless way to see a broken one) and then stop worrying.

---

## 5. Target 4 — Makin' Magic quests, call 38, and "pyrite" autonomy

**Verdict: call 38 is landed and the quest system needs no further engine work. "Pyrite" does not
exist in the shipped data and should be re-scoped, not implemented.**

### 5.1 Call 38 — **already implemented** (parallel L1 wave); here is the independent confirmation

**Status: done.** `VMGenericTS1Call.cs` case 38 now delegates to a `GetTokensFromString(context)`
method built on `VMTS1PresentationHelper.TokenListRequest` / `ReadTokenList` /
`TokenizeTokenList`, with a `ResolveLocalLiteral(context, table, index)` resolver, and there are new
tests (`Simitone.Tests/Engine/VMTS1TokenTableTests.cs`, `VMTS1PresentationHelperTests.cs`). This
section records what an independent read of the retail data says about it, plus the one anomaly worth
a reviewer's eye. **No further implementation work.**

The four call sites are all in `ExpansionPack7/MagicMasterSpells.iff` (`'Magic Controller'`, OBJD
`0x41A7`, GUID **`0xB6C90029`**, `Global = 1` ⇒ auto-spawned by `VMTS1ActivatorNew`). Reached from
`Global.iff` 493/494/499/500, each of which does `set_to_next` to that object and then
`run_tree_by_name`.

**The register contract, confirmed.** In 4098 `'Get Spell From Ingredients'`:

```
[ 2] Local[1] == Literal[4]              T->31      ; selector
[31] Local[5] = Literal[9]               T->4       ; loop bound 9
[ 3] Local[1] == Literal[3]              T->30
[30] Local[5] = Literal[24]              T->4       ; loop bound 24
[ 7] Temps[0]  = Local[1]                T->6       ; <-- Temp0 = the STR *chunk id* (3 or 4)
[ 6] Temps[1]  = Local[0]                T->32      ; <-- Temp1 = the loop index
[32] Local[0]  > Local[5]                T->27
[27] Temps[0]  == Literal[-1]            T->RETURN_FALSE
[ 5] call 38                             T->8       ; the four call sites all have F = 253 ("carry on")
[ 8] Temps[0]  == Literal[-1]            T->RETURN_FALSE
[10] Temps[0]-- > Literal[3]            T->15      ; "exactly three ingredients"
[13] Local[3] == Temps[2]                T->14
[14] Local[4] == Temps[3]                T->16
[16] Temp0 = 0                                                  ; "no such recipe"
```

and 4104 `'Get Recipe From Ingredients'` is the same shape with `[2] Local[1] == Literal[5] →
[29] Local[5] = Literal[5]` and `[1] Local[1] == Literal[6] → [30] Local[5] = Literal[4]`, and
`[6] Temps[0] = Local[1]`, `[5] Temps[1] = Local[0]`.

**The decisive cross-check** (this is what pins the reading): each tree's loop bound is *exactly* the
number of usable entries in the `STR` chunk whose **chunk id** equals the value loaded into `Temp0`.

| tree | `Temp0` selector | bound | matching chunk | usable entries |
|---|---|---|---|---|
| 4098 | `3` | 24 | `STR 0x0003` | 24 (`[0]` = "not used", `[1..24]` recipes) |
| 4098 | `4` | 9 | `STR 0x0004` | 9 |
| 4104 | `5` | 5 | `STR 0x0005` | 9, but `[5]` onwards is the tail it iterates |
| 4104 | `6` | 4 | `STR 0x0006` | 5, `[6..9]` empty |

`Temp0` is therefore a **chunk id, not a string index** — which is exactly why a literal id could
never have disambiguated the tables, and why the per-tree-literal-table theory is wrong. `STR 0x0006`
is a *partial* duplicate of `STR 0x0005` (identical at `[1..5]`, empty from `[6]`), and it is the one
4104's "mode 6" branch iterates; the shipped implementation is documented to treat an unfilled entry
as a parse failure rather than a crash, which is the right answer for it.

Other tables in the same file, for the UI and for cross-checking:
`STR 0x0002` = ingredient names (`[1]`"Honey" `[2]`"Grapes" `[3]`"Pixie Dust" …),
`STR 0x0007` = `"GUID Lookup"` listing the token GUIDs as hex (`[1]`"7A7D732E" `[2]`"7C780508" …),
matching `TokensMagic.iff` `0x41C1 'Magic Token - Ingredient #01( Honey )' GUID=0x7A7D732E`,
`0x41BA '… #02( Grapes )' GUID=0x7C780508` (8 of them).

> **Reviewer note (the one anomaly).** In 4098 the instruction *before* the call is
> `[4] Temps[0] = Literal[1]` with `T->[7]` and `F->[5]` — and `[7]` is `Temps[0] = Local[1]`, i.e. it
> **overwrites** `Temp0` with the real table id. So the only edge into `call 38` at `[5]` from inside
> the loop is the *false* branch of an assignment, i.e. the call is only ever made with `Temp0` still
> holding the literal `1`, which names no `STR` chunk. The same shape appears in 4104 (`[3]`) and,
> by symmetry, in 4099/4103. Either TS1's assignment can return false here (in which case the shipped
> call *always* sees table id 1 and every recipe fails, which the game obviously did not do), or the
> `[4]`/`[3]` instruction is unreachable and the call is entered by some path this reading misses.
> **This is worth ten minutes with a debugger on a Magictown lot** (V-MAG-2 in §6.1). It is not a
> blocker — the answer is a safe failure either way — but it is the one place where the register
> story does not close from static data alone.

### 5.1b What the quest scripts need *besides* call 38: nothing

The token counters are `NGBH` inventory items, not a new storage system:

```
Global.iff 502 'Magic - Add Token'   [0] Params[0] == Tuning[0x4880] T->29
                                       [29] manage_inventory [00,08,01,09,2E,73,7D,7A]  ; AddToken type=8
Global.iff 498 'Magic - Remove Token'      ... RemoveToken type=8, same GUIDs
Global.iff 506 'Magic - Find Token( Invisible )' ... FindToken type=6, same GUIDs
```

Eight branches each (`0x4880 … 0x489C` = `Global.iff` `OTF 0x0111` keys 0…28), dispatch on
**`Params[0]`** (scope 9) — implemented. Spell/recipe "made" flags are `Global.iff` 495/496, which
`set_to_next` to the Magic Controller and `run_tree_by_name` a named tree on it. Quests are
`Global.iff` 489 `'Am I On a Quest?'` (three `manage_inventory` FindToken probes on GUIDs
`0xA30053E8` / `0x51084400` / `0xC81E3C91`, type 6 and type 0) and 490
`'Magic - Teleport to Stack Object'` (51 instructions, pure `find_location_for` +
`create_object_instance 0x8C94DFD4` + `set_to_next` + `verify person in stack obj`).

`ControllerQuest.iff` (OBJD `0x41A7`, GUID `0xC953333E`, label `'Magic - Controller - Quest'`,
semi `SocialGlobals`, **40 attributes**, `Global = 0`, `usesFnTable=1` with `main = 0x1001`) has
**zero `generic_sims_call` sites**; its opcode set is 2, 7, 8, 13, 16, 18, 22, 27, 31, 32, 42, 44, 45,
49, 51 + globals 271/272/273/280/287/288/355/490 — all implemented. Because `Global = 0` it is
**not** auto-spawned, so it must be created by the Makin' Magic phone plugin / `MagicStarterKit` on
the lot. **QA row: confirm the quest controller exists on a Magictown lot; if quests never start, this
object is the reason.**

### 5.2 "Pyrite autonomy" — the term is not in the shipped data

Searched every `TTA`/`STR`/`TTAB` string, every OBJD label and every BHAV literal in all 11 fars for
`pyrit` / `Pyrite` / `Pyrit`: **zero hits.** There is no pyrite object, no pyrite string and no
pyrite tree. The nearest shipped things, in decreasing likelihood of what was meant:

| candidate | file | notes |
|---|---|---|
| **Magic Growth family** | `MGBleanstalk.iff`, `MGCrystals.iff` (`'Magic Growth - Crystal - Spook'`, GUID `0xC5049AEE`), `MGFlower.iff`, `MGRoot.iff` | `MGBleanstalk.iff` 4110 `'Climb into lot 99'` `[10][16]` and 4108 `'Bring Sim to lot 99'` `[14]` are the **only** Makin' Magic sites of call 3 (`PullDownTaxiDialog`) outside the phone plugins — i.e. the growth's arrival/departure is gated on the same missing dialog. Semis are `ArtGlobals!Magic Growth - Beanstalk`. |
| `NectarController.iff`, `NPC_Controller_Spellbound.iff` | `ExpansionPack7` | Magictown life |
| `ControllerQuest.iff` | `ExpansionPack7` | the quest system (above) |
| `MagicArena.iff`, `MagicMiniGolf*.iff` | `ExpansionPack7` | Magictown minigames |

**Action: re-scope the item.** The "autonomous magic creature" behaviour that the data can express is
(a) the Magic Growth arrival/departure, which is blocked only by call 3 (§6 item 1), and (b) the
quest controller, which needs no new engine work (§5.1b). Nothing else. If "pyrite" is a
Simitone-side naming or a mis-transcription, the correct outcome is **no new engine work** — say so in
the roadmap rather than inventing a feature.

### 5.3 The spellbook / cookbook UI already has the data

`STR 0x0002` (ingredient names) and `STR 0x0007` (token GUIDs) in `MagicMasterSpells.iff`, plus
`GameData/magic.iff` (25 spell titles / 9 recipes — what the Wave-G2 `MakingMagicTables` reader
already uses), are the same data the UI needs. No new extraction job. Note that
`VMGenericTS1Call.cs`'s own call-38 remark now records the four callers
(`Cookbook.iff`, `SpellBook.iff`, `CharmMaker.iff`, `BakersOven.iff`, `NectarPress.iff` via
`Global.iff` 493/494/495/496/499/500) — that list is the last piece of chain evidence §5.1 could not
read from the call sites alone, and it closes the loop.

---

## 6. Ordered work items (value ÷ risk)

> **Status 2026-09-28:** items **1, 3, 5, 6** and **7** landed in Waves M–O; item **2** is the
> V-MAG-2 playtest only; item **4** is the human matrix; item **8** is permanently blocked.
> See `docs/ADDON_PROGRESSION_PLAN.md` §12.

| # | Item | Owner | Value | Risk | Blocked? |
|---|---|---|---|---|---|
| **1** | **Call 3 `PullDownTaxiDialog`** — add `VMDialogType.TS1TaxiLeave` (values 5–15 are taken by Downtown…Cookbook) + a `tso.client` panel; the answer register is **never read** (proved: `VacationPedMarker.iff` 4128 `[61..64]` and `PedMarkersStudio.iff` 4110 `[6]` both continue the idle loop), so a fixed "Leave the lot?" yes/no that returns `GOTO_TRUE` is sufficient. It unblocks the vacation arrival tail, the Studio Town arrival, and `MGBleanstalk` | FreeSO `tso.simantics/Primitives/VMGenericTS1Call.cs` + `VMDialogPrivateStrings.cs` + a new `tso.client/UI/Panels/*` | High — the last real missing call, and it is player-facing | Med — 2 files outside `tso.simantics` (the plan notes `VMDialogPrivateStrings.cs` was declared out of scope in an earlier wave; that needs re-deciding) | **DONE — Wave M.** Data: none needed. The earlier "do not return `GOTO_FALSE` or it self-loops at `[14]`" note was **wrong**: `VacationPedMarker.iff` 4128 `[13]` is `T->61 F->253`, and `VMThread.MoveToInstruction`'s `case 253` picks the non-253 pointer, so both answers land on `[61]` and `[14]` is dead code. The implementation still always returns `GOTO_TRUE`, now for a verified reason. |
| **2** | ~~Call 38 table selection~~ — **DONE** by the parallel L1 wave. Residual: the `[4] Temps[0] = Literal[1]` anomaly in `MagicMasterSpells.iff` 4098/4104 (§5.1 reviewer note) — a ten-minute debugger check on a Magictown lot, not a code change | QA only (§6.1 V-MAG-2) | — | Very low | No |
| **3** | **`push_interaction` null guard** — `VMPushInteraction.cs:42`, one line | FreeSO `tso.simantics` | Med — pure insurance; 2 287 shipped sites are provably safe today | Very low | No |
| **4** | **QA matrix rows for all four targets** (below) | handed to the user | High — it is the only thing standing between this document and a known-good state | None | Needs a human at the controls (prohibited to automate) |
| **5** | **`NeighborPersonData` clamp** — `VMMemory.cs` scope 32 uses `.ElementAt(data)` on an 88-short array; clamp instead of throwing | FreeSO `tso.simantics` | Low — latent, retail never exceeds 87 | Very low | No |
| **6** | **Doc corrections**: (a) `VMGenericTS1Call.cs` — call 0 *has* 2 call sites (`Objects/Tutorial.iff` 4137/4148); (b) the same file's note says `PedMarkersStudio` 4110 `[15]` goes to 4124 *"Make Car based on Fame"* — it goes to 4124 *"Clear Studio Token"*; *"Make Car based on Fame"* is **4113**; (c) `ADDON_PROGRESSION_PLAN.md` §4.5 — replace the "pet show/training UI" row and the "roommate / rental UI" row with "pure lot-script content, verified" | FreeSO comment + Simitone docs | Med — stops the next wave re-deriving these | None | No |
| **7** | **Terminology / scope closure for "pyrite autonomy"** — no such object in the data; re-scope to Magic Growth (blocked only by #1) + quest controller (#2) | Simitone docs | Med | None | Terminology, not data |
| **8** | **Call 35 `TakeTaxiHook`** | — | — | — | **BLOCKED — see §7** |

### 6.1 QA rows to hand to the user

Format matches `docs/PHASE3_TEST_MATRIX.md`. Each is a headless-verifiable *observation* the user
reports; none may be automated.

| ID | Expansion | Steps | Expected (from the disassembly) | Fails if |
|---|---|---|---|---|
| V-VAC-1 | Vacation | Pay booth → "Go on vacation" → land on House40 | `Call 26` runs; the family walks to the Downtown sim; the car drives in (`special_effect 513,73`) | arrival marker does not run → check that the pay-booth phone plugin object is on the lot |
| V-VAC-2 | Vacation | Play ~1 in-game hour, then leave | `Controller - Vacation - Score` (0x9CC72382) exists; `Main - Score` runs `Process Score`; `MyAttrs[5]` changes by ±(6/3/−2) | score attribute never moves → check `BCON 0x1002` / `Tuning[256…261]` resolution |
| V-VAC-3 | Vacation | Return home, save, reload, go to a *different* vacation lot | `Temps[0]` of the score controller is pre-seeded from `NGBH` type 6 GUID `0x842A8C3B` | score resets to 0 → `NGBH.Write` / `SaveNeighbourhood` |
| V-VAC-4 | Vacation | Talk to the Vacation Director about a souvenir | `Interaction - Give Souvenir` creates `HDSI_Souvenir.iff` (0x2F1DDC9C) and pushes interactions 27/28 | no souvenir |
| V-VAC-5 | Vacation | Check in at the lodge front desk | §66/§72/§62 charged × party size; a Hotel Key (`0x3AC022D6`) is added per sim; if `Count Beds on Lot` ≤ 2 the desk **refuses outright** (`STR 0x012D[5]`, "still under construction") — the "check in anyway?" offer is dead code | no key / wrong price → check `Tuning[256]` and `set_to_next` search type 6 |
| V-VAC-6 | Vacation | Check out, then leave the island | keys removed (`4129 'Clear Hotel Keys'`), score persisted | keys survive |
| V-SS-1 | StudioTown | Phone → "Go to a studio lot" | `PedMarkersStudio` 4110 runs; car model matches `StackPersonData[81]` (star power): `<2`→`0x5326FC22`, `<6`→`0xD5A8B9A6`, `<10`→`0x105BCC90`, else `0xADADCF4C` | wrong car → fame ladder |
| V-SS-2 | StudioTown | With `MyPersonData[80]` (fame) > 0, sit at a Home Lot | an Obsessed Fan is created by `0x94400258` (auto-spawned) | no fan → check the generator's `Create Obsessed Fan?` gate |
| V-SS-3 | StudioTown | Use the satellite dish / fame aquarium / skydiving simulator | `View TEST` trees pass; `Temps[0] = Tuning[129]` writes the once-a-day flag | interaction never offered |
| V-PET-1 | Unleashed | Community lot with a Pet Pedestal, `Gameplay → Skills` **on** | judge/trainer/pet scene plays; ribbon `0xD880432D` added | no ribbon → check the *cfp animations* and `Tuning[0/1/2]` |
| V-PET-2 | Unleashed | Same, with Skills **off** | pet's slots 10/11/12 never rise → always "booby prize" | *this is expected retail behaviour* — do not "fix" it |
| V-PET-3 | Unleashed | Train the pet (`SocialPetSolo` / `PetCommandSocials` / parrot), save, reload | slots 10/11/12 persisted (`NBR`) | skills reset |
| V-MAG-1 | Magic | Magictown lot | `Magic Controller` (0xB6C90029) and `Magic - Controller - Quest` (0xC953333E) both exist | quest controller missing → it is `Global=0`, so it must come from the phone plugin |
| V-MAG-2 | Magic | Cast any spell / cook any recipe | the correct recipe is chosen from `STR 0x0003` / `0x0004` / `0x0005` / `0x0006`; a systematic "this combination does not exist" ⇒ the §5.1 reviewer note (call 38 is only reachable from the false branch of an assignment) | "this combination does not exist" |
| V-MAG-3 | Magic | Climb the beanstalk (`MGBleanstalk.iff` 4110) | arrival/departure; gated on call 3 | blocked on item #1 |

---

## 7. Explicitly blocked / not answerable from the install

1. **Call 35 `TakeTaxiHook` — no data exists.** Both call sites are in
   `ExpansionPack6/PhonePluginStudioLots.iff`:
   * 4100 `'Interaction - Go to Studio Lot'` `[66]`: `T->62` (`Global 479 'Is lot a Studio Lot?'`),
     `F->0` → `[67] StackObjID = MyObj[11]` → `[68] Global 485 'Is Sim in Stack Object on Fame Track?'`
     → `[28]`.
   * 4118 `'Interaction - Go Elsewhere'` `[82]`: `T->46` (`Global 4113 'send me out of world'`),
     `F->0` → `[83] StackObjID = MyObj[11]` → `[84] Global 485` → (`T->87`, `F->85`);
     `[85] change_action_string(302, 11)` — the "your agent's phone won't stop ringing" prompt —
     → `[86] StackObjID = Local[3]` → **back to `[82]`, i.e. the same call again**.

   So the question is "does an Obsessed Fan own this departure?", and the two answers are "the car
   drives away now" and "the phone keeps ringing, ask again". **No TS1 chunk records ownership of a
   trip**: there is no per-trip object, and the fan (`NPC_Superstar_ObsessedFan.iff`) is a lot actor
   with no link back to a departure. I checked the candidates that would hold it — `NGBH` inventory
   (8 magic tokens, 6 vacation tokens, hotel key, prize tokens; nothing trip-shaped),
   `Neighbour.PersonData` (88 slots, max index 87 used), `FAMI`, `SIMI` lot globals (38 shorts, all
   identified in §1.3), and the `TS1GameState` cross-lot struct. Inventing a rule (a fame threshold, a
   random roll) would pick one of two shipped endings at random. **Keep the documented no-op
   returning `GOTO_TRUE`** (the "car simply drives away" ending — the one that cannot strand a
   traveller). This is a documented, permanent block, not a backlog item.

2. **Call 3's register contract** — the answer is provably discarded, so nothing is lost by not
   modelling it; but the *panel* has to be written, and `tso.client` ownership is a scheduling
   decision, not a data question.

3. **`Global[10]`'s retail meaning is *not* answerable from the front desk** — the only shipped BHAV
   that reads it, `FrontDesks.iff` 4108 `[49]` (`Temps[0] == Global[10]`, where `Temp0` is the
   Hotel-Key count), is **unreachable**: `[48]` jumps to `[50]` and nothing else targets `[49]`.
   The three Simitone readers (`UIMainPanel`, `TS1GameScreen`, `SaveService`) treat it as the house
   number, which matches every shipped template's `SIMI` (§1.3). **No action; do not build on it.**

4. **"Pyrite"** — not in the data (§5.2). This is a requirements question, not a data question.

5. **Nothing in this document required decrypting an encrypted cabinet.** Every `.far` in
   `GameData`, `Deluxe`, `ExpansionPack…7`, `ExpansionShared` and all 70 `Downloads\*\*.far` read
   cleanly; the only unreadable file class is `*.stx` (the string tables), which are duplicates of
   the `.iff` entries and were not needed.

---

## 8. Appendix — exact reproduction

```powershell
$BV  = "$env:LOCALAPPDATA\Temp\opencode\bhavscan2\bin\Release\net9.0\bhavscan2.exe"
$S   = "D:\Program Files\Sims"
$FARS = @("$S\GameData\Global\Global.far","$S\GameData\Objects\Objects.far","$S\Deluxe\Deluxe.far",
          "$S\ExpansionPack\ExpansionPack.far","$S\ExpansionPack2\ExpansionPack2.far",
          "$S\ExpansionPack3\ExpansionPack3.far","$S\ExpansionPack4\ExpansionPack4.far",
          "$S\ExpansionPack5\ExpansionPack5.far","$S\ExpansionPack6\ExpansionPack6.far",
          "$S\ExpansionPack7\ExpansionPack7.far","$S\ExpansionShared\ExpansionShared.far")

& $BV census    $FARS                       # §1.1
& $BV opcensus  $FARS                       # §1.2
& $BV pushaudit $FARS                       # §1.3 push_interaction
& $BV pdrange   $FARS                       # §1.3 person data
& $BV tad       $FARS                       # §5.1 Params/Tuning dispatch
& $BV pdw 10    $FARS                       # §4.2 pet training authors
& $BV objids ""                             # every OBJD in the install
& $BV objtwhere                             # §2.7 no OBJT chunks are shipped

& $BV findobj 9CC72382                      # §2.1 the vacation score controller
& $BV findobj 842A8C3B                      # §2.2 the score token object
& $BV findobj 2F1DDC9C                      # §2.4 the souvenir social
& $BV findobj B6C90029                      # §5.1 the magic controller
& $BV findobj 7A7D732E                      # §5.1 ingredient #01
& $BV findobj 94400258                      # §3.3 the obsessed-fan generator
& $BV findobj C1BA4467                      # §4.1 the pet-show controller
& $BV findobj D81E3895                      # §4.1 second-prize ribbon

& $BV disraw "$S\GameData\Global\Global.far" "Global.iff" 456 0 12     # §2.1
& $BV disraw "$S\GameData\Global\Global.far" "Global.iff" 459 0 10     # §2.2
& $BV disraw "$S\ExpansionPack4\ExpansionPack4.far" "VacationPedMarker.iff" 4110 0 6
& $BV disraw "$S\ExpansionPack4\ExpansionPack4.far" "FrontDesks.iff" 4108 0 54
& $BV bcon    "$S\ExpansionPack4\ExpansionPack4.far" "FrontDesks.iff"
& $BV disraw "$S\ExpansionPack4\ExpansionPack4.far" "NPC_Vacation_Controller.iff" 4108 0 20
& $BV bcon    "$S\ExpansionPack4\ExpansionPack4.far" "NPC_Vacation_Controller.iff"   # threshold 150
& $BV disraw "$S\ExpansionPack6\ExpansionPack6.far" "PedMarkersStudio.iff" 4110 0 19
& $BV disraw "$S\ExpansionPack6\ExpansionPack6.far" "PedMarkersStudio.iff" 4113 0 18
& $BV disraw "$S\ExpansionPack6\ExpansionPack6.far" "PhonePluginStudioLots.iff" 4118 82 88
& $BV disraw "$S\ExpansionPack5\ExpansionPack5.far" "Control-PetShow.iff" 4103 0 21
& $BV bcon    "$S\ExpansionPack5\ExpansionPack5.far" "Control-PetShow.iff"        # 1500/2250/75
& $BV disraw "$S\ExpansionPack5\ExpansionPack5.far" "PetPedestal.iff" 4108 0 20
& $BV lits    "$S\ExpansionPack7\ExpansionPack7.far" "MagicMasterSpells.iff"   # §5.1 tables
& $BV simi    "$S\TemplateVacation\House40.iff" "$S\TemplateStudiotown\House85.iff"  # §1.3
```

## 9. Cross-references

* `FreeSO/TSOClient/tso.simantics/Primitives/VMGenericTS1Call.cs` — the call table; the
  "still not implemented" block at `:1399` is the file this plan audits, and §1.1/§7 correct three
  of its statements.
* `FreeSO/TSOClient/tso.simantics/Model/VMGenericTS1CallMode.cs` — the authoritative call names.
* `FreeSO/TSOClient/tso.simantics/VMContext.cs:355-499` — the registered opcode list (basis of §1.2).
* `FreeSO/TSOClient/tso.simantics/Utils/VMTS1ActivatorNew.cs:672-687` — the `Global == 1`
  controller spawn that §3.3/§4.1/§5.1 rely on.
* `FreeSO/TSOClient/tso.files/Formats/IFF/Chunks/NGBH.cs` — the `InventoryByID` store that carries
  the vacation score, prize tokens, hotel key, souvenirs, magic tokens and pet ribbons.
* `docs/ADDON_PROGRESSION_PLAN.md` §4.4–4.8, §10 items 3/6, §11 Wave K/L — the roadmap rows this
  plan supersedes or corrects.
* `docs/PHASE3_TEST_MATRIX.md` — where §6.1's rows belong.
