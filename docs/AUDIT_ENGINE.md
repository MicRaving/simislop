# SimAntics engine audit — FreeSO/TSOClient/tso.simantics

Read-only pass over the VM, primitives, engine/threading, entities, marshals, net commands
and TS1 helpers on branch `simitone-local`. Focus is TS1-in-single-player, so the
`vm.TS1` / `VMGenericTS1Call` / `VMTS1*` / `VMTS1Activator*` paths got the most attention.

**How to read this.** Every finding is `id · file:line · what's wrong · why it matters ·
severity · fix · confidence`. Severity is `crash` / `correctness` / `perf` / `consistency` /
`cosmetic`. Confidence is `confirmed` (I read the code path end to end, or reproduced it
against the shipped install or a marshal), `likely` (the code is unambiguous but I could not
prove a reachable trigger), or `smell` (real, but the impact depends on data I did not have).
Findings are ordered by severity. Nothing here is padded; the compiler warnings, the dead
branches and the copy-paste drift are listed at the end and are the cheap half of a fix wave.

**Reproduction notes.** Two things were actually run:
* `dotnet build -t:Compile` over `FSO.SimAntics.csproj` (0 errors, 198 warnings) — the
  `CS0184`, `CS0162`, `CS0436`, `CS0219` and `CS0168` findings below are quoted from it.
* The read-only retail disassembler at `%LOCALAPPDATA%\Temp\opencode\bhavscan2` against
  `D:\Program Files\Sims` (all 84 non-sound `.far`s). Findings **E-26**, **E-28** and the
  ">255 nodes is not a bug" note in *Not findings* come from it. Its `bhavscan2.csproj`
  HintPaths pointed at a build output that no longer existed, so two already-built
  `FSO.Files.dll` / `FSO.Content.dll` were copied from `Simitone.Client\bin\Release\net10.0`
  into the directory the project expected. No SimAntics source was touched, and the
  30 already-modified files `git status` reports under `tso.simantics` are pre-existing
  uncommitted fork work, not mine.

---

## Crash

### E-01 · `Entities/VMMultitileGroup.cs:452` · index a filtered list with the unfiltered length
```csharp
for (int i = 0; i < input.Objects.Length; i++) ((VMGameObject)Objects[i]).EnableParticle(256);
```
The loop just above (`Load`, lines 431-448) *skips* ids whose object failed to resolve
(`if (obj == null) continue;`), so `Objects` is shorter than `input.Objects`. This loop is
reached only when the group's base object is `Broken`, and a broken multitile is exactly the
case where a sub-object is most likely to be missing. Two faults in one line: the index can
run off the end, and `Objects[i]` may be an avatar, which is not a `VMGameObject`.
* **Why it matters** `VM.LoadAsync` builds one `VMMultitileGroup` per marshal entry; this
  throws during lot load, before the lot is usable, with no catch on the path.
* **Fix** `for (int i = 0; i < Objects.Count; i++) if (Objects[i] is VMGameObject gob) gob.EnableParticle(256);`
* **Confidence** confirmed (read).

### E-02 · `Entities/VMEntity.cs:1741` · master definition dereferenced without a null check
```csharp
if (input.MasterGUID != 0)
{
    var masterDef = FSO.Content.Content.Get().WorldObjects.Get(input.MasterGUID);
    MasterDefinition = masterDef.OBJ;      // NRE when the master is not installed
    UseTreeTableOf(masterDef);
}
```
The constructor path is careful about a missing `objDefinition` (`LoadAsync` even records a
`VMLoadError` and sets `LoadFailed` for it), but the *load* path does not. A save written on a
machine that had a multitile master, opened on one that does not, dies on the first entity.
* **Why it matters** any `LoadErrors`-tolerant design is defeated by the one check that was
  left out; a partial mod install turns into "cannot open the lot".
* **Fix** mirror `LoadAsync`: if `masterDef == null`, log a `VMLoadError`-shaped warning,
  leave `MasterDefinition` null and continue.
* **Confidence** confirmed (read).

### E-03 · `Engine/VMStackFrame.cs:160` + `Utils/VMTS1ActivatorNew.cs:83` · the SemiGlobal NRE that was fixed in one place only
```csharp
if (input.RoutineID >= 8192) Routine = (VMRoutine)ScopeResource.SemiGlobal.GetRoutine(input.RoutineID);
```
`SemiGlobal` is only set by the GLOB chunk lookup, so it is null for a GLOB-less object, and
retail calls the `0x7FFF`/`0xFFFF` "no such tree" sentinel from GLOB-less objects in bulk.
`VMThread.ExecuteSubRoutine` (lines 626-638) carries a twenty-line comment naming **131 shipped
sites over 46 objects in the 11 TS1 fars** and exactly this fix. The other two copies of the
same routine-resolution rule never got it. `VMTS1ActivatorNew.ConvertThread` (lines 295-296)
then does `routine.Instructions[callingFrame.InstructionPointer]` on the possibly-null result,
with no null check and no bounds check on the `ushort` pointer.
* **Why it matters** this is the single most-duplicated rule in the engine and the fix is
  demonstrably incomplete. A lot save that names a semi-global routine from a GLOB-less object
  takes the whole lot down at load.
* **Fix** extract one `static VMRoutine ResolveRoutine(GameObjectResource res, ushort id)`
  into `VMEntity` or `VMThread` that handles `res == null`, `SemiGlobal == null` and
  `id >= 0x7FFF`, and call it from all three sites. In `ConvertThread`, null-check the result
  and skip the `SpecialResult` fixup when the routine is missing.
