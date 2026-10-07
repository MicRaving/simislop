# FreeSO TSO-only systems — what can be back-ported to Simitone's single-player TS1

**Assessment date:** 2026-09-28
**Read-only.** Nothing in this document has been implemented; it is an inventory of code that
exists in the pinned `FreeSO/TSOClient` tree and a judgement on each item.

**Method:** code reading plus on-disk inspection only. No game was launched, no UI was driven, no
process was touched. Every claim below cites a `file:line` in this tree so it can be re-checked
without re-running any of this work.

---

## 0. The one structural fact that governs every verdict

Simitone is not "TS1 mode with the online bits switched off". It is **TS1 mode running FreeSO's
*server* VM in-process**, with the server's database replaced by a local stub:

```csharp
// Client/Simitone/Simitone.Client/UI/Screens/TS1GameScreen.cs:814-819
var globalLink = new VMTS1GlobalLinkStub();
Driver = new VMServerDriver(globalLink);

vm = new VM(new VMContext(World), Driver, new UIHeadlineRendererProvider());
vm.ListenBHAVChanges();
vm.Init();
```

The same construction is repeated on the marshal-resume path
(`UI/Services/LotLifecycleService.cs:151-178`), the neighbourhood exporter
(`Utils/SimitoneNeighOBJExporter.cs:67`) and the CAS screen (`UI/Screens/TS1CASScreen.cs:1932`).
`VMTS1GlobalLinkStub` is a Simitone-authored local implementation of FreeSO's 40-method
`IVMTSOGlobalLink` (`tso.simantics/Engine/TSOGlobalLink/VMTS1GlobalLinkStub.cs:19-345`).

Two consequences follow, and they decide most of this document:

1. **Anything that lives in `FSO.SimAntics` (`tso.simantics`) is already loaded, already ticking and
   already reachable from a TS1 lot.** The "is it TSO-only?" question for that assembly reduces to
   "is the `if (VM.TS1)` gate on it?", not "is there a client to render it?".

2. **Anything that lives in `FSO.Client` (`tso.client`) is not in Simitone's UI assembly at all.**
   `Simitone.Client.csproj` references `FSO.UI`, `FSO.Common`, `FSO.Files`, `FSO.SimAntics`,
   `FSO.Vitaboy*` and `FSO.LotView` — **not** `FSO.Client`. `Simitone.Windows.csproj` does reference
   `FSO.Client`, but only so its assembly can load; the comment at
   `Simitone.Windows.csproj` group (4) says so explicitly. So a `tso.client` system is "TSO-only" for
   Simitone in a much harder sense than a `tso.simantics` one: the code is there, compiled, on disk,
   and unreachable from the game's own UI tree.

`Simitone.Client` has already hand-ported the `tso.client` panels that matter for single-player play
— `UILotControl` (94 KB), `UIObjectHolder`, `UIPieMenu`, `UIQueryPanel`, `UIInteractionQueue`,
`UIMainPanel`, `UILotControlTouchHelper`, plus a `UI/Panels/LiveSubpanels/` tree. That is the
established pattern for re-enabling a `tso.client` system, and its cost is the yardstick for the
effort estimates below.

---

## 1. Headline verdict table

| # | System | Lives in | Server needed? | Verdict | Effort | Risk |
|---|--------|----------|----------------|---------|--------|------|
| 1 | **EOD minigame framework** (server side) | `tso.simantics/NetPlay/EODs` | **No — already running** | **BACKPORT** | XS (wire-up only) | Low |
| 2 | **EOD minigame UI** (39 files, 871 KB) | `tso.client/UI/Panels/EODs` | No | **BACKPORT** | M (3–5 day-equivalent) | Med |
| 3 | **Casino economy on TS1 family funds** | `VMTS1GlobalLinkStub` | No | **BACKPORT** (falls out of 1+2) | XS | Low |
| 4 | **EOD per-object persistence** | `EODPersist` → `GlobalLink` | No, needs a local store | **ADAPT** | S | Low |
| 5 | **Lot facade rendering (`FSOF`)** | `tso.world/Facade`, `tso.files/RC` | No | **BACKPORT** | S | Low |
| 6 | **City map / `CityGeometry` / `Terrain` renderer** | `tso.client/Rendering/City` | No (rendering) | **ADAPT** | L | Med |
| 7 | **City realestate / lot purchase** | `tso.client/Controllers/TerrainController` | **Yes** | **NOT APPLICABLE** | — | — |
| 8 | **Lot thumbnails / `FSOF` fetch** | `LotThumbContent` | **Yes** (API/CDN) | **NOT APPLICABLE** | — | — |
| 9 | **Dynamic tuning (`vm.Tuning`)** | `FSO.SimAntics` + `tuning.dat` | Push-only; local file possible | **ADAPT** | S | Low |
| 10 | **Object upgrades** | `tso.content/Upgrades` | No (`Content/upgrades.json`) | **ADAPT** | S | Low (value gated) |
| 11 | **Weather** | `tso.world/Model/WeatherController` | No | **ALREADY DONE** | — | — |
| 12 | **Pet / stray autonomy** | `VMTS1PetAutonomyHelper` | No | **ALREADY DONE** | — | — |
| 13 | **Finale seasonal event** | `tso.common/Rendering/FinaleUtils` | No | **DEAD CODE** | — | — |
| 14 | **TSO job & career service** | `VMOnlineJobsCall` | n/a | **NOT APPLICABLE** (TS1 has its own) | — | — |
| 15 | **Bulletin board / governance / donators** | `tso.client/UI/Panels/Neighborhoods` | **Yes** | **NOT APPLICABLE** | — | — |
| 16 | **Player chat / chat expansion** | `VMNetChatCmd` + `UI/Panels/Chat` | **Yes** (other people) | **NOT APPLICABLE** | — | — |
| 17 | **Vitaboy / BCC appearance + outfit market** | `tso.content/Avatar*` | **Yes** (outfit DB) | **NOT APPLICABLE** | — | — |
| 18 | **TSO inventory / object store** | `VMInventoryOperations` | No, but empty | **ADAPT** | L | Med |
| 19 | **SM64 (Super Mod 64 platformer)** | `tso.world/Components/SM64*` | No, but TSO-only data | **NOT APPLICABLE** | — | — |
| 20 | **`TSODataDefinition` schema** | `tso.files/.../tsodata` | **Yes** | **NOT APPLICABLE** | — | — |
| 21 | **Offline sims / `IServer` abstraction** | — | — | **DOES NOT EXIST** in this fork | — | — |

