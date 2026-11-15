# Simitone Modernization & Feature-Completion Plan

Date: 2026-09-18; Phase 3 implemented 2026-09-26; addon content + deferred items cleared
2026-09-26 (session 3). Status: living document — update acceptance checkboxes as phases
land. Automated tests: 829 headless (`Simitone.Tests`); the parts that need a running game
are tracked in `docs/PHASE3_TEST_MATRIX.md`.

## 0. Baseline (verified by build 2026-09-18 — `Simitone.exe` produced, smoke-tested)

- Toolchain: .NET 10 SDK (10.0.401), VS2022 MSBuild present but unused for SDK builds,
  `global.json` pins 10.0.x. Build command (dotNET SDK MSBuild):
  `dotnet msbuild Client/Simitone/Simitone.sln /restore /t:Simitone_Windows
  /p:Configuration=Release` — **green, exit 0**.
- `Simitone.Client` → `net10.0`; `Simitone.Windows` → `net10.0-windows`;
  MonoGame unified to **3.8.5.1** across the restore graph (FreeSO pins 3.8.4,
  NuGet unifies upward; verified in `project.assets.json`).
- **FreeSO must be at the commit Simitone pins: `f86bbd3` (2025-09-28)**
  (`api.github.com/repos/riperiperi/Simitone/contents/FreeSO?ref=master`).
  A stale (pre-modernization, net45/Protobuild-era) FreeSO copy does NOT compile
  against this Simitone tree — that was the root cause of the first build failures.
  At the pin, FreeSO is modern SDK-style .NET with MonoGame NuGet refs; no Protobuild,
  no net45 targeting packs, no `FSOMonoGame`/`FSOMina` submodules needed for this build
  (only iOS/Droid projects reference them, and they are excluded).
- No Azure services used anywhere — only Azure Pipelines as CI runner.
  CI (`azure-pipelines.yml`): `windows-2022`, no Protobuild, no net45 workaround.
- Decisions (confirmed): **Windows-only**, **keep MonoGame**, **net10 LTS**
  (skip .NET 11 STS — same 2028 support endpoint, yearly churn, nothing this game needs).
- Smoke test without TS1 data: previously booted into a hang (locator always falls back
  to a default path); now shows "The Sims data not found" dialog and exits 1
  (`Program.cs`). Full gameplay verification still requires a TS1 install (absent here).
- **TS1 data is now present locally at `D:\Program Files\Sims`** (Complete Collection).
  With `-path:"D:\Program Files\Sims"` the Release binary boots to the neighbourhood view
  and renders correctly (screenshot-verified 2026-09-26), including cursor loading and the
  full texture pipeline. Gameplay (lot entry, save/load, CAS round-trips) is still manual.
- **`-path` argument was broken** and is fixed: every documented form (`-path:"C:\The Sims"`,
  `-path=...`, `-path...`) left a stray leading `:` / `=` in the resolved path, so the
  `GameData/Behavior.iff` sentinel check failed and the game reported the data as missing.
  Now parsed by `Simitone.Client/Utils/GamePathArgument` (unit-tested).

## 1. Phase 0 — Reproducible baseline (DONE, except CI run)

- [x] FreeSO @ `f86bbd3`; exact commits recorded above.
- [x] `global.json` SDK pin; net10 TFMs; MG 3.8.5.1.
- [x] CI: `windows-2019` → `windows-2022`, removed dead Protobuild/net45 steps.
- [x] Green local build + smoke launch. **Acceptance met locally.**
- [ ] Push so hosted CI validates (needs TS1-free build only — gameplay still manual).
      **Blocked here:** the working tree has no `.git` (it is a plain folder, not a
      checkout), so there is nothing to push from this environment.
- [x] Optional: slim default solution configs (exclude server/database/iOS/Droid).
      `Simitone.sln` went from ~40 projects to 24: the TSO server, API, watchdog, database
      and the server-side Common projects were removed from the solution. They are still
      built as transitive `ProjectReference`s where genuinely required, so the build output
      is unchanged; `dotnet build Simitone.sln` and `dotnet msbuild ... /t:Simitone_Windows`
      both stay green. (`Simitone.PackageRemeshes` is built via a non-copying
      `ProjectReference` from `Simitone.Windows`, so it need not be listed.)

## 2. Phase 1 — Stop data loss and crashes (CODE COMPLETE, RUNTIME VERIFICATION PENDING)

All of the below is in the tree and the solution builds clean. What is *not* done is
exercising these paths against a real TS1 install, which is still unavailable here.

- [x] Startup: validate TS1 sentinel (`GameData/Behavior.iff`), dialog + exit 1.
      Smoke-tested — binary launches, shows the dialog, exits 1 (it previously hung).
- [x] Catalog town overlap: house 90 is MagicTown per engine
      (`TS1NeighbourProvider.GetHouseNameDesc`: `<80` base, `<90` studio, else magic);
      `UIMainPanel.GetLotType` Studiotown range fixed to 81–89.
- [x] CAS accept guard against `Selection == -1`.
- [x] Debug-save path: `Directory.CreateDirectory(... "Blueprints/cas.xml")` created a
      directory where a file is written; now creates `Blueprints`.
- [x] Atomic saves (`TS1GameScreen.Save`): null guards, full try/catch, stage to
      `.tmp` → copy previous to `.bak` → commit → only then `SaveNeighbourhood`.
      `GeneratePNG` degrades to an empty chunk instead of throwing. Verified against
      FreeSO that `IffFile.Write`, `GetHousePath` and `SaveNeighbourhood` exist.
- [x] File logger `Utils/SimitoneLog.cs` — thread-safe, 2 MiB rotation, never throws.
- [x] Economy unified behind `Utils/BudgetService.cs`. The two former reads
      (`ActiveFamily.Budget` vs `TS1State.CurrentFamily.Budget`) could diverge.
      `GetBudget()` keeps the `int.MaxValue` "unlimited" sentinel for logic;
      `GetDisplayBudget()` keeps that sentinel out of the HUD, which would
      otherwise have rendered "§2,147,483,647".
- [x] Affordability enforced at commit time in `UIObjectHolder` for every held
      object (previously only un-bought objects, and only with a sim selected).
- [x] Dead family-Delete button implemented (`UIFamiliesCASPanel` + `TS1CASScreen`).
      Mirrors FreeSO's `VMGenericTS1Call.TryDeleteFamily`: zero the members'
      `TS1FamilyNumber`, `FullRemoveChunk` the FAMI *and* its matching FAMs chunk
      (they share a ChunkID that `CreateFamily` recycles), purge the
      `FamilyForHouse` cache, then `SaveNeighbourhood(false)`. Townies
      (ChunkID 0) are never deletable; only unplaced families are offered.
