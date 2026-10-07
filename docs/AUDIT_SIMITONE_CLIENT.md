# Simitone Client — code audit (Waves A–U)

Scope: `Client/Simitone/Simitone.Client/**` and `Client/Simitone/Simitone.Windows/**`. FreeSO submodule, test style and test-only concerns are out of scope (the test project's *production* wiring is in scope where it constrains behaviour).

Method: read-only source review. Verified with `dotnet build Simitone.Client.csproj -t:Compile -c Release` — **0 errors**, 84 warnings, all `NU1701` NuGet restore warnings (no C# compiler warnings). Line numbers are against the tree as read.

Severity scale: **S1** player-visible data loss / crash / security · **S2** correctness bug with a real user path · **S3** robustness, robustness-of-garbage-input, or a maintenance trap that will produce S2 later · **S4** hygiene.

---

## S1

### SIM-01 · `simitone.ini` is written twice by two classes that each believe they own it — one silently erases the other
**`Utils/../UI/Panels/Options/SunCycleSetting.cs:164-175` and `Neighbourhood/NeighbourhoodViewSetting.cs:278-290`**

Both settings declare `FileName = "simitone.ini"` and both write it through FreeSO's `IniConfig.Save()`, which does `FileMode.Create` (truncate) and then writes **only the properties of the concrete subclass**:

```
// FreeSO/TSOClient/tso.common/IniConfig.cs:71-79
using (var stream = new StreamWriter(File.Open(ActivePath, FileMode.Create, ...)))
    ... foreach (var prop in props) { if (prop.Name == "Default" || prop.Name == "DefaultValues") continue; ... }
```

`SunCycleIni` declares one settable property (`SunCycle`); `NeighbourhoodViewIni` declares one (`NeighbourhoodView`). So:

* `SunCycleSetting.SetEnabled(x, save:true)` → `simitone.ini` contains only `SunCycle=...`; the `NeighbourhoodView` key is **gone**.
* `NeighbourhoodViewRow.Apply()` → `NeighbourhoodViewSetting.SetAndSave` → `simitone.ini` contains only `NeighbourhoodView=...`; `SunCycle` is **gone**.

The `NeighbourhoodViewIni` doc comment at line 296 explicitly claims the opposite: *"`SunCycleSetting` writes the other half of this file; `IniConfig` merges, so neither key can lose the other."* **`IniConfig` does not merge — it truncates.** The comment documents a guarantee the base class does not provide.

**Why it matters:** both settings are read back through `Resolve(...)`, whose contract is "an unknown/missing value is the default". So the loss is *silent*: the player's "Dynamic Sun off" reverts to on (default `true`) the next time they touch the Neighbourhood View radio, and vice versa (default `World3D`). No error, no log line. This is exactly the class of bug the `Resolve` "garbage in, known-good out" contract was written to make safe — and it makes the wrong value safe-looking instead of right.

**Fix:** give each setting its own file (e.g. `simitone-sun.ini`, `simitone-view.ini`) or, keeping one file, replace `IniConfig` with a small explicit reader/writer that parses `key=value` lines, updates only its own key, and rewrites the file preserving all other keys. The second is the smaller behavioural change: `SimitoneStrings.ParseTable` already demonstrates the line-parsing shape. At minimum, delete the false claim in the `NeighbourhoodViewIni` remarks.

**Confidence:** high — the base-class code is unambiguous and both subclasses are in scope; nothing in the tree re-reads the file between the two writes.

---

### SIM-02 · `AtomicFileCommit` promises "never a truncated file where a good one used to be"; step 4 can leave exactly that
**`Utils/AtomicFileCommit.cs:84-85`**

```csharp
File.Copy(tmpPath, path, true);   // target opened for writing in place, truncating
File.Delete(tmpPath);
```

The class contract at lines 26-27 is *"At no point is the target opened for writing in place, so a reader can never observe a truncated file."* `File.Copy(..., overwrite: true)` opens the destination `FileMode.Create` and streams into it, so the target **is** opened for writing in place. The in-flight window is the length of the copy — for a `House{NN}.iff` with two embedded PNG thumbnails that is hundreds of milliseconds to seconds on a spinning disk.

The comment at line 21-23 partly acknowledges this (*"File.Move cannot overwrite portably, hence copy-then-delete"*) but then claims the consequence is safe, which is only true for the *data*, not for the *file*: the `.bak` is intact, so nothing is lost permanently, but a reader at that instant sees a short `House{NN}.iff`. And the crash-between-copy-and-`Delete` case leaves a complete `.tmp` next to a truncated target — recoverable, but a state the file's own contract says cannot occur.

**Why it matters:** this is the single write path for home-lot saves (`SaveService.cs:158`), travel slots (`SaveService.cs:295`), bulldozes (`NeighborhoodService.cs:716`), rezone writes (`NeighborhoodService.cs:874`), the names sidecar (`NeighbourhoodManager.cs:728`) and the layout sidecar (`NeighbourhoodLayoutStore.cs:202`). A partially-observed save file is precisely the failure the class was extracted to prevent.

**Fix:** on .NET Core 3.0+ `File.Move(tmpPath, path, overwrite: true)` exists and is a same-volume rename — atomic on NTFS. Replace the copy+delete with it. If the `overwrite` overload is unavailable on the target framework, keep the current sequence but delete the "never a truncated file" claim and state the real guarantee ("the previous contents are always in `.bak`; the target may be briefly short during the commit").

**Confidence:** high on the mechanism; medium on the practical exposure window (depends on how large a lot file is in practice).

---

## S2

### SIM-03 · `UIClockPanel` has its own copy of the speed map, and it is missing the `Disabled` entry its callers rely on
**`UI/Panels/UIClockPanel.cs:37-45, 135`**

```csharp
public static Dictionary<int, int> RemapSpeed = new Dictionary<int, int>() {
    {0, 4}, {1, 1}, {3, 2}, {10, 3},        // <- no {-1, Disabled}
};
public static Dictionary<int, int> ReverseRemap = RemapSpeed.ToDictionary(x => x.Value, x => x.Key);
```

Three copies of this table now exist: `GameplaySpeed.RemapSpeed` (`TS1Constants.cs:52`), `UIDesktopUCP.RemapSpeed` (`UIDesktopUCP.cs:104`, correctly derived from the shared one) and `UIClockPanel.RemapSpeed` (hand-typed literals). The `UIDesktopUCP` remarks even say *"UIClockPanel publishes an identical table and the two are expected to agree exactly; deriving this one from the shared constants is what keeps them from drifting"* — the drift-protection is one-directional.

**Why it matters:** `UIClockPanel.Update:135` does `RemapSpeed[Math.Max(0, VM.SpeedMultiplier)]`. `Math.Max(0, -1)` maps `Disabled` to `0` → `Pause`, which happens to be the right answer here, so the missing entry is currently benign. But it is only benign by coincidence: the dictionary is `public static` and mutable, the numbers are bare literals rather than the named constants, and any future speed (`{7, …}` in the VM, a value `LotLifecycleService.BlueprintReset` could produce) makes this an unguarded `KeyNotFoundException` **per frame, in `Update`**.

**Fix:** `public static readonly Dictionary<int,int> RemapSpeed = GameplaySpeed.RemapSpeed.Where(k => k.Key != GameplaySpeed.Disabled).ToDictionary(k => k.Key, k => k.Value);` — identical to `UIDesktopUCP.cs:104`. Or better, delete `UIClockPanel`'s copy and have it reference `UIDesktopUCP.RemapSpeed` if the clock is only ever built alongside the UCP.

**Confidence:** high on the duplication; the concrete crash requires a speed value the VM does not currently produce, so the present-day impact is a maintenance trap rather than an active bug.

---

### SIM-04 · `UIDesktopUCP` re-loads the pause/speed background texture from content every frame the speed changes
**`UI/Panels/Desktop/UIDesktopUCP.cs:136-141` (mirrored in `UIClockPanel.cs:138-139`)**

```csharp
if (speed == 4) InnerBg.Texture = Content.Get().CustomUI.Get("clockinbg_pause.png").Get(GameFacade.GraphicsDevice);
else if (LastSpeed == 4) InnerBg.Texture = Content.Get().CustomUI.Get("clockinbg.png").Get(GameFacade.GraphicsDevice);
```

`Content.Get(...).Get(device)` on a custom-UI entry goes through the content pipeline each time it is asked. It is inside `if (speed != LastSpeed || disabled != LastSpeedDisabled)` so it only fires on a transition, not every frame — but the original code (`UIClockPanel`) had the same shape and the `UIDesktopUCP` copy is the commented-out original, so the *live* copy is the one that matters. Any UI element that changes speed repeatedly (a player mashing D1/D2/D3, or a script) re-resolves the texture per transition.

**Why it matters:** not a crash, but a per-transition content lookup on the UI thread for what should be two cached `Texture2D` fields. `UIClockPanel` is the worse case: it does the lookup and *assigns to `InnerBg.Texture`* without null-checking, and `Content.Get().CustomUI.Get(...)` returns null for a missing entry on some content trees — a null texture assignment there is a draw-time failure.

**Fix:** resolve both textures once in the constructor (or into `static readonly` fields beside the existing `FloorNames`) and swap the reference. This is a straight port of what `UISettingsPanel.GetApplyButtonTexture` (`UISettingsPanel.cs:422`) already does for its plates.

**Confidence:** medium — the practical cost depends on whether `Content.Get().Get` caches internally; the null-assignment risk in `UIClockPanel` is the more concrete half.

---

### SIM-05 · `UINeighbourhoodManagementPanel` row/detail reads re-read and re-parse `names.json` from disk on every selection change
**`UI/Panels/UINeighbourhoodManagementPanel.cs:1502, 1514, 1644, 1673` → `Utils/NeighbourhoodManager.cs:550-555, 567-647`**

`RowName(id)`, `RowDetail(id)` and `RefreshButtons()` each call `NeighbourhoodManager.GetName(_userDir, id)`. `GetName` → `ReadNames` → `File.Exists` + `File.ReadAllText` + a full `JsonDocument.Parse` **per call**, with no caching. `RefreshButtons` alone calls it twice (once directly, once through `RowDetail` in `DescribeSelection`), and `MakeRow` calls `RowName` + `RowDetail` per row (9 rows).

**Why it matters:** ~20 file reads and JSON parses of the same small file every time the list is refreshed — on construction, on every `GameResized()`, and after every rename/create/delete. It is correct (it will pick up an external edit) but it is a per-refresh IO storm on a UI-thread path that runs on window resize, which is exactly when the user is dragging a window edge.

**Fix:** `NeighbourhoodManager` already exposes `GetNames(userDir)` returning the whole map. Have the panel call it **once** per `RefreshList()` into a local `IReadOnlyDictionary<int,string>` and pass the name down. The class-level remark that the sidecar is "otherwise independent of the folders" means one read per refresh is already the correct freshness model.

**Confidence:** high on the call pattern; severity is S2 because `GameResized` makes it user-reachable, but it is a performance/latency bug rather than a correctness one.

---

### SIM-06 · `Program.cs` first-run graphics defaults are stamped *before* `RememberInstall` rewrites `config.ini`, contradicting the stated invariant
**`Simitone.Windows/Program.cs:249-265` vs `459-465`**

`RememberInstall`'s own remark states the contract:

> *"`GlobalSettings.Default.Save()` rewrites the whole file, so this is the only place startup saves. **The rest of the startup sequence must not start calling it: the first-run graphics defaults above rely on config.ini not being rewritten under them.**"*

But `RememberInstall` **is** called later in the same `Main`, from `Settle` (`Program.cs:471-476`) for the `SingleCandidate` and `MultipleCandidates`/hand-picked branches — and those run *after* the block at line 249 has already stamped `LightingMode`/`ComplexShaders`/`EnableTransitions`/`AntiAlias` into the in-memory `GlobalSettings.Default`. `Save()` then writes the whole in-memory object to disk, so the stamped values do survive. The bug is the reverse direction: on a **first run that finds exactly one install**, `hadConfigFile` is false, the defaults are stamped, and `Settle` persists them — fine. On a **first run with no config but a stale remembered path** the branch differs. The fragile part is that the ordering is load-bearing and undocumented at the call site, and the `else` branch at line 256-265 *mutates* `GlobalSettings.Default.LightingMode` (a side effect on a value the comment implies is read-only) without a matching `Save()`.

**Why it matters:** this is a maintenance trap, not an observed failure: the invariant is real, it is stated in one place, and it is not enforced anywhere. A future `-aa`-style flag, a second `Save()`, or a reorder of the two blocks silently changes what a first run persists.

**Fix:** make the ordering explicit and enforced — resolve the install, run the graphics-defaults block, and only then call anything that saves; or (better) have `RememberInstall` take the graphics defaults as a parameter so there is exactly one place that both stamps and persists. Add a comment at the `Settle` call sites repeating the invariant, since `Program.cs:249` is 200 lines away from the remark that states it.

**Confidence:** medium — I could not construct a concrete mis-persist without running the game; the finding is that the documented invariant is unguarded and its two halves live 200 lines apart.

---

### SIM-07 · `GameController.EnterGameMode` calls `int.Parse` on a substring of an unvalidated lot name
**`GameController.cs:85-99`**

```csharp
if (lotName.Length > 1 && lotName[0] == '!') { ... }
...
if (mode != NeighSelectionMode.Normal) screen.StartMoveIn(int.Parse(lotName.Substring(2)));
```

`lotName` is whatever the caller passed. `LoadingScreen.Update` calls `EnterGameMode("", false)` (safe), but the method is `public static` and the `!n`/`!m` prefix convention is the documented move-in protocol. `lotName == "!n"` (length 2) passes the `Length > 1` guard, sets `mode = MoveIn`, and then `Substring(2)` returns `""` → `int.Parse("")` throws `FormatException` **from inside a `GameThread.NextUpdate` callback**, i.e. on the game thread, with no handler.

**Why it matters:** an unhandled exception on the game thread is the process-death case the rest of this codebase is careful to avoid (see the `catch` discipline in `SaveService`, `NeighborhoodService`, `LotLifecycleService`). The guard checks the flag but never checks that a number follows.

**Fix:** `if (mode != NeighSelectionMode.Normal && int.TryParse(lotName.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lot))` and fall back to `NeighSelectionMode.Normal` + a log line otherwise. Also guard `lotName == null` — the current `lotName.Length` dereferences without a null check, and `InitializeLot` at `TS1GameScreen.cs:1236` *does* null-check its own copy of the same value, so the two disagree about whether null is possible.

**Confidence:** high on the defect; medium on reachability (no current caller passes `"!n"`), which is why it is S2 rather than S1.

---

### SIM-08 · `LotLifecycleService.BlueprintReset` swallows *every* exception and re-reads the file — including exceptions from the reset itself
**`UI/Services/LotLifecycleService.cs:225-261`**

```csharp
try {
    using (var file = new BinaryReader(File.OpenRead(... ".fsov")))
    { var marshal = new VMMarshal(); marshal.Deserialize(file); vm.Load(marshal); vm.Reset(); }
}
catch (Exception) {
    ...
    XMLData = File.ReadAllBytes(path),   // <- not in a try
```

Two problems in one `catch`:

1. The `catch` is bare (`catch (Exception)` with no logging), and it is reached for **any** throw in the block — including `vm.Load(marshal)` and `vm.Reset()` half way through. So a failure *after* the marshal was applied to the VM falls back to sending a `VMBlueprintRestoreCmd` on top of a VM that is already in an unknown state. The next line, `File.ReadAllBytes(path)`, is **not** in a try either, so a missing `path` throws straight out of the catch and out of `BlueprintReset`, onto the caller in `TS1GameScreen.InitializeLot` (`TS1GameScreen.cs:1275`).
2. `JobLevelFromPath` (`LotLifecycleService.cs:130-134`) is documented as *deliberately unforgiving* — `short.Parse(path.Substring(path.Length - 6, 2))` throws on a short path. That is fine as a pure function, but it is called from **inside the catch block** at line 246, so a malformed path turns a logged-and-recovered blueprint restore into an unhandled throw on the game thread.

**Why it matters:** the "reset this lot" path is reachable from the management panel and the lot control. An unlogged `catch {}` here is exactly the "hiding errors" pattern the rest of the newer code is careful to avoid, and it is the one place where the fallback is *more* dangerous than the failure it is catching.

**Fix:** narrow the `catch` to the file read + deserialize (`catch (Exception e) { SimitoneLog.Warn(...); }`), so a `vm.Load` failure propagates instead of being papered over with a second load. Wrap the fallback body in its own `try`/`catch` with a log line. Consider `JobLevelFromPath` returning `-1` on a malformed path rather than throwing, now that it is on a recovery path.

**Confidence:** high on both defects; medium on the exact blast radius without running the game.

---

## S3

### SIM-09 · `NeighbourhoodManager.ReservedNameChars` is duplicated as a `string` in the panel, and the two can drift
**`Utils/NeighbourhoodManager.cs:211` vs `UI/Panels/UINeighbourhoodManagementPanel.cs:217`**

```csharp
// NeighbourhoodManager.cs:211  (private, char[])
private static readonly char[] ReservedNameChars = { ':', '*', '?', '"', '<', '>', '|' };

// UINeighbourhoodManagementPanel.cs:217  (public const string)
public const string ReservedNameChars = ":*?\"<>|";
```

The panel's own remark says the two are "one rule: a character added to one and not the other would produce a message naming a character the button did not care about" — and then declines to enforce it. `NeighbourhoodManager`'s copy is `private`, so it *cannot* be shared without a visibility change. The tests can compare them, but nothing in production does.

**Why it matters:** this is a *stated* invariant with no mechanism. The sets are currently equal (both 7 chars, same order), which is what makes it invisible until someone adds a character to one.

**Fix:** make `NeighbourhoodManager`'s array `internal`/`public` and have the panel derive its message text from it (`new string(NeighbourhoodManager.ReservedNameChars)`), or move the single definition to a neutral holder both reference. Keep the `const` only if the *order* is part of the message, which it is not.

**Confidence:** high.

---

### SIM-10 · `SaveService.WriteTravelSlot` builds the template name with culture-sensitive `ToString()`
**`UI/Services/SaveService.cs:299`**

```csharp
+ ". The shipped House" + houseID.ToString().PadLeft(2, '0') + ".iff template was left untouched."
```

Everything else in this area uses `CultureInfo.InvariantCulture` (`TravelSave.cs:117`, `NeighbourhoodManager.cs:295/335/429`, `SaveService`'s own log line at 163). This one does not. It only affects a log line, so it is not a correctness bug for the save — but it is the single place in the file that would print a non-ASCII digit to a log a developer is expected to read, and the surrounding code establishes the opposite convention.

**Fix:** `houseID.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')`. One line.

**Confidence:** high on the inconsistency; impact is cosmetic.

---

### SIM-11 · `LotZoning.TryParse` uses culture-sensitive `short.TryParse` and `LotZoning.Format` uses culture-sensitive `int.ToString` — for data the provider reads with the *same* culture-sensitive parse
**`Utils/LotZoning.cs:160, 133`**

```csharp
if (!short.TryParse(split[0], out lot)) return false;   // current culture
return lot.ToString() + "," + ZoneToken(zone);          // current culture
```

The class remark acknowledges this is deliberate (*"short.Parse's own rules, minus the throw: NumberStyles.Integer on the current culture, so a line the provider can read is a line this can read"*). That reasoning is correct and I am **not** calling it a bug.

The trap is that it is correct *only for as long as both sides stay on the current culture*. `GetHousePath` and the house-file names use `InvariantCulture` (`TravelSave.cs:117`, `NeighbourhoodAddLot.cs:274`); if a lot number ever round-trips through a culture-sensitive `ToString`/`Parse` pair with a non-invariant culture in between (an Arabic-Indic digit locale, a `NumberFormatInfo` with a different `NegativeSign`), the zoning line written by `Format` would not be the line `TryParse` reads back, and `RezoneLot` would write a line the provider reads as residential. The two cultures are the same process-global today, so this cannot fire; it is a coupling worth naming.

**Fix:** make both invariant and note in the remark that the provider is the authority being mirrored — or leave as is and add a one-line comment saying *"this pair must stay on the same culture the provider uses; do not 'fix' one without the other"*. The former is safer.

**Confidence:** high on the observation; not an active bug.

---

### SIM-12 · `UIMainPanel.FloorNames` and `UIDesktopUCP.FloorNames` are two `public static` arrays initialised from the same five string ids
**`UI/Panels/UIMainPanel.cs:126` and `UI/Panels/Desktop/UIDesktopUCP.cs:77-84`**

Both are `public static string[]` (mutable, exposed), both are built from ids `400`–`404`, and `UIDesktopUCP`'s comment asserts *"UIMainPanel publishes an identical array from the same ids, so the desktop clock and the panel beside it can never be given different words for the same floor."* They agree today because both read the same table. But `public static string[]` means any caller can mutate either, and the two initialisers are separate.

**Why it matters:** a maintenance trap, not a bug. `UIDesktopUCP` correctly derives its *speed* map from `GameplaySpeed` (see SIM-03) but hand-duplicates its *floor names*, which is inconsistent within the same file.

**Fix:** make both `static readonly` and derive one from the other (`public static readonly string[] FloorNames = UIMainPanel.FloorNames;` or move to a shared `LivePanelStrings`-style holder, which already exists at `UI/Panels/LiveSubpanels/LivePanelStrings.cs`).

**Confidence:** high.

---

### SIM-13 · `GraphicsOptions.Defaults` and `StartupSettings` document the first-run set twice, and they have already diverged on one value
**`Utils/GraphicsOptions.cs:393-410` vs `Simitone.Windows/Program.cs:251-254`**

`GraphicsOptions.Defaults` is documented as *"The preferences Simitone stamps into a brand new `config.ini`, i.e. what a player who has changed nothing ends up with"*, and it is only reached from `GraphicsPreferences.From(null)`. The values that are actually stamped on first run live ~1000 lines away in `Program.cs`:

| value | `GraphicsOptions.Defaults` | `Program.cs` first-run stamp |
|---|---|---|
| `AntiAlias` | `StartupSettings.MinAntiAlias` (0) | `aa ? 1 : 0` (0) — agrees |
| `SurroundingLots` | `SurroundingLotModeRow.Default` (1) | **not stamped** — falls to FreeSO's ini default |
| `SunCycle` | `SunCycleSetting.DefaultEnabled` (true) | **not stamped** — `simitone.ini` default |
| `NeighbourhoodView` | `NeighbourhoodViewSetting.Default` (3D) | **not stamped** — `simitone.ini` default |

So "what an untouched install looks like" has **two** answers, and only four of the eleven values in `Defaults` are actually written by the first-run block. `StartupSettings` states the same first-run set in a *third* place (its own `DefaultLightingMode` / `DefaultComplexShaders` / `DefaultEnableTransitions` consts, which `Program.cs` reads but `Defaults` does not — `Defaults` re-states `complexShaders: true` and `enableTransitions: true` as literals).

**Why it matters:** this is the "settings defaults documented one way and coded another" case. Nothing asserts that `GraphicsOptions.Defaults` and `Program.cs`'s stamp agree, and they cover different subsets of the same concept.

**Fix:** have `Program.cs` apply the first-run defaults by writing through `GraphicsOptions`/`GraphicsPreferences` rather than assigning four `GlobalSettings` properties by hand, and have `GraphicsOptions.Defaults` be the single named source (`StartupSettings` then keeps only the *resolution* clamps, which are a different job). Failing that, add a test asserting the two sets agree field-by-field.

**Confidence:** high on the divergence; the divergence is currently benign because the un-stamped four fall to the same values by default.

---

### SIM-14 · `SimsInstallCandidate` stores `Path` un-canonicalised but its own docs say to compare with `IsSamePath`
**`Utils/SimsInstallCandidate.cs:91` vs `74-75, 99`**

`Canonical` is a *static helper*, not the value the object holds: `Path` is "stored exactly as the probe produced it", and every comparison is expected to go through `IsSamePath`. But the roster is de-duplicated by `FindByPath` → `IsSamePath` at insertion time, so the invariant "no two entries in a roster are the same directory" is enforced **only on the way in**. Any later mutation of a `candidate.Path` (it is a get-only auto-property, so only via a subclass or reflection) or any code that builds a roster by hand breaks it silently.

More concretely: `WindowsLocator.FindAllTheSims1Installations` (`WindowsLocator.cs:106-111`) builds a *second* list by filtering `probed` — re-checking `IsSimsInstallDir` on entries that were already filtered. It works, but the double filter means the "roster is deduped" property is now a property of the *caller*, not the type.

**Why it matters:** minor. A `readonly struct`-like value with a canonical form would make this unrepresentable; the current shape makes it a convention that lives in three places.

**Fix:** canonicalise once in the constructor (`Path = Canonical(path)`) and expose the original separately if the raw spelling is needed for display. That collapses `IsSamePath` to a plain string compare at the call sites and makes the dedupe a property of the value.

**Confidence:** medium — this is a design observation, not a demonstrated defect.

---

### SIM-15 · `SoundVolumeSetting.Save` writes the mute ini *after* the volume ini on every settings change, and the two pages disagree about who saves
**`UI/Panels/Options/SoundVolumeSetting.cs:143-160` and `UI/Panels/Options/UIOptionsSubpanels.cs:411-420`**

`UIAudioOptionsSubpanel.OnSettingsChanged` calls `settings.Save()` (rewrites all of `config.ini`) then `SoundVolumeSetting.Save()` (rewrites `simitone-sound.ini`) **on every slider frame**. `IniConfig.Save()` uses `FileMode.Create` on a `StreamWriter`, so each frame is a full truncate-and-rewrite of two files, and `IniConfig.Save` swallows every exception — so a persistently failing write (file locked by an antivirus scanner, read-only folder, disk full) produces **no log line at all** and the player watches a slider that appears to do nothing.

`SunCycleSetting.Save` (`SunCycleSetting.cs:164-175`) and `NeighbourhoodViewSetting.Save` (`NeighbourhoodViewSetting.cs:278-290`) have the same shape but are called once per discrete click, so the exposure is much smaller.

**Why it matters:** the swallowed write failure is the concrete part. A sidecar that cannot be written is invisible; the in-memory `cached`/`cachedMutes` still says the new value, so the row shows the player's choice for the rest of the session and it silently reverts on next launch.

**Fix:** catch in Simitone's own wrapper (it already has a `try`) and log a warning with the path — `SunCycleSetting.Save` has a `catch (Exception) { }` and `NeighbourhoodViewSetting.Save` has one too; both should call `SimitoneLog.Warn`. Optionally debounce `SoundVolumeSetting.Save` to the end of the drag.

**Confidence:** high on the swallow-and-silence; the write amplification is real but bounded.

---

### SIM-16 · `UINeighbourhoodManagementPanel.Show()` replaces the open page but never clears `_open` when the page closes itself
**`UI/Panels/UINeighbourhoodManagementPanel.cs:1234, 1268-1280`**

```csharp
private static UINeighbourhoodManagementPanel _open;

public static UINeighbourhoodManagementPanel Show() {
    if (_open != null) { UIScreen.RemoveDialog(_open); _open = null; }
    var panel = new UINeighbourhoodManagementPanel();
    _open = panel;
    UIScreen.GlobalShowDialog(panel, true);
    return panel;
}
```

`_open` is a `static` that is only ever nulled inside `Show()`. The Close button (`MakeButton(NeighbourhoodPanelModel.CloseCaption, (b) => Close())`, line 1295) and the dialog's own dismiss leave `_open` pointing at a torn-down panel. On the next `Show()`, `UIScreen.RemoveDialog(_open)` is called on a panel that is no longer in the dialog stack.

**Why it matters:** the panel holds a `UITouchScroll` with up to nine rows, each holding textures, plus a reference to `FSOEnvironment.UserDir` — so a stale `_open` is a live object graph kept alive for the process. `RemoveDialog` on an already-removed element is very likely a no-op, but the reference is a process-lifetime leak of a UI tree, and the remark at line 1231-1233 calls this field out as the thing preventing a *second* copy stacking — a guarantee that only holds for the first dismissal path.

**Fix:** add `public override void Removed() { base.Removed(); if (_open == this) _open = null; }`. That covers the Close button, the modal dismiss and a screen change alike.

**Confidence:** high that the leak exists; medium on whether `RemoveDialog` on a stale element is safe (not verified against `UIScreen`).

---

### SIM-17 · `ConfirmOnceGate` is behind `DialogService`, but the gate and the dialog field are two pieces of state that must agree
**`UI/Services/DialogService.cs:57-73, 194-212`**

`ConfirmGate.IsLatched` and `CurrentDialog` are set in different places: `TryBeginConfirm` latches the gate, `ShowModal` sets the dialog, `CloseAndRelease`/`CloseAndReleaseThen` clear both. A caller that calls `TryBeginConfirm()` and then never reaches `ShowModal` (an early `return` between the two) leaves the gate latched with `CurrentDialog == null`, and every subsequent confirm silently takes the "already confirming" branch and runs its work *without* a dialog. `UINeighbourhoodManagementPanel.AskForOpen` (`UINeighbourhoodManagementPanel.cs:1859-1867`) does `TryBeginConfirm()` then `ShowConfirm(...)` with nothing in between, so it is correct today; `TS1GameScreen.CloseAttempt` does `TryBeginConfirm()` then `ShowModal(...)` inside a `GameThread.NextUpdate` callback, which is also correct. The class is nonetheless two states where one would do.

**Why it matters:** maintenance trap. The `DialogService` remarks make a point of the ordering being load-bearing, which is right, but the "latched with no dialog" state is representable and unobservable.

**Fix:** have `TryBeginConfirm` return the claim and have `ShowModal` be the only place the dialog is recorded, with a single `Release()` that clears both unconditionally — and assert (in `DialogServiceTests`) that `HasOpenDialog == (CurrentDialog != null)` after every transition.

**Confidence:** medium — I did not find a live path that hits the bad state; this is about the shape.

---

## S4 / maintenance notes (not bugs)

* **`UIOptionsPanel.SELECTED_PAGE` is a `private static int`** (`UIOptionsPanel.cs:31, 169-170`) that survives a screen change and is written on every page switch. A per-screen field would be equivalent and would not need the static. Not a bug; flagging because it is mutable static state that outlives the screen.
* **`UIMainPanel`, `UIClockPanel`, `UIMainPanelMode` etc. carry `public static` mutable dictionaries** (`UIClockPanel.cs:37-45`, `BuyCatalogService.cs:70, 88, 94, 163, 192, 345, 358`, `UIIconCache.cs:25`, `UICatalogItem.cs:35`). `UIDesktopUCP.cs:104-109` already does this correctly with `static readonly` + derivation; the rest are `public static` and freely mutable. Converting them to `static readonly` (and, for the tables, deriving from a single owner) costs nothing and removes a class of accident.
* **`UICatalogItem.IconCache`** (`LiveSubpanels/Catalog/UICatalogItem.cs:35`) is a `public static Dictionary<uint, Texture2D>` that is **never cleared** — `ClearIconCache` exists and its only call site is commented out (`UIBuyBrowsePanel.cs:654`). Every icon the player ever opens in Buy mode is a resident `Texture2D` for the process lifetime. `UIIconCache.AvatarHeadCache` (`UI/Model/UIIconCache.cs:25`) has the same shape with no clear method at all. On a long session with many catalog pages this is a slow, unbounded GPU-memory growth. This is a real leak, listed here because it is pre-existing rather than a Wave A–U regression.
* **`NeighbourhoodManager.Delete` re-checks the folder exists twice** (`NeighbourhoodManager.cs:1030` and again implicitly via the `Directory.Delete`) and the `try { if (!Directory.Exists(path)) ... } catch { ... }` at 1028-1040 is immediately followed by a second `try` around `Directory.Delete`. Harmless; the double check is the "belt and braces" the file's own comments describe.
* **`SimitoneStrings.Substitute`** (`SimitoneStrings.cs:281-305`) reimplements `ContentStrings.GetString`'s `%s`/`%d` substitution, including its quirk of dropping a lone `%`. The comment says this is deliberate and that "a call site may be reading from TS1's tables and from Simitone's with the same arguments, and the two must not format differently." That is a real coupling to a FreeSO implementation detail with no test pinning FreeSO's behaviour — if `ContentStrings` ever changes, the two diverge silently. Consider a test that runs the same string through both.
* **`StartupSettings.ResolveAntiAlias` collapses out-of-range to "Off"** (`StartupSettings.cs:107-111`) while `ResolveLightingMode` collapses to the *default* (ultra). The two siblings use opposite policies for the same class of input. Both are defensible and both are documented; the asymmetry is worth a comment so a reader does not assume they are interchangeable.
* **`GameInstallLadder.FirstHit` accepts an empty string as a hit** and the remarks say so explicitly ("a probe that answers with an empty string has answered the question it was asked"). `WindowsLocator.FindMaxisRegistryInstallDir` guards `IsNullOrEmpty` (`WindowsLocator.cs:155`) for exactly this reason. The contract is documented and enforced at the one probe that needs it — worth keeping in mind if a probe is added.
* **`UINeighbourhoodManagementPanel.Confirm`** (line 2139-2150) builds a second, hand-rolled confirm idiom alongside `DialogService.ShowConfirm` for the delete case, because it needs a width and a Yes/No with no cancel. The two can drift (only one of them goes through the gate). Not a bug — the delete case deliberately does not take the latch — but it is the second copy of a rule the class was written to have one copy of.

---

## What I checked and found clean

Worth recording so the next audit does not repeat it:

* `AtomicFileCommit` staging order, `.bak` semantics and return contract — correct apart from SIM-02.
* `TravelSave` header validation, magic/version/family ordering, `HasMarshalPreamble` before `Deserialize`, and the `MarshalMagic` trap it guards — all correct, and the reasoning is sound.
* `LotPersistence.IsPersistableLot` / `TravelSave.IsTravelLot` — exact complements, as documented.
* `LotTravel.ShouldStashCurrentLot` / `TryRestore<T>` — correct, and the `!=` comparison is the right one.
* `TS1InstallSelection.Resolve` / `IsAlreadyConfigured` / `IsSteamInstall` / `BootstrapSourceDirectory` — the upgrade-silent argument for `IsAlreadyConfigured` holds; the `-path`-outranks-stored-preference ordering is right.
* `GamePathArgument.Normalize` — the `:`/`=` stripping and the single-trailing-slash invariant are correct for all four documented forms.
* `NeighbourhoodManager` name sanitisation — `IsValidName`/`SanitizeName` are total, idempotent, and the reserved-name pair (`.`/`..`) is handled where character-level sanitisation cannot reach it.
* `NeighbourhoodLayoutStore.IsInsideUserData` — the `root + Path.DirectorySeparatorChar` comparison correctly refuses `UserDataExtra` for `UserData`.
* `GraphicsOptions.ResolveShadowDetail` / `ResolveWaterReflections` nearest-step clamping — boundaries correct (`<=Low`, `<=Medium`, else `High`).
* `SimsInstallCandidate.Canonical` drive-root handling — `C:/` is preserved, `C:/x/` is trimmed. Correct.
* `Building with `-t:Compile -c Release`: 0 errors, no C# warnings.** No nullable or unused-symbol diagnostics anywhere in the Wave A–U code.