---

## 2. The headline candidate: the EOD minigame framework

### 2.1 What it is

"Electronic Device Object" is FreeSO/TSO's reimplementation of The Sims 1's minigame objects. In
retail TS1 these were native DLLs loaded at runtime; in FreeSO they were re-authored in C# as a pair
of mirrored halves:

* **`tso.simantics/NetPlay/EODs/Handlers/*`** — 34 plugin classes, 640 KB. The
  *authoritative* half. It runs **inside the VM**, on the simulation thread, and is driven by
  `VMInvokePlugin` (opcode 62, `tso.simantics/VMContext.cs:429-434`). The game script calls the
  plugin, the plugin blocks the calling thread "in the format of an animation", and the sim carries
  on. `tso.simantics/NetPlay/EODs/VMEODServer.cs:11-24` documents this contract in the source.
  (Whole `NetPlay/EODs` tree: 48 files, 707 KB.)
* **`tso.client/UI/Panels/EODs/*`** — 34 files at the top level plus 5 in `Archetypes/` and
  `Utils/`: 39 files, 871 KB. The *presentational* half: a per-plugin `UIEOD` subclass, dispatched
  by plugin id in `UIEODController.IDToHandler`
  (`tso.client/UI/Panels/EODs/UIEODController.cs:11-46`).

The two tables are the same 20-odd plugin ids. The first 17 entries in each
(`0x2a6356a0` Signs … `0x4A245A22` Two-Person Job Object Maze) are **retail TS1's original EODs**;
the entries commented `//freeso specific` are FreeSO additions
(`VMEODFNewspaperPlugin`, `VMEODHoldEmCasinoPlugin`, `VMEODBulletinPlugin`,
`VMEODCooldownEventPlugin`, `VMEODGameshowBuzzerHost/PlayerPlugin`) and `//freeso-archive` is
`VMEODPropertySelectPlugin`.

### 2.2 Why it looks TSO-only

Nothing in the server half is gated on `VM.TS1`, nothing in it is gated on `IsServer`, and nothing in
it reaches for a database except through `IVMTSOGlobalLink` — which Simitone has already replaced.
The *UI* half is unreachable, because it lives in an assembly Simitone's UI does not reference, and
because the one hook that would deliver it is commented out.

### 2.3 The finding: the server half is already live in Simitone

This is the load-bearing fact of the whole assessment, and it is verifiable in four hops:

**(a) `EODHost` is constructed, because Simitone uses `VMServerDriver`.**

```csharp
// tso.simantics/VM.cs:145
public VMEODHost EODHost; //only present if we're a server
// tso.simantics/VM.cs:237   (inside VM.Init())
if (Driver is VMServerDriver) EODHost = new VMEODHost();
```

`VM.cs:56-59` defines `IsServer` as `GlobalLink != null`, and `VMServerDriver` sets `GlobalLink` in
its constructor (`NetPlay/Drivers/VMServerDriver.cs:65-67`), so Simitone's VM reports itself as a
server. `EODHost` is non-null, and it is ticked every simulation frame:

```csharp
// tso.simantics/VM.cs:386-390  (inside VM.InternalTick)
public void InternalTick(uint tickID)
{
    Scheduler.BeginTick(tickID);
    if (GlobalLink != null) GlobalLink.Tick(this);
    if (EODHost != null) EODHost.Tick();
```

**(b) The inbound script→plugin path has no TSO gate.**
`VMInvokePlugin.cs:99-107` calls `context.VM.EODHost.Connect(...)` whenever
`context.VM.IsServer`. The JIT agrees: `SharedPrimitives.TSOInvokePlugin = 62` is on the yielder list
(`FSO.SimAntics.JIT/Translation/Primitives/AbstractTranslationPrimitive.cs:16`), so the translated
code suspends the thread correctly in either game.

**(c) The outbound plugin→UI path routes correctly for one local player.**
The UI half of a message is `VMNetEODMessageCmd.Execute`:

```csharp
// tso.simantics/NetPlay/Model/Commands/VMNetEODMessageCmd.cs:16-26
public override bool Execute(VM vm, VMAvatar caller)
{
    //only run by client. send to their UI handler.
    if (caller != null && caller.PersistID == vm.MyUID)
    {
        vm.SignalEODMessage(this);
    }
    return true;
}
```

The gate is `caller.PersistID == vm.MyUID`. In Simitone:

* `vm.MyUID = 65537` (`TS1GameScreen.cs:981`, `LotLifecycleService.cs:152`, and
  `UIHouseSelectPanel.cs:894` for the headless path).
* The active sim is given exactly that id when the lot is built —
  `tso.simantics/Utils/VMTS1ActivatorNew.cs:564`:
  `ent.PersistID = selectedPerson == ent.ObjectID ? 65537 : (uint)ent.ObjectID;`
* Switching sims re-issues the same id — `VMGenericTS1Call.cs:351-367`
  (`MakeTemp0SelectedSim`) sets `caller.PersistID = 0` on the old sim and
  `target.PersistID = vm.MyUID` on the new one, and re-registers it in `ObjectQueries`.

So for the sim the player is actually controlling, `GetAvatarByPersist(65537)` returns non-null and
`PersistID == MyUID` holds. The message reaches `vm.OnEODMessage`.

**(d) Nothing consumes `OnEODMessage` in Simitone — it is one commented-out line.**