* **Confidence** confirmed (read; the 131-site figure is the existing comment's).

### E-04 · `Utils/VMTS1ActivatorNew.cs:225, 278, 295, 407, 133, 150` · six unguarded indices into save data
```csharp
var ownerType = objt.Entries[frame.CodeOwnerObjType - 1];   // 0 -> index -1
var frame = thread.Stack[use.StackLength - 1];              // 0 -> -1, > len -> OOR
var calleeObj = objm.ObjectData[action.TargetID].Instance;   // KeyNotFoundException
var prim = routine.Instructions[callingFrame.InstructionPointer];  // ushort, no bounds check
var target = ava.MotiveChanges[delta.Motive];               // fixed array of 16
```
Plus `ConvertArchitecture`'s `iff.Get<ARRY>(8).TransposeData` (line 124), which has no null
check while the *next* line does check `iff.Get<ARRY>(11)`.
* **Why it matters** this is the whole TS1 lot-load path. One malformed `House*.iff` or
  `OBJM` save and the lot will not open. The `ARRY 8` case is the most likely of the six:
  ARRY 8 is the "flags" plane and is the one chunk a hand-built or third-party house file is
  most likely to be missing.
* **Fix** validate each index before use and skip the instance (`continue`) rather than throw;
  for the ARRY reads use `?.TransposeData ?? empty` and let the later resize pass no-op.
* **Confidence** confirmed (read).

### E-05 · `Entities/VMAvatar.cs:1147` · `ClearSlot` dereferences an empty slot
```csharp
public override void ClearSlot(int slot)
{
    Contained[slot].Container = null;     // NRE if the slot is already empty
```
`VMGameObject.ClearSlot` (line 298) has exactly this guard — `if (Contained[slot] == null) return;`
— and `VMAvatar` does not. `VMAvatar.PrePositionChange` (line 1086) and
`VMGenericTS1Call.SwapMyAndStackObjectsSlots` (line 38) both call it, and `ContainerSlot` goes
stale whenever a contained object is removed by a path that does not go through
`ClearSlot`. `GetSlot` and `PlaceInSlot` are likewise unchecked against `slot` being negative
or ≥ 3.
* **Why it matters** this is the same bug in two classes that were written as a pair; the
  guarded one is the template the unguarded one should have followed.
* **Fix** port `VMGameObject`'s bounds+null checks verbatim into `VMAvatar.GetSlot`,
  `PlaceInSlot`, `ClearSlot` and `GetSlotHeight`.
* **Confidence** likely (the guard is provably missing; reaching it needs a stale `ContainerSlot`).

### E-06 · `Entities/VMEntity.cs:881` · `Thread.Context` read before the `Thread == null` test
```csharp
case VMStackObjectVariable.LockoutCount:
    var count = ObjectData[(short)var];
    if (TimestampLockoutCount > Thread.Context.VM.Scheduler.CurrentTickID) TimestampLockoutCount = 0;
    return (short)((Thread == null) ? count : Math.Max((long)count - (...), 0));
```
The null test is two lines below the dereference. `VMEntity.Init` only creates a `Thread` when
`!GhostImage`, so every ghost image (and every entity between construction and `Init`) throws
on a `LockoutCount` read. The same pattern — `Thread.Context` with no guard — appears in
`SetValue` at lines 902 (`Burning` → `this.Reset(Thread.Context)`), 921
(`FSOEngineQuery` → `IsInUse(Thread.Context, …)`) and 951 (`CurrentOutfit`).
* **Fix** hoist the guard: `var tick = Thread?.Context.VM.Scheduler.CurrentTickID ?? 0;`
* **Confidence** confirmed (read).

### E-07 · `Primitives/VMGenericTS1Call.cs:192, 216` · `(VMAvatar)` cast on a script-supplied object id
```csharp
var ntarget = (VMAvatar)context.VM.GetObjectById(context.Thread.TempRegisters[0]);   // :192
var eperson = (VMAvatar)context.VM.GetObjectById(context.Thread.TempRegisters[0]);   // :216
```
`Temp0` is whatever the last call on the thread left there. A game-object id throws
`InvalidCastException`; at line 216 there is not even a null check before
`eperson.GetPersonData(...)`. `MakeTemp0SelectedSim` twelve lines below *does* check
(`if (target == null || target is VMGameObject) return GOTO_FALSE;`), which is the proof that
the author knew and these two were missed.
* **Why it matters** call 19 and call 21 are the Downtown date-spawn and the "inventory sim
  data effects" apply; both run on a register the script sets indirectly, and a wrong value
  resets the sim's thread.
* **Fix** `if (context.VM.GetObjectById(id) is not VMAvatar ava) return GOTO_FALSE;` in both.
* **Confidence** confirmed (read).

### E-08 · `Primitives/VMGenericTS1Call.cs:31, 45, 64, 137, 330, 339` · unguarded stack object, active action and `CurrentFamily`
| line | what |
|---|---|
| 31 | `context.StackObject.Container` — no null check (calls 1 and 2 assume one) |
| 45 | `context.Thread.ActiveAction.IconOwner` — `ActiveAction` is null whenever `ActiveQueueBlock < 0`, i.e. whenever the call is not made from inside a running interaction |
| 64 | `family.ValueInArch` — `GetFamily(Temp0)` is not null-checked (call 5) |
| 137 | `crossData.ActiveFamily.ChunkID` — `CurrentFamily` may be null (call 17) |
| 330 | `context.VM.TS1State.CurrentFamily.SelectWholeFamily()` (call 33) |
| 339 | `AddToFamily(context.VM.TS1State.CurrentFamily, …)`, which dereferences `family.FamilyGUIDs` (call 34) |
* **Why it matters** calls 1, 2, 5, 17, 33 and 34 all take their subject from a register or a
  lot state that a lot without a family, or a controller, can leave null. Each is one
  `VMSimanticsException` dialog plus a thread reset.
* **Fix** one `TryGetFamily(context, out family)` helper used by 4/6/33/34 and a
  `context.StackObject is VMEntity so` guard at the top of 1/2, mirroring what call 43
  (`FamilySpellsIntoController`) already does.
* **Confidence** confirmed (read).

### E-09 · `Engine/VMRoutingFrame.cs:758` · animation list indexed with no count guard
```csharp
case VMRoutingFrameState.WALKING:
    ...
    avatar.Animations[0].Weight = (8 - Velocity) / 8f;
    avatar.Animations[1].Weight = (Velocity / 8f) * 0.66f;
    avatar.Animations[2].Weight = (Velocity / 8f) * 0.33f;
```
The `WaitTime` branch twenty lines above (line 529) *does* guard:
`if (avatar.Animations.Count < 3) StartWalkAnimation();`. The `WALKING` branch has no such
guard, and `Animations` is cleared by `Turn()` (line 1134), by the `END_TURN` handler
(line 717), by `VMAvatar.Reset` (line 423) and is *empty* for any avatar loaded by
`VMTS1ActivatorNew` whose saved animation string is blank.
* **Why it matters** an `IndexOutOfRangeException` here is caught by `VMThread.Tick`'s handler
  and turned into a "SimAntics Exception" dialog, a thread reset, and — because `Delete` is
  armed for `DialogCooldown > 30*20-10` — eventually a deleted sim.
* **Fix** hoist the `Count < 3 → StartWalkAnimation()` guard into a small
  `EnsureWalkAnimations(avatar)` and call it from both branches.
* **Confidence** likely (the asymmetry with line 529 is the evidence; I did not reproduce it).

### E-10 · `Entities/VMAvatar.cs:449, 457, 463, 451` · `short.Parse` and a nullable animation state in a tick handler
```csharp
avatar.CurrentAnimationState.EventQueue.Add(eventValue);   // CurrentAnimationState is null when Animations is empty
...
var eventValue = short.Parse(rhevt);                       // FormatException out of Tick
var eventValue = short.Parse(lhevt);
```
`CurrentAnimationState` is documented (line 42) to return null when `Animations.Count == 0`.
`HandleTimePropsEvent` is called from `VMAvatar.Tick` for every time-property list crossed.
* **Why it matters** a malformed `righthand=`/`lefthand=` string in an `.anim` file throws out
  of `Tick`, which is *not* inside the VM's exception funnel — it escapes into the caller of
  `VM.PreDraw`/`VM.InternalTick`.
* **Fix** `short.TryParse` (matching the `xevt` line two above, which already does), and
  `if (avatar.CurrentAnimationState is VMAnimationState st) st.EventQueue.Add(...)`.
* **Confidence** confirmed (read).

### E-11 · `Entities/VMAvatar.cs:370-390` · `GlobWalk.Length` written into a fixed 50-slot array
```csharp
public string[] WalkAnimations = new string[50];
...
for (int i = 0; i < GlobWalk.Length; i++) WalkAnimations[i] = GlobWalk.GetString(i);
```
`context.Globals.Resource.Get<STR>(150 or 151)` is dereferenced with no null check, and its
length is used unbounded — for `GlobWalk`, `GlobSwim` and again for the per-object `STR 150`.
The array is 50 because `VMRoutingFrame` indexes up to `anims[25]`.
* **Why it matters** runs once per avatar on every lot load; a global STR longer than 50 entries
  is a lot-load crash, not a per-sim one.
* **Fix** `if (GlobWalk != null) for (int i = 0; i < Math.Min(GlobWalk.Length, WalkAnimations.Length); i++) …`
* **Confidence** likely (the bound is missing; whether any shipped STR exceeds 50 I did not measure).

### E-12 · `Utils/VMTS1Activator.cs:257` · unbounded parent-chain walk
```csharp
MappedObject src = obj;
while (src != null && src.ParentID != 0)
{
    if (objm.ObjectData.TryGetValue(src.ParentID, out src))
        if (src.ParentID == 0) { obj.ArryX = src.ArryX; … }
}
```
A `ParentID` cycle in the save never reaches the loop's exit condition, so the lot load hangs
forever — no exception, no log, no way out but killing the process. `VMTS1Activator` is still
live: `Client/Simitone/Simitone.Client/Neighbourhood/NeighbourhoodFacadeBuilder.cs:121` and
`Utils/SimitoneNeighOBJExporter.cs:76` both construct it.
* **Why it matters** a hang is worse than a crash for a player; and this file is the *older*
  activator, so it is the one with the least review.
* **Fix** cap the walk (`for (int hops = 0; hops < objm.ObjectData.Count && src != null && src.ParentID != 0; hops++)`)
  and `break` on a repeated id.
* **Confidence** confirmed (read).

### E-13 · `Entities/VMEntity.cs:1173, 1781` · two more unchecked `Thread` / provider derefs on load paths
* `LoadCrossRef`: `HeadlineRenderer = context.VM.Headline.Get(Headline);` — `VM.Headline` is a
  provider supplied by the client; there is no null check and no `Headline != null` guard on
  the *provider*.
* `VMAvatar.LoadCrossRef:1309-1310`: `var bodyStr = Object.Resource.Get<STR>(Object.OBJ.BodyStringID);
  HeadOutfit = new VMOutfitReference(bodyStr, true);` — a TS1 object with no body string
  produces a null reference, and the `HeadOutfit` setter immediately does
  `Avatar.Head = value.GetContent()`.
* **Fix** null-check both; return early from `LoadCrossRef` when the headline provider is absent.
* **Confidence** likely.

### E-14 · `Engine/VMSlotParser.cs:193` · division by a content-supplied resolution
```csharp
var resolutionBound = (MaxProximity / Slot.Resolution) * Slot.Resolution;
for (int x = -resolutionBound; x <= resolutionBound; x += Slot.Resolution)
```
`SLOT.Resolution` is a field of the SLOT chunk. A slot with `Resolution == 0` is a
`DivideByZeroException` on the first routing attempt, and had it survived, the `x += 0` in the
loop head is an infinite loop.
* **Fix** `if (Slot.Resolution <= 0) { outputFunc(0, 0, 0); return; }` at the top of the
  `else` branch.
* **Confidence** likely (no shipped SLOT measured).

### E-15 · `NetPlay/Drivers/VMFSORDriver.cs:60` · bare `catch { }` around a half-applied tick
```csharp
try
{
    var tick = new VMNetTick();
    tick.Deserialize(Reader);
    …
    InternalTick(vm, tick);
    TickID = tick.TickID;
    success = true;
}
catch
{
    //might be incomplete, but that shouldn't crash the program…
}
```
The catch covers `InternalTick` as well as the deserialize, so a SimAntics exception thrown
halfway through applying a recorded tick leaves the VM in a torn state and playback continues
silently from the next tick. The comment is right that it should not crash; the problem is that
it cannot distinguish "stream truncated" from "half a tick applied".
* **Fix** narrow the try to the read, and wrap `InternalTick` separately with a trace that
  records the tick id it failed on.
* **Confidence** confirmed (read).

### E-16 · `Engine/VMThread.cs:296, 515` · unguarded `(VMAvatar)` casts in the queue
```csharp
// EndCurrentInteraction, line 296 — outside the `Entity is VMAvatar` guard on line 286:
((VMAvatar)Entity).SetPersonData(VMPersonDataVariable.Priority, …);

// EvaluateQueuePriorities, line 515 — after the ActiveQueueBlock guard, but not an avatar test:
int CurrentPriority = (int)((VMAvatar)Entity).GetPersonData(VMPersonDataVariable.Priority);
```
Line 286 guards a *different* statement with the same test, which makes the omission at 296
look accidental rather than deliberate. `EvaluateQueuePriorities` is reachable from
`EnqueueAction`, which `VMPushInteraction`/`VMSetToNext` can call on a non-avatar.
* **Fix** hoist the `is VMAvatar` test to the top of `EndCurrentInteraction` and make
  `EvaluateQueuePriorities` return early for a non-avatar.
* **Confidence** likely.

---

## Correctness

### E-17 · `VMContext.cs:1267-1269` · `IsUserOutOfBounds` returns **false** for out-of-bounds tiles
```csharp
if (pos.TileX < 0 || pos.TileX >= _Arch.Width)  return false;
else if (pos.TileY < 0 || pos.TileY >= _Arch.Height) return false;
else if (pos.Level < 1 || pos.Level > _Arch.BuildableFloors) return false;
else return !fine[pos.TileX + pos.TileY * _Arch.Width];
```
The `else` branch is right; the three early returns are inverted — they answer "in bounds" for
exactly the tiles that are out of bounds. The `else` (non-fine) branch of the same method
(1264-1279) gets all three comparisons right, which is what makes this a transcription slip
rather than a decision.
* **Why it matters** `VMEntity.IndividualUserMovable:1177` is
  `if (context.IsUserOutOfBounds(Position)) return VMPlacementError.CantBePickedupOutOfBounds;`
  — that gate is a no-op whenever a fine buildable area exists, so an object that has been
  pushed off the edge of the lot can be picked up. `VMEntity.PositionValid:1270` also computes
  `userOOB` from this, though it is saved there by the independent `IsOutOfBounds` check on
  the same line.
* **Fix** `return true;` on all three.
* **Confidence** confirmed (read).

### E-18 · `VMContext.cs:789-804` · `RefreshRoomScore` clobbers its own `info` in the loop
```csharp
var info = RoomInfo[room];
room = info.Room.LightBaseRoom;
info = RoomInfo[room];                       // the base room
…
foreach (var rm in info.Room.SupportRooms)
{
    info = RoomInfo[rm];                     // ← reuses the outer variable
    …
}
float areaRScale = Math.Max(1, area / 12f);
if (info.Room.IsOutside) areaRScale = 30;    // reads the LAST support room
roomScore -= (info.Room.IsOutside) ? 15 : 10;
```
The sibling method `RefreshLighting` does the identical walk and uses a *separate* `info2` for
the inner loop (line 852), so `info` there is still the base room at line 969. That divergence
is the proof this is a bug, not a choice.
* **Why it matters** the room score is scaled by the wrong area multiplier and takes the
  wrong outside penalty whenever the base room is not the last support room, and it is written
  into `light.RoomScore` for the shared light object — so a bad score leaks to every room in
  the group. Room score drives light placement and the "dark room" sim behaviours.
* **Fix** `var info2 = RoomInfo[rm];` in the loop body, as `RefreshLighting` does.
* **Confidence** confirmed (read; divergence from `RefreshLighting` line 852 is the evidence).

### E-19 · `Entities/VMMultitileGroup.cs:376` · the rotation result is thrown away
```csharp
var vec = new Vector3(diff.x, diff.y, 0);
Vector3.Transform(vec, rotMat);              // pure function — return value discarded
AddDynamicObject(obj, new LotTilePos((short)Math.Round(vec.X), (short)Math.Round(vec.Y), diff.Level));
```
`XNA.Vector3.Transform` returns a new vector; it does not mutate. `rotMat` is built three
lines above from `bObj.Direction`, so the intent is unambiguous.
* **Why it matters** `Combine` is reached from `VMEntity.UpdateDynamicMultitile` when two
  instances of the same object come adjacent. The combined tiles are then added with
  *unrotated* offsets, so a dynamic multitile assembled from objects facing anything other than
  north gets its tiles in the wrong places. `Combine` also never removes the objects from
  `other`, so the old group keeps listing entities that now belong to this one.
* **Fix** `vec = Vector3.Transform(vec, rotMat);` and have `Combine` clear `other.Objects` /
  `other.Offsets` as it moves them.
* **Confidence** confirmed (read).

### E-20 · `Engine/VMMemory.cs:456` · `%` where a mask was meant
```csharp
case VMOBJDVariable.Version1:
    return (short)(objd.Version % 0xFFFF);
```
`Version` is a 32-bit word. `0x12345 % 0xFFFF` is `0x22C6`, not `0x2345`: modulo 65535 is not
the low 16 bits, it is the low 16 bits *minus the carry out of bit 15*. `Version2` on the next
line does `(short)(objd.Version >> 16)`, i.e. the intended split, so the two halves no longer
reassemble.
* **Why it matters** any script that reads the object's version low word (Tuning/debug tools,
  mods checking object age) gets a value that is wrong for roughly every version with bit 15
  or bit 16 set.
