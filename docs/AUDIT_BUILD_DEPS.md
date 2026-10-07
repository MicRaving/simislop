# Build, dependency and hygiene audit — `D:\Simitone`

**Date:** 2026-09-28 · **Mode:** read-only. No project, package, config or source file was modified.
The only file this audit created is this document.

**Command actually run** (the only build performed):

```
dotnet build Client\Simitone\Simitone.sln -c Release -t:Compile --no-incremental -v:n -bl:...
```

Result: **Build succeeded, 946 Warning(s), 0 Error(s)**, 10 m 39 s wall clock.
`-t:Compile` was used deliberately so no `bin\` output was touched (the player's own game may be
holding that folder). Nothing was launched, clicked, or moved. No process was killed.

Submodule state at time of audit: `FreeSO/.git` HEAD = `7e76c1960252ddd033d8937d5edb47d326f5b69c`
("Fixing busted floor placement issue", 2026-04-10). No tags reachable. The audit root has **no
`.git` directory**, so "tracked vs untracked" claims below are inferred from `.gitignore` rules, not
from `git status` — see confidence notes.

---

## 1. Version inventory

### 1.1 Toolchain and target frameworks

| Thing | Value | Where | Comment |
|---|---|---|---|
| .NET SDK pinned | `10.0.100`, `rollForward: latestFeature` | `global.json` | Installed here: 10.0.401 → accepted |
| .NET SDKs installed on this box | 9.0.318, **10.0.401** | `dotnet --list-sdks` | 9.x needed only to *target* net9.0, not to run anything Simitone ships |
| CI SDK | `dotnet-version: '10.0.x'` | `.github/workflows/build.yml:33` | Satisfies `global.json`; no 9.x runtime installed, which is fine (nothing net9.0 is executed) |
| `Simitone.Windows` | `net10.0-windows`, `WinExe`, `UseWindowsForms` | `Simitone.Windows.csproj:4,7,60` | AssemblyName `Simitone` |
| `Simitone.Client` | `net10.0`, `ImplicitUsings=disable`, `Nullable=disable` | `Simitone.Client.csproj:4-6` | |
| `Simitone.GenerateMeshes` | `net10.0`, `Exe` | `Simitone.GenerateMeshes.csproj:4-5` | Not in the `.sln` (built transitively) |
| `Simitone.PackageRemeshes` | `net10.0`, `Exe` | `Simitone.PackageRemeshes.csproj:4-5` | Not in the `.sln` (built transitively) |
| `Simitone.Tests` | `net10.0`, `LangVersion 14.0` | `Simitone.Tests.csproj:4-5` | Only project that sets `LangVersion`; siblings rely on the net10.0 default (C# 14) — consistent |
| FreeSO library TFM | `net9.0` (18 projects) | `FreeSO/TSOClient/**` | one major behind Simitone's net10.0 |
| FreeSO WinForms TFM | `net9.0-windows` (FSO.IDE, FSO.Windows, FSO.Patcher, tso.debug, FSO.Server.Debug, FSOFacadeWorker) | | |
| `MSDFData`, `MSDFExtension` | `net8.0` | `FreeSO/Other/libs/MSDFData` | two majors behind |
| Non-SDK / no TFM | FSO.iOS, FSODroid, FSO.Server.Framework, `System.Drawing`, CityDataModel, LoginDataModel, TargaImage/VS2008 | `packages.config` era | See finding **B-06** |
| `.sln` | 24 projects, all reachable from the two entry points (verified by graph walk) | `Client/Simitone/Simitone.sln` | No orphans. But it still carries `Ad-Hoc`/`AppStore`/`iPhone`/`iPhoneSimulator` solution configurations — Xamarin-era leftovers, see **H-05** |

### 1.2 Direct `PackageReference` inventory (all 42 distinct packages)

| Package | Declared version(s) | Projects | Note |
|---|---|---|---|
| **MonoGame.Framework.DesktopGL** | **3.8.4, 3.8.5.1** | 1 + 14 | **Mismatch — see A-01** |
| **MonoGame.Framework.WindowsDX** | **3.8.4, 3.8.5.1** | 1 + 4 | **Mismatch — see A-01** |
| MonoGame.Framework.Content.Pipeline | 3.8.4 | 1 (`MSDFExtension`) | project not in any solution |
| **SixLabors.ImageSharp** | 3.1.11 | 5 | consistent, deliberately declared twice in Simitone (A-02) |
| Newtonsoft.Json | 13.0.4 | 5 | consistent (13.0.3 is the last "well-known" release; 13.0.4 exists and restores) |
| Ninject | 3.3.6 | 9 | consistent |
| Mina | 2.0.12 | 2 | **NU1701, .NET Framework only — B-02** |
| MP3Sharp | 1.0.5 | 2 | |
| **Microsoft.CodeAnalysis.CSharp** | **3.4.0-beta1-final** | 1 (`FSO.SimAntics.JIT.Roslyn`) | **2018 pre-release. See A-03** |
| **System.Data.SqlClient** | **4.9.0** | 1 | obsolete-by-design; the compiler itself says "Use the Microsoft.Data.SqlClient package instead" (CS0618 × 4) |
| **System.Data.SQLite.Core** | **1.0.119** | 1 | last published 2018; ships `SQLite.Interop.dll` natives the trim target then deletes |
| **System.IO.FileSystem / .Primitives** | **4.3.0** | 1 / 2 | out-of-band legacy packages, no-ops on modern .NET |
| **System.Security.Cryptography.Algorithms** | **4.3.1** | 1 | same |
| **System.Resources.Extensions** | **8.0.0, 9.0.0, 9.0.9** | 1 / 1 / 1 | **Three versions in three projects — see A-04** |
| System.Runtime.Caching | 9.0.9 | 1 | |
| System.Configuration.ConfigurationManager / System.Drawing.Common / System.Speech / System.Diagnostics.PerformanceCounter | 9.0.9 | 2 / 2 / 1 / 1 | pinned to the 9.x servicing band while the tree targets net10.0 |
| **RestSharp** | **112.1.0** | 1 (`FSO.Server.Clients`) | current; the old `RestSharp 105.2.3` in `FSO.iOS`/`FSODroid` is a dead `packages.config` reference |
| Octokit | 0.36.0 | 1 | pre-1.0, unmaintained since 2019 |
| AWSSDK.Core / AWSSDK.S3 | 3.3.103.48 / 3.3.104.36 | 1 each | v3 line, 2019-era; AWS SDK v4 is current |
| Dapper | 2.1.66 | 1 | current |
| MySql.Data | 9.4.0 | 1 | current |
| MySQL-side transitives (dragged in, never declared) | BouncyCastle.Cryptography, Google.Protobuf, K4os.Compression.LZ4(+Streams), K4os.Hash.xxHash, ZstdSharp | 0 | pinned only by `MySql.Data 9.4.0` |
| **Portable.JWT** (+ Portable.BouncyCastle 1.8.0) | 1.0.5 | 1 | **unmaintained fork, 2016. See A-05** |
| CommandLineParser | 1.9.71 | 1 | |
| NLog / Ninject.Extensions.ChildKernel | 6.0.4 / 3.3.0 | 2 / 1 | |
| Microsoft.Data.Sqlite.Core | 9.0.9 | 1 | |
| Open.Nat | 2.1.0 | 1 | **NU1701, .NET Framework only — B-02** |
| DeltaQ | 2.1.0 | 1 | |
| **xxHashSharp** | 1.0.0 | 1 | **NU1701, .NET Framework only — B-02** |
| **FreeSO.HandEvaluator** | 1.2.0 | 1 | **NU1701, .NET Framework only — B-02** |
| **SharpGLTF.Toolkit** | 1.0.0-alpha0011 | 1 (FSO.IDE) | 2021 alpha, on the shipped Volanic path |
| RoyT.TrueType | 0.2.0 | 1 (`MSDFExtension`, dead) | |
| Be.Windows.Forms.HexBox | 1.6.1 | 1 (`FSO.Server.Debug`, not built) | |
| xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk / coverlet.collector | 2.9.3 / 3.1.4 / 17.14.1 / 6.0.4 | `Simitone.Tests` | consistent, current-ish |

### 1.3 Transitive packages restored for the wrong TFM (NU1701)

`NU1701` = "restored using `.NETFramework,Version=v4.6.1 … v4.8.1` instead of the project target
framework". 20 distinct packages, 512 emitted lines:

| Package | Version | Inherited from |
|---|---|---|
| `Common.Logging` / `Common.Logging.Core` | 3.3.1 | `FreeSO.HandEvaluator`, `DeltaQ` |
| `Mina` | 2.0.12 | declared directly in FSO.Common / FSO.UI |
| `xxHashSharp` | 1.0.0 | declared in FSO.Files |
| `Portable.BouncyCastle` | 1.8.0 | `Portable.JWT` |
| `Portable.JWT` | 1.0.5 | declared in FSO.Server.Common |
| `FreeSO.HandEvaluator` | 1.2.0 | declared in FSO.SimAntics |
| `Open.NAT` | 2.1.0 | declared in FSO.Client |
| `CommandLineParser` | 1.9.71 | declared in FSO.Server |
| `System.Reflection.Extensions` 4.0.0, `System.IO.FileSystem(.Primitives)` 4.0.0, `System.Runtime.Handles` 4.0.0, `System.Linq` 4.0.0, `System.Collections` 4.0.10, `System.Globalization` 4.0.10, `System.Runtime.Extensions` 4.0.10, `System.Threading` 4.0.10, `System.Diagnostics.Debug` 4.0.10 | | legacy out-of-band shims |

These are consumed by the *net9.0* FreeSO projects but bubble up to every consumer, including
Simitone's own net10.0 projects — which is why `Simitone.Client.csproj` (which declares nothing but
ImageSharp) reports 5 NU1701s of its own.

### 1.4 Warning census (946 total, from the build above)

Counts below are **unique** (deduped by project+file+line+code). MSBuild's own total of 946 counts
some warnings once per consuming project; the unique figure is the honest one and is what the
"trivial vs systemic" split uses.

| Category | Unique | Family | Verdict |
|---|---:|---|---|
| `NU1701` | 512 lines / 20 packages | restore | systemic, see **B-02** |
| `CS0168` | 85 | CS unused local | trivial |
| `CS0169` | 53 | CS unused field | trivial |
| `IL2026` | 51 | trim analyzer | **noise by design — B-01** |
| `IL3050` | 44 | AOT analyzer | **noise by design — B-01** |
| `CS0414` | 33 | CS assigned-never-used field | trivial |
| `IL2075` | 30 | trim analyzer | **noise by design — B-01** |
| `CS0436` | 28 | **type collision with imported type** | **real, see B-05** |
| `CS0219` | 28 | CS assigned-never-used local | trivial |
| `CS0108` | 18 | CS member hides inherited | trivial-ish, **one is a real defect: B-06** |
| `IL2072` | 17 | trim analyzer | **noise by design** |
| `CA1416` | 15 | platform compatibility | see **B-07** |
| `CA2200` | 15 | rethrow loses stack trace | trivial-to-fix, real |
| `CA2022` | 13 | IDisposable not disposed | trivial-to-fix, real |
| `CS0618` | 12 | obsolete API | see **B-08** |
| `CS0162` | 10 | unreachable code | trivial |
| `CS8073` | 10 | always-true comparison | **one is a real defect: B-09** |
| `IL2091` | 7 | trim analyzer | noise |
| `CS0649` | 6 | field never assigned | trivial |
| `xUnit2000` | 6 | test arg order | trivial |
| `CS0067` | 6 | event never used | trivial |
| `IL2070` | 6 | trim analyzer | noise |
| `SYSLIB0014` | 6 | `WebClient` obsolete | see **B-08** |
| `xUnit2017` | 5 | test idiom | trivial |
| `CS8632` | 5 | **nullable annotation in a `Nullable=disable` project** | see **B-10** |
| `IL2087` | 5 | trim analyzer | noise |
| `SYSLIB0023` | 4 | `RNGCryptoServiceProvider` | see **B-08** |
| `IL2077` | 4 | trim analyzer | noise |
| `CS0659` | 3 | member overrides `==` not `Equals` | trivial |
| `IL2080` | 3 | trim analyzer | noise |
| `CS0114` | 3 | member hides inherited **without** `new`/`override` | **real: B-06** |
| `xUnit2013` | 3 | test idiom | trivial |
| `IL3000` | 3 | AOT "cannot analyze" | noise |
| `SYSLIB0051/0057/0041/0039/0030/0021/0003` | 1–2 each (20 total) | obsolete BCL APIs | see **B-08** |
| `IL2090, IL2060, IL2067, IL2062` | 1–2 each | trim analyzer | noise |
| `CS8848` | 2 | `+` precedence ambiguity | trivial, real |
| `ASP5001` | 2 | obsolete MVC `CompatibilityVersion` | dead code, **B-08** |
| `ASP0019` | 2 | `IHeaderDictionary.Add` will throw | **real bug, dead code: B-11** |
| `MVC1005` | 1 | `UseMvc` + endpoint routing | **real misconfiguration, dead code: B-11** |
| `CS1690` | 1 | field of `[Serializable]` class | trivial |
| `CS0472/CS0642/CS0660/CS0661/CS0672/CS0675/CS0184/CS0105/CS8981` | 1 each | assorted | trivial |
| `xUnit1031`, `xUnit2031` | 1 each | test idiom | trivial |

**Split:**

* **Trivially fixable dead code: ~268** (`CS0168/0169/0414/0219/0649/0067/0162/0108/0114/0661/0660/0659/0672/0675/8848/8073/1690/0472/0642/8981/0184/0105`)
* **Test-style warnings: 16**
* **Real but mechanical quality warnings: ~50** (`CA1416` 15, `CA2200` 15, `CA2022` 13, `SYSLIB*` 20, `ASP*`+`MVC*` 5)
* **Genuinely outdated/suppressed-danger warnings: 45** (`CS0436` 28, `CS0618` 12, `CS8632` 5)
* **Pure noise from a misconfiguration: 176** (all `IL2xxx`/`IL3xxx`)
* **NU1701: 20 packages**

**Per-project, unique located warnings:**

| Project | Warnings | Note |
|---|---:|---|
| FSO.Client | 131 | |
| FSO.Server | 69 | built only to be deleted by the trim |
| FSO.Common | 43 | |
| FSO.SimAntics | 38 | |
| FSO.UI | 37 | |
| FSO.Server.Api.Core | 33 | built only to be deleted by the trim |
| FSO.IDE (Volcanic) | 25 | **shipped** |
| FSO.Common.DataService | 23 | built only to be deleted |
| FSO.Server.Database | 20 | built only to be deleted |
| FSO.LotView | 19 | |
| FSO.Server.Protocol | 18 | built only to be deleted |
| FSO.Files | 17 | |
| **Simitone.Tests** | **16** | |
| FSO.Server.Clients | 15 | built only to be deleted |
| **Simitone.Client** | **15** | |
| FSO.SimAntics.JIT.Roslyn | 12 | |
| FSO.Server.Common | 9 | built only to be deleted |
| FSO.Content | 7 | |
| TargaImage | 6 | |
| TargaImagePCL | 5 | |
| FSO.SimAntics.JIT | 5 | |
| FSO.Server.Watchdog | 3 | built only to be deleted |
| FSO.Common.DatabaseService / FSO.Server.Domain | 2 each | built only to be deleted |
| FSO.HIT / FSO.Vitaboy / FSO.Vitaboy.Engine | 1–2 each | |
| **Simitone.Windows** | **0** | clean |

> **Headline:** ~200 of the 574 unique warnings (35 %) come from the *The Sims Online server* projects
> that `Simitone.Windows.csproj`'s own `TrimSingleplayerOutput` target deletes from `$(OutDir)`
> immediately after the build. Simitone's own code accounts for 31 warnings across 3 projects and
> `Simitone.Windows` is warning-free.

---

## 2. Findings — outdated / mismatched software

### A-01 · MonoGame version split across the graph (3.8.5.1 vs 3.8.4)
* **Where** `Client/Simitone/Simitone.Windows/Simitone.Windows.csproj:84-85` (3.8.5.1) vs 15 FreeSO
  projects at 3.8.4 (`FSO.Client.csproj:137-138`, `FSO.Common`, `FSO.UI`, `FSO.Content`,
  `FSO.SimAntics`, `FSO.Files`, `FSO.HIT`, `FSO.LotView`, `FSO.Vitaboy`, `FSO.Vitaboy.Engine`,
  `MSDFData`, `FSO.IDE`, `FSO.Windows`, `FSO.Debug`, `FSO.Server`).
* **What's wrong** Two different MonoGame versions are declared in one restore graph. NuGet unifies
  upward, so the *resolved* graph is 3.8.5.1 (confirmed in
  `Simitone.Windows/obj/project.assets.json`), but the submodule's project files still claim 3.8.4.
  The build compiles FreeSO source against 3.8.5.1 headers while the submodule says otherwise, so a
  submodule update that bumps FreeSO to any version *other than* 3.8.5.1 silently changes what
  Simitone compiles against.
* **Why it matters** `CopyMonoGameDLLs` (see **A-06**) reads the **declared** version, not the
  **resolved** one. The day the two diverge for any reason, the target copies a `MonoGame.Framework.dll`
  that is not the one the assemblies were compiled against — a runtime-only failure that no compile
  step catches. Also, the free choice of "whatever MonoGame is in the submodule" is a real
  maintenance hazard: a FreeSO bump can change Simitone's renderer out from under it with no diff in
  the Simitone tree.
* **Severity** Medium
* **Fix** Pick one owner. Either (a) add a `Directory.Packages.props` with
  `<PackageVersion Include="MonoGame.Framework.*" Version="3.8.5.1" />` and have every project use
  `VersionOverride`/`CentralPackageTransitivePinningEnabled`, or (b) pin the submodule and add a
  build-time assertion: after restore, a target that fails if
  `@(PackageReference->WithMetadataValue('Identity','MonoGame.Framework.DesktopGL')->'%(Version)')`
  differs from the version actually present under `$(NuGetPackageRoot)monogame.framework.desktopgl/`.
* **Confidence** High — both versions present in the local NuGet cache, resolution confirmed in
  `project.assets.json`.

### A-02 · `SixLabors.ImageSharp` declared in two projects for the same reason
* **Where** `Simitone.Client.csproj:43` and `Simitone.Windows.csproj:89`, both `3.1.11`.
* **What's wrong** Not a bug — the comments explain it. Both projects call into
  `BitmapDecoder`, so both declare the version explicitly rather than inheriting a transitive one.
* **Why it matters** Only as a maintenance pin: this is the correct pattern, but nothing checks the
  two stay equal. Also note the cache on this machine holds ImageSharp `8.0.1, 9.0.1, 10.0.1,
  10.0.2, 11.0.2, 12.0.2` alongside `3.1.11`, so the wildcard fallback in **A-06** would be
  ambiguous here.
* **Severity** Info
* **Fix** Leave the declarations; add a test or a target asserting the two versions are identical.
* **Confidence** High.

### A-03 · `Microsoft.CodeAnalysis.CSharp 3.4.0-beta1-final` (Roslyn 3.4, 2018)
* **Where** `FreeSO/TSOClient/FSO.SimAntics.JIT.Roslyn/FSO.SimAntics.JIT.Roslyn.csproj`
* **What's wrong** A seven-year-old *pre-release* Roslyn is used to compile game scripts at runtime
  (the SimAntics JIT). Everything else in the tree builds with .NET 10's Roslyn 5.x.
* **Why it matters** It is an alpha/beta artifact, it drags in its own
  `System.Collections.Immutable`/`System.Reflection.Metadata` versions, and it pins the JIT to a C# 7.3
  language level for anything it compiles. If a `.sims`/`.ts` script ever needs a newer language
  feature, the fix is "upgrade this package", not "change a line". It is also the single most
  obviously *outdated* dependency in the repository.
* **Severity** Medium (it works; it is a fossil, and a beta at that)
* **Fix** Upgrade to `Microsoft.CodeAnalysis.CSharp 4.x` (the last `Microsoft.CodeAnalysis.*` line) and
  set `LanguageVersion` explicitly in the `CSharpCompilationOptions` the JIT builds, so the bump
  cannot silently change the accepted grammar. Verify with a scripted-object test before/after.
* **Confidence** High on the version being old; Medium on the upgrade being drop-in (I did not
  attempt it).

### A-04 · `System.Resources.Extensions` at three different versions
* **Where** `FSO.Files.csproj` → `8.0.0`; `FSO.UI.csproj` and `FSO.Server.Debug.csproj` → `9.0.0`;
  `FSO.Patcher.csproj` and `SimplePaletteQuantizer.csproj` → `9.0.9`.
* **What's wrong** Three versions of the same assembly in one solution.
* **Why it matters** NuGet unifies to `9.0.9`, so the `8.0.0` and `9.0.0` declarations are
  decorative. Two 9.x spellings of the same servicing band is pure noise in a diff. This is the
  cheapest example of the whole class "no central package management" — see **H-08**.
* **Severity** Low
* **Fix** `Directory.Packages.props` with `ManagePackageVersionsCentrally=true`, or at minimum
  normalise the three to `9.0.9` in one commit.
* **Confidence** High.

### A-05 · `Portable.JWT 1.0.5` + `Portable.BouncyCastle 1.8.0`
* **Where** `FreeSO/TSOClient/FSO.Server.Common/FSO.Server.Common.csproj`
* **What's wrong** Both are abandoned personal forks (2016, last commit years ago) of libraries that
  upstream abandoned. `Portable.BouncyCastle 1.8.0` is the pre-1.9 BouncyCastle API with known CVEs
  behind it.
* **Why it matters** It is the *only* `JWT.dll` / `crypto.dll` in the output, and
  `Simitone.Windows.csproj:332-333` explicitly trims both away — so for Simitone it is dead weight
  that is compiled on every build. It does matter for anyone who runs the TSO server path.
* **Severity** Low for Simitone, Medium for the upstream project
* **Fix** Since the assemblies are trimmed anyway, the cheapest correct move is to stop building
  them: the `FSO.Server.Common` reference chain into Simitone is only needed because `FSO.Client`
  references the whole server closure. See **B-12**. If the server must keep working, migrate to
  `System.IdentityModel.Tokens.Jwt` + `BouncyCastle.Cryptography`.
* **Confidence** High on the age/derivation; Medium on the CVE specifics (not re-checked against a
  current advisory DB in this pass).

### A-06 · `CopyMonoGameDLLs` reads the *declared* version, not the resolved one
* **Where** `Simitone.Windows.csproj:438-495`
* **What's wrong** The target extracts the version from the `PackageReference` item
  (`@(PackageReference->WithMetadataValue(...)->'%(Version)')`, lines 449-455) and globs
  `$(NuGetPackageRoot)monogame.framework.desktopgl/$(that version)/lib/**/MonoGame.Framework.dll`.
  NuGet's *actual* resolution is a separate computation. They agree today (A-01) by luck.
  The fallback at lines 468-470 globs `*/lib/**` and errors out if more than one version is in the
  cache — and this machine's cache already has **two** (3.8.4 and 3.8.5.1), so the fallback is
  already unusable here.
* **Why it matters** A `MonoGame.Framework.dll` from the wrong version is copied next to assemblies
  compiled against another: `TypeLoadException`/`MissingMethodException` at first launch, not at
  build time. The "unambiguous fallback" is a false comfort: a developer with a stale cache gets a
  hard error, a developer with a clean cache gets the *wrong-but-single* version.
* **Severity** Medium
* **Fix** Resolve from `$(NuGetPackageRoot)` + the version that restore actually picked. The robust
  source is the `.nuget.g.props`/`project.assets.json` that restore writes, or a `PackageDownload`
  item with the same version, or simply add an `<Error>` when the declared version is not what
  `project.assets.json` records. Drop the wildcard fallback entirely — a loud failure beats a
  silent wrong-version copy.
* **Confidence** High — the two cache versions were listed directly.

### A-07 · `System.Data.SqlClient 4.9.0` and `System.Data.SQLite.Core 1.0.119`
* **Where** `FreeSO/TSOClient/FSO.Server.Database/FSO.Server.Database.csproj`
* **What's wrong** `System.Data.SqlClient` is the legacy package (Microsoft's own obsoletion message
  fires: 4 × `CS0618 "Use the Microsoft.Data.SqlClient package instead"`). `System.Data.SQLite.Core
  1.0.119` was last published in 2018 and brings its own `SQLite.Interop.dll` natives.
* **Why it matters** The legacy SqlClient package drags in the Windows SSPI/GSSAPI native shims
  (`sni.dll`, `gssapi64.dll`, `k5sprt64.dll`, `krb5_64.dll`, `krbcc64.dll`, `comerr64.dll`) that
  `Simitone.Windows.csproj:315-322` then deletes one-by-one by hand. It is also the source of the
  `runtimes\win\lib\net8.0\System.Data.SqlClient.dll` trim entry — a path that changes whenever the
  package's target framework moves (see **B-13**).
* **Severity** Low for Simitone (all trimmed), Medium for upstream
* **Fix** `Microsoft.Data.SqlClient` (current, actively serviced, no native shims to trim) and
  `Microsoft.Data.Sqlite.Core` (already present in the graph via `FSO.Server.Api.Core`).
* **Confidence** High.

### A-08 · Legacy out-of-band packages still declared
* **Where** `FSO.Client.csproj:142-143` (`System.IO.FileSystem` 4.3.0,
  `System.IO.FileSystem.Primitives` 4.3.0); `FSO.Server.Core.csproj`;
  `FSO.Server.Api.Core.csproj` (`System.Security.Cryptography.Algorithms` 4.3.1)
* **What's wrong** These are `netstandard1.x`/`.NET Framework` shim packages that exist only for
  pre-.NET-Core. On `net9.0`/`net10.0` the real types come from the framework; the packages
  contribute nothing but 4 extra NU1701 lines each.
* **Why it matters** Pure noise, but noise that trains people to ignore the NU1701 channel — which
  is where the genuinely-relevant ones live.
* **Severity** Low
* **Fix** Delete all three `PackageReference` lines; restore and confirm the build is unchanged.
* **Confidence** High.

### A-09 · .NET servicing-band packages pinned to 9.0.x under a net10.0 tree
* **Where** `System.Configuration.ConfigurationManager 9.0.9` (`FSO.Server.Debug`,
  `SimplePaletteQuantizer`), `System.Drawing.Common 9.0.9` (`FSO.UI`, `FSO.Server.Debug`),
  `System.Speech 9.0.9` (`FSO.Windows`), `System.Diagnostics.PerformanceCounter 9.0.9` (`FSO.Server`),
  `Microsoft.Data.Sqlite.Core 9.0.9` (`FSO.Server.Api.Core`)
* **What's wrong** The graph targets `net10.0` but five Microsoft packages are pinned to the 9.0
  servicing band. They will unify fine (framework-provided on net10.0) but they will not receive
  net10.0 servicing, and the last 9.0.x is a fixed point — no more updates.
* **Why it matters** Low today, but this is the exact list that goes stale on the next SDK bump and
  nobody notices because NU1701/NU1903 don't fire for in-band packages.
* **Severity** Low
* **Fix** Move all five to the 10.0.x band (or higher) when convenient.
* **Confidence** High on the versions; Medium that they matter (they are framework-shadowed).

### A-10 · `AWSSDK.*` v3 and `Octokit 0.36.0`
* **Where** `FSO.Server.Api.Core.csproj`
* **What's wrong** AWS SDK for .NET v3 (2019-era, out of support; v4 is the current line) and Octokit
  `0.36.0`, a pre-1.0 library last published in 2019.
* **Why it matters** Only `FSO.Server.Api.Core` uses them, and `Simitone.Windows.csproj:328-330`
  trims `AWSSDK.Core.dll`, `AWSSDK.S3.dll` and `Octokit.dll` away. So this is dead supply-chain
  surface in a singleplayer, offline, offline-only game.
* **Severity** Low for Simitone
* **Fix** Same as A-05: remove them from Simitone's build closure (see **B-12**) rather than
  upgrading them.
* **Confidence** High.

---

## 3. Findings — warnings that should be errors

### B-01 · 176 trim/AOT analyzer warnings from a feature Simitone does not use
* **Where** `IsAotCompatible=True` on 15 FreeSO library projects (`FSO.Client.csproj:8,11,21,26`,
  `FSO.Common`, `FSO.Content`, `FSO.Files`, `FSO.LotView`, `FSO.SimAntics`, `FSO.SimAntics.JIT`,
  `FSO.SimAntics.JIT.Roslyn`, `FSO.Server.Common`, `FSO.Vitaboy`, `FSO.Vitaboy.Engine`, `FSO.HIT`;
  note it is declared **twice** in each — once `True`, once `true` — and again in two
  `Condition="'$(Configuration)|$(Platform)'=='Debug|AnyCPU'"` blocks that set the identical value).
  `FSO.Client.csproj:9-12` additionally sets `PublishAot=true`, `PublishTrimmed=true`, `TrimMode=full`
  on a **Library**.
* **What's wrong** `IsAotCompatible` turns on the ILLink/AOT analyzers for *every* build. They emit
  176 unique warnings. The single largest cluster is `IL2026`/`IL3050` on
  `Newtonsoft.Json.JsonConvert.DeserializeObject<T>` / `SerializeObject` in `UIHintManager.cs`.
* **Why it matters** Simitone is a `net10.0-windows` `WinExe` with a MonoGame graphics context, a
  WinForms dialog, Ninject and a JIT that emits code at runtime. It is **never** trimmed and **never**
  AOT-compiled. So all 176 warnings are unfalsifiable noise. They are the single biggest reason
  nobody can see the 45 warnings that do matter. The `PublishAot`/`PublishTrimmed` on a library
  project are inert, and the doubled `IsAotCompatible` declarations are copy-paste residue.
* **Severity** Medium (build hygiene; it is *not* a correctness bug)
* **Fix** Set `<IsAotCompatible>false</IsAotCompatible>` on the Simitone-reachable FreeSO projects
  (or add `<NoWarn>$(NoWarn);IL2026;IL2070;IL2072;IL2075;IL2077;IL2080;IL2087;IL2090;IL2091;IL3000;IL3050</NoWarn>`
  — a `NoWarn` is better than a lie about AOT-compatibility). Delete the duplicate declarations and
  the `PublishAot`/`PublishTrimmed`/`TrimMode` block from library projects. Do **not** suppress
  `IL2026` for the *server* projects if you intend to keep the TSO server buildable.
* **Confidence** High — the warning IDs and the properties that cause them were both read directly.

### B-02 · 20 packages restored for the wrong target framework (NU1701)
* **Where** See §1.3. Reaches Simitone's own net10.0 projects with 5 warnings each.
* **What's wrong** The restore graph pulls a .NET Framework-era dependency chain into net9.0/net10.0
  projects. The worst offenders (`Mina`, `xxHashSharp`, `FreeSO.HandEvaluator`, `Open.NAT`,
  `Portable.JWT`, `Portable.BouncyCastle`, `CommandLineParser`) are all unmaintained.
* **Why it matters** The `net461`-compiled IL is *loaded* at runtime (e.g. `Mina` sockets in
  `FSO.Common`, `xxHashSharp` in `FSO.Files`) rather than merely referenced. It happens to work, but
  it is unsupported and unpatched. It also accounts for over half the build's warning volume.
* **Severity** Medium
* **Fix** In order of value: (1) `Mina` and `xxHashSharp` have maintained ports — swap them;
  (2) `FreeSO.HandEvaluator` is FreeSO's own package, so it can be ported; (3) `Portable.JWT` →
  `System.IdentityModel.Tokens.Jwt` (see A-05); (4) once the NU1701 count is low enough, add
  `<WarningsAsErrors>NU1701</WarningsAsErrors>` or `<TreatWarningsAsErrors>` for restore so it cannot
  regress.
* **Confidence** High.

### B-03 · `#nullable disable` everywhere, with no incremental opt-in
* **Where** `<Nullable>disable</Nullable>` on **all 5** Simitone projects and every FreeSO project.
  `Simitone.Tests.csproj:6-8` even documents the choice ("matches the rest of the repo").
* **What's wrong** Nullability is off across a ~600-file codebase. The `Simitone.Windows` csproj
  contains an unusually thoughtful 40-line justification for its *one* `NoWarn` (WFO0003) and none
  for the far larger decision to disable nullable.
* **Why it matters** The `CS8073` in B-09 is exactly the class of bug a nullable-aware compiler would
  have surfaced differently. More practically: the codebase is frozen out of a whole analyzer family
  (CS86xx) permanently, and new code has no way to opt in except per-file `#nullable enable` — of
  which there are **zero** in the tree.
* **Severity** Medium
* **Fix** Do not flip it wholesale. Instead: keep `disable` at project level, and enable
  `<Nullable>annotations</Nullable>` (warnings off, annotations honoured) on `Simitone.Client` and
  `Simitone.Client.csproj`'s new code, or add `#nullable enable annotations` to new files. Zero-risk,
  and it lets CS86xx fire where someone opts in.
* **Confidence** High.

### B-04 · `<NoWarn>` in the tree: exactly one entry, and it is well argued
* **Where** `Simitone.Windows.csproj:52` → `<NoWarn>$(NoWarn);WFO0003</NoWarn>`
* **What's wrong** Nothing wrong. This is the model to copy: the file carries a 40-line rationale
  (the `ApplicationHighDpiMode` property is inert because nothing calls the source-generated
  `ApplicationConfiguration.Initialize()`, so the `<dpiAware>` manifest element is the real mechanism;
  PerMonitorV2 is deliberately not requested) **and** a test (`HighDpiConfigurationTests`) that
  reads both files and fails if they drift.
* **Why it matters** Worth calling out explicitly because the rest of the repo's suppression
  strategy is *absence of suppression* — and the gap is filled with noise instead (B-01).
* **Severity** Info
* **Fix** None. Reuse this pattern (rationale in the file + a test) for any future suppression.
* **Confidence** High.

### B-05 · 28 × `CS0436` — real type collisions between FSO.UI and FSO.Client
* **Where** `FreeSO/TSOClient/tso.client/UI/Controls/Catalog/UICatalog.cs:99,128,156,183,206,223,240`
  and `UI/Panels/LotControls/{UICustomLotControl,UIFloorPainter,UIGrassPaint,UIRoofer,
  UITerrainFlatten,UITerrainRaiser,UIWallPainter,UIWallPlacer}.cs`
* **What's wrong** `FSO.UI` and `FSO.Client` both define `UICustomLotControl`, `UIFloorPainter`,
  `UIRoofer`, `UIWallPlacer`, `UITerrainRaiser`, `UITerrainFlatten`, `UIGrassPaint`,
  `UIWallPainter` — as **different types** (an abstract class in one, an interface in the other).
  The compiler resolves the ambiguity in favour of the *local* (FSO.Client) type.
* **Why it matters** This is the collision that forces `Simitone.Client` to **not** reference
  `FSO.Client` at all (see the long comment at `Simitone.Client.csproj:46-67`, which documents
  `Aliases="tso"` as tried and rejected). It means the two assemblies can never be referenced
  together, which constrains the whole architecture. And the compiler only *warns*: any file that
  silently relied on the FSO.UI version would bind to the FSO.Client one with no diagnostic beyond
  this warning.
* **Severity** Medium (structural, not a live bug)
* **Fix** Renaming one side is the only real cure and is a large FreeSO change. Short term: add
  `<NoWarn>$(NoWarn);CS0436</NoWarn>` **with** the above rationale in `FSO.Client.csproj` so the 28
  warnings stop burying other diagnostics, and record the collision in `docs/FORK_INGESTION.md` so a
  future submodule merge that moves one of these types is caught. Do **not** suppress it in
  `Simitone.Client` — that project never sees these warnings because it doesn't reference
  `FSO.Client`.
* **Confidence** High.

### B-06 · `CS0108`/`CS0114` in Simitone's own code — `Rotation` hides, and cannot override
* **Where** `Client/Simitone/Simitone.Client/UI/Screens/TS1GameScreen.cs:167-168`
* **What's wrong** `FSO.UI/Framework/UIElement.cs:238` declares `public virtual float Rotation`.
  `TS1GameScreen` declares `private int _Rotation` / `public int Rotation` — a **different type** that
  therefore *cannot* be an `override`. The setter has real side effects: it re-centres the lot camera
  (`World.State.CenterTile = World.EstTileAtPosWithScroll(...)`, line 177) and switches
  `World.State.Rotation` (lines 180-189).
* **Why it matters** Any call site that writes `Rotation` through a `UIElement`-typed reference gets
  the base `float` property and the camera never re-centres and the world rotation never updates.
  Silently divergent behaviour depending on the static type is the classic symptom. This is the only
  substantive compiler warning in Simitone's own game code.
* **Severity** Medium
* **Fix** Make it a real override — the base is `float`, so `public override float Rotation` with the
  side effects, and convert the `int` 0-3 switch to the `WorldRotation` enum directly. Then audit
  call sites for `Rotation` assigned on a `UIElement` reference.
* **Confidence** High on the diagnosis; Medium on blast radius (I did not enumerate call sites).

### B-07 · 15 × `CA1416` — Windows-only APIs in cross-platform projects
* **Where** `tso.client/Utils/GameLocator/WindowsLocator.cs:19-29` (all 10 hits: `RegistryHive`,
  `RegistryKey.OpenBaseKey`, `OpenSubKey`, `GetSubKeyNames`),
  `FSO.Server/Servers/Lot/Lifecycle/CityConnections.cs:38-45,97-98` (5 hits: `PerformanceCounter`)
* **What's wrong** Both projects target bare `net9.0` and call `Microsoft.Win32.Registry` and
  `System.Diagnostics.PerformanceCounter`, which are Windows-only.
* **Why it matters** Simitone is Windows-only *by policy* (README: "Only for Desktop Windows"), so
  these are not live bugs. But the analyzer is telling the truth the project files deny: if anyone
  ever wanted a non-Windows build, these are the exact call sites that throw `PlatformNotSupportedException`.
  `WindowsLocator` is the game-installer path — if the locator were ever called on a non-Windows
  platform, the registry access would throw.
* **Severity** Low
* **Fix** Either multi-target to `net9.0;net9.0-windows` so the analyzer is satisfied properly, or
  add a `RuntimeInformation.IsOSPlatform` guard and `<NoWarn>CA1416</NoWarn>` with that rationale.
  Do **not** suppress globally.
* **Confidence** High.

### B-08 · 32 obsolete-API warnings (CS0618 + SYSLIB) — three of them are security-relevant
* **Where / what**
  * `FSO.Server/Framework/Aries/AbstractAriesServer.cs:109` — **`SYSLIB0039`: `SslProtocols.Tls`**, i.e.
    TLS 1.0, flagged by the BCL as "has known vulnerabilities and is not recommended".
  * `FSO.Server.Protocol/Utils/SslClientFilter.cs:25` — **`CS0618`: `SslProtocols.Default`**,
    "has been deprecated and is not supported".
  * `FSO.Server.Protocol/Utils/ChallengeResponse.cs:51` + `FSO.Server.Common/PasswordHasher.cs:65` —
    **`SYSLIB0041`: `Rfc2898DeriveBytes`** with the default algorithm and iteration count
    (PBKDF2-HMAC-SHA1, **1000 iterations**).
  * 4 × `SYSLIB0023` `RNGCryptoServiceProvider` (`GithubController.cs:57`,
    `ChallengeResponse.cs:12` ×2, `PasswordHasher.cs:90` ×2) and 1 × `SYSLIB0021` `SHA256Managed`
    (`SaltedHash.cs:37`).
  * 6 × `SYSLIB0014` `new WebClient()` — obsolete, sync-over-async.
  * 8 × `CS0618` ASP.NET Core 2.x-era `IApplicationLifetime`, `IHostingEnvironment`,
    `CompatibilityVersion.Version_2_1` in `FSO.Server.Api.Core/{Program,Startup}.cs`.
  * `tso.vitaboy.model/Mesh.cs:137` — obsolete 6-arg
    `GraphicsDevice.DrawIndexedPrimitives`.
* **Why it matters** For Simitone itself, all of these are either in assemblies the trim removes
  (`FSO.Server*`, `FSO.Server.Protocol`, `FSO.Server.Api.Core`, `FSO.Server.Common`) or on
  code paths a singleplayer game never executes. **None is a live Simitone vulnerability.** But
  `PasswordHasher`/`ChallengeResponse` are the TSO auth path, and if `-ide` or the TSO server is
  ever run, TLS 1.0 and 1000-iteration PBKDF2 are real weaknesses. The `WebClient` deprecations in
  `tso.client` (`EmojiCache.cs:52`, `UIArchiveHostInformation.cs:279`,
  `UIWebDownloaderDialog.cs:24`) *are* in a shipped assembly, though not on a hot path.
* **Severity** Low for Simitone; High if the TSO server path is ever considered supported
* **Fix** Minimum: record in the fork docs that the TSO server stack is unsupported and is compiled
  only to be trimmed. Substantive: `SslProtocols.Tls12 | Tls13`, `RandomNumberGenerator.Create()`,
  `SHA256.Create()`, `Rfc2898DeriveBytes` with an explicit `HashAlgorithmName.SHA256` and
  ≥600 000 iterations, `HttpClient`, and the 4-arg `DrawIndexedPrimitives`.
* **Confidence** High.

### B-09 · `CS8073` in Simitone's own code — a null check that can never be false
* **Where** `Client/Simitone/Simitone.Client/UI/Services/NeighborhoodService.cs:957`
* **What's wrong**
  ```csharp
  return entity.Position != null && entity.Position.x == short.MinValue;
  ```
  `FSO.SimAntics/Marshals/VMEntityMarshal.cs:38` declares `public LotTilePos Position;` and
  `LotTilePos` is a **`struct`** (`FSO.LotView/Model/LotTilePos.cs:7`). C# lifts `!=` when one side
  is `null`, so `entity.Position != null` is **always true**.
* **Why it matters** Not a crash (a struct field is never `null`; the code is safe) — it is a
  **dead guard that documents a false belief**. The author clearly expected `Position` might be
  null, which means either the type changed under them or they read a different `Position` (there
  are several `Position` members in the marshal hierarchy). Left in place, the next person to make
  `Position` genuinely nullable inherits a guard that silently becomes correct in a form the
  compiler will no longer flag. This is exactly the sort of thing a 946-warning build hides.
* **Severity** Low (latent hazard, not a live bug)
* **Fix** Delete the `!= null` half. Then decide deliberately whether the method should be reading
  a *nullable* position from somewhere else — the doc comment says it detects
  `LotTilePos.OUT_OF_WORLD`, which is `new LotTilePos(-32768, -32768, 1)`, so matching on
  `.x == short.MinValue` is right and only the guard is wrong.
* **Confidence** High — types read from both declarations.

### B-10 · 5 × `CS8632` — nullable annotations used where they are ignored
* **Where** `FreeSO/TSOClient/tso.common/Audio/MP3Player.cs:19,20,32,33,285`
* **What's wrong** `FSO.Common` sets `<Nullable>disable</Nullable>`, but the file annotates
  reference types with `?` anyway. The annotations are silently discarded, so a reader (and the
  compiler) believes there is a null check where there is none.
* **Why it matters** The dangerous case: a `string?` in a `Nullable=disable` project looks
  null-checked to a human reviewer but has no effect on any warning.
* **Severity** Low
* **Fix** Add `#nullable enable` to the top of `MP3Player.cs` (or to the whole of
  `tso.common/Audio/`) so the annotations mean something.
* **Confidence** High.

### B-11 · Real defects in dead code (`FSO.Server.Api.Core`)
* **Where** `FSO.Server.Api.Core/Startup.cs:54` and `Utils/ApiResponse.cs:35,36`
* **What's wrong**
  * `MVC1005`: `UseMvc()` is called while endpoint routing is enabled. The analyzer states this
    combination is unsupported; in practice `UseMvc` after `UseRouting` throws or silently does
    nothing. The `Startup` also still sets `CompatibilityVersion.Version_2_1` (`ASP5001` ×2).
  * `ASP0019` ×2: `IHeaderDictionary.Add(key, value)` throws `ArgumentException` on a duplicate key.
    The fix is `Append` or the indexer.
* **Why it matters** Both are genuine bugs. Both are also entirely unreachable from Simitone:
  `Simitone.Windows.csproj:239-244` deletes `FSO.Server.Api.Core.dll`/`.exe` from the output. They
  exist only because `FSO.Client` drags the whole server closure into the build.
* **Severity** Low for Simitone (dead), Medium for upstream
* **Fix** Do not spend time here for Simitone. Delete the closure instead (see **B-12**). If you
  care about upstream FreeSO, fix `Startup.cs` and `ApiResponse.cs` there.
* **Confidence** High.

### B-12 · ~200 warnings and real compile time spent on assemblies that are then deleted
* **Where** `Simitone.Windows.csproj:219-336` (the `SingleplayerTrimFile` allowlist) vs. the
  `ProjectReference` closure it deletes from.
* **What's wrong** `FSO.Client` (the `-ide` path) `ProjectReference`s `FSO.Server`,
  `FSO.Server.Database`, `FSO.Server.DataService`, `FSO.Server.Clients`, `FSO.Server.Protocol`,
  `FSO.Server.Updater`, `FSO.Content.TSO`. That drags 9 more projects into every build, and the trim
  target then deletes `server.dll`, `FSO.Server.*`, `watchdog.*`, `MySql.Data.dll`,
  `System.Data.SqlClient.dll`, `System.Data.SQLite.dll`, `AWSSDK.*`, `Octokit.dll`, `RestSharp.dll`,
  `JWT.dll`, `crypto.dll`, `NLog.dll` and the native SSPI/SQLite `.dll`s.
* **Why it matters** Measured on this machine: **203 of 574 unique warnings (35 %)** come from those
  9 projects, including the whole of the NU1701/ASP/SYSLIB/TLS noise. The build is 10 m 39 s.
  None of that output survives to the game.
* **Severity** Medium (build time + signal-to-noise, and it is a *deliberate* tradeoff — the
  comments at lines 195-214 are careful and the opt-out is documented)
* **Fix** Split the game build from the `-ide` build: a `Simitone.Game.slnf` (solution filter) or a
  `-p:BuildIde=false` that drops the `FSO.IDE` reference and its closure. The trim target then
  becomes belt-and-braces instead of the primary mechanism. This is the single highest-leverage
  change to warning volume.
* **Confidence** High on the numbers; Medium on the solution-filter approach (FSO.UI's own
  references would need checking — `FSO.Client` is referenced by `FSO.IDE` and by nothing else in
  the sln, so it may already be separable).

---

## 4. Findings — build hazards and clean-machine traps

### C-01 · 4.1 GB of retail game data and 1.5 GB of redistributed EA content sit un-ignored at the repo root
* **Where** `D:\Simitone\SimsCompleteCollectionUSA\` (8 files, **2.59 GB**) and
  `D:\Simitone\mods\` (12,387 files, **1.50 GB**) — neither appears in `.gitignore`.
* **What's wrong**
  * `SimsCompleteCollectionUSA/` holds `Sims, The - Complete Collection (USA) (Disc 1..4).bin`
    (≈752 MB each) and the matching `.cue` files — **complete disc images of the retail game**.
  * `mods/` holds `Official_Maxis_HouseParty_HotDate_Lots/`,
    `Official_MaxisDLC_Families/`, `Official_MaxisObjectsDownloads/`, `Official_MaxisSkins/`
    (EA retail expansion content), plus `cto-gifts-prizes-amp-other-member-creations_archive.torrent`.
  Meanwhile `.gitignore:191-217` goes to real trouble to protect exactly this:
  > `# The user's copy of The Sims. NEVER commit this - it is copyrighted commercial data…`
  > `Sims1/`, `"The Sims"/`, `Sims1/**/*.iff`
* **Why it matters** The `.gitignore` is correct in intent and has two holes big enough to drive a
  4 GB `git add`. This is the most serious repository-hygiene item in the audit: it is a copyright
  exposure (redistributing EA's disc images and DLC), it is a repo-size problem, and a `.torrent`
  file is content most hosts and many corporate proxies will refuse or quarantine.
* **Severity** **High**
* **Fix** Immediately add to `.gitignore`:
  ```
  SimsCompleteCollectionUSA/
  *.cue
  *.bin
  mods/
  *.torrent
  ```
  then verify with `git status --ignored` / `git check-ignore -v` that nothing under those two
  folders is staged. If any of it was ever committed, `git rm -r --cached` it and rewrite history
  before the next push.
* **Confidence** High on the files existing and being un-ignored (there is no `.git` here, so I
  could not run `git status`; the `.gitignore` rules are exhaustive and none match these paths —
  `Sims1/` does not match `SimsCompleteCollectionUSA/`, and no rule mentions `mods/` or `*.bin`).
  **Medium** on whether they are actually tracked, because the audit root has no `.git`.

### C-02 · Two MonoGame backends, one assembly name — a `Delete`-then-`Copy` dance
* **Where** `Simitone.Windows.csproj:83-90` and `438-495`
* **What's wrong** `Simitone.Windows` references **both** `MonoGame.Framework.DesktopGL` and
  `MonoGame.Framework.WindowsDX` 3.8.5.1. Both packages contain a file called
  `MonoGame.Framework.dll`. `CopyMonoGameDLLs` therefore **deletes**
  `$(OutputPath)MonoGame.Framework.dll` (line 488) and copies the two payloads to
  `Monogame\Windows\` and `Monogame\WindowsGL\`, where `MonogameLinker.Link` picks one at runtime.
* **Why it matters** The root `MonoGame.Framework.dll` is deleted with `ContinueOnError="true"`,
  so a leftover root copy is a *silent* outcome. And the two `Copy` tasks at lines 491/494 have
  **no** `ContinueOnError` — see C-03.
* **Severity** Info (the design is deliberate and well documented; flagging it because it is fragile)
* **Fix** Already mitigated by the version-exact glob. Add a `<Error>` after line 488 if
  `$(OutputPath)MonoGame.Framework.dll` still exists, so a failed delete is loud rather than silent.
* **Confidence** High.

### C-03 · Two build tasks with no error tolerance: a running game can break the build
* **Where** `Simitone.Windows.csproj:491` and `:494` (`CopyMonoGameDLLs`) — and, more seriously, the
  SDK's own `_CopyOutOfDateSourceItemsToOutputDirectory` copying
  `Simitone.Client.csproj:14-37`'s globs.
* **What's wrong** Every *other* filesystem-mutating target in this project is careful:
  `PackageRemeshAddon` uses `ContinueOnError="WarnAndContinue"` on both `Exec`s and both `Touch`es;
  `TrimSingleplayerOutput` uses `ContinueOnError="true"` on `Delete` and `RemoveDir` and
  `WarnAndContinue` on the measure `Exec`. The two `Copy` tasks in `CopyMonoGameDLLs` are the
  exception — no `ContinueOnError`, so a locked destination is **MSB3021/MSB3025 and a failed
  build**.
  Worse, `Simitone.Client.csproj:14-37` globs `Content\**\*.fsom` — and
  `Content\MeshReplace` currently holds **5,916 `.fsom` files (8.1 MB)**. Every build re-copies
  thousands of content files through the *unprotected* SDK copy path.
* **Why it matters** This is exactly the "player's game is holding the output folder" scenario. A
  running Simitone has mesh/`.iff`/texture file handles open. The next build's content copy can fail
  with a copy error, and the error message points at MSBuild, not at "close the game".
* **Severity** Medium
* **Fix** (a) Add `ContinueOnError="WarnAndContinue"` to both `Copy` tasks in `CopyMonoGameDLLs`.
  (b) If lock collisions are actually observed, either set
  `<CopyRetryCount>`/`<CopyRetryDelayPeriodMS>` on the content items, or change the content globs
  to a single `_CopyOutOfDateSourceItemsToOutputDirectory`-friendly `Link` + `SkipUnchangedFiles`
  (the SDK already uses `SkipUnchangedFiles=true` for the default items — the cost here is the
  5,916-item *incremental check*, not the copy). (c) Document "close the game before rebuilding"
  in the README's build section.
* **Confidence** High on the missing `ContinueOnError` (read directly); Medium on whether a running
  game actually holds these files (I did not attempt to detect open handles — that would have
  required inspecting the running process).

### C-04 · The trim allowlist contains TFM-version-dependent paths that fail **silently**
* **Where** `Simitone.Windows.csproj:280` and `:314`
  ```xml
  <SingleplayerTrimFile Include="runtimes\win\lib\net9.0\System.Runtime.Caching.dll" />
  <SingleplayerTrimFile Include="runtimes\win\lib\net8.0\System.Data.SqlClient.dll" />
  ```
* **What's wrong** Both paths embed a *target framework version* of a transitively-pinned package
  (`System.Runtime.Caching 9.0.9` ships `runtimes/win/lib/net9.0/`;
  `System.Data.SqlClient 4.9.0` ships `runtimes/win/lib/net8.0/`). Lines 400-402 do
  `<_TrimFile Remove="@(_TrimFile)" Condition="!Exists(...)" />` — a missing entry is dropped from
  the list **without a warning**, by design ("an SDK upgrade that stops shipping one of these must
  not break the build").
* **Why it matters** That design goal and this hazard are in direct conflict. Bump
  `System.Runtime.Caching` to 10.0.x and the path becomes `net10.0`; the entry silently stops
  matching; `System.Runtime.Caching.dll` is **no longer removed from the output** and nobody finds
  out, because the "nothing on the allowlist was present" message at line 421 only fires when
  *nothing at all* matched. A partially-applied trim is worse than none: it ships TSO server
  dependencies while looking like it trimmed.
* **Severity** Medium
* **Fix** Add a self-check: at the end of `TrimSingleplayerOutput`, compare the count of
  `@(SingleplayerTrimFile)` that matched against the declared count and emit a
  `<Warning>` (not an error) listing entries that were dropped as missing. Even better, for these
  two, glob by filename rather than by full path:
  `<SingleplayerTrimFile Include="runtimes\win\lib\**\System.Runtime.Caching.dll" />`.
* **Confidence** High — the `Remove` on `!Exists` at line 400 is explicit.

### C-05 · Both `PackageRemeshAddon` and `TrimSingleplayerOutput` run in **Debug** too, contradicting the docs
* **Where** `Simitone.Windows.csproj:152-156` and `:382-384`
* **What's wrong** Neither target has a `'$(Configuration)' == 'Release'` condition. Only
  `TrimSingleplayerOutput`'s *default* is unconditional:
  ```xml
  <TrimSingleplayerOutput Condition=" '$(TrimSingleplayerOutput)' == '' ">true</TrimSingleplayerOutput>
  ```
  But `README.md:39-40` says *"The **Release** build also trims the TSO server stack and non-Windows
  natives"*, and `README.md:34-35` says *"the **Release** build flattens it into
  `Content/MeshReplace`"*. The csproj's own comment at line 141-142 repeats the claim.
* **Why it matters** A developer on a Debug build gets the server stack silently deleted from
  `bin\Debug\…`, and cannot step into the TSO server or the remesh packager while debugging. The
  CI workflow offers `Debug` as a `workflow_dispatch` choice (`build.yml:15-16`), so this is
  reachable from CI too. It is a documentation/behaviour divergence, and the docs are the
  authoritative thing a new contributor reads.
* **Severity** Medium
* **Fix** Either add the `Configuration` guard to match the docs, or change the docs to say
  "every configuration". The former is almost certainly the intent — add
  `Condition="'$(Configuration)' == 'Release' And ..."` to both targets, or default
  `TrimSingleplayerOutput` to `false` unless `Release`.
* **Confidence** High — the absence of a Configuration condition is verifiable by reading the
  16 `Condition=` attributes in the file, none of which mention `$(Configuration)`.

### C-06 · `generate-meshes.ps1` hardcodes two target-framework literals
* **Where** `generate-meshes.ps1:102` and `:241`
  ```powershell
  $ToolDll = Join-Path $RepoRoot "Client\Simitone\Simitone.GenerateMeshes\bin\$Configuration\net10.0\Simitone.GenerateMeshes.dll"
  $binDir = Join-Path $RepoRoot "Client\Simitone\Simitone.Windows\bin\$Configuration\net10.0-windows\Content\MeshReplace"
  ```
* **What's wrong** `net10.0` and `net10.0-windows` are duplicated from the two `.csproj` files,
  which are the real source of truth. The script's only protection is a `Test-Path` that throws
  *"Generator not found at …"* (line 193-195) — confusing, and the `-NoBuild` path (line 187) has
  no way to recover.
* **Why it matters** A TFM bump breaks the mesh-authoring tool with an error that points at the
  wrong file. This is the classic drift that a `Directory.Build.props` with a
  `<SimitoneTargetFramework>` property would eliminate.
* **Severity** Low
* **Fix** Either hardcode once and reference, or ask MSBuild:
  `& dotnet msbuild $ToolProject -getProperty:TargetFramework` (supported on SDK 8+). Simplest
  robust version: `Get-ChildItem (Join-Path $RepoRoot 'Client\Simitone\Simitone.GenerateMeshes\bin') -Recurse -Filter 'Simitone.GenerateMeshes.dll' | Select -First 1`.
* **Confidence** High.

### C-07 · `generate-meshes.ps1` probes a machine-specific absolute path
* **Where** `generate-meshes.ps1:122-126`
  ```powershell
  $candidates = @('D:\Program Files\Sims', 'C:\Program Files\Sims', 'C:\Program Files (x86)\Sims')
  ```
* **What's wrong** `D:\Program Files\Sims` is one developer's layout, hardcoded ahead of the portable
  `$env:ProgramFiles` derivations on lines 127-128.
* **Why it matters** Low — every candidate is `Test-Path`-guarded and there is a clear error at
  line 137. But it leaks a personal machine layout into a shared repo and adds a pointless stat on
  every run.
* **Severity** Low
* **Fix** Drop the `D:\` entry, or move it to the end of the list.
* **Confidence** High.

### C-08 · The dead MonoGame content pipeline (the known commented-out build step)
* **Where** `FreeSO/TSOClient/tso.content/FSO.Content.csproj:27` and `:83-102`
  ```xml
  <!-- <PackageReference Include="MonoGame.Content.Builder.Task" Version="3.8.4" /> -->
  ...
  <!-- <ItemGroup>
         <None Update="ContentSrc\TSOClientContent.mgcb"> ... </None>
         <None Update="ContentSrc\msdfgen.exe"> ... </None>
         ...
       </ItemGroup> -->
  ```
* **What's wrong** The MonoGame content-builder task and the whole `.mgcb` compile step are
  commented out. Also `FSO.Content.csproj:60-62` still copies `Content\iOS\**\*.*` into a Windows
  build, and `:63-68` copies both `Content\DX\` and `Content\OGL\`.
* **Why it matters** **This is correct for Simitone and must stay that way.** Simitone and FreeSO
  load raw `.iff`/`.fsom`/`.piff` at runtime and ship no `.xnb` at all; enabling
  `MonoGame.Content.Builder.Task` would add an MGCB pass to every build for zero output and a new
  failure mode. The `MSDFExtension` project (which *does* carry
  `MonoGame.Framework.Content.Pipeline 3.8.4`) is not in any solution.
  Flagged because it is exactly the kind of commented-out block that gets "helpfully"
  un-commented by a future contributor.
* **Severity** Info (correct as-is)
* **Fix** Add a one-line comment above line 27 saying explicitly that the content pipeline is
  deliberately disabled because Simitone ships no XNB and loads `.iff` directly, so nobody
  "fixes" it. Optionally drop the `Content\iOS` glob.
* **Confidence** High.

### C-09 · `<EmbeddedResource>` carrying `<CopyToOutputDirectory>`
* **Where** `Simitone.Windows.csproj:77-80`
  ```xml
  <EmbeddedResource Include="Icon.bmp">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </EmbeddedResource>
  ```
* **What's wrong** `CopyToOutputDirectory` is meaningless on an `EmbeddedResource` — the SDK does not
  copy embedded resources to the output. The metadata is a silent no-op.
* **Why it matters** Trivial in effect (the bitmap is embedded, which is what the build actually
  does) but it misleads: a reader concludes `Icon.bmp` lands next to the exe, and if some future
  change loads it from disk it will not be there. Nothing in `Simitone.Windows`' code references
  `Icon.bmp` or `Icon.ico`; the icon comes from `<ApplicationIcon>Icon.ico</ApplicationIcon>`.
* **Severity** Low
* **Fix** Delete the metadata, and delete `Icon.bmp` (262 KB) if nothing embeds it meaningfully.
* **Confidence** High on the no-op; Medium on whether `Icon.bmp` is loadable as a resource.

### C-10 · `NuGetGlobalPackages` overridden in the csproj, where NuGet's own value belongs
* **Where** `Simitone.Windows.csproj:190-193`
  ```xml
  <NuGetGlobalPackages Condition=" '$(OS)' == 'Windows_NT' ">$(USERPROFILE)\.nuget\packages</NuGetGlobalPackages>
  <NuGetGlobalPackages Condition=" '$(OS)' != 'Windows_NT' ">$(HOME)/.nuget/packages</NuGetGlobalPackages>
  ```
* **What's wrong** `NuGetGlobalPackages` is a NuGet-owned property. Overriding it in a csproj
  hard-codes `%USERPROFILE%\.nuget\packages` and therefore **ignores `NUGET_PACKAGES`**, a
  `nuget.config` `<config><add key="globalPackagesFolder" …/></config>` setting, or `RestorePackagesPath`.
  It is also a *project*-scoped override, so it only applies to this one project while the 23 other
  projects in the graph use whatever NuGet chose. In practice the fallback is dead code, because
  `CopyMonoGameDLLs:443` prefers `$(NuGetPackageRoot)` (which restore sets) and only falls back to
  this. The `!= 'Windows_NT'` branch is unreachable in practice: this project is `net10.0-windows`.
* **Why it matters** The stated intent ("this repo's own fallback for non-Windows shells") is
  unreachable — you cannot build a `net10.0-windows` WinExe on a non-Windows shell. What it
  actually does is risk a split between this project's restore folder and the rest of the graph.
* **Severity** Low
* **Fix** Delete lines 190-193 entirely. `$(NuGetPackageRoot)` is always set by restore, which is
  what line 443 already uses. If a fallback is genuinely wanted, put it in a repo-root
  `NuGet.config` — which the repo does not have at all (**H-06**).
* **Confidence** Medium — I did not trace NuGet's property-precedence rules inside restore.

### C-11 · The `.sln` still carries Xamarin-era solution configurations
* **Where** `Client/Simitone/Simitone.sln:56-65` and 400+ `ProjectConfigurationPlatforms` lines
* **What's wrong** `SolutionConfigurationPlatforms` declares `Ad-Hoc|Any CPU`, `Ad-Hoc|iPhone`,
  `Ad-Hoc|iPhoneSimulator`, `Ad-Hoc|x86`, `Ad-Hoc|x64`, `AppStore|…` — five platforms × four
  configurations = 20 solution configurations for 24 SDK-style projects that only ever build
  `Debug|Any CPU` and `Release|Any CPU`. Roughly 620 of the sln's 1,045 lines are this.
* **Why it matters** Pure noise that hides real project state in every sln diff, and it means
  `dotnet build -c Debug -p:Platform=x64` silently produces a `Debug|x64` configuration that
  nobody has ever defined, mapping `ActiveCfg` to `Debug|Any CPU` in some cases and
  `Ad-Hoc|Any CPU` in others (see lines 86-87 vs 107).
* **Severity** Low
* **Fix** Regenerate the sln from `dotnet sln add` against a fresh file, or prune the
  `ProjectConfigurationPlatforms` global section to the two configurations in use.
* **Confidence** High.

### C-12 · Non-SDK / `packages.config` projects that cannot build on a clean machine
* **Where** `FreeSO/TSOClient/FSO.iOS/FSOiOS.csproj`, `FreeSO/TSOClient/FSODroid/FSODroid.csproj`
  (both with `packages.config` and `HintPath` entries pointing at `..\packages\…`,
  `..\tso.client\bin\WindowsGL\Release\*.dll`, and
  `..\..\Other\libs\FSOMonoGame\MonoGame.Framework\bin\iOS\iPhone\MonoGame.Framework.dll` — a
  path that does not exist in this tree); also `FreeSO/TSOClient/System.Drawing`,
  `FSO.Server.Framework`, `FreeSO/Other/libs/{CityDataModel,LoginDataModel,TargaImage/VS2008}`.
* **What's wrong** These are Xamarin/MonoTouch-era projects referencing `Xamarin.iOS`,
  `Mono.Android`, `Shim`, `DbLinq`, `Newtonsoft.Json 12.0.2` and MonoGame 3.5.1 binaries by path.
* **Why it matters** They are **not in `Simitone.sln`** and not in `FreeSO.sln`'s modern set, so
  nothing Simitone builds touches them — the audit confirmed all 24 sln projects are reachable.
  But they are the reason `FreeSO/TSOClient/FreeSO.sln` is not a usable "build the submodule"
  reference, and they are where a `git submodule` bump goes to die.
* **Severity** Info
* **Fix** Nothing required. Optionally add a `README` note in `FreeSO/TSOClient/` listing which
  projects are buildable, so nobody tries `msbuild FreeSO.sln`.
* **Confidence** High.

### C-13 · Committed binary DLLs with `HintPath` references
* **Where** `FSO.Client.csproj:163-168` → `NVorbis.dll` (66 KB), `OpenTK.dll` (**4.1 MB**);
  `FSO.LotView.csproj:36-37` → `Mario.dll` (353 KB);
  `FSO.Server.csproj:53-54` → `..\tso.world\Mario.dll`;
  `FSO.UI.csproj:50-51` → `Dependencies\GOLDEngine.dll` (31 KB).
  All four files are present in the tree.
* **What's wrong** Four unversioned, un-restored binary dependencies committed as raw files with
  `HintPath` assembly references. There is no `PackageReference`, so NuGet knows nothing about them
  — no version, no transitive graph, no security metadata, and the runtime does not get a
  `.deps.json` entry that can be audited.
* **Why it matters** They build today only because the binaries are committed. A `git clean -xfd`
  on a *partial* clone (`--filter=blob:none`, which `actions/checkout` can be configured to use)
  would fetch them lazily, so CI survives — but any `.gitignore` change that catches `*.dll` breaks
  the build with `MSB3245: Could not resolve this reference`. `OpenTK.dll` at 4.1 MB is a very old
  X11-era build; if anything in FSO still touches OpenTK it is shipping an unmaintained native-adjacent
  library into a Windows game. `GOLDEngine.dll` and `Mario.dll` are third-party Sims-format
  libraries with their own licences that need attribution.
* **Severity** Medium
* **Fix** Prefer `PackageReference` (NVorbis and OpenTK both have maintained packages) or, if the
  binaries must stay, add `**/Dependencies/*.dll` style exceptions to `.gitignore` and record the
  version + licence + source URL of each next to it. Audit whether `OpenTK` is still reachable at
  all — if not, delete the reference and the 4.1 MB.
* **Confidence** High on the mechanism; Medium on whether OpenTK is still called.

---

## 5. Findings — repository hygiene, CI and licensing

### H-01 · **No LICENSE file at the Simitone root**, and no licence text in the build output
* **Where** `D:\Simitone\` contains `.gitattributes`, `.gitignore`, `.gitmodules`, `global.json`,
  `README.md`, `generate-meshes.ps1` — **and no `LICENSE`**. The only licence in the tree is
  `FreeSO/LICENSE.md` = **Mozilla Public License 2.0**, inside the submodule.
* **What's wrong** Simitone is a derivative of FreeSO (a fork: `riperiperi/Simitone` builds
  directly on FreeSO's `tso.client`/`tso.simantics`/`tso.world` sources, which the `.sln` proves).
  MPL-2.0 §3.1 requires the licence to be made available with any distribution of the software, and
  §3.2/§3.3 require modified MPL files to remain available under MPL. The **shipped artefact**
  `SimitoneWindows.zip` (README:3) contains `FSO.Common.dll`, `FSO.SimAntics.dll`, `FSO.LotView.dll`,
  `FSO.Files.dll`, `FSO.UI.dll`, `FSO.Vitaboy*.dll`, `FSO.Content.dll` etc. I checked
  `bin\Release\net10.0-windows\` for any `LICENSE*`/`NOTICE*`/`COPYING*` file: **none is present**.
* **Why it matters** The project is distributing MPL-covered binaries with no licence text
  anywhere in the repository or the output, and the fork's own terms are unstated. There is a
  partial precedent in the tree: `FreeSO/TSOClient/tso.content/Content/UI/LICENSE.txt` (MIT,
  `Mu-An Chiou`) *is* correctly copied to the output via
  `FSO.Content.csproj:48-50` — so the project already knows how to do this and hasn't for the
  MPL-covered code.
* **Severity** **High**
* **Fix** (1) Add a root `LICENSE` stating Simitone's terms and explicitly noting that the
  `FreeSO/**` subtree and the derived `FSO.*` assemblies remain **MPL-2.0**. (2) Add a
  `<Content Include="LICENSE"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>`
  to `Simitone.Windows.csproj` so every release zip carries it. (3) Add attribution to
  `README.md`'s existing *Attributions* section (it currently lists only the Icons8 eyedropper)
  for: FreeSO/MPL-2.0, `GOLDEngine`, `Mario`, `NVorbis`, `OpenTK`, `GOLDEngine`, `Mu-An Chiou`'s
  emoji assets, and the FSO.Remeshes pack authors.
* **Confidence** High that no root licence exists and none is in the output. **Medium** on the exact
  MPL-2.0 clause numbers (paraphrased from memory, not re-read against the licence text — the
  15 KB `FreeSO/LICENSE.md` was opened but not fully parsed). A licence review is warranted.

### H-02 · `.DS_Store` and `Simitone.lnk` at the repo root, neither ignored
* **Where** `D:\Simitone\.DS_Store` and `D:\Simitone\Simitone.lnk`
* **What's wrong** `.DS_Store` is macOS Finder cruft (binary, changes for no reason, produces noisy
  diffs and is a malware-scanner false-positive magnet). `Simitone.lnk` is a Windows shortcut whose
  target is an absolute path on one machine's disk.
* **Why it matters** Neither is in `.gitignore` (which covers `*.suo`, `*.user`, `*~`, `~$*` but
  not `.DS_Store` or `*.lnk`). They will be committed on the first `git add -A`. A `.lnk` in a
  repo is also a mild security smell — Windows will warn on any repo containing one.
* **Severity** Low
* **Fix** Add `.DS_Store`, `Thumbs.db`, `*.lnk` to `.gitignore`; delete the two files.
* **Confidence** High.

### H-03 · The `.gitignore` policy for generated meshes is self-contradictory
* **Where** `.gitignore:215-217`
  ```
  # Meshes produced by generate-meshes.ps1 (machine-local, derived from the user's
  # own game data; distribute them separately, never commit them).
  GeneratedMeshes/
  ```
  vs. `Client/Simitone/Simitone.Client/Content/MeshReplace/` (**5,916 `.fsom`, 8.1 MB**) which
  `generate-meshes.ps1:240` writes to under `-InstallOutput`, and which
  `generate-meshes.ps1:235` and `:58-60` describe as *"the client's **committed**
  `Content\MeshReplace`"*.
* **What's wrong** Two different policies for the same artefacts, both documented. The scratch
  output is correctly ignored; the shipped copy is (deliberately) committed. The stated reason for
  the ignore rule — *"derived from the user's own game data"* — applies equally to the committed
  folder.
* **Why it matters** Not a legal problem (`.fsom` meshes are generated geometry, not EA assets), but
  it means the repo carries 5,916 generated files that the project also insists must not be
  committed, and it is the reason the build spends time on thousands of content items (C-03).
* **Severity** Low
* **Fix** Pick one. If `Content/MeshReplace` is genuinely meant to be committed (it is the shipped
  fallback pack, so probably yes), move it under a clearly-labelled section and reword the
  `GeneratedMeshes/` comment to say the rule applies to the *scratch* folder only.
* **Confidence** High.

### H-04 · The CI workflow and the actual build commands
* **Where** `.github/workflows/build.yml`
* **Assessment — mostly correct, four real issues:**

  | # | Finding | Severity | Fix |
  |---|---|---|---|
  | a | **Submodule/vendoring contradiction.** `submodules: recursive` with the comment *"FreeSO is vendored in this tree, but it is still a submodule, so the checkout has to populate it — the solution file references FreeSO's projects directly."* If FreeSO is **tracked as ordinary files** in this repo, `actions/checkout` with `submodules: recursive` will run `git submodule update`, which **replaces the directory with the pinned commit and discards the vendored state**. If it is a real submodule, the comment is misleading. Either way the two facts cannot both be true, and getting it wrong silently reverts FreeSO. | **High** | Resolve the ambiguity: either make it a real submodule (and pin the commit deliberately, noting `7e76c196` in the docs) or drop `submodules:` and the comment. Add a CI step that asserts the FreeSO commit it built against, e.g. `git -C FreeSO rev-parse HEAD`. |
  | b | `dotnet test … -c …` has no `--no-build` and no `--no-restore`, so it **restores and rebuilds the entire FreeSO closure a second time** (~11 min total instead of ~11). | Low | `dotnet test … --no-build --no-restore` after the explicit build step. |
  | c | The `Test` step runs even for `workflow_dispatch` with `configuration: Debug`, but the `Build` step's `-c ${{ inputs.configuration \|\| 'Release' }}` is correct for all event types. No bug — noting it because `${{ inputs.* }}` in a `push` context evaluates to empty and the `\|\| 'Release'` fallback is doing real work that is easy to break. | Info | Consider `${{ inputs.configuration }}` → an `env:`-level `CONFIGURATION` computed once. |
  | d | **No warning gate at all.** CI never fails on a new warning. Given 946 warnings and B-01/B-02, the very first thing to do is make the *number* visible. | Medium | Add `-warnaserror:NU1701` in a follow-up job, or at minimum emit `dotnet build … 2>&1 | grep -oP 'warning \K[A-Za-z]+\d+' | sort | uniq -c` as a build-log step so a regression is visible in the run. Also: no NuGet cache (`actions/cache` keyed on `**/packages.lock.json` or the csproj hashes) — 11 minutes of restore+build on every push, every time. | 

  One thing CI gets **right** and worth keeping: the artifact path
  `Client/Simitone/Simitone.Windows/bin/${{…}}/net10.0-windows/` matches the real
  `TargetFramework` exactly, and `if-no-files-found: warn` is a reasonable choice for a job whose
  point is a green check, not a release.
* **Severity** see table
* **Confidence** High for b/c/d; **Medium** for (a) because the audit root has no `.git`, so I
  could not determine whether `FreeSO` is tracked as a submodule or as vendored files.

### H-05 · `global.json` has no `rollForward` safety net for the *runtime*, and no `allowPrerelease`
* **Where** `global.json`
  ```json
  { "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }
  ```
* **What's wrong** `latestFeature` will happily roll forward to 10.0.**999**, including previews if
  `allowPrerelease` is ever turned on. There is no `"runtime"` or `"rollForward"` entry, so a
  machine with only the 9.0 runtime can build but not *run* anything net10.0 — including
  `dotnet test`, if it were ever pointed at a net9.0 test project.
* **Why it matters** For a repo whose CI installs `10.0.x` and whose contributors are on varying
  SDKs, this is currently the right choice (it is what lets 10.0.401 satisfy a 10.0.100 pin, which
  it does on this machine). The finding is that it is *loose* in a way that will surface the day
  .NET 10.1 ships with a breaking SDK change.
* **Severity** Info
* **Fix** Consider `"rollForward": "latestFeature"` (keep) plus an explicit
  `"allowPrerelease": false`, and add the pinned SDK to the README's build prerequisites so a
  contributor knows what to install. Optionally set
  `<CheckEolTargetFramework>false</CheckEolTargetFramework>` deliberately (or leave it — the default
  true is right).
* **Confidence** High.

### H-06 · No `NuGet.config` anywhere in the repo
* **Where** repo root, `Client/`, `FreeSO/` — none found
* **What's wrong** Restore resolves against whatever the machine's global `NuGet.config` says, with
  no repo-level source pinning, no `packageSourceMapping`, and no `globalPackagesFolder`
  declaration. The 42 direct packages plus the NU1701 transitives (`Common.Logging`, `Mina`,
  `xxHashSharp`, `FreeSO.HandEvaluator`, `Portable.BouncyCastle`…) all come from **nuget.org by
  default**, and several of them are unmaintained.
* **Why it matters** A machine with a private feed configured (or an air-gapped mirror) will
  restore differently, and nothing in the repo records the intent. A `packageSourceMapping` would
  also let you pin the risky packages to nuget.org explicitly.
* **Severity** Low
* **Fix** Add a minimal repo-root `NuGet.config` with `<clear />` + `nuget.org` and a comment
  explaining why; delete `Simitone.Windows.csproj:190-193` at the same time (C-10), which is the
  thing it would otherwise have duplicated badly.
* **Confidence** High.

### H-07 · `content` at the repo root is a runtime log directory
* **Where** `D:\Simitone\Content\` containing only `logs\`
* **Assessment** `.gitignore:220-221` covers `*.log` and `logs/`, so this is handled. The directory
  itself is not ignored (only its contents). No action needed beyond an optional `Content/logs/`
  entry. **Severity** Info, **Confidence** High.

### H-08 · Suppression / hygiene inventory (the good news, and the gaps)

| Signal | Count | Where | Verdict |
|---|---:|---|---|
| `#pragma warning disable` in `Client/**` | **0** | — | Excellent |
| `#pragma warning disable` in `FreeSO/TSOClient/**` | 1 | — | Negligible |
| `[SuppressMessage]` anywhere | **0** | — | Excellent |
| `#nullable enable/disable/restore` directives | **0** | — | Consistent (project-level only, B-03) |
| `<NoWarn>` in the tree | 1 | `Simitone.Windows.csproj:52` | Well-argued + test-backed (B-04) |
| `[Obsolete]` attributes in `Client/**` | **0** | — | Clean; no deprecated API is being re-exported |
| `NotImplementedException` in `Client/**` | 0 (1 mention, in a test *comment*) | `BitmapDecoderTests.cs:120` | Clean |
| `TODO`/`FIXME`/`HACK`/`XXX` in `Client/**` | 15 occurrences across 13 files | `UIBuyBrowsePanel` (2), `UIObjectHolder` (2), then 1 each in `NeighbourhoodAnimations`, `SimitoneGame`, `TS1CASScreen`, `TS1GameScreen`, `UIHeadlineRenderer`, `UILotControl`, `UILotControlTouchHelper`, `UIRelationshipSubpanel`, `UITS1NeighborhoodVisitLotPanel`, and 3 test files | Low and appropriate for an actively developed fork |
| `.editorconfig` | 1, at `FreeSO/TSOClient/FSO.Server/Admin/` | — | Effectively none. No `dotnet_diagnostic` severities, no `IDE0xxx` triage, no formatting rules |
| `Directory.Build.props` / `Directory.Build.targets` / `Directory.Packages.props` | **none** | — | This is the root cause of A-01, A-04, C-06 and the `LangVersion` inconsistency |
| `.ruleset` / `stylecop.json` | none | — | — |
| `TreatWarningsAsErrors` / `WarningsAsErrors` | none | — | See H-04(d) |
| `NuGet.config` | none | — | H-06 |

**Severity** Low · **Confidence** High

### H-09 · Attributing the two 4.1 MB and 353 KB committed binaries
* **Where** C-13 (`OpenTK.dll`, `Mario.dll`, `GOLDEngine.dll`, `NVorbis.dll`)
* **Assessment** Folded into C-13. The specific ask is that the README's *Attributions* section
  gain an entry per committed binary, with version and licence, as the free alternative to
  converting them to `PackageReference`. **Severity** Low, **Confidence** Medium (I did not read the
  embedded licence resources of the DLLs).

---

## 6. Suggested order of work

**Do this week (cheap, high value, no code risk):**

1. **C-01** — add the four missing `.gitignore` lines and verify nothing is staged. (Legal exposure.)
2. **H-01** — add a root `LICENSE` + copy it to the output. (Legal exposure.)
3. **H-02** — `.DS_Store`, `*.lnk`, `.DS_Store`, `Thumbs.db`.
4. **A-08** — delete the three dead legacy `PackageReference`s.
5. **B-01** — set `IsAotCompatible=false` / add the IL `NoWarn` and delete the duplicated
   declarations. Drops ~176 warnings, i.e. ~31 % of the total, for a one-file change.
6. **B-05/C-05** — add the `CS0436` and `Configuration` conditions **with** the rationale
   comments, in the style the WFO0003 `NoWarn` already sets.

**Next (structural, one PR each):**

7. **B-12** — split the game build from the `-ide` build (solution filter or `BuildIde=false`).
   Biggest single reduction in build time and warning volume.
8. **B-02/A-05/A-10** — work up the NU1701 list, most-maintained-first, then add
   `WarningsAsErrors` for restore so it cannot regress.
9. **C-04** — make the trim's silent-miss self-reporting, and de-TFM-ify the two `runtimes\…` paths.
10. **A-01 + A-06** — one MonoGame version, resolved from `project.assets.json`, asserted at build time.
11. **C-03** — `ContinueOnError` on the two `Copy` tasks + a README note.

**Then (per-submodule, upstream fixes — file as FreeSO issues, do not fork-patch):**

12. **A-03** (Roslyn 3.4-beta), **A-07** (SqlClient/SQLite), **B-06** (`Rotation` override),
    **B-08** (TLS 1.0, PBKDF2 1000 iterations), **B-11** (`UseMvc` + endpoint routing,
    `IHeaderDictionary.Add`).

**Housekeeping worth a single commit:**

13. **H-06** `NuGet.config` + delete **C-10**'s `NuGetGlobalPackages` override.
14. **C-09** dead metadata, **C-07** stray `D:\` path, **C-11** sln pruning.
15. **H-08** an `.editorconfig` with `dotnet_analyzer_diagnostic` severities, so the 45 real warnings
    can be baselined and the next 45 can be errors.

---

## 7. What I checked and found clean

Recording these so a follow-up audit does not repeat the work:

* **Build health.** `dotnet build Simitone.sln -c Release -t:Compile` succeeds with **0 errors** in
  10 m 39 s. There are no MSB30xx (copy-error) or MSB3021/3030 (locked file) warnings in this
  build.
* **No dead projects in the solution.** I walked the `ProjectReference` graph from
  `Simitone.Windows` and `Simitone.Tests` (including the two
  `ReferenceOutputAssembly="false"` build-order-only edges) and confirmed **all 24** sln projects are
  reachable. `Simitone.GenerateMeshes` and `Simitone.PackageRemeshes` are correctly absent from the
  sln (they are pulled in as build-only references) and still build.
* **NuGet resolution is coherent.** `Simitone.Windows/obj/project.assets.json` resolves exactly one
  version each of MonoGame.DesktopGL/WindowsDX (3.8.5.1) and `SixLabors.ImageSharp` (3.1.11). No
  NU1605 (version conflict detected), no NU1608 (version constraint exceeds), no NU1702, no
  NU1902/1903/1904 (vulnerability advisories) in the build output.
* **The remesh-pack globs match the real pack layout.** `RemeshPackDir\*\*\*.fsom` and
  `\*\*\metadata.json` correctly match `remeshes\<Author>\<Group>\<file>` (38 authors, 420 groups,
  2,877 `.fsom`). The deliberate two-level depth limit in the csproj comment is accurate.
* **`Simitone.Client`'s content globs all match real files**: 245 `.png`, 4 `.piff`, 5,918 `.fsom`,
  1 `.fsov`, 1 `.cst`, 2 `.wav`, 18 `.uis` under `Content\`.
* **The content-policy `.gitignore` rules for the Sims data work**: `.tools/`,
  `FSO.Remeshes-main/`, `FSO.Remeshes/`, `GeneratedMeshes/`, `Sims1/`, `"The Sims"/` are all present
  and all present-in-the-tree folders are correctly excluded — *except* the two in C-01.
* **Suppressions in Simitone's own code: essentially none** (H-08). 31 warnings across
  `Simitone.Client` + `Simitone.Tests`, **0** in `Simitone.Windows`, and none of them a suppressed
  class of problem. 14 of the 15 `Simitone.Client` warnings are unused-variable/field noise; the
  two that matter are B-06 and B-09.
* **The singleplayer trim target is well built.** Explicit file list (no globs over the output),
  measures-then-deletes ordering, `ContinueOnError` on every filesystem mutation, absent files
  dropped rather than fatal, an opt-out switch, and a documented "if it stops being true the smoke
  test is the backstop" policy. The only defect is C-04's two TFM-pinned paths.
* **`App.manifest` DPI reasoning is documented and test-guarded** — `HighDpiConfigurationTests`
  reads both the manifest and the csproj and fails if they drift to PerMonitorV2. This is the
  highest-quality comment-to-code ratio in the repository.
* **The comment quality in `Simitone.Windows.csproj` and `Simitone.Client.csproj` is exceptional** —
  the MonoGame dual-backend rationale, the WFO0003 argument, the FSO.Client-reference
  non-reference argument, the content-scoping rationale for each glob, the trim allowlist's
  provenance. Where these files are wrong (C-05), it is that they say "Release" and the code does
  not check the configuration.
* **FreeSO's own content licence is handled correctly** — `tso.content/Content/UI/LICENSE.txt`
  (MIT, Mu-An Chiou) is explicitly copied to the output (`FSO.Content.csproj:48-50`). The pattern
  exists; it just was not applied to the MPL-covered code (H-01).