```csharp
// Client/Simitone/Simitone.Client/UI/Screens/TS1GameScreen.cs:840-843
vm.OnFullRefresh += VMRefreshed;
//vm.OnEODMessage += LotControl.EODs.OnEODMessage;
vm.OnRequestLotSwitch += VMLotSwitch;
vm.OnGenericVMEvent += Vm_OnGenericVMEvent;
```

FreeSO's two game screens do subscribe — `tso.client/UI/Screens/CoreGameScreen.cs:742` and
`tso.client/UI/Screens/SandboxGameScreen.cs:520` — and FreeSO's `UILotControl` creates the
controller at `tso.client/UI/Panels/UILotControl.cs:185` (`EODs = new UIEODController(this);`,
declared `:93`). Simitone's own `UILotControl`
(`Client/Simitone/Simitone.Client/UI/Panels/UILotControl.cs:47-…`, 94 KB, implementing
`FSO.UI.Panels.LotControls.ILotControl`) has no `EODs` field at all — grep for `EOD` in that file
returns nothing.

**What this means in one sentence: the minigames are running inside Simitone's VM right now, playing
themselves into a void, because the button that shows the window is commented out.**

### 2.4 The strings are already shipping

This removes the most tedious part of the port. The EOD string tables are FreeSO's own `.cst` files
under `FreeSO/TSOClient/FSO.Content.TSO/Content/UI/uitext/english.dir/`, and they reach Simitone's
output because `tso.client/FSO.Client.csproj` has a `ProjectReference` to `FSO.Content.TSO`, whose
csproj globs `Content\**\*.*` with `CopyToOutputDirectory=PreserveNewest`, and `Simitone.Windows`
references `FSO.Client`. The build output already contains:

```
Simitone.Windows/bin/Release/net10.0-windows/Content/UI/uitext/english.dir/
  _f108_newspapereodstrings.cst    _f111_casinoeodstrings.cst
  _f110_manageeodstrings.cst       _f112_misceodstrings.cst
  _f126_bandtwostrings.cst         _f127_gameshoweod.cst
  _f120..123_bulletin*.cst          _f129_servermessages.cst
```

`ContentStrings` loads these in TS1 mode as well as TSO mode — SimitoneStrings' own doc comment says
so (`Client/Simitone/Simitone.Client/Utils/SimitoneStrings.cs:33-35`). So `UIBlackjackEOD.cs`'s
`GameFacade.Strings.GetString("f111", "2")` and friends resolve with no new content work.

### 2.5 The Live Mode fit

`UIEODController` is deliberately "a container so that it can hold EODs without them being active in
Live Mode" (`UIEODController.cs:48`), and `UILiveMode` reads three fields off it
(`tso.client/UI/Panels/UILiveMode.cs:386-388, 664-676`): the timer visibility, `EODMessage` and
`EODTime`. Simitone already has Live Mode (`UILotControl.cs:78 public bool LiveMode = true;`,
`:748 LiveModeUpdate`, `UIMainPanel.cs:432`, and the `UI/Panels/LiveSubpanels/` tree with six
subpanels), so the integration point exists and is already shaped like the one FSO expects.

### 2.6 Effort, risk, and the one real refactor

**The honest cost is the UI port, not the framework.** 39 files / 871 KB is a lot of code, but it is
mechanical: they depend on `FSO.Client.UI.Framework`, `FSO.Client.UI.Controls`, `FSO.SimAntics*` and
`GameFacade`, all of which Simitone already uses directly (`TS1GameScreen.cs:2-5` imports them).

The one genuine integration problem is a type, not a feature:

```csharp
// tso.client/UI/Panels/EODs/UIEOD.cs:26-37
public UILotControl LotController
{
    get {
        var screen = UIScreen.Current as IGameScreen;
        if (screen == null){ return null; }
        return screen.LotControl;
    }
}
```

and

```csharp
// tso.client/UI/Screens/IGameScreen.cs:16
UILotControl LotControl { get; set; }
```