* **Fix** `return (short)(objd.Version & 0xFFFF);` (or `% 0x10000`).
* **Confidence** confirmed (read; arithmetic verified).

### E-21 · `VM.cs:1229-1237` · `ListenBHAVChanges` and `SuppressBHAVChanges` are identical
```csharp
public void ListenBHAVChanges()     { OnBHAVChange -= VM_OnBHAVChange; }
public void SuppressBHAVChanges()   { OnBHAVChange -= VM_OnBHAVChange; }
```
The "listen" method must be `+=`. As written, a caller that asks to listen silently unsubscribes,
and `VM.BHAVDirty` is only ever set through this path.
* **Why it matters** the two method names are the only documentation of the intended asymmetry;
  the fact that they are byte-identical is exactly what makes the next reader "fix" the wrong
  one. Also note `OnBHAVChange` is a `static event` on `VM` with **no `+=` anywhere in the
  tree** — so this subscription has never been established, and BHAV reassembly never dirties
  a lot.
* **Fix** `+=` in `ListenBHAVChanges`; then find the intended subscribe site (the BHAV
  editor / `VMTranslator` host) and confirm it calls it.
* **Confidence** confirmed (read; grepped for subscribers).

### E-22 · `Entities/VMAvatar.cs:1218-1228` · `Save` aliases four live mutable structures
```csharp
PersonData = (short[])PersonData.Clone(),     // cloned …
MotiveData = (short[])MotiveData.Clone(),
…
MotiveChanges = MotiveChanges,                 // … these four are not
MotiveDecay = MotiveDecay,
DefaultSuits = DefaultSuits,
DynamicSuits = DynamicSuits,
Decoration = Decoration,
```
`VMEntity.SaveEnt` clones `ObjectData`; `VMStackFrame.Save` clones `Locals` and `Args`. This
method clones two of seven fields and hands the marshal live references to the other five,
including a `VMMotiveChange[]` that `VMAvatar.Tick` mutates 16 times a frame.
* **Why it matters** `VM.Save()` is not a snapshot on the paths that keep it: `VMBlueprintRestoreCmd`
  / `VMFSORDriver` capture state and the VM keeps ticking. The "captured" state then changes
  underneath the consumer. The two clones make the intent obvious and the omission look like a
  bug rather than a choice.