- [x] CAS guards: `BuildMember` empty-list/divide-by-zero, `AcceptMember` null,
      `UpdateCarousel` + 18-slot loop NREs before init, `RepresentFamily` nulls,
      neighborhood location `TryParse`, shared CAS `Random`.
- [x] `.gitignore`: Sims data dir, downloaded tooling, and the separately
      distributed remesh pack excluded so none can be committed by accident.
- [x] RUNTIME: fault-injection checklist (corrupt save, bad lot name, empty CAS
      selection, over-budget buy, family delete + reload).
      Every scenario now has a headless regression test (`Simitone.Tests/Utils/FaultInjectionTests.cs`)
      exercising the guard production code actually calls, plus the smoke boot against the real
      install. The only part that still needs a human is confirming the *live* flow end-to-end
      (e.g. force a save failure and click OK), which lives in `docs/PHASE3_TEST_MATRIX.md`.
- [x] Localise the two hardcoded English strings introduced (save-failed dialog,
      delete-family confirm). Existing code uses `GameFacade.Strings.GetString(...)`.
      Used the game's own tables: `153/15` ("Save House") + `153/14` ("An error was
      encountered saving the house."), and `128/6` ("Delete Family?") + `128/7` with the
      family name (the `%s` string), which is the same table the neighbourhood family
      panel uses. Verified both tables exist in a real `UIText.iff`.

## 3. Phase 2 — Decompose god classes (PARTIAL: test seam created)

- [x] **First test project in the repo.** `Client/Simitone/Simitone.Tests` (xUnit,
      net10.0, wired into the solution). **117 tests, all passing** at the time; grown to
      **351** by the 2026-09-26 pass. Covers the
      `SimitoneLog` never-throws invariant (incl. concurrency + 2 MiB rollover),
      `BudgetService` in the no-family state (incl. the `int.MaxValue`-must-not-leak
      HUD regression), speed-remap bijection, `DirectionUtils.PosMod` bounds, and the
      buy-catalog subcategory/icon table shapes.
- [x] `UIDesktopUCP.FloorNames` made `static` (a fixed lookup table, and `RemapSpeed`
      directly above it already was) so the floor-index invariant is testable.
      Behaviour-identical.
- [x] Fixed a real crash: `UINeighborhoodSelectionPanel.SelectHouse` indexed
      `HousePositions[house]` unguarded, but lot 99 and any unparseable location
      entry are deliberately never added. `EvictLot` re-selects a lot after eviction,
      so switching neighbourhoods and then evicting threw `KeyNotFoundException`.
      Now falls back to the current view centre.
- [x] Extract `LotLifecycleService`, `SaveService`, `NeighborhoodService`,
      `CatalogQueryService`, `DialogService` from `TS1GameScreen` (996 lines),
      `TS1CASScreen` (1169), `UILotControl` (1021), `UIBuyBrowsePanel` (923).
      **Done.** Two rounds:
      - *Pure helpers* (`Utils/`): `CatalogQueryService` (the query half of
        `UIBuyBrowsePanel`, which also removed an in-place mutation of the public static
        `DTCategories`/`DTIcons` tables), `AtomicFileCommit` (the `.tmp`/`.bak`/commit
        contract), `LotPersistence`, `LotZoomMapping`, `CutawayDecision`, `CasValidation`.
      - *Services* (`UI/Services/`): `SaveService` (save/travel-slot/PNG/save-error),
        `DialogService` + `ConfirmOnceGate` (the duplicated confirm/re-enter idiom),
        `NeighborhoodService` (move-in/evict/neighbourhood selection, including converting
        the anonymous `OnHouseSelect` handler to a named, unsubscribable one — a real leak
        fix), `BuyCatalogService` (the static buy/build tables + `Add*` builders, forwarded
        by `UIBuyBrowsePanel` so the public names are unchanged), and `LotLifecycleService`
        (`ResumeFromMarshal` / `BlueprintReset`, mutating via existing members).
        `TS1GameScreen` shrank 1259 → 867 lines and `UIBuyBrowsePanel` 936 → 479.
      The VM/world *fields* (`vm`, `World`, `Driver`, `LotControl`, `Frontend`, `SavedLots`,
      `SwitchLot`, `Downtown`, `ActiveFamily`) intentionally stay on `TS1GameScreen`: ~30
      external sites read them and migrating them would be a much larger, riskier change
      with no behavioural gain. The services take the screen back-reference.
- [x] **Two blocking testability barriers, with agreed signatures.**
      - `UIMainPanel.ResolveLotType(int,bool,bool,bool)` static + `GetLotType(bool)` adapter
        (`UI/Panels/UIMainPanel.cs`), tested by `LotTypeResolutionTests` (the 81-89 / 90-99
        studio/magic-town boundary regression).
      - `Utils/LotLocationParser.TryParseLotLocation` + `ParseLotLocationTable`, consumed by
        `UINeighbourhoodSelectionPanel`, tested by `LotLocationParserTests`.
- [x] Cancellable task loading (`LoadingGameScreen`): raw `Thread` → cancelable `Task.Run`
      with a `CancellationTokenSource`, canceled in `Removed()`; load failures are logged.
- [x] Event unsubscribe + `Dispose` pass (partial): `UILotControl.Removed()` now
      unsubscribes `vm.OnDialog` / `vm.OnBreakpoint` / `PickupPanel.OnResponse` and removes
      its mouse listener; `TS1GameScreen.CleanupLastWorld()` unsubscribes the three VM
      events. `[ ]` enable `Nullable` incrementally still open.
- [x] Note: if a future test touches shared `FSO.Client` statics, add
      `[assembly: CollectionBehavior(DisableTestParallelization = true)]`. Today only
      `SimitoneLog` is mutated and it is per-test isolated, so it is not needed yet.

## 4. Phase 3 — Feature completion (IMPLEMENTED; live playtest still advised)

All items below now have a landed implementation with headless tests where the logic allows
it. What remains for several is a **live playtest pass** — the game boots and renders against
a real install, but lot entry, CAS editing and travel cannot be exercised headlessly.