`IGameScreen` is in `FSO.Client` and types `LotControl` as **FSO.Client's** `UILotControl`.
`Simitone.Client.UI.Screens.TS1GameScreen` does not implement `IGameScreen`, and cannot without
adopting the wrong `UILotControl` (and without a `ChatPanel`, which FSO's `CoreGameScreen.cs:547`
uses and Simitone's lot control does not have).

Cheapest fix, ~3 files: stop routing through `IGameScreen`. `UIEODController` already holds a
reference to the lot control it was constructed with (`EODController.Lot`, used at `UIEOD.cs:64,70`)
— make `UIEOD.LotController` resolve through that reference instead, typed as
`FSO.UI.Panels.LotControls.ILotControl` (`FSO.UI/Panels/LotControls/ILotControl.cs:7-13`, which
already exposes `ActiveEntity`, `World`, `Budget`, `Rotate` and is already implemented by Simitone's
lot control). That is a contained edit to `UIEOD.cs` and `UIEODController.cs` and touches no
`FSO.Client` screen.

Files to drop in the port, deliberately:

* `UIManageEODObjectPanel.cs` (50 KB) — the admin/IDE object manager; pulls in
  `FSO.Client.UI.Panels.Neighborhoods` and `FSO.Client.Controllers`.
* `UISecureTradeEOD.cs` (27 KB) + `VMEODSecureTradePlugin.cs` (15 KB) — needs two players *and* a
  server; `VMTS1GlobalLinkStub.cs:308, 312, 336, 341` leaves `SecureTrade` and `FindLotAndValue`
  completely empty.
* `UIRackOwnerEOD.cs` / `UIRackEOD.cs` / `UIPropertySelectEOD.cs` — outfit market;
  `VMTS1GlobalLinkStub.cs:282-298` returns an empty outfit array and no-ops every purchase.
  (The rack *plugin* still loads — see 2.7 — it just has nothing to sell.)

**Risk: low.** The framework is deterministic, single-threaded on the sim thread, and the message
path is already proven by the `65537` PersistID convention. The realistic failure modes are (i) the
`IGameScreen` refactor being wider than expected, and (ii) a plugin assuming a second client. Both
are contained.

---

## 2.7 Bonus finding: the TS1 family funds already work as a casino bank

The stub's economy is not a no-op — it is a *TS1* economy:

```csharp
// tso.simantics/Engine/TSOGlobalLink/VMTS1GlobalLinkStub.cs:64-73
public uint GetBudgetForFamily(VM vm, VMEntity ent)
{
    if (ent != null && ent is VMAvatar)
    {
        return (uint?)vm.TS1State.CurrentFamily?.Budget ?? uint.MaxValue;
    }
    return uint.MaxValue; //maxis has infinite money
}
```

Every casino plugin withdraws from the *table* and pays the *avatar*:

| Plugin | Withdraw (table → player) | Deposit (player → table) |
|---|---|---|
| `VMEODBlackjackPlugin` | `:834` | `:884` |
| `VMEODRoulettePlugin` | `:665` | `:715` |
| `VMEODSlotsPlugin` | `:264` | `:314` (also `:365` bet, `:511` winnings) |
| `VMEODHoldEmCasinoPlugin` | `:577` | `:626` (also `:1610`, `:1747`) |

The table's id is a `VMGameObject`, so `GetBudgetForFamily` returns `uint.MaxValue` for it — the
house is infinitely solvent, exactly as the comment says. The player's id is `65537`, so the money
comes out of and goes back into **`TS1State.CurrentFamily.Budget`**, the same field
`VMTS1Budget.cs:18-21` (opcode 26) manipulates. `CanTransactBudgetForFamily` returns `false` on
overdraft, so the "Bet_Too_High" / "Bet_NSF" alert paths
(`UIBlackjackEOD.cs:167,168`) will fire correctly.

**Blackjack, roulette, slots and hold'em would pay real TS1 family funds with no economy code at
all.** That is a materially different proposition from "port a casino", and it is the single
strongest reason to do the EOD port.

---

## 2.8 EOD plugins that will *stall* and must be handled deliberately

`VMTS1GlobalLinkStub` implements several `GlobalLink` methods with an **empty body** — the callback
is never invoked. Two plugins await such a callback during `OnConnection`, so they will open and
then sit there:

* **`VMEODBulletinPlugin`** — `Server.vm.GlobalLink.GetBulletinState(...)` at
  `Handlers/VMEODBulletinPlugin.cs:78`; stub body at `VMTS1GlobalLinkStub.cs:316-319`.
* **`VMEODFNewspaperPlugin`** — `Server.vm.GlobalLink.GetDynPayouts(...)` at
  `Handlers/VMEODFNewspaperPlugin.cs:20`; stub body at `VMTS1GlobalLinkStub.cs:304-306`.

Both are FreeSO-only additions (not retail TS1 EODs), so the minimum correct action is to leave them
out of the first port. The better action, if either is wanted, is to make the stub *call back* with a
locally-computed value — the newspaper payout graph is `VMEODFNewspaperData`, a plain serializable
struct (`VMEODFNewspaperPlugin.cs:54-…`) that could be derived from TS1 skill data.

**Nothing else stalls.** `LoadPluginPersist` returns `null` on a background thread
(`VMTS1GlobalLinkStub.cs:209-215`) and the contract is "null if none available"
(`IVMTSOGlobalLink.cs:63`), which is the documented no-state case, not a hang. This affects
`VMEODGameCompDrawACardPlugin` (`:45`, `:204`), `VMEODPermissionDoorPlugin` (`:31`, `:65`),
`VMEODRackPlugin`/`VMEODRackOwnerPlugin` (`:29`, `:132-133`), and
`VMEODCooldownEventPlugin` (`:93`, `:129`). Those plugins work; they just do not *remember*.

### 2.9 EOD persistence — the one small adapt that pays for itself

`EODPersist` (`tso.simantics/NetPlay/EODs/Utils/EODPersist.cs:48, 60`) routes object state through
`SavePluginPersist`/`LoadPluginPersist`, which Simitone's stub discards
(`VMTS1GlobalLinkStub.cs:217-220` — the body is a commented-out `//Database.SavePluginPersist`).

The fix is small and local: Simitone already has a per-lot persistence service
(`UI/Services/LotLifecycleService.cs`, `Utils/LotPersistence.cs`, `Utils/TS1UserData.cs`) and already
persists a non-VM sidecar for weather (`LotWeatherState.WriteSidecar`, called from
`TS1GameScreen.cs:719-724`). Pointing `SavePluginPersist` at the same sidecar would give the
two-player maze its completed state, the permission door its schedule, and the cooldowns their
history, for the cost of two method bodies. **Verdict: ADAPT, small, low risk, do it in the same
pass.**

---

## 3. Second candidate: lot facades (`FSOF`)

**What it is.** `tso.world/Facade/LotFacadeGenerator.cs` (35 KB) turns a lot blueprint into a
`FSOF` mesh — the low-poly building shell used to represent a lot from outside. `FSOF` is a plain
local file format, read without any network: `LotThumbContent.cs:40-45` loads
`Content/3D/defaulthouse.fsof` off disk, and the generator's output is written the same way.

**Why it looks TSO-only.** In TSO the facade is fetched per-lot from the shard
(`LotThumbContent.cs:27-28, 74-78` — either a `CityResourceController` or an `ApiClient` HTTP call).
Only the *fetch* is online; the *format and the generator* are not.

**Could TS1 data drive it?** Yes, and partly already does — Simitone's neighbourhood OBJ exporter
already calls it (`Utils/SimitoneNeighOBJExporter.cs:107-115`). The new neighbourhood code
explicitly reserves the hook: `Neighbourhood/INeighbourhoodWorld.cs:264` documents "the facade a
neighbourhood screen will eventually own: which lot is active, what time it…".

**Value.** Facades are the difference between a neighbourhood map of coloured rectangles and a
neighbourhood map of houses. It is the highest value-per-line visual backport in the tree.

**Effort / risk.** Small / low. It is in `FSO.LotView` and `FSO.Files`, both already referenced by
`Simitone.Client`. The work is a local writer for `FSOF` per lot in the neighbourhood store, not a
port.

**Verdict: BACKPORT.**

---

## 4. Third candidate: the city map renderer

**What it is.** `tso.client/Rendering/City/` — 20 files, ~301 KB. `Terrain.cs` (92 KB) is the scene;
`CityGeometry.cs` (48.6 KB) builds the terrain mesh; `CityFoliage`, `CityNeighGeom` (Voronoi
neighbourhood overlays), `CityCamera2D` / `CityCamera3D`, `CityContent`, and the `Plugins/` map
painter. `CityMap.cs` (`tso.content/Model/`) is a pure local reader for seven 512×512 images per
city.

**Why it looks TSO-only.** TSO's map is a 512×512 grid of buildable lots with server-owned real
estate. But the split is cleaner than it looks:

* **Data side is local.** `CityMapsProvider.cs:22-40` scans `Content/cities/city_XXXX` and
  `ContentDir/Cities`; `CityMap.cs:29-72` opens `elevation`, `forestdensity`, `foresttype`,
  `roadmap`, `terraintype`, `vertexcolor`, `thumbnail` as `.bmp` or `.png`. No network.
  **`Simitone.Windows/bin/Release/net10.0-windows/Content/Cities/city_0100/` already ships all
  seven `.png` files** — the data is in the build output today and nothing reads it.
* **The renderer is local.** `Terrain.LoadContent` (`Terrain.cs:192-207`) needs only a
  `GraphicsDevice`, three MonoGame effects (`Effects/VerShader`, `PixShader`, `colorpoly2D`, present
  in `Content/DX/Effects` and `Content/OGL/Effects`), and a `CityMap`.
* **The only data entry point is one method.** `Terrain.populateCityLookup(LotTileEntry[])`
  (`Terrain.cs:231-249`) takes a flat array of lot tiles. Simitone's neighbourhood store can produce
  that in a few lines.

**TS1 relevance that is already encoded.** `TerrainType` in `CityMap.cs:162-164` already carries
`TS1DarkGrass`, `TS1AutumnGrass` and `TS1Cloud` — the enum has TS1 terrain types in it, so somebody
already anticipated feeding TS1 ground cover through this pipeline.

**Effort / risk.** Large / medium. The genuine problems are not the mesh code:

* `Terrain.cs:299` reaches for `FindController<TerrainController>()?.Parent`, i.e. the view is
  hard-wired to the online controller. That is one null-guarded call and can simply be made
  optional, but it is a coupling to break.
* `Terrain` extends FSO.Client's view base, so adopting it means an `FSO.Client` view type inside
  Simitone's scene graph, or a re-parent. That is a real structural cost and the reason this is
  *adapt*, not *backport*.
* The 2D/3D camera pair, the zoom-to-lot handoff and the sky dome all assume a lot on the other side
  of a click, which in Simitone means `LotTravel` (`Utils/LotTravel.cs`) rather than a server
  `ShowLotPage`.

**Value.** High visually, and it would give the neighbourhood map real terrain, foliage and
day/night instead of the current flat presentation. But it competes with
`Client/Simitone/Simitone.Client/Neighbourhood/` (Scope A, `docs/NEIGHBOURHOOD_SCOPE_A_PLAN.md`),
which is already building a neighbourhood layout from TS1 data. **The two should be reconciled, not
run in parallel** — `CityGeometry` is a plausible renderer for Scope A's `NeighbourhoodLayout`, and
the cheaper first step is to evaluate that pairing before committing to a port.

**Verdict: ADAPT.** Do not attempt before the EOD port; do attempt once the neighbourhood work has a
renderer decision to make anyway.

---

## 5. Fourth candidate: dynamic tuning

**What it is.** `vm.Tuning` is a `DynamicTuning` table the TSO server pushes into the lot at join
time (`tso.simantics/VM.cs:105`; the only writer is
`tso.simantics/NetPlay/Model/Commands/VMNetTuningCmd.cs:13`, `vm.Tuning = Tuning;`). It is the
shard's live feature-flag and pacing table. It is marshalled with the lot
(`Marshals/VMMarshal.cs:42, 110`), so it is per-lot state.

**Why TSO-only.** Nothing in TS1 sends `VMNetTuningCmd`, so `vm.Tuning` is `null` in Simitone, and
`VMContext.InitSpecialTuning` (`VMContext.cs:602-627`) returns immediately at line 604. The static
tuning file `tuning.dat` is likewise TSO-branch-only
(`tso.content/Content.cs:165`, inside the `else` of the TS1 check).

**What is currently being lost** — every consumer in the tree:

| Consumer | Location | What it would control |
|---|---|---|
| `special/0/1` | `VMContext.cs:605` | `DisableAvatarCollision` — already read per-frame at `VMAvatar.cs:414` |
| `special/0/2` | `VMContext.cs:606, 622-625` | `Blueprint.MinOutMul` — lot minimum lighting |
| `special/0/3` | `VMContext.cs:620` | `World.ForceAdvLight` |
| `aprilfools/0/2019` | `VMContext.cs:607` | `3DFloorGeometry.af2019` |
| `platformer/0/0` | `VMContext.cs:608` | `SM64Component.Allowed` |
| `overfill` table | `Engine/VMTuningCache.cs:12` | object overfill limits |
| `category_mul` | `Primitives/VMRelationship.cs:123` | relationship/skill/money weight by lot category |
| `feature/0/1` | `Engine/VMRoutingFrame.cs:384` | bezier pathfinding |

Every one of these is `?.`-guarded or early-returned, so the null is handled correctly everywhere —
nothing is broken, it is simply inert.

**Could local data drive it?** Yes. The object-upgrade provider already demonstrates the pattern in
this very codebase: `ObjectUpgradeProvider.LoadJSONTuning()`
(`tso.content/Upgrades/ObjectUpgradeProvider.cs:29-43`) reads a local
`Content/upgrades.json` instead of `LoadNetTuning(byte[])` (`:51-69`), and the server is the only
caller of the former in a normal client
(`tso.content/Content.cs:203`, `if (Mode == ContentMode.SERVER) Upgrades.LoadJSONTuning();`).
A `Content/tuning.json` read once at lot load and pushed through the existing `VMNetTuningCmd`
would light up the whole table. The one thing to be careful about is
`VMTuningCache.cs:12` and `VMRelationship.cs:123`, which dereference `vm.TSOState.PropertyCategory`
— both are behind a `vm.Tuning?.` null test, so as soon as `Tuning` becomes non-null in TS1 they
will need the TS1 lot state to answer `PropertyCategory` (it is on `VMAbstractLotState`, so
`TS1State.PropertyCategory` resolves; verify before enabling).

**Value.** Medium. Mostly a *modding and balance* lever rather than a player-facing feature —
exactly the kind of thing the `UICheatTextbox` in Simitone already does by hand
(`UI/Panels/UICheatTextbox.cs`). **Effort: small. Risk: low.**

**Verdict: ADAPT** (local `tuning.json` loader), and only after the EOD port.

---

## 6. Object upgrades — small adapt, but the value is gated

`ObjectUpgradeProvider` (`tso.content/Upgrades/`) has a server path (`LoadNetTuning`) and a local
path (`LoadJSONTuning`, reading `Content/upgrades.json`), and the whole model
(`UpgradesFile` / `UpgradeIff` / `UpgradeLevel` / `UpgradeGroup` / `UpgradeSubstitution`) is plain
serializable data with no server dependency. It is only constructed in the `!TS1` branch of
`tso.content/Content.cs:167` and initialised at `:202`.

**The honest problem:** upgrade definitions name TSO object GUIDs and patch TSO `.iff` files. Without
TSO object patches in the install there is nothing for an upgrade to point at. The system would be
**correct and inert**. It becomes valuable the moment Simitone carries a patch library of its own —
`Content/Patch/Pets`, `Content/Patch/Pizza`, `Content/Patch/TSOPorts`,
`Content/Patch/Overfill` already exist in the build output. **Effort: small. Risk: low. Value:
presently near zero. Verdict: ADAPT, deferred, pair it with the patch library.**

---

## 7. Things already done — do not re-open

### 7.1 Weather — complete

`tso.world/Model/WeatherController.cs` (7 KB) is not TSO-only at all; it is lot-local
(`Blueprint`, `Particles`, `Blueprint.OutsideColor`). Its one server coupling is
`GetAutoWeatherIntensity` reading `Common.Model.DynamicTuning.Global` (`:182, 196`) to pick snow vs
rain and to allow disabling — and Simitone has already replaced the *manual* path wholesale:

* `Client/Simitone/Simitone.Client/WeatherSounds.cs` (audio, CC0 clips, `-nosound` handling)
* `LotWeatherState` — the tick/state machine and sidecar persistence
* `TS1GameScreen.cs:646-706` `UpdateWeatherEffects` reads `vm.Context.Blueprint.Weather`, applies
  `weather.SetWeather(...)`, and drives `LotWeatherState.Tick`
* `TS1GameScreen.cs:719-724` `PersistWeather` writes a sidecar; `:809` reads it on lot load
* `UI/Panels/UICheatTextbox.cs:108-112, 238` — a weather cheat that goes through
  `WeatherCommandParser`, deliberately *not* through `VMCheatContext`

Weather even has the 12-bit `WeatherData` encoding (`WeatherController.cs:22-27`) reimplemented as
`WeatherCommandParser.Encode` with rain / snow / hail / thunder. **Nothing to do.**

### 7.2 Pet and stray autonomy — complete

`tso.simantics/Utils/VMTS1PetAutonomyHelper.cs` is a Simitone-authored analysis-and-helper class
referenced from `VMFindBestAction.cs:122, 128`. It already documents, from the shipped `CatGlobals` /
`DogGlobals` BHAVs, that the pet Hunger clamp (-90) sits above the failure threshold (-98), that the
free-will exemption is load-bearing, and that strays skip every `VMGameObject`. **Nothing to do.**

### 7.3 The Finale seasonal event — dead code, leave it

`tso.common/Rendering/FinaleUtils.cs:71-76`:

```csharp
return time.Year == 2024 && time.Month == 12 && ((time.Day == 8 && time.Hour == 23) || (time.Day == 9 && time.Hour == 0));
```

A hardcoded two-hour window in December 2024 that has not fired since. It still gates
`WeatherController.cs:92, 171` and `AbstractSkyDome.cs:48`, so a reader may mistake it for a live
feature. It is not one, and porting it would be porting an expired date literal. **No action.**

---

## 8. Genuinely server-dependent — do not attempt

These are not "hard". They are undefined without a shard.

### 8.1 City realestate, lot purchase, hover/price — `TerrainController`

`tso.client/Controllers/TerrainController.cs:44-63` takes `IClientDataService`,
`Network.Network`, `IRealestateDomain` and `PurchaseLotRegulator` and captures
`network.MyShard.Id` in its constructor. Every user action is a server round-trip: hover
(`:208-233`, a 250 ms debounce then `DataService.Get<Lot>`), click (`:269-364`), buy
(`:369-485`), move lot (`:523-534`), mayor/town-hall placement (`:551-573`). A "TS1" version would
not be a backport, it would be a re-implementation with no spec. **NOT APPLICABLE.**

### 8.2 Lot thumbnails and facade *fetch* — `LotThumbContent`

`LotThumbContent.cs:27-28, 37` constructs an `ApiClient` against
`ApiClient.CDNUrl ?? GlobalSettings.Default.GameEntryUrl` and takes a `CityResourceController`;
`GetLotEntryForFrame` (`:50-80`) resolves facades through one or the other. There is no local
source of thumbnails. The `FSOF` *format* is reusable (§3); this class is not.
**NOT APPLICABLE** — but note this is the reason §3 matters.

### 8.3 Bulletin board, neighbourhood governance, ratings, donators, top-10

`tso.client/UI/Panels/Neighborhoods/` is 22 files (`UIBulletinBoard` 37 KB, `UINeighPage` 41 KB,
`UITop10Pedestal` 12 KB, `UIDonatorDialog`, `UIManageDonatorDialog`, the whole `UIVote*` family,
the `UIRating*` family). Ratings, posts, mayor elections, nominations and donator tiers are all
per-account, cross-lot, server-ranked. `VMEODBulletinPlugin` is the in-VM face of the same thing and
depends on `GetBulletinState` (empty in the stub). **NOT APPLICABLE.**

### 8.4 Player chat and chat expansion

`VMNetChatCmd.cs` (16.6 KB) carries the channel, bubble, the `Tuning = vm.Tuning` payload at
`:169` for the "chat with style" expansion system, and the whole emoji/suggestion apparatus
(`UI/EmojiSuggestions.cs`, `_f113_chatexpansionstrings.cst`). It exists to talk to other people.

One genuinely useful sub-case: **chat balloons over Sims** are *not* this system — they are
`set_balloon_headline` (`tso.simantics/Primitives/…`) plus
`tso.client/UI/Panels/UIChatBalloon.cs` (15 KB), and Simitone already has the renderer side
(`Simitone.Client/UI/Panels/WorldUI/UIHeadlineRenderer.cs`, `UIMoneyHeadline.cs`, `UIMoneyPanel`).
**NOT APPLICABLE** for the text channel; balloons are already covered.

### 8.5 Vitaboy / BCC appearance and the outfit market

`Content.cs:148-156` makes the whole TSO avatar stack TSO-branch-only: `AvatarBindingProvider`,
`AvatarAppearanceProvider` (`.bcc`), `AvatarOutfitProvider`, `AvatarPurchasables`,
`AvatarCollectionsProvider`, `AvatarThumbnailProvider`, `AvatarAnimationProvider`,
`AvatarSkeletonProvider`. TS1 has its own equivalents and Simitone already wired them:
`TS1BMFProvider` / `TS1AvatarTextureProvider` / the `TS1BCF*` trio
(`Content.cs:119-124, 142-145`) behind a 1900-line `TS1CASScreen.cs`.

The one piece with any TS1 interest is the **outfit rack** — but that is an EOD
(`VMEODRackPlugin` / `VMEODRackOwnerPlugin`, `RackOutfitsProvider`), and its market
(`GetOutfits` / `PurchaseOutfit` / `UpdateOutfitSalePrice` / `DeleteOutfit`) is
`VMTS1GlobalLinkStub.cs:277-298` returning an empty array. **NOT APPLICABLE** as a market; the rack
*shell* can ship with the EOD port and simply have nothing in it.

### 8.6 `TSODataDefinition` — the server's schema

`tso.files/Formats/tsodata/TSODataDefinition.cs` describes the shard's masked-struct wire format
(`Content/FSODataDefinition.dat` or `TSOData_datadefinition.dat`, loaded at `Content.cs:222-241`).
Its only real consumers are `tso.client/FSO.Server.DataService/DataService.cs` and the IDE's editors.
There is no second consumer to serve. **NOT APPLICABLE.**

### 8.7 SM64 (Super Mod 64 platformer)

`tso.world/Components/SM64Component.cs` (61 KB) plus `SM64Geo/`, `SM64GeometryEmit`, and
`VMNetSM64Position/Event/AnimDataCmd`. Gated on `VM.Tuning.GetTuning("platformer", 0, 0) == 1`
(`VMContext.cs:608`) — i.e. on a server-supplied flag — and driven by SM64-format avatars, which are
TSO content. The `VM.cs:440-457` block is a per-tick command flood for other players' Mario
instances. **NOT APPLICABLE.**

### 8.8 TSO inventory / object store — adapt, but large

`VMInventoryOperations.cs` (opcode 60) plus `VMNetBuyObjectCmd`, `VMNetPlaceInventoryCmd`,
`VMNetSendToInventoryCmd`, `VMNetDeleteObjectCmd`, `VMNetAsyncPrice/SaleCmd`. Every entry point is a
`GlobalLink` call the stub leaves empty (`VMTS1GlobalLinkStub.cs:245-270`:
`RetrieveFromInventory*` returns a blank `VMInventoryRestoreObject`, `ConsumeInventory`,
`DeleteObject` and `ForceInInventory` do nothing).

Two things *do* already work and are worth knowing about:
`RegisterNewObject` (`:222-231`) hands out sequential persist ids from `0x1000000` via
`VMNetUpdatePersistStateCmd`, and `MoveToInventory` (`:233-237`) calls back `true` with the object's
own id. So a local store has a working id allocator and a working "it succeeded" path already.

But Simitone has **already built its own object catalogue** — `Utils/CatalogQueryService.cs`,
`UI/Services/BuyCatalogService.cs`, `UI/Panels/LiveSubpanels/UIBuyBrowsePanel.cs`,
`UI/Panels/UIObjectHolder.cs` (29 KB). Re-enabling TSO's inventory would mean two parallel object
stores. **Verdict: ADAPT, large, medium risk, and only if the existing catalogue turns out to be
missing something. Not a backport.**

---

## 9. Candidates that do not exist in this fork

The brief listed "offline sims" and "the `IServer`/`VM` networking abstraction". A full-tree grep for
`OfflineSims|OfflineSim|NPCSim|IServer\b` across `FreeSO/TSOClient` returns **zero matches**. This
fork has no offline-sim subsystem and no `IServer` interface; the closest things are
`VM.Sandbox()` / `VM.SandboxRestore()` (`tso.simantics/VM.cs:676-695`), which exist only so the IDE
can run a script against a throwaway VM, and `SandboxMode/BanList.cs` (1.6 KB) under
`NetPlay/`, which is a TSO sandbox server's ban list. **There is nothing here to back-port and
nothing here to lose.**

---

## 10. The `if (VM.TS1)` / `if (!VM.TS1)` gates, ranked

Eighty `VM.TS1` *sites* are not the whole story either: 113 `VM.TS1` line matches across 44 files
(one in the JIT, the rest in `tso.simantics`). Reading them in full, the overwhelming majority are
**TS1-*enabling***, not TSO-only:

* **TS1-only paths being added:** family management and cross-neighbourhood travel
  (`VMGenericTS1Call.cs:52-78, 119-331`), family budget (`VMTS1Budget.cs:18-21`), TATT traits
  (`VMMemory.cs:153-161, 594-598`), pet/child rules (`VMFindBestAction.cs:122, 128`,
  `VMExpression.cs:193, 221`, `VMAvatar.cs:296-369, 936`, `VMAnimateSim.cs:47-131`,
  `VMRelationship.cs:42`, `VMMemory.cs:682`), TS1 routing and queue semantics
  (`VMThread.cs:785-873`, `VMRoutingFrame.cs:818`), TS1 neighbourhood creation
  (`VMCreateObjectInstance.cs:96, 167`), and TS1 attire lookup (`VMSuitProvider.cs:197-212`,
  `VMChangeSuitOrAccessory.cs:21`).
* **TSO-only skips worth knowing about, all of them already accounted for above:**
  `VMAvatar.cs:414` (avatar collision tuning), `VMBurn.cs:17` (the TSO "special" tuning burn),
  `VMIdleForInput.cs:18` (TS1 push-on-queue), `VMArchitecture.cs:497` (net command caller
  resolution), `VMDialogPrivateStrings.cs:74, 84, 109` (global blocking dialog), and
  `VMFindBestObjectForFunction.cs:79, 92` (`RepairState` / `VMTSOObjectState.Broken` — the TSO
  broken-object repair loop).
* **Two `VM.TS1` sites are latent hazards rather than features:** `VMAvatar.cs:936` reads
  `GetPersonSuitTS1`, and `VMOnlineJobsCall.cs:21` dereferences `context.VM.TSOState.JobUI` with
  **no null guard** — in TS1 that is `VM.TS1State`, so `TSOState` is `null` and the line would
  throw. It does not fire today because no TS1 script uses opcode 189. It is a trap for anyone who
  ever points a TS1 BHAV at it.

**Conclusion from this section:** there is no large TSO-only feature hiding behind an
`if (VM.TS1)` in the simulation core. The gates are all small, all already understood, and the big
TSO-only surface is the client UI and the server data layer, both of which this document has
classified above.

---

## 11. Recommended order

1. **EOD server-half smoke test.** No code: drive one retail EOD object (a sign is the simplest —
   `UISignsEOD.cs` is 8.4 KB of UI and `VMEODSignsPlugin.cs` is 5.2 KB) and confirm
   `vm.OnEODMessage` fires for persist id `65537`. If this does not fire, everything below is blocked
   and the cause is in §2.3 step (c).
2. **EOD UI port.** 39 files, minus the three exclusions in §2.6, plus the `IGameScreen` →
   `ILotControl` refactor and the one-line `OnEODMessage` subscription. Start with the small ones
   (`UISignsEOD`, `UITrunkEOD`, `UITimerPluginEOD`, `UIDanceFloorEOD`, `UIPizzaMakerEOD`,
   `UIScoreboardEOD`) before the 100 KB+ card games.
3. **Casino economy verification** (§2.7) — the first thing to actually *play* after step 2.
4. **EOD persistence against the existing sidecar** (§2.9) — two method bodies, immediate payoff.
5. **`FSOF` facades for the neighbourhood map** (§3) — independent of EOD; do it whenever
   `Neighbourhood/INeighbourhoodWorld.cs:264` is next touched.
6. **Local `tuning.json`** (§5) — small, and it converts a pile of currently-inert `?.` code into a
   live balance/modding surface.
7. **City renderer** (§4) — largest and most contested; sequence it against the Scope A
   neighbourhood work rather than ahead of it.

Do **not** schedule items 8.1–8.8. They are not backports.

---

## Corrections from Waves R-T (2026-09-28)

- **EOD framework — IMPLEMENTED** (Wave R2 + S5). The client is wired and 18 of 27 plugins are
  eligible; the TS1 install ships **no `.uis` layouts** (a TSO asset), so Simitone now *authors* 18
  non-EA layouts from a pure generator. Still needs a human playtest.
- **The §5 tuning warning is partly wrong.** `PropertyCategory` is on **`VMTSOLotState`**, not
  `VMAbstractLotState`, so `TS1State.PropertyCategory` does **not** resolve. `VMRelationship`
  (opcode 26) runs in TS1 and does `vm.Tuning?.GetTuning("category_mul", 0, vm.TSOState.PropertyCategory)`;
  today the `?.` short-circuits and never evaluates the argument. Making `vm.Tuning` non-null in TS1
  therefore needs a TS1 lot category (or a `vm.TS1` guard) **before** it is enabled. The TS1 tuning
  data path itself has landed (`Content.GlobalTuning` is non-null; `Content.DynamicTuning` carries the
  `vm.Tuning` payload).
- **Object upgrades — NOT APPLICABLE as written.** Zero of the 72 `upgrades.json` targets resolve in
  TS1 (they name TSO object files/piffs), the subs target TSO `OTF` scopes (TS1 only has `BCON`), the
  prices are TSO catalog GUIDs, and every consumer is TSO-only. It needs an `OBJf`-based TS1 upgrade
  library, not a call site.
- **`FSOF` lot facades — DUPLICATE.** TS1 lot data can drive `LotFacadeGenerator`, and the landed
  neighbourhood facade path already does exactly that (and persists via the layout sidecar); the only
  `FSOF` reader in the tree is bound to an online shard's `ApiClient`.