* **Fix** clone all of them (`(VMMotiveChange[])MotiveChanges.Clone()` needs a per-element copy
  because the elements are classes — see `VMMotiveChange`), or make `VM.Save()` document that
  its result is only valid until the next tick and have the FSOR path freeze the VM.
* **Confidence** confirmed (read).

### E-23 · `Entities/VMEntity.cs:598-621` · `Reset` returns with `InReset` still true
```csharp
InReset = true;
if (this.Thread == null) return;     // ← InReset never cleared
```
Any other exception inside `Reset` leaks the same way. Once `InReset` is stuck, every later
`Reset` on that entity returns immediately — which is exactly the path
`VMThread.Tick`'s exception handler uses to recover a sim.
* **Fix** `if (this.Thread == null) { InReset = false; return; }`, and wrap the body in
  `try/finally { InReset = false; }`.
* **Confidence** confirmed (read).

### E-24 · `Engine/VMStackFrame.cs:168-177` vs `Engine/VMThread.cs:853-854` · `Locals` sized two different ways
```csharp
// Push:      frame.Locals = new short[Math.Max(frame.Routine.Locals, frame.Routine.Arguments)];
// Load:      if (… && Routine.Locals > input.Locals.Length) { Locals = new short[Routine.Locals]; … }
```
`Load` omits the `Arguments` term, and does not pad `Args` at all (while
`ExecuteSubRoutine:606` always allocates `max(Arguments, 4)`).
* **Why it matters** a frame restored from a save whose routine declares more parameters than
  locals gets a too-short `Locals`; the first `Local[N]` write — scope 25 / 40 — throws inside
  a script. TS1 saves are produced by `VMTS1ActivatorNew.ConvertThread` which writes the raw
  TS1 local count, so the two rules disagree by construction.
* **Fix** factor one `static short[] SizeLocals(VMRoutine r, short[] incoming)` and call it from
  both; pad `Args` to `max(Routine.Arguments, 4)` on load.
* **Confidence** likely.

### E-25 · `Engine/VMActionCallback.cs:53-58, 83-88` · byte-truncated interaction id, and a dead `else` arm
```csharp
var Action = tt.InteractionByIndex[(byte)Interaction];   // 300 aliases 44
ActionName = ttas.GetString((int)Action.TTAIndex);       // ttas can be null
…
if (true) { … return true; } else { …EnqueueAction(…); return false; }   // CS0162
```
`VMEntity.GetAction` (line 1124) uses `(uint)interaction` for the same lookup, so the two copies
of "look up an interaction by number" have drifted. `Run` null-checks `Target` but not
`Caller`, and `Caller.Thread.Stack.Last()` can be null.
* **Why it matters** the `else` arm is the entire "enqueue it and let allow-push run it" design,
  and the whole `ActionFlags` / `ActionName` computation above it exists only to feed it. A
  reader has to work out that all of that is dead.
* **Fix** call `Target.GetAction(...)` (the shared implementation) instead of re-implementing
  the lookup; delete the dead arm; null-check `Caller.Thread` and the stack top.
* **Confidence** confirmed (read; CS0162 from the build).

### E-26 · `Entities/VMAvatar.cs:915-922` · person data 74 is never stored
```csharp
case VMPersonDataVariable.RenderDisplayFlags:
    if (WorldUI != null) { …; return true; }    // ← returns before PersonData[...] = value
```
Every other case falls through to `PersonData[(ushort)variable] = value` on line 968. This one
runs its change handler and returns, so the value never lands in the array and never reaches
the save.
* **How I confirmed it** the only person-data-74 write in the entire installed game is a
  re-assert, so today the gap is invisible:
  `bhavscan2 pdw 74 <all 84 fars>` returns exactly one hit —
  `ExpansionPack/ChemistrySet.iff | Chemistry Set | bhav 4150 'create monster' [4]
  LHS s3[29]… op5 bytes=4A,00,4A,00,00,05,13,12`, i.e. `StackPersonData[74] = StackPersonData[74]`
  (`4A 00` = 74 on both sides, opcode 5 = `=`, scope `13` = StackObjectPersonData). A mod that
  writes a *different* value to slot 74 gets the render side effect and no data.