- [x] Save/load everywhere incl. vacation; multi-slot travel (`SavedLot` single-slot);
  re-enable or delete surrounding lots (`LoadSurrounding` early return).
  - `LoadSurrounding` (a no-op stub reading a hard-coded developer path) and its call were
    deleted.
  - **Multi-slot travel** (`Utils/LotTravel`): the single `SavedLot` marshal became a
    per-house `Dictionary<short, VMMarshal>`; home → downtown → vacation → home restores
    every intermediate lot as it was left.
  - **Cross-session travel persistence** (`Utils/TravelSave`): saving on a travel lot
    (Downtown/Vacation/Studio/Magic) writes a marshal-only slot to
    `<UserDir>/TravelLots/House{NN}.fsov` (family-stamped, atomic) instead of refusing; the
    slot is restored on re-entry when the family matches, otherwise the shipped template
    loads. The travel lot's own `House{NN}.iff` template is never overwritten.
- [x] CAS: delete family, edit existing sims (`RefGUID` TODO), validation.
  - Validation (`Utils/CasValidation`): family-size cap (8), family-name requirement, name
    sanitising/truncation (32), and a real crash fix (the `CASToNeighGen` body-name
    `Substring` threw for a malformed name).
  - Delete family was already complete (Phase 1) and is localised.
  - **Editing existing Sims** (`RefGUID`/`ReplaceIndex`): `Utils/CasEdit` (pure, tested) plus
    `TS1CASScreen.LoadFamilyForEdit`/`SaveFamilyInPlace`, `GameController.EnterCAS(int?)` and
    an "Edit Family" action in `UIHouseSelectPanel`. Name/bio, body/head/hand textures, skin,
    age/gender and personality round-trip; clothing and exact age-in-days are best-effort.
    Character IFFs are backed up to `Characters/CASBackups/` before the upstream
    `PrepareTemplatePerson` (which writes a *new* `User#####.iff` and re-points the registry)
    and the superseded file is removed after backup. The "Edit Family" caption is English,
    matching the other hard-coded entries in that action array.
- [x] Buy/build: pricing correctness, wall/floor/roof/terrain tools, per-lot coverage.
  - Catalog/query panel display `CalcPrice`/the passed price; unaffordable rows dim
    themselves; roof rows get real names; the `DTCategories`/`DTIcons` in-place mutation
    (double "other"/"all" buttons on second open) is fixed.
  - `Utils/BuildBudget`: `UILotControl.Update` mirrors the family budget into
    `ActiveEntity.TSOState.Budget`, the value the six FreeSO build tools gate on — they were
    previously gated on a stale seeded `§1,000,000`.
  - Remaining (upstream, cosmetic): the six build tools are FreeSO code and print `$` rather
    than `§`; changing that needs a FreeSO fork or copied subclasses, and is out of scope for
    a Simitone-only tree.