* **Fix** drop the `return true;` and let the shared store at the bottom of the method run.
* **Confidence** confirmed (read + install-wide scan).

### E-27 · `Primitives/VMExpression.cs:96, 104, 186` · flag bit numbers are never validated
```csharp
var bitval = 1 << (rhsValue - 1);        // rhsValue is a script register
```
`rhsValue == 0` gives `1 << 31`; `33` gives `1 << 0` — a `flagSet 33` silently sets **bit 0**;
anything ≥ 64 aliases into the low bits again, and the result is then truncated to a `short` on
store. Same shape in `ClearFlag` and `IsFlagSet`.
* **Why it matters** this is exactly the word `VM.GameEditionGlobal` is built on, and both
  `VM.IsObjectFlagBit` and `VM.SetObjectFlag` validate the bit number before touching it. The
  primitive — the only path content can actually reach — does not, so a mod gets silent
  corruption of an unrelated flag rather than a refusal.
* **Also in the same switch** `ModEquals` computes `((lhsValue % rhsValue) + rhsValue) % rhsValue`,
  which throws `OverflowException` for `int.MinValue % -1`.
* **Fix** reject `rhsValue < 1 || rhsValue > 16` (return `GOTO_FALSE` or a trace) before
  shifting; guard the modulo against `rhsValue == -1`.
* **Confidence** confirmed (read).

### E-28 · `Primitives/VMExpression.cs:165-177` · the `MyObj[29] < 1024` "HACK" rewrites a room test as a level test, using the *callee*
```csharp
if (rhsValue == 1024 && operand.LhsData == (int)VMStackObjectVariable.Room   // 29
                     && operand.LhsOwner == VMVariableScope.MyObject)         // 3
    result = context.Caller.Position.Level
           - (context.Callee.Position.Level - context.Callee.Object.OBJ.LevelOffset) <= 0;
```
* **How I confirmed it is live** `bhavscan2 scope 3,7 <all fars>` returns 12 sites of exactly
  this shape, all `s3[29] op14 (>=) s7[1024]` or `s3[29] op1 (<) s7[1024]`:
  `Objects/stair.iff` bhav 4124 'Use' [0] and 4125 'Use TEST' [0];
  `ExpansionPack/Stair2.iff` 4124/4125; `ExpansionPack2/stair3.iff` 4124/4125;
  `ExpansionPack4/Stair4.iff` 4124/4125; `ExpansionPack5/StairsSpiral.iff` 4124/4125 plus
  4113 'goto other side' [21] and 4097 'goto other side - Dog' [21]. So the comment's "only
  used in stairs" is right.
* **Why it matters** two problems. (a) The comparison the script asked for — *is my room on
  the upstairs side* — never happens; a substitution does, so any mod that reads `MyObj[29]`
  inside a stair's `Use` tree gets an answer about levels that retail would not give.
  (b) The expression is about `MyObject` (the caller) but the result is computed from
  `context.Callee`, dereferenced with no null check. In any frame built without a callee this
  is an NRE inside the hottest primitive in the engine.
* **Fix** keep the substitution (retail needs it) but (i) move it behind a `Callee != null`
  guard, (ii) express it in one place — a `VMMemory`/scope hook for `MyObject[29]` — so it is
  not a special case inside `VMExpression`, and (iii) say in the comment that a *room*
  comparison is being replaced, so nobody reads it as a fix for room decoding.
* **Confidence** confirmed (read + install-wide scan).

### E-29 · `Utils/VMTS1Activator.cs:77-81` · `SIMI.GlobalData` is aliased into `VM.GlobalState` and then written to
```csharp
VM.GlobalState = simi.GlobalData;     // no copy
VM.GlobalState[20] = 255;             // mutates the SIMI chunk in place
VM.GlobalState[25] = 4;
VM.GlobalState[17] = 4;
```
`simi` is also assigned to `VM.TS1State.SimulationInfo` on line 69, so the "engine-owned,
read-only from script" game-edition word lives inside the lot's own SIMI and is written back
out on save. The **newer** activator gets this right — `VMTS1ActivatorNew.cs:427` does
`simi.GlobalData.ToArray()` — and also names the constant (`VM.GameEditionGlobalValue`)
whereas the older one hardcodes `255` in two places.
* **Why it matters** two copies of the TS1 load path that disagree about a documented,
deliberately-pinned invariant (`VM.GameEditionGlobalValue` is asserted by
`Client/Simitone/Simitone.Tests/Engine/VMGameEditionGlobalTests.cs`). Changing the constant
fixes one path and silently keeps the other at 255.
* **Fix** `(short[])simi.GlobalData.Clone()` and reference `VM.GameEditionGlobalValue`; then
  delete `VMTS1Activator` in favour of `VMTS1ActivatorNew` (see E-30/E-31 for the rest of
  what it still gets wrong).
* **Confidence** confirmed (read; both files compared side by side).

### E-30 · `Utils/VMTS1Activator.cs:274, 284` · objects on tile (0,0) are never placed
```csharp
if (obj.ContainerID == 0 && obj.ArryX != 0 && obj.ArryY != 0) nobj.SetPosition(…);
```
`ArryX`/`ArryY` default to 0 for an instance that the ARRY 3/103 plane never mentioned, so
`!= 0` is doubling as "has a position". A lot object that legitimately sits on the origin tile
is therefore left `OUT_OF_WORLD` — invisible and unroutable.
* **Why it matters** the origin tile is inside the buildable area; the neighbourhood builder
  (`NeighbourhoodFacadeBuilder`) runs this path for every lot it generates.
* **Fix** add an explicit "has position" flag rather than overloading zero.
* **Confidence** confirmed (read).

### E-31 · `Utils/VMTS1Activator.cs:67-68` · `Entities` and `Scheduler` replaced, `ObjectsById` and `SoundEntities` are not
```csharp
VM.Entities = new List<VMEntity>();
VM.Scheduler = new Engine.VMScheduler(VM);
```
`VM.ObjectsById` is `private` and is only ever mutated by `VM.AddEntity` / `VM.RemoveEntity`,
so replacing the entity list behind its back leaves the id map pointing at the *previous* lot's
entities. `VM.SoundEntities` is likewise never cleared.
* **Why it matters** `VM.GetObjectById` — used by every `execute_tree_by_name` operand, every
  `find_best_object_for_function`, every script-supplied object id and the whole relationship
  subsystem — answers from the dead lot. This is the "collection that only grows" case: the
  old lot's entities are retained for the lifetime of the VM.
* **Fix** add a `VM.ResetEntityCollections()` that clears `Entities`, `ObjectsById`,
  `SoundEntities`, re-primes `ObjectId` and resets the scheduler, and have both activators
  call it.
* **Confidence** confirmed for the missing clear; likely that a second load on the same VM
  happens (I did not trace `NeighbourhoodFacadeBuilder`'s VM lifetime).

### E-32 · `Model/Platform/VMDefaultValidator.cs:39` · `obj is VMAvatar` on a `VMGameObject` parameter
```csharp
public override bool CanManageAsyncSale(VMAvatar ava, VMGameObject obj)
{
    if (ava == null || obj == null || obj is VMAvatar || obj.PersistID == 0) return false;
```
`obj` is declared `VMGameObject`, so the third disjunct is constant-false. The compiler says so:
`VMDefaultValidator.cs(39,47): warning CS0184: The given expression is never of the provided
('VMAvatar') type`. The same test at line 69 and 73, inside `GetDeleteMode(DeleteMode, VMAvatar,
VMEntity)`, *is* meaningful — so the guard exists in one method and is silently dead in the other.
* **Fix** either widen `obj` to `VMEntity` (then the check means something) or delete the
  disjunct; do not leave a type-narrowed copy of a check that works elsewhere.
* **Confidence** confirmed (read + build).

### E-33 · `Entities/VMMultitileGroup.cs:430, 463` · `Objects` reset, `Offsets` not
Both `Load` and `LoadGhost` do `Objects = new List<VMEntity>();` and then `Offsets.Add(...)`.
`Offsets` is never cleared, so a second load on the same group leaves it longer than `Objects`,
and `BaseObject` (line 60), `ChangePosition` (line 191) and `SetVisualPosition` (line 334) all
index both with `Objects.Count`.
* **Fix** `Offsets = new List<LotTilePos>();` next to the `Objects` reset in both.
* **Confidence** confirmed (read).

### E-34 · `Entities/VMEntity.cs:150` · `while` used where `if` was meant
```csharp
public virtual short GetAttribute(int index)
{
    while (index >= Attributes.Count) return 0;
    return Attributes[index];
}
```
It works only because the body returns on the first pass. One `break` added later turns it into
an infinite loop over a script-supplied index.
* **Fix** `if`. (Note `SetAttribute` two lines below genuinely *is* a loop and must stay one —
  that pairing is the trap.)
* **Confidence** confirmed (read).

### E-35 · `Entities/VMAvatar.cs:977` · `InheritNeighbor` swaps the whole person-data array for the NBR's
```csharp
if (neigh.PersonData != null) PersonData = neigh.PersonData.ToArray();
```
The array is then re-asserted for seven slots (Gender, NeighborId, PersonType, VisitorSchedule,
GreetStatus, IsGhost) and nothing else. If the NBR's array is shorter than 101 — which
`VMAvatarMarshal.Deserialize:64` (`new short[Math.Max(101, pdats)]`) and `VMAvatar.Tick:529`
(`PersonData.Length > (int)VMPersonDataVariable.OnlineJobStatusFlags`) both exist to cope with —
every later `PersonData[(int)slot]` above the NBR's length throws.
* **Fix** copy into the existing 101-slot array rather than replacing it:
  `Array.Copy(neigh.PersonData, PersonData, Math.Min(neigh.PersonData.Length, PersonData.Length));`
* **Confidence** likely.

### E-36 · `Marshals/VMAvatarMarshal.cs:53-58, 67-69` · motive arrays are not floored, unlike person data
```csharp
var motCs = reader.ReadInt32();
MotiveChanges = new VMMotiveChange[motCs];     // no floor at 16
…
var mdats = reader.ReadInt32();
MotiveData  = new short[mdats];                // dead: overwritten on the next line
MotiveData  = VMSerializableUtils.ReadArray<short>(reader, mdats);   // no floor at 16
```
Three lines earlier, `PersonData` gets `Math.Max(101, pdats)`. The other two do not, and
`VMAvatar.GetMotiveData`/`SetMotiveData` bound-check the *index* (`> 15`) but never the array
length.
* **Why it matters** `VMTS1ActivatorNew.ConvertAvatar:380` writes
  `MotiveData = person.MotiveData.Select(...).ToArray()` straight from the OBJM person record,
  so the array length is whatever the save had. A save with 12 motives makes
  `set_motive_deltas` on motive 13 an `IndexOutOfRangeException`.
* **Fix** `Math.Max(16, …)` on both counts, and delete the dead allocation on 68.
* **Confidence** confirmed (read).

### E-37 · `Model/VMMotiveChange.cs:92` · an unreachable clamp, and the other three motive-array consumers are unguarded
```csharp
if (((rate > 0) && (motive > MaxValue)) || ((rate < 0) && (motive < MaxValue))) { return; }  // line 88
…
motive += (short)(fractional);
…
if (((rate > 0) && (motive > MaxValue)) || ((rate < 0) && (motive < MaxValue))) { motive = MaxValue; }  // line 92
```
Line 92 re-tests the identical predicate that line 88 already returned on, so it can never fire:
the clamp is dead code. (The predicate itself is correct — the two disjuncts are mirror images,
and the `return` is the intended "already past the limit, do not clamp".)
* **Why it matters** cosmetic on its own, but the same file is where the fork already
  acknowledged the real hazard: `VMMotiveChange.ClearOne` (added in this branch) is
  bounds-guarded and its doc says the array "may be shorter than 16 entries (a partially
  deserialised avatar)". The other three consumers of that same array are not guarded:
  `VMAvatar.SetMotiveChange:814`, `VMAvatar.HasMotiveChange:825`, and the
  `for (int i = 0; i < 16; i++) MotiveChanges[i].Tick(this);` in `VMAvatar.Tick:607-610` — the
  last of which throws on *every* tick of *every* avatar whose `MotiveChanges` came in short,
  which is what E-36 says the deserialiser produces.
* **Fix** delete line 92; apply `ClearOne`'s bounds check to `SetMotiveChange` /
  `HasMotiveChange`; loop `MotiveChanges.Length` (or floor the array in E-36) in `Tick`.
* **Severity** cosmetic for line 92, crash for the three guards · **Confidence** confirmed (read)

### E-38 · `Entities/VMGameObject.cs:286-290` · `GetSlotHeight` ignores its argument
```csharp
public override int GetSlotHeight(int slot)
{
    if (slot < TotalSlots()) return Slots.Slots[0][0].Height;   // always slot 0, first entry
    else return -1;
}
```
It should be `Slots.Slots[slot][0]`. `Slots` is null when the object has no SLOT chunk and
`Slots.Slots[0]` can be empty, so both `GetSlotHeight(0)` on a slot-less object and
`GetSlotHeight(-1)` throw. `VMContext.GetObjPlace:1427` and `:1443` are the only callers and
both pass 0, so the bug is currently invisible — which is exactly why it is still here.
* **Fix** bounds-check `slot` and `Slots.Slots[slot].Count`, and index by `slot`.
* **Confidence** confirmed (read).