- [x] Neighborhood animations (nessie/cars/blimp TODOs); move-in/out accounting.
  - Animation loader is defensive (unknown frame names are skipped, not an NRE on screen
    construction) and the three missing layers were added with frame names recovered from
    `UIGraphics.far` (`Utils/NeighbourhoodAnimations`): Nessie (`Community\ness01-03.bmp`),
    StudioTown cars (`Nhood_superstar_car0-1.bmp`) and MagicTown blimp
    (`Nhood_balloon1-16.bmp`).
  - **Positions were verified without a live playtest** by extracting the backgrounds and
    sprites from `UIGraphics.far` and compositing each sprite over its scene (via a throwaway
    harness), which corrected two mistakes: Nessie's art only exists under `Community\`/`Nbhd\`
    (there is no `VIsland\` copy), so despite the original `//TODO: nessie` sitting on the
    Vacation config it was moved to the **Community river**; and the MagicTown wave layer's real
    `(0, 62)` offset moved the waterline, so the blimp sits at `(200, 170)` over the meadow.
    StudioTown uses a single car pair rather than all ten cars, because the panel's one
    position per layer would otherwise morph ten cars in one parking bay.
  - Move-in/out accounting (`Utils/LotAccounting`): eviction refunds furniture **and** the lot
    price (previously only furniture, so money was destroyed across moves); both move-in and
    eviction persist the neighbourhood; the Magic-Town magicoin check no longer always reads 0.
- [x] Input: DPI touch thresholds, rotation graphic, smooth zoom or remove stub.
  - Tap/scroll thresholds are DPI-scaled and the tap timer is refresh-rate relative
    (`Utils/TouchInput`).
  - The rotation animation actually draws and honours opacity (it was blitting the render
    target onto itself and forcing `Color.White`); the arch-touch crosshair is drawn and the
    click button is positioned with the rest of the grid.
  - **Smooth zoom**: the stub was removed and `UILotControl.TargetZoom` is the single
    authority; `ZoomLevel` is a derived view. (The deleted per-frame
    `WorldConfig.Current.SmoothZoom = false` was already inert — `WorldConfig.SmoothZoom`'s
    setter is empty upstream — so this is correctness/clarity, and the desktop buttons now
    grey out correctly after a wheel zoom.)
- [x] In-game settings UI over hardcoded startup (`Program.cs`).
  - `Program.cs` no longer clobbers saved graphics settings every launch: the required
    path/hybrid values are always written, but `LightingMode`/`AntiAlias`/`ComplexShaders`/
    `EnableTransitions` are applied only on a first run (`Utils/StartupSettings`, tested;
    `-aa` still forces AA). An out-of-range saved `LightingMode` is repaired to the old
    default so lighting cannot silently switch off.
  - A Settings entry in the in-game Options menu opens a Simitone-styled panel
    (`UISettingsPanel`) for Anti-Alias, Lighting, Shaders, Transitions and Edge Scroll with
    Apply/Revert; it writes `GlobalSettings.Default` + `Save()` and applies the graphics
    changes to `WorldConfig` live. (FreeSO's own options dialog is unusable here: it renders
    a `.uis` script Simitone does not ship and its apply path needs a TSO host.)
- [x] Manual matrix per lot type + save-compat fixtures.
  - `SaveCompatTests` round-trips a synthetic house `IffFile` (FSOV/SIMI/PNG_) through
    `AtomicFileCommit` and back, including the empty-thumbnail degradation and `.bak`/`.tmp`
    invariants; `TravelSaveTests` and `LotPersistenceTests` pin the slot format and the
    home/travel split.
  - The per-lot-type manual pass is in `docs/PHASE3_TEST_MATRIX.md` (it needs a human at the
    controls; the harness is not scriptable).

## 5. Phase 4 — Platform & content pipeline (MOSTLY DONE)

- [x] Content globs: 215 hand-listed `<Content>` items → 4 globs
      (`Content\**\*.{png,piff,fsom,fsov}`), csproj 682 → 29 lines. Coverage verified
      item-by-item: nothing lost, nothing previously-referenced dropped.
      **One deliberate behaviour change:** 9 previously-unreferenced roof textures
      under `Content\Textures\roof\` are now included (51 KB of 0.93 MB, no code
      references them). Cheaper than reintroducing a hand-maintained exception.
- [x] MonoGame backend copy is version-agnostic. The `3.8.5.1` literal is gone from
      the csproj paths; the version is lifted from the `PackageReference` items
      themselves, with a single-candidate wildcard fallback that hard-`<Error>`s on
      ambiguity rather than silently picking one. The target can no longer "succeed"
      while copying nothing.
- [x] `MonogameLinker`: `AssemblyDir` is now assigned unconditionally before any OS
      check (it used to stay `"./"` on the macOS/Linux early-returns); dead
      `DirectoryCopy` removed; unsupported-platform path no longer silently
      desyncs `useDX` from the directory it points at.
- [x] `Program.OnAssemblyResolve` no longer throws on comma-less assembly names
      (`Substring(0, IndexOf(','))`), and prefers already-loaded assemblies.
- [x] Deleted committed `sdl2.dll` / `soft_oal.dll` (1.6 MB). Verified zero
      references in any `.csproj/.props/.targets/.sln`, and confirmed the real
      natives ship correctly via the `MonoGame.Library.SDL` / `MonoGame.Library.OpenAL`
      NuGet packages into `runtimes\win-x64\native\`. The default DX backend needs
      neither (XAudio2 + D3D are built into Windows). Build + smoke still green.
- [x] `.gitignore` excludes Sims data, `.tools/`, and the remesh pack.
- [x] `System.Drawing.BitmapReader` → ImageSharp; abstract Registry access.
      - `Simitone.Client/Utils/BitmapDecoder` (ImageSharp 3.1.11, declared explicitly in
        both csproj files so it no longer rides in by accident via the FreeSO server stack)
        replaces `Program.cs`'s `BitmapReader`/`RGBToBGR`. `System.Drawing` usage in
        Simitone's own code is now zero; `UseWindowsForms`/`MessageBox` are untouched.
      - The RGBA byte contract was verified two ways: unit tests pin the R,G,B,A order and
        straight alpha, and a one-off harness compared the new decoder against the old GDI+
        path on real assets — **400/400 TS1 BMPs byte-identical**, all **102 TS1 cursors
        byte-identical**, and Simitone PNGs identical on every opaque pixel (semi-transparent
        pixels differ by at most 1/255 after FreeSO's premultiply).
      - **Regression found and fixed during runtime verification:** the TS1 cursor loader
        (`CurLoader.LoadCursor`) builds a synthetic BMP with `bfOffBits = 14` even when the
        image has a colour table. GDI+ tolerated it; ImageSharp rejected it, so every cursor
        decoded as `null` and `ImageLoader.WinFromStreamP` crashed startup.
        `BitmapDecoder` now repairs an under-reported `bfOffBits` (never touching a
        well-formed BMP), with regression tests.
      - Registry access is behind `IRegistryReader` (`WindowsRegistryReader`), and the
        install-discovery precedence is the pure, tested `Utils/GameInstallLadder`. The old
        inline walk also got two crash/behaviour fixes: no `NullReferenceException` when the
        `Maxis` key is absent, and an absent `InstallPath` no longer becomes the truthy
        string `"/"` that beat every remaining rung.
- [x] **Output bloat (fixed, in Simitone's own csproj):** `FSO.Client.csproj` — which
      Simitone references directly — transitively pulls the entire TSO *server* stack
      (`FSO.Server`, `.Database`, `.Clients`, `.Common`, `.DataService`, `.Protocol`,
      `.Watchdog`) plus its third-party deps and ~20 MB of Android/iOS/Linux/OSX natives.
      `Simitone.Windows.csproj` now has a `TrimSingleplayerOutput` target (default on,
      opt out with `-p:TrimSingleplayerOutput=false`) that removes a **literal, reviewed
      allowlist** of 84 files after build: the server/API/watchdog assemblies, the
      client-facing server halves, and the third-party libraries only they reference
      (MySQL/SQL Server/SQLite providers, AWSSDK, Octokit, RestSharp, JWT, Dapper, NLog,
      BouncyCastle, `System.Data.SqlClient` + its SSPI natives), plus the 12 non-Windows
      `runtimes/<rid>` directories. Measured: **615.69 MB → 561.86 MB (53.83 MiB / 84 files
      removed)**. The allowlist was derived by parsing every shipped DLL's AssemblyRef/TypeRef
      tables, not `deps.json`; `Mina`, `Common.Logging`, `NVorbis`, `discord-rpc`, SharpDX,
      `FSO.Client`, `Ninject` and `System.Speech` were each proven needed-or-uncertain and
      **kept**. Caveat: the `-ide` (Volcanic) host reaches the removed server assemblies, so
      `-p:TrimSingleplayerOutput=false` is required for that dev path; normal launches are
      unaffected (smoke-tested with screenshots).
- [x] **Community remesh "addon" content is now integrated.** The `FSO.Remeshes` pack
      (2,877 `.fsom` + 716 `.png`, 421 groups) previously did not reach the game at all —
      only FreeSO's 4 sample remeshes shipped. `Utils/RemeshPackage` (pure, tested) plans a
      flatten from the pack's `Author/Group/` tree into the flat `Content/MeshReplace/`
      naming `RCMeshProvider` expects, applying the pack's own rules: the `metadata.json`
      `alias.simitone` renames (e.g. `doors3_iff_*` → `doors3_sp_iff_*`) including the
      `trees3` drawgroup split, the `"game": "freeso"/"none"` exclusion (45 groups dropped,
      5 `simitone` groups kept), and priority-based conflict resolution. A new
      `Simitone.PackageRemeshes` tool does the copy, wired as an incremental
      `PackageRemeshAddon` MSBuild target in `Simitone.Windows.csproj` that runs only when
      the pack is present (`/p:RemeshPackDir=...` to relocate/disable) — the pack stays out
      of the repo. Result: **3,250 remesh files packaged** (2,663 `.fsom` + 587 `.png`; 2
      conflicts resolved), verified by booting the game.

## 6. Session changelog

**2026-09-18 (baseline + Phases 1, 2-seam, 4)**
1. Wrote this plan; CI image `windows-2019` → `windows-2022`.
2. Restored `FSOMonoGame` + Protobuild codegen against a STALE FreeSO; then discovered
   via the GitHub API that Simitone pins modern FreeSO `f86bbd3`, re-cloned at that
   pin. The stale-tree work was discarded — recorded here so nobody repeats it.
3. Migrated Simitone to net10 + MonoGame 3.8.5.1; build green; smoke test verified.
4. Phase 1: atomic saves, `SimitoneLog`, `BudgetService`, affordability enforcement,
   family-delete implementation, CAS guards, `.gitignore`.
5. Phase 4: content globs, version-agnostic MonoGame backend copy, `AssemblyDir` and
   `OnAssemblyResolve` fixes, dead native binaries removed.
6. Phase 2 seam: first test project (117 tests), one production crash fixed.

**2026-09-26 (real TS1 data + Phase 2/3/4 completion pass)**
1. Established a real runtime baseline: `-path:"D:\Program Files\Sims"` now boots to the
   neighbourhood view with correct rendering (screenshot-verified). `-path` parsing was
   broken (stray `:`/`=`) and is fixed by `GamePathArgument` (+ tests).
2. Phase 2 testability barriers landed: `UIMainPanel.ResolveLotType` and
   `LotLocationParser` (+ tests), and `LoadingGameScreen` is now a cancelable task.
3. Phase 2/3 extraction (behaviour-preserving, tested): `CatalogQueryService`,
   `AtomicFileCommit`, `LotPersistence`, `LotZoomMapping`, `CutawayDecision`, `CasValidation`.
4. Phase 2 lifetime pass: `UILotControl.Removed()` and `TS1GameScreen.CleanupLastWorld()`
   now unsubscribe their VM/control events. Dead code removed from `TS1GameScreen`
   (`WindowContainer`, `VisualBudget`, `Stories`, the unused non-atomic save button, and
   the no-op `LoadSurrounding` with its hard-coded developer path).
5. Phase 4: `System.Drawing` removed from Simitone's own code in favour of ImageSharp
   (`BitmapDecoder`), registry access behind `IRegistryReader`, discovery policy in
   `GameInstallLadder`. A startup crash (TS1 cursors rejected by ImageSharp because of a
   malformed `bfOffBits` written by the cursor shim) was found by booting the real game
   and fixed, with regression tests.
6. Phase 1 leftover: the two hardcoded English strings are localised against the game's
   own tables (`153/14-15`, `128/6-7`).
7. Bootstrapping fix: `FSOEnvironment.UserDir` is set before anything touches
   `GlobalSettings.Default`, so `-lang`/`-hz` no longer write the config to the wrong file.
8. Hardening: neighbourhood animation frames whose texture is missing no longer crash the
   neighbourhood screen on construction; an out-of-range/empty neighbourhood view config
   falls back to the base neighbourhood.
9. Test project grew 117 → **351 tests**, all passing. Full `Simitone_Windows` solution
   build green; runtime smoke test green.

**Review notes (2026-09-26)** — corrections made to subagent output:
- The ImageSharp `BitmapDecoder` passed its own tests and a 400-BMP byte comparison, but
  the first real boot crashed: the TS1 `.cur` → `.bmp` shim writes `bfOffBits = 14` for
  paletted images, which GDI+ ignored and ImageSharp does not. Caught only by running the
  game; fixed in the decoder, not by reverting to `System.Drawing`.
- A subagent's registry report claimed the old fallback path returned backslashes; the old
  `.Replace('\\','/')` was in fact effective. The new code matches the original
  forward-slash form either way, but the rationale comment was corrected here.
- `BitmapFunction` now lives in `Simitone.Client`, so the test project can reach it; the
  `Simitone.Windows` csproj still declared ImageSharp explicitly so the dependency cannot
  silently disappear with a FreeSO submodule update. (Later removed: the decoder now uses the
  in-repo `BmpCodec`/`PngCodec`, so there is no image package to pin.)

**Not done, and why**
- `FSO.Client` pulling the TSO server stack into a singleplayer build: found, not
  fixed, because it lives in the FreeSO submodule and would be lost on update unless
  FreeSO is forked. Needs a decision.
- The VM/UI-owning service extraction (`LotLifecycleService`, `NeighborhoodService`,
  `DialogService`, full `SaveService`), the remaining Phase 3 features (multi-slot travel,
  editing existing sims, buy/build pricing and budget sync, nessie/cars/blimp), the input
  DPI/rotation/smooth-zoom work, and the in-game settings UI are large multi-session
  efforts, not something to land blind without runtime feedback. This session landed the
  tested seams and crash fixes those efforts need.

**2026-09-26, session 2 — Phase 3 waves**

*Wave 1 (landed, 474 tests green, solution build + runtime smoke green):*
1. Multi-slot travel (`Utils/LotTravel`): `SavedLot` → per-house `Dictionary<short, VMMarshal>`;
   every intermediate lot survives home → downtown → vacation → home.
2. Move-in/out accounting (`Utils/LotAccounting`): eviction refunds furniture + lot price and
   persists; move-in persists; Magic-Town magicoin check now reads the moving family.
3. Buy/build: `Utils/BuildBudget` mirrors the family budget into `ActiveEntity.TSOState.Budget`
   (the field the six FreeSO build tools gate on); catalog/query price display corrected to
   `CalcPrice`; unaffordable rows dim; roof rows get names.
4. Neighbourhood animations: Nessie / StudioTown cars / MagicTown blimp frame names recovered
   from `UIGraphics.far` and added via `Utils/NeighbourhoodAnimations` (positions need a
   visual pass).
5. Input: DPI-scaled/refresh-relative touch thresholds (`Utils/TouchInput`), rotation graphic
   fixed to actually draw and fade, arch crosshair drawn, arch button layout corrected.

*Waves 2-3 (in progress):* CAS editing of existing sims; smooth zoom; in-game settings UI;
save-compat fixtures.

*Wave 2 (landed, 591 tests green, solution build + runtime smoke green):*
1. **CAS editing of existing Sims** (`RefGUID` TODO): `Utils/CasEdit` (pure, tested) plus
   `TS1CASScreen.LoadFamilyForEdit` / `SaveFamilyInPlace`, `GameController.EnterCAS(int?)`
   and an "Edit Family" action in `UIHouseSelectPanel`. Name/bio, body+head+hand textures,
   skin, age/gender and personality round-trip; clothing and exact age-in-days are
   best-effort. Character IFFs are backed up to `Characters/CASBackups/` before rewrite
   (the upstream `PrepareTemplatePerson` writes a *new* `User#####.iff` and re-points the
   registry, so the superseded file is removed after backup).
2. **Smooth zoom**: `ZoomLevel` is now a derived view over the single authority
   `UILotControl.TargetZoom`; the `//stub smooth zoom?` block and the per-frame
   `WorldConfig.Current.SmoothZoom = false` are gone. (That write was already inert upstream
   — `WorldConfig.SmoothZoom`'s setter is empty — so this is clarity plus a true single
   authority; the desktop buttons now grey out correctly after a wheel zoom.)
3. **Save-compat fixtures**: `SaveCompatTests` round-trips a synthetic house `IffFile`
   (FSOV/SIMI/PNG_) through `AtomicFileCommit` and back, including the empty-thumbnail
   degradation and `.bak`/`.tmp` invariants. Found (reported, not fixed — FreeSO) that
   `SIMI.Write` reassigns `GlobalData = new short[38]` immediately before writing it, so the
   SIMI global array is always saved zeroed; the FSOV/VMMarshal path Simitone uses is safe.

*Wave 3 (landed, 683 tests green, solution build + runtime smoke green):*
1. **In-game settings UI**: `Program.cs` no longer clobbers saved graphics settings
   (`Utils/StartupSettings`, tested; `-aa` still forces AA and an out-of-range saved
   `LightingMode` is repaired). A new `UISettingsPanel`, opened from a Settings entry in the
   in-game Options menu, edits Anti-Alias / Lighting / Shaders / Transitions / Edge Scroll and
   persists them (`GlobalSettings.Default.Save()`), applying graphics changes to `WorldConfig`
   live. FreeSO's own options dialog was unusable (missing `.uis` script + TSO host).