### E-39 · `NetPlay/Model/…` / `VM.Save` · `VMArchitecture.Save` hands the live wall/floor arrays to the marshal
`VMArchitecture.cs:1057-1080` deliberately does not clone `Walls`, `Floors`, `Terrain` or
`FineBuildableArea` ("so many deep structures that it would cause a notable performance
impact"), and `VM.Save:909` passes `PlatformState` by reference rather than cloning. Meanwhile
`VM.Save` *does* clone `GlobalState` and `VMEntity.SaveEnt` *does* clone `ObjectData`.
* **Why it matters** the two consumers that keep a `VMMarshal` while the VM runs —
  `VMFSORDriver` (a replayed tick can put a `VMStateSyncCmd` in) and the sandbox/blueprint
  path — will see those arrays mutate underneath them. The per-entity clone next door is what
  makes the omission look deliberate rather than forgotten.
* **Fix** either freeze the VM across a capture, or document `VMMarshal` as "valid until the
  next tick" in one place and have the FSOR path honour it.
* **Confidence** likely.

### E-40 · `Primitives/VMExpression.cs:145` / `VMMemory.cs:369-426` · tuning-table decode and lookup
* `GetTuningVariable` leaves `mode = 0` for any `tableID >= 192` **and does not reduce
  `tableID`**, so `tableID + TableIDOffsets[0]` overflows `ushort` and wraps into a different
  local table than the one named. Every branch of the `switch` then leaves `tuningCache` null
  if `context.ScopeResource` or `context.Global` is null, and line 423 dereferences it.
* `GetTuningVariableLabel:444` does `tuning.GetKey(keyID).Label` with no null check on either.
* **Fix** add the `else throw`/`return` for `tableID >= 192`, and null-check `tuningCache`
  before `TryGetValue`.
* **Confidence** confirmed (read; the upstream shape is the same, which is why it survived).

---

## Perf

### E-41 · `Entities/VMMultitileGroup.cs:51, 57, 337, 399` · `Objects.Count()` instead of `.Count`
```csharp
for (int i = 0; i < Objects.Count(); i++)     // LINQ Count() — enumerates, boxes an enumerator
```
`Objects` is a `List<VMEntity>` with a `Count` property. `Count()` is the `Enumerable`
extension: it walks the list and allocates an enumerator **on every loop iteration**, so each
of these loops is O(n²) with an allocation per step. `BaseObject` (line 47) does two of them
and is called from `ChangePosition` via `Offsets[Objects.IndexOf(BaseObject)]`, from
`SetVisualPosition`, and from `GhostCopyGroup`.
* **Fix** `.Count` everywhere in this file.
* **Confidence** confirmed (read).

### E-42 · `Engine/VMRoutingFrame.cs:147` · a `VMObstacle` allocated per collision test
```csharp
var obs = new VMObstacle(pos.x - 6, pos.y - 6, pos.x + 6, pos.y + 6);
return obs.Contains(new Point(tpos.x, tpos.y));
```
`IntersectsOurDestination` is called from `VMSlotParser.VerifyAndAddLocation:308` inside a
`foreach (var avatar in context.ObjectQueries.Avatars)`, for **every candidate slot** of
**every routing frame of every avatar**. A single route to a chair with 20 candidates and 8
avatars is 160 `VMObstacle` allocations plus 160 `Point` boxes.
* **Fix** hoist the obstacle to a field on `VMRoutingFrame` (or a `[ThreadStatic]` scratch) and
  reuse it, as `RefreshLighting` already does with its `objs` list.
* **Confidence** confirmed (read).

### E-43 · `Entities/VMAvatar.cs:739, 585` · divide by zero in the render path
`FractionalAnim` computes `state.Weight / totalWeight` where `totalWeight` is the sum over
`Animations`; during a turn (`Turn()` sets one animation) or right after
`PreExit` (which trims to a single standing pose) the sum is 0 for any state whose weight is 0,
and `0/0` is `NaN` on the way into `Animator.RenderFrame`. `Tick` guards the *frame advance*
with `state.Weight != 0` but not the render.
* **Fix** skip states with `Weight == 0` in `FractionalAnim`, the way `Tick` does.
* **Confidence** likely.

### E-44 · `Entities/VMEntity.cs:1217` and `VMAvatar.cs:635` · O(n²) queue scans on the hot path
* `GetUsers`: `for (int i = 0; i <= ava.Thread.ActiveQueueBlock; i++)` for every avatar — this
  is the implementation behind `VMStackObjectVariable.UseCount`, which every
  2-sim interaction object reads. `ava.Thread` is unguarded.
* `UserLeaveLot`: `Thread.Queue.Exists(x => x.ActionRoutine.ID == LEAVE_LOT_TREE &&
  Thread.Queue.IndexOf(x) <= Thread.ActiveQueueBlock + 1)` — `IndexOf` inside the predicate.
  `x.ActionRoutine` can be null for a queue entry whose routine did not resolve.
* **Fix** maintain a per-queue active-block length instead of `IndexOf`; guard `ava.Thread`.
* **Confidence** confirmed (read).

### E-45 · `VMContext.cs:809-816` · `ElementAt` over a `HashSet`, plus a full light rebuild per deferred room
```csharp
for (int i = 0; i < DeferredLightingRefresh.Count; i++)
    RefreshLighting(DeferredLightingRefresh.ElementAt(i), …, new HashSet<ushort>());
```
`ElementAt` on a `HashSet<ushort>` is O(n) and boxes the enumerator, so the loop is O(n²); and
`RefreshLighting` rebuilds the entire room's light graph and (on the last iteration) reallocates
and copies the whole `Blueprint.Light` array. `RefreshRoomScore` (E-18) can be reached from the
same pass.
* **Fix** snapshot into a `ushort[]` once (`ToArray()`), and mark `commit` once at the end.
* **Confidence** likely.

---

## Consistency / drift

### E-46 · `Primitives/VMSlotParser.cs:172` — the comment contradicts the code
```csharp
if (Results.Count > 1) Results = Results.OrderBy(x => -x.Score).ToList(); //avoid sort because it acts incredibly unusually
```
The comment says the opposite of what the line does — and it is the only occurrence of that
rationale in the tree (`grep "acts incredibly unusually"` hits only this line), so whoever wrote
it was arguing against a sort they then performed. Make the comment say what the line does, and
decide which behaviour is wanted: `OrderBy` is a stable sort, so it is deterministic.
* **Severity** cosmetic · **Confidence** confirmed

### E-47 · `Utils/VMTS1Activator.cs:308-384` — four copies of the same resize loop
`ResizeFlags`, `ResizeFloors`, `ResizeWalls`, `ResizeGrass` are byte-for-byte the same nested
loop with a different element type, and all four `return` the 64×64 source unchanged for
`size >= 64` (so a lot larger than 64 silently gets a mismatched array length against
`VMArchitecture(size, size, …)`). One generic `ResizeArray<T>(T[] src, int size)` fixes all
four and the `>= 64` case.
* **Severity** consistency · **Confidence** confirmed (read)

### E-48 · `Entities/VMMultitileGroup.cs:348` vs `VMContext.cs:1515` — two magic "not out of world" room values
`SetVisualPosition` writes `sub.WorldUI.Room = 65535`; `GhostCopyGroup` calls
`SetRoom(65534)`. `VMEntity.SetRoom` stores `room - 1`, so these are `65534` and `65533` in
object data. Name one constant.
* **Severity** consistency · **Confidence** confirmed (read)

### E-49 · `Engine/VMRoutingFrame.cs:46-53, 1051` — mutable statics and a side effect in a predicate
`ROUTE_FAIL_TREE`, `GOTO_GUID`, `SHOO_INTERACTION`, `SHOO_TREE`, `WAIT_TIMEOUT`, `MAX_RETRIES`
and `DEBUG_DRAW` are all `private static` non-`readonly` ints. `CanPortalTurn()` is
```csharp
return (rf.State != VMRoutingFrameState.ROOM_PORTAL || rf.PortalTurns++ == 0);
```
— it increments `PortalTurns` on *every* call, and it is called from `BeginWalk`, `EndWalk` and
`Tick`. So `PortalTurns` counts calls, not turns, and `AttemptWalk`'s
`(parent.State == ROOM_PORTAL || parent.PortalTurns > 0)` reads a counter of unrelated calls.
* **Severity** consistency · **Confidence** confirmed (read)

### E-50 · `Engine/VMThread.cs:173-174` and `:183-185` — `RunInMyStack` restores nothing on its early path
```csharp
Queue = new List<VMQueuedAction>();
if (Queue.Count > 0) Queue.Add(Queue[0]);      // can never fire — Queue was just created empty
IsCheck = true;
ExecuteSubRoutine(…);
Stack.RemoveAt(0);
if (Stack.Count == 0) { Stack = OldStack; Queue = OldQueue; return false; }   // IsCheck/ActiveQueueBlock not restored
```
After that early return the thread's `IsCheck` is permanently `true`, and `VMThread.Tick` then
returns at `if (IsCheck) return; //running out of execution means check trees have ended.`
forever — the sim's main tree never runs again. The empty `catch` around the child's
`NextInstruction` loop (197-201) hides whatever caused it, and re-throws with `throw e`
(CA2200, stack trace reset).
* **Severity** correctness · **Confidence** likely

### E-51 · `Engine/VMThread.cs:890-904` — `hitParentEnd` computed and never used
In the `FSOPushTail` branch the loop only compares priorities, so it walks straight past
`VMQueueMode.ParentExit` boundaries that the general branch below it (905-929) stops at. The
local is assigned twice in the method and used in only one of them.
* **Severity** correctness · **Confidence** likely

### E-52 · `Primitives/VMGrab.cs:12` — unreachable `return` (CS0162)
Also `context.StackObject` is passed to `PlaceInSlot` unchecked, and `PlaceInSlot` does
`obj.Dead` on the second line, so a null stack object throws rather than returning false.
* **Severity** cosmetic (the crash is a crash finding already covered by E-08's pattern) · **Confidence** confirmed

### E-53 · Compiler-confirmed dead code
| file:line | warning |
|---|---|
| `Primitives/VMGrab.cs:12` | CS0162 unreachable code |
| `Engine/VMActionCallback.cs:93` | CS0162 unreachable code (see E-25) |
| `Model/Platform/VMDefaultValidator.cs:39` | CS0184 never of the provided type (see E-32) |
| `Model/Routing/VMObstacleSet.cs` (12 sites: lines 20, 35, 56, 81, 103, 105, 129, 160, 178, 211, 247, 300) | CS0436 `VMObstacleSetNode` conflicts with the same type in `FSO.Common`; the local definition wins |
| `Utils/VMTS1Activator.cs:181` | CS0219 `j` assigned never used |
| `Engine/VMSlotParser.cs:110` | CS0168 `baseRot` declared never used |
| `Engine/VMMemory.cs:378, 379` | CS0168 `bcon`, `tuning` declared never used |
| `Engine/VMThread.cs:199, 469` | CA2200 re-throwing caught exception changes stack information |

The `VMObstacleSetNode` collision deserves a look beyond the warning: two assemblies define the
same type name and the compiler silently picks the one in `tso.simantics`. Anything that binds
to `FSO.Common`'s copy is now operating on a different type than the obstacle set that
`VMSlotParser` and `VMRoutingFrame` populate.
* **Severity** consistency · **Confidence** confirmed (build output)

### E-54 · `Entities/VMAvatar.cs:294-343` — a bare `catch { }` around the whole body-resolution block
```csharp
try
{
    if (context.VM.TS1) { … } else {
        var skinToneSpl = skinTone.Split(';').Where(x => !string.IsNullOrEmpty(x)).ToArray();
        var randSkinTone = skinToneSpl[context.NextRandom((ulong)skinToneSpl.Length)];   // empty → [0] throws
        …
    }
}
catch { /*head or body invalid, resort to default.*/ }
```
The catch swallows everything, including the out-of-range index the next line can raise, and
execution continues into the gender/name/age block — so a malformed body string yields a
half-applied avatar (skin tone changed, outfits not) with no trace. The file's own
`VMEODDiagnostics` comment (`NetPlay/EODs/Utils/VMEODDiagnostics.cs:75-83`) says exactly this
about an empty catch elsewhere in the engine: "every owner check in the TS1 owner-mode path was
invisible" because of it.
* **Fix** narrow the catch to `IndexOutOfRangeException`/`FormatException`, or guard
  `skinToneSpl.Length == 0` explicitly, and `Trace` the failure.
* **Severity** correctness · **Confidence** confirmed (read)

---

## Not findings (checked, and I was wrong)

Recorded so the next pass does not re-derive them.

* **`VMStackFrame.InstructionPointer` is a `byte`** (`VMStackFrame.cs:28`) and
  `Load` truncates `(byte)input.InstructionPointer`, while `VMTS1ActivatorNew` writes a
  `ushort` `NodeID`. I was ready to report this as a silent corruption of TS1 trees with more
  than 255 nodes. It is a non-issue: the BHAV format stores the true/false branch pair as two
  bytes inside a 16-bit operand field (the disassembler's `TF` reads `T = field & 0xFF`,
  `F = field >> 8`), so branch targets cannot address node 256 in the first place.
  `bhavscan2 ls` for the far followed by `bhavscan2 names <far> <entry>` over all 24
  semiglobals puts the largest tree at **159** nodes (`FunHouseGlobals.iff` bhav 8206), and
  retail is self-consistent with the byte limit.
* **`VM.NextObjID` returning 0 on overflow** (`VM.cs:742`) — reachable only with all 32767
  object ids live, and a `short` wrap makes the loop exit rather than hang. Not worth a fix wave.
* **`VMAvatar.Tick`'s loop-animation wrap** (lines 589-596) — `PlayingBackwards` adds
  `NumFrames` and forward subtracts it. That is correct for the respective directions; I
  checked before reporting it.

---

## Suggested fix-wave order

1. **Lot-load crashes** (E-01 … E-04, E-12): these all fire before the lot is usable, and three
   of them are the same missing-guard repeated. One shared "resolve or skip" helper per rule.
2. **Tick-time crashes** (E-05 … E-10, E-13, E-14, E-16): each is a null/index check on a path
   script data can reach. Mechanical, low risk, high payoff.
3. **The inverted/divergent correctness bugs** (E-17, E-18, E-20, E-21, E-27, E-32): tiny diffs,
   visible behaviour changes, and E-18/E-20/E-32 all have an obvious "the sibling method gets
   it right" template already in the tree.
4. **The TS1 activator split** (E-29 … E-31): `VMTS1Activator` is the older, less reviewed copy
   and is still constructed by two client call sites. Either delete it or make it delegate to
   `VMTS1ActivatorNew`.
5. **The duplicated routine-resolution rule** (E-03) and the duplicated queue-entry lookup
   (E-25): both are "one shared helper, three call sites", which is the class of fix that stops
   the next drift.
6. **Aliasing on the capture path** (E-22, E-39): needs a decision, not just a patch — either
   freeze the VM across a capture or document the lifetime.
7. **The perf five** (E-41 … E-45) and the cosmetic list (E-46 … E-54).

---

## Corrections from the fix wave (2026-09-28)

- **E-40 — the banding half of the claim is wrong.** A table id is `data >> 7` of a `ushort`, so it
  tops out at 511 and `+4096` cannot wrap a `ushort`; keeping the local band is correct. Only the
  unguarded `tuningCache` dereference (and the `TuningReplacement`/label derefs) were real, and
  those are fixed.
- **E-27 — the fix is stricter than the finding.** Flag bits outside 1..16 are now refused rather
  than silently corrupting bit 0. The audit's census of the shipped data found none in range, so
  this is a behaviour change only for content that was already wrong.