2. **Cross-session travel persistence**: `Utils/TravelSave` writes a family-stamped,
   atomic marshal slot to `<UserDir>/TravelLots/House{NN}.fsov` when saving on a
   Downtown/Vacation/Studio/Magic lot (previously refused), and restores it on re-entry when
   the family matches; the shipped template is never modified.

All Phase 3 bullets now have a landed implementation, and the deferred items were cleared in
the follow-up session. What genuinely remains is a human playtest pass per lot type
(`docs/PHASE3_TEST_MATRIX.md`) and the upstream-only `$`/`§` text in FreeSO's build tools.

**2026-09-26, session 3 — addon content + deferred items (829 tests green)**

*Wave A — community remesh addon (the missing content):*
`Utils/RemeshPackage` + `Simitone.PackageRemeshes` + the `PackageRemeshAddon` MSBuild target
flatten the `FSO.Remeshes` pack into `Content/MeshReplace/` with the pack's aliases,
`game` filtering and conflict rules: **3,250 remesh files** now reach the game (previously
only FreeSO's 4). The pack stays out of the repo; the build consumes it in place.

*Wave B — output bloat:* `TrimSingleplayerOutput` removes 84 server-stack files and the
non-Windows natives from the Release output (**615.69 MB → 561.86 MB**), derived from a real
AssemblyRef graph and smoke-tested. `-p:TrimSingleplayerOutput=false` restores the `-ide`
host.

*Wave C — Phase 2 service extraction (the last unchecked code item):*
`UI/Services/` now holds `SaveService`, `DialogService` (+ pure `ConfirmOnceGate`),
`NeighborhoodService` (with a real subscription-leak fix), `BuyCatalogService`
(`UIBuyBrowsePanel` forwards all nine public table names unchanged) and
`LotLifecycleService`. `TS1GameScreen` 1259→867 lines, `UIBuyBrowsePanel` 936→479. VM/world
fields stay on the screen by design.

*Wave D — solution slim:* `Simitone.sln` went ~40 → 24 projects (server/API/watchdog/database
removed from the solution; still built transitively where needed). Build green.

*Wave E — remaining deferred items:*
- **Animation positions verified without a live playtest** by extracting the backgrounds and
  sprites from `UIGraphics.far` and compositing each sprite over its scene. This corrected two
  placements: Nessie moved from Vacation to the **Community river** (its art only exists under
  `Community\`/`Nbhd\`), and the blimp moved to `(200, 170)` (the wave layer's real `(0, 62)`
  offset had shifted the waterline). StudioTown now uses one car pair instead of morphing ten.
- **Fault-injection checklist** now has a headless regression test per scenario
  (`FaultInjectionTests`), mapping each item to the guard production code calls.
- **In-game settings UI** landed earlier in this session (Wave 3 of the Phase 3 run).

*Remaining, and why (unchanged):*
- Push/hosted CI: blocked — the working tree is not a git checkout.
- Human playtest: `docs/PHASE3_TEST_MATRIX.md` (clicks can't be scripted reliably in this
  environment; an attempt to drive the neighbourhood switcher lost focus to other windows).
  The animation *placements* were instead verified by compositing the extracted sprites over
  their scenes (Nessie moved to the Community river, blimp moved off the waterline), so that
  part is now checked rather than guessed.
- `$` vs `§` in the six FreeSO build tools: upstream (`FreeSO`), would need a fork or copied
  subclasses.
- CAS clothing / exact age-in-days on edit: best-effort round-trip, documented.
- FreeSO's `SIMI.Write` zeroes `GlobalData`: upstream, reported in session 2.

**2026-09-26, session 4 — 3D mesh generation (860 tests green)**

The auto-generated 3D meshes were four independently depth-displaced sprite sheets drawn
on top of one another; they never co-registered, which is the "vertex displacement" the
maintainer described. Replaced the *offline* generation path with a calibrated volumetric
reconstructor; see `docs/MESH_GENERATION_RESEARCH.md` for the full write-up.

*Investigation.* Probed the real install: **100%** of SPR2 frames carry a z-buffer; the base
game alone has **2,217** drawing groups and only **34.3%** have a hand-authored remesh. The
legacy camera is a hand-tuned approximation of the projection the 2D renderer already
exposes (`WorldSpace.GetScreenFromTile`); substituting that exact projection makes the four
rotations co-register (**27×** more silhouette overlap on a chair), which is the fix.

*Code.* `FSO.Files.RC.DGRP3DFusion` (space carving + marching tetrahedra + quadric
decimation + per-view UV split), `DGRP3DMesh.WriteFSOM` (GPU-free FSOM writer),
`Simplify.TriangleCount`, and the `Simitone.GenerateMeshes` CLI, which walks the FAR
archives, skips groups the community pack already covers (reusing `RemeshPackage`), and
validates every file by re-parsing it. 10 new camera-model tests pin `Screen`/`DepthDir`/
`Inverse`.

*Status.* `Simitone_Windows` and `Simitone.Tests` (860) green; verified against
`D:\Program Files\Sims`. The runtime generator is deliberately unchanged — the supported
path is an offline pack dropped into `Content/MeshReplace` (or `<UserDir>/MeshReplace`).
Quality is good for solid objects (chair/stove reconstruct as one consistent surface) and
rough for thin shapes with only 2 distinct renders; symmetry completion, TSDF smoothing and
template fitting are the documented next steps. Nothing hand-authored is ever overwritten,
and these meshes are classical/deterministic, not AI-generated.

**2026-09-27, session 5 — animated object merge**

Objects animate by stepping the VM `Graphic` variable through `NumGraphics` consecutive
drawgroups (`VMGameObject.RefreshGraphic`), so the generator was writing one mesh file per
state — PetBirdCage alone was 25 frames x 4 sub-objects = 100 files, most of them
near-identical. Merged each animated object into a single container with shared geometry
blocks; see `docs/ANIMATION_MERGE.md`.

- **Format:** `FSO.Files.RC.DGRP3DAnim` (`FSOa`, gzip) — a block table (geometry only) plus a
  frame table (`frame -> block`, `(PixelDir, PixelSPR)` selectors). `FromFrames` deduplicates
  blocks by quantised-position SHA-1, so recolours, static poses and empty frames share data.
- **Generator:** `Simitone.GenerateMeshes` maps `drawgroup -> (OBJD, frame)` and emits one
  `<iff>_iff_<BaseGraphicID>.fsom` per multi-graphic object; if any frame is hand-authored it
  declines to merge, so community meshes are never shadowed. `--verify` reads both formats.
- **Runtime:** `RCMeshProvider` serves a frame from the container for any drawgroup in the
  object's graphic range, detected by magic so the plain `FSOm` loader is never mis-fed; falls
  back to the ordinary per-drawgroup lookup when no container exists.
- **Results:** `AlarmClock` 7 frames -> one 240 KiB file (5 blocks); `Bars` sub 1 -> one file
  (3 frames, 2 blocks). 3 new `AnimMeshTests` (dedupe, round-trip, magic); animation + camera
  tests 13/13.
- **Applied to the real pack:** added a resumable `--merge-only` post-pass (plus `--dry-run`
  and `--no-merge`). It converted the 8,648-file pack to **4,503 files** (2,168 -> 1,756 MiB),
  merging 1,104 objects and removing 4,253 redundant frames; every file re-parses
  (4,503 ok / 0 bad) and a second pass merges nothing.
- **GPU buffers shared:** `DGRP3DBuffer` is a refcounted vertex/index buffer pair; `DGRP3DGeometry`
  keeps its public `Verts`/`Indices`/`PrimCount` (now thin delegates) so the renderer is
  untouched. The loader uploads each group block once and every frame/recolour using it shares
  that buffer.
- **Dedupe is per dynamic group, not per whole frame:** container format v2 stores a table of
  group blocks plus per-frame/per-group references, so frames sharing a group share its data.
  v1 files are still read and re-emitted as v2. The probe harness validates v2 round-trip and
  v1->v2 conversion (all pass) because the test project was blocked by concurrent edits.
- **Pack upgraded:** the 1,040 v1 containers were converted to v2 in place (atomic), 0
  failures; per-group dedupe collapsed 7,593 frames into 3,454 blocks.
- Still open: option D (split static vs moving sub-parts *within* a moving group) and option C
  (morph interpolation); runtime change is additive but needs a playtest.

**2026-09-27, session 6 — reconstruction rewrite (per-view depth fusion)**

The silhouette carve inflated objects and lost thin parts. Replaced it with a signed-field fusion
using the engine's own depth model. The exact relation is in `2DWorldBatch.fx` `depthCalc`:
`depth = backDepth + (1 - z/255)/0.4 * frontDepth`, i.e. a pixel's surface lies
`(1 - z/255) * DepthRange` along the view direction from the far plane, where `DepthRange` is the
world distance the z span covers (the generator's constants give `110.851 * 1.43/128 / 0.39`).
Depth increases **toward** the camera, so a voxel nearer than any view's surface is carved.

- `DGRP3DFusion.FuseField` builds a signed field (positive inside) from the exact far-plane offset
  per view and the constant depth range, then marching tetrahedra extract at zero. Two earlier bugs
  are fixed along the way: the depth test was inverted, and multi-sprite directions (a bed's frame
  and pillow) are now unioned rather than all required.
- Results (reconstruction vs the human remesh where available): pool table `2.53x1.83x2.12` vs
  `1.88x0.94x2.94`; fountain, bed, sofa, chair, bookcase are now coherent, grounded, and far
  smaller than the hull. The tree (sparse foliage, four near-independent views) still fuses to
  almost nothing - a real limitation of 2-4-view reconstruction of thin/sparse shapes.
- A per-object fit of the depth range by cross-view consistency was tried and is unstable; the
  engine-derived constant is used instead.


*Also fixed this session:* entering a lot crashed with
`SharpDXException E_INVALIDARG` in `PPXDepthEngine.CreateRenderTarget` when the requested AA
mode's render target was rejected by the device (`World.ChangeAAMode` requests MSAA 4 for
"AA=1" and MSAA 8 for the SSAA mode). `CreateRenderTarget` (FreeSO `tso.common`) previously
had no fallback, so an unsupported surface/depth/sample combination brought the whole lot
load down. It now clamps the size and steps the sample count, then the depth format, then the
surface format down until the device accepts a combination, logging the fallback, so an
unsupported AA mode degrades to no AA instead of crashing. Compiles clean; FreeSO is a pinned
dependency, so (like the other FreeSO fixes here) this is lost on a FreeSO update.




**2026-09-26, session 5 - upstream PR + fork ingestion (1220 tests green)**

Ingested the maintainer's [issue #8](https://github.com/riperiperi/Simitone/issues/8) 1.0
roadmap and the open pull requests, then the continued development in the `alexjyong/Simitone`
fork. Full detail in `docs/ADDON_PROGRESSION_PLAN.md` (roadmap + PR plan) and
`docs/FORK_INGESTION.md` (fork assessment + progress).

- **PRs ingested (hand-ported; every touchpoint had diverged):** #62/#61 (lot-entry camera,
  right-click select, middle-click rotate), #57 (return to the correct neighbourhood; fixed the
  upstream 81..90/90..99 overlap), #56 (CAS name caption), #59 (eyedropper + cursor art),
  and #68 (Options Menu, neighbourhood camera machine + wipe transitions, `UITS1NeighborhoodVisitLotPanel`,
  gameplay-speed centralisation, ~30 UI fixes), plus a Credits panel and a GitHub Actions
  workflow. #64 was already fixed by our Phase 1 work (verified).
- **Fork commits ingested:** `evict-fix` (which is really **lot Bulldoze** — resets
  `House{NN}.iff` while keeping build fabric/mailbox/controllers), Steam-saves first-run guard
  (`Utils/TS1UserData`), #27 buy/build **catalog search**, `custom-skin-fix` (lazy memoised
  catalogue names + materialised row lists), `fame-implementation` (a bounds-checked fame
  filter), and #16 **update checker** (`Services/UpdateChecker`).
- **Key findings:** FreeSO has **no fame career track** (`PromoteFameIfNeeded`/`DemoteFameIfNeeded`
  are unimplemented and `TS1FameScore` is never written), so the fame tab is inert until the
  FreeSO pass; the fork's failed-content notification needs `Content.FailedContentFiles`, absent
  at our pin; and the update checker is inert until a `<Version>` is stamped (SDK default 1.0.0).
- **Deferred (FreeSO-side, per decision):** #60 fresh-lot controller spawn + the virgin-install
  `TemplateUserData` bootstrap bug, #63 free will, #65 DPI/animation, #58 Steam installs,
  #67 weather, #11 censor blur, #14 fresh lots, #28 asset slimming.
- Suite 829 -> **1220**; after every wave: full solution build, runtime smoke, and doc updates.

**2026-09-27, session 6 - bug fix, mesh ingest, visuals, feature audit (1974 tests green)**

- **Critical gameplay fix:** `LotLifecycleService.BlueprintReset` passed `ActiveFamily == null`
  into `IsSimless(hasActiveFamily, ...)` - the negation - so every lot loaded as simless
  (global 32 = 1): live mode disabled, money HUD hidden, buy mode, speed -1. This is the
  "family moved in but live mode cannot be activated / no economy" report. Fixed with
  `SimlessForLoad(FAMI, bool)`; runtime-verified (Live mode, SS7,447 HUD, clock running).
- **Generated meshes sanity-tested and ingested:** 6,110 of the offline generator's meshes
  validated (0 hard-invalid; 1,520 with NaN normals from upstream `GenerateNormals`, ingested
  because the in-game fallback is identical) and shipped in `Content/MeshReplace/`. See
  `docs/MESH_INGESTION_REPORT.md`; a follow-up is syncing the generator's final ~8,648.
- **Visual features:** FreeSO `WorldConfig.ShadowDetail` now sizes every light/shadow target
  and `WaterShininess` drives water/pool specular; exposed in Options > Graphics with
  `docs/VISUAL_FEATURES.md` (+85 tests).
- **FreeSO ingest:** submodule moved to `alexjyong/FreeSO@7e76c196` (35 commits) and their
  Simitone halves ported - weather, free will, failed-content notification + DPI manifest,
  Steam install selector. See `docs/FORK_INGESTION.md` sec 8.
- **Roadmap fixes:** Neighbourhood Management page (wiring the finished backend), virgin-install
  bootstrap fallback (`TemplateUserData` in FreeSO + `Program.cs` change), localisation
  infrastructure (`SimitoneStrings` + `_f900_simitone.cst`), and a per-sim auto-follow control.
  Roadmap status audit appended to `docs/ADDON_PROGRESSION_PLAN.md` sec 10.
- Suite 1426 -> **1974**; build + smoke green.

**2026-09-27, session 7 - reconstruction: sparse shapes, fallbacks, mesh cleanup**

- **z-buffer semantics confirmed from the engine** (`_2DWorldBatch.cs` +
  `2DWorldBatch.fx`): a pixel's surface sits `(1 - z/255) * DepthRange` from the
  exact far plane along the view direction; the generator's constants give
  `DepthRange ~= 3.18`.
- **Sparse shapes fixed:** the visual hull now uses *filled* silhouettes (alpha
  holes closed), depth carving abstains where a direction has no depth instead of
  vetoing, an empty carve falls back to the silhouette hull, and objects whose
  four rotations barely intersect (trees) get a thin single-view relief instead
  of nothing.
- **Automatic mesh cleanup added:** non-manifold face removal, hole capping,
  volume-preserving Taubin smoothing, then the existing quadric decimation and an
  unreferenced-vertex sweep.
- **Registration:** ICP depth-range fitting was implemented but is unstable
  (per-view drift / global collapse), so it defaults off; the seed range is used.
  Screen-space silhouette alignment runs only for near-empty hulls.
- **Point-cloud implicit surface (`FusePoints`, screened-Poisson style) was
  implemented** and is off by default: with the engine-derived range the four
  views' points do not coincide, so it inflates. A range sweep (`scan` probe)
  shows the cross-view nearest-neighbour objective is degenerate (pool table
  wants R~8, fountain R~0.5), i.e. the absolute depth scale is unobservable from
  the four views - the real blocker behind the residual inflation.
- Solid objects reconstruct into coherent grounded surfaces; the four-view hull
  is still inflated in X/Y and the absolute depth scale remains the open problem.
  Details, references, and next steps in `docs/MESH_GENERATION_RESEARCH.md` sec 11.
- **Synthetic validation:** a known 2x1x3 box's four-view visual hull is 3.08x2.54x4.03
  (inherently inflated) but the exact-depth carve recovers it as 2.50x0.96x2.91 - the
  projection and carve are correct, the residual error is the depth scale plus inconsistent
  sprites.
- **Bounding-box calibration against the remeshes is unreliable** (their orientation
  convention differs), so the engine's range (3.176) is kept, with adaptive growth when a
  carve would over-thin an object.
- **Final quality passes:** grounding to Y=0, region-coherent view assignment (removes
  speckle), fragmentation->relief fallback for billboards (trees), NaN-normal fix in
  `DGRP3DVert.GenerateNormals`, dead code removed.
- Full regeneration with the final algorithm running in the background to
  `GeneratedMeshes.v2` (the existing pack is left untouched).
- Fusion/AnimMesh tests pass (14/14); `FSO.Files` and `FSO.Content` build clean.
