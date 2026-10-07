# MESH_INGESTION_REPORT.md

Validation and ingestion of the offline-generated 3D meshes from `Simitone.GenerateMeshes`
into the shipping game. Written by the ingestion/validation pass; the generator and
`MESH_GENERATION_RESEARCH.md` remain the source of truth for *how* the meshes are made, this
document records *whether they are good* and *where they went*.

Snapshot: **2026-09-26 23:55**. The generator (pid 37012) was **still running** throughout, so
every count here is a snapshot of a moving target. See [Re-running the sync](#re-running-the-sync).

> **Superseded.** The generator has since exited and the set was re-validated and fully ingested
> to its final 8,648 files. The counts in §1, §5 and §6 below are the mid-run snapshot; the
> authoritative numbers are in [Final sync](#final-sync-2026-09-27-0337).

---

## 1. Headline

| | |
|---|---|
| Generated meshes examined | **6,104** (1.69 GB, 69.3 M triangles, 54.1 M vertices) |
| Structurally valid (loadable, non-degenerate) | **6,104 (100.0 %)** |
| **Hard-invalid** (rejected, not ingested) | **0** |
| Carrying a NaN-normal defect (see [§4](#4-the-one-real-defect-non-finite-normals)) | **1,520 (24.9 %)** — ingested anyway |
| Filenames naming a drawgroup that really exists in the TS1 install | **6,104 / 6,104 (100 %)** |
| Stored `(PixelDir, PixelSPR)` sprite references in range for that rotation | **all of them (100 %)** |
| Ingested to `Client/Simitone/Simitone.Client/Content/MeshReplace/` | **6,110** (final sync; the generator produced 43 more in the minutes after - see [Re-running the sync](#re-running-the-sync)) |
| Shipped in `bin\Release\net10.0-windows\Content\MeshReplace\` | **8,773 `.fsom`** = 6,110 generated + 2,663 community pack, 0 overlap, 0 missing |
| `dotnet build Simitone.Windows.csproj -c Release` | succeeded, 0 errors |
| `dotnet test Simitone.Tests.csproj -c Release` | **1,676 passed, 0 failed** |

The single most important result: **there were no malformed, truncated, degenerate or
unloadable files in the whole 6,104-file set.** Every file is a byte-exact, well-formed FSOM
that `DGRP3DMesh` reads to completion, with in-range indices, finite positions and UVs, sane
bounds, and a filename that resolves to a real object/drawgroup in the real game data.

---

## 2. How the validation was done

Two independent harnesses, both under
`C:\Users\marvi\AppData\Local\Temp\opencode\` (throwaway, as instructed):

* **`meshcheck`** (`meshcheck\Program.cs`, self-contained, no project references) - the
  primary pass. It re-implements the exact read sequence of `DGRP3DMesh.LoadData` +
  `DGRP3DGeometry(IoBuffer, …)` from `FreeSO/TSOClient/tso.files/RC/`, and adds sanity criteria
  on top. It is deliberately self-contained so that it can never rebuild or lock the projects the
  live generator is running out of.
* **`dgrptruth`** (`dgrptruth\Program.cs`) - ground truth. References the **prebuilt**
  `FSO.Files.dll` via `HintPath` and uses the real `FAR1Archive` / `IffFile` / `DGRP` reader
  against `D:\Program Files\Sims` to answer "does this drawgroup really exist, and is that
  sprite index really valid for that rotation?". 552 IFF stems, 81 archives, 0 read errors.
* **`handparse.ps1`** - an independent PowerShell re-implementation used to spot-check the C#
  harness by hand (below).

### The harness itself was validated first

Before trusting it on generated data, `meshcheck` was pointed at 12 known-good
**hand-authored community pack** meshes: **12/12 parsed, all with 0 leftover bytes**. (4 of them
initially failed until the depth-mask branch was added - the harness was incomplete, not the
data bad. `DGRP3DMesh.Save` writes a mask geometry that `WriteFSOM` never does.)

---

## 3. Rejection criteria

A file is **hard-invalid** - and therefore not ingested - if any of these hold. All are hard
errors in the sense that either `DGRP3DMesh` cannot read the file to completion, or the mesh
would be unrenderable/degenerate.

**Container / structure**

| Criterion | Meaning | Found |
|---|---|---|
| `gzip_*`, `io_*` | not a readable gzip stream, empty, or unreadable | 0 |
| `bad_magic` | first 4 bytes are not `FSOm` | 0 |
| `truncated_payload` | reader hit end of payload (file caught mid-write, or corrupt) | 0 |
| `parse_error` | any other exception during the read | 0 |
| `bad_version` | `version` outside 1..3 | 0 |
| `stale_reconstruct` | `reconstructVersion != 0 && < 2` → `LoadData` throws *"Reconstruction outdated"* | 0 |
| `bad_group_count` / `bad_geom_count` | group or geometry count outside 1..4096 | 0 |
| `bad_vert_count` / `bad_index_count` | outside 0..2,000,000 / 0..6,000,000 | 0 |
| `index_count_not_multiple_of_3` | index count is not a whole number of triangles | 0 |
| `index_out_of_vertex_range` | any index `< 0` or `>= vertCount` (would fault the `IndexBuffer`/draw) | 0 |
| `no_triangles` / `no_geometry` | nothing to render | 0 |

**Vertex data / dimensions** (the "non-degenerate" criteria)

| Criterion | Meaning | Found |
|---|---|---|
| `non_finite_position` | any NaN/Inf in a vertex position | **0** |
| `non_finite_uv` | any NaN/Inf in a texture coordinate | **0** |
| `non_finite_normal` | any NaN/Inf in a normal | **1,520** - see §4 |
| `non_finite_bounds` | any NaN/Inf in the 6 stored bound floats | 0 |
| `inverted_bounds` | `min > max` on any axis | 0 |
| `degenerate_zero_bounds` | all three extents `< 1e-3` (occupies no volume) | 0 |
| `all_zero_bounds` | bounds are exactly the origin box | 0 |
| `implausible_bounds_scale` | max extent `> 1000` world units (bigger than a lot) | 0 |
| `bounds_exclude_vertices` | a vertex falls outside the stored bounds (+0.02 slack) - would break culling | 0 |

**Filename / drawgroup plausibility**

| Criterion | Meaning | Found |
|---|---|---|
| `filename_not_stem_dgrpid` | does not match `<stem>_<id>.fsom` | 0 |
| `filename_dgrpid_out_of_range` / `_overflow` | id is not a `ushort` | 0 |
| id exists in that IFF (ground truth, `dgrptruth`) | real `DGRP.ChunkID` | 0 bad of 6,104 |
| `(PixelDir, PixelSPR)` in range for that rotation (ground truth) | resolvable sprite | 0 bad |

### Non-fatal observations (recorded, not rejected)

* `written_just_before_scan` - 1 file (`mgcrystals_iff_400`); the generator wrote it seconds
  before the scan started. **No file was caught mid-write** (zero `truncated_payload` across
  three passes over 5,837 / 6,060 / 6,104 files).
* `sub_cm_extent` - 10 files, max extent `< 0.01` world units:
  `aquariumfame_iff_200..205` (8 tris / 16 verts), `magicminigolfdragon_iff_108/138`,
  `magicminigolfwindmill_iff_106/116`. Geometrically near-worthless slivers, but the source
  sprites really are that thin, and the in-game fallback would produce the same sliver.
* `stem_unusual_chars` - 5 files whose IFF name contains a hyphen
  (`control-garden_iff_100`, `control-petshow_iff_100`, `strength-o-meter_iff_100..102`).
  These are real TS1 filenames; the runtime probe lowercases and does the same `.`→`_` replace,
  so they resolve.
* `degenerate (repeated-index) triangles` - 3,237 files contain at least one, 22,021 of
  39.1 M triangles (0.06 %). These are exactly the triangles that produce the §4 defect.
* 0 files with UV outside `[-0.02, 1.02]`; 0 files with a zero-length normal; max normal
  length seen 1.0000.
* 0 `trailing_bytes` and 0 depth masks (as expected: `WriteFSOM` always writes
  `MaskType.None`).
* 0 case-insensitive filename collisions inside the generated set, and **0 collisions with the
  community pack** - the generator's `haveMesh` skip logic worked.

### Distribution (valid files, 4,584 files of the 6,104-file pass)

```
tris   min=7    p50=5,561   p95=26,664  max=73,295
verts  min=15   p50=4,425   p95=19,411  max=63,835
geoms  min=1    p50=4       max=80
groups min=1    p50=1       max=206
max bounds extent  p50=2.50  p95=6.24  max=14.52 world units   (1 world unit = 1/3 tile)
drawgroup ids: 0-999: 3,736 | 1000-1999: 451 | 2000-2999: 168 | 3000-3999: 40
               4000-4999: 9 | 5000-5999: 6 | 6000-6999: 1
511 distinct IFF stems, all ending in _iff
```

---

## 4. The one real defect: non-finite normals

**1,520 of 6,104 files (24.9 %) contain 3,468 NaN normals out of 54.1 M vertices - a median of
1-2 bad vertices per affected file, worst case 72** (`mgbeanstalk_iff_100`, which has 488 k
vertices). Positions and UVs are never affected. The NaN triple is always `(NaN, NaN, NaN)`.

### Root cause

`DGRP3DVert.GenerateNormals` (`FreeSO/TSOClient/tso.files/RC/DGRP3DVert.cs:84-89`):

```csharp
for (int i = 0; i < verts.Count; i++)
{
    var v = verts[i];
    v.Normal.Normalize();      // 0 * (1/0) = 0 * Infinity = NaN
    verts[i] = v;
}
```

A vertex whose accumulated normal is exactly zero - i.e. one touched only by zero-area
triangles - has `Normalize()` compute `1f / sqrt(0)` = `Infinity` and then multiply `0 *
Infinity`, yielding `NaN` in all three components. The 21,287 degenerate triangles in the
output are the trigger. This is **pre-existing upstream FreeSO behaviour**, not something the
generator introduced.

### Why these were ingested rather than rejected

`DGRP3DMesh`'s in-game fallback (`new DGRP3DMesh(dgrp, obj, GD, dir)`), used for any drawgroup
with no replacement file, calls **the same** `DGRP3DVert.GenerateNormals` on the same kind of
degenerate triangles. So excluding these 1,520 files would not remove a single NaN normal from
the game; it would only downgrade ~25 % of the objects from a fused mesh to the legacy
four-sheet displacement. The files are loadable and 99.99 % correct, so they were kept and the
defect is reported here instead. The list is in
`C:\Users\marvi\AppData\Local\Temp\opencode\run3\normal_only.txt` if the decision is revisited.

### Recommended fix (not applied - see §7)

Sanitise in `Simitone.GenerateMeshes/Program.cs`'s `Convert()` before writing: replace any
non-finite normal with a neighbour's, or with `Vector3.UnitY`. A one-line guard in
`DGRP3DFusion.FuseGroup` (skip zero-area triangles before the normal pass) would be cleaner
still, but that file is FreeSO-owned.

---

## 5. Ingestion: location and rationale

**Chosen: `D:\Simitone\Client\Simitone\Simitone.Client\Content\MeshReplace\`** (flat, 6,110
`.fsom`).

Why not the synthetic author/group under `FSO.Remeshes-main\remeshes`:

1. **The pack is optional and the build treats it as such.** `PackageRemeshAddon` is
   `Condition="Exists('$(RemeshPackDir)\metadata.json')"`. If the generated set lived only in
   the pack, a build on a machine without that (gitignored, machine-local) third-party checkout
   would silently ship **zero** generated meshes. From `Content/` they always ship.
2. **Zero build-system change.** `Simitone.Client.csproj` already has
   `<Content Include="Content\**\*.fsom" CopyToOutputDirectory="PreserveNewest" />`. Nothing
   new to maintain or test.
3. **Precedence comes out right for free.** `PackageRemeshAddon` runs `AfterTargets="Build"`,
   i.e. *after* the Content copy, and `Simitone.PackageRemeshes` only overwrites the names it
   produces (`File.Copy(..., true)`, "Unrelated files in the folder are left alone"). So a
   hand-authored community mesh always beats a generated one for the same drawgroup - the right
   authoring order, with no `priority` bookkeeping. Verified by re-running the packager over the
   populated output: **8,722 fsom before and after, and a generated file's SHA-256 unchanged.**
4. **Ownership.** These are Simitone-derived assets, not community remeshes; they do not belong
   in a third-party content repo.
5. **No texture dependencies.** Generated meshes use `PixelDir` 0..3, i.e. the object's own DGRP
   sprites, so no `_TEX_*.png` siblings are needed. (The community pack meshes all use
   `PixelDir 65535` + `_TEX_*.png`, which is why the pack route would have needed its textures
   handled too.)

### Verification of the shipped output

```
built game  bin\Release\net10.0-windows\Content\MeshReplace\
  8,773 .fsom  +  589 .png  +  .remeshpack.stamp
    6,110  from the generated set   (0 missing from bin; 40/40 sampled byte-identical)
    2,663  from the community pack  (0 overlap with the generated set)
        0  unexplained
```

---

## 6. Build, test and smoke results

* `dotnet build Client\Simitone\Simitone.Client\Simitone.Client.csproj -c Release` - succeeded,
  0 errors.
* `dotnet build Client\Simitone\Simitone.Windows\Simitone.Windows.csproj -c Release` -
  **fails** while the generator runs, and *only* because of it:
  `Simitone.Windows.csproj` has a build-only `ProjectReference` to `Simitone.GenerateMeshes`, and
  the live generator (pid 37012) holds `FSO.Common.dll` / `FSO.Files.dll` / `Simitone.Client.dll`
  open inside `Simitone.GenerateMeshes\bin\Release\net10.0\`, so the copy step for *that* project
  exhausts its 10 retries (MSB3021/MSB3027). Nothing to do with the content.
  Worked around as instructed with `-p:BuildProjectReferences=false` after building
  `Simitone.Client` on its own - **succeeded, 0 errors** (494 warnings, all pre-existing
  `NU1701` restore noise from FreeSO's net9.0 projects). A normal build will succeed once the
  generator exits; retrying plain is the right first option then.
* `dotnet test Client\Simitone\Simitone.Tests\Simitone.Tests.csproj -c Release` -
  **Passed! Failed: 0, Passed: 1676, Skipped: 0, Total: 1676** (12 s).
* **Hand re-parse** (`handparse.ps1`, independent PowerShell implementation, no shared code with
  the C# harness):
  * `arcadecabinetsspellbound_iff_200.fsom` - `FSOm`, v3/reconstruct 2, stored name matches the
    filename, 1 group / 4 geometries (PixelDir 0,2,3,1 at PixelSPR 0), 15,965 verts, 22,206 tris,
    **0 bytes left over**, 0 non-finite floats, no out-of-range index, bounds extent
    3.95 x 6.88 x 2.97 world units (~1.3 x 2.3 x 1.0 tiles - a plausible arcade cabinet).
  * `arcadecabinetsspellbound_iff_100.fsom` - same structure, **exactly 6 non-finite floats, all
    of them normals** (`normal v1372 = (NaN, NaN, NaN)`), 0 positions, 0 UVs, 0 bytes left over.
  Both agree with the C# harness to the vertex and triangle.
* **Game boot.** An instance (pid 28356, started 23:44:59) is running from the freshly built
  output and `%USERPROFILE%\Documents\Simitone\logs\simitone.log` shows a clean startup
  (`-path override in use`, free will, weather sounds from the new `Content` dir) through
  `OnHouseSelect` → `InitializeLot` → `Frontend: ... starting in BUY mode`, with **zero** lines
  matching `error|exception|fail|warn`. That instance belongs to another session, so it was left
  alone. **Caveat: it ran in 2D BUY mode, so `RCMeshProvider` was never asked for a mesh - this
  run proves the extra 6,110 files do not break startup or the content directory scan, but it is
  not evidence that a mesh was uploaded to the GPU.** See §7.

### Operational caveat worth knowing

`RCMeshProvider`'s constructor does:

```csharp
var repldir = Path.Combine(FSOEnvironment.ContentDir, "MeshReplace/");
var userrepl = Path.Combine(FSOEnvironment.UserDir, "MeshReplace/");
if (Directory.Exists(userrepl)) repldir = userrepl;   // <-- user folder *replaces* the content folder
```

If a user ever creates `%USERPROFILE%\Documents\Simitone\MeshReplace\`, **all 6,110 generated
meshes are ignored** (the folder is a replacement, not an overlay). It does not exist today, and
the constructor only ever creates the *content* one, so this cannot happen by accident - but it
will surprise someone who drops a hand mesh in the user folder.

---

## 7. What remains

1. **The generator is still running** (pid 37012, alive at 23:55, ~6 files/min). Everything
   above is a snapshot; new files need the sync in the next section. Nothing was killed.
2. **The NaN-normal defect is unfixed** - 25 % of meshes carry 1-2 bad normals each. The fix
   belongs in `Simitone.GenerateMeshes/Program.cs` (`Convert()`, before `WriteFSOM`); it was
   deliberately *not* applied because the generator holds that project's `bin` locked, so a
   rebuild to verify it was impossible, and a partial write to a file the live process is
   executing from is not worth the risk. Applying it means a re-run of the generator.
3. **No GPU-side load has been observed.** The boot test in §6 never entered 3D mode. To close
   this, start the game, toggle 3D on a lot, and check the log - `RCMeshProvider.Get` swallows
   load exceptions (`catch (Exception) { result = null; }`, `RCMeshProvider.cs:59-64`) and
   silently falls back, so *absence* of log errors is not proof; the check that matters is that
   objects visibly render with these meshes (or that `MeshCache` does **not** gain new entries
   for drawgroups that now have a `MeshReplace` file, which is the positive signal that the
   replacement was used).
4. **Precedence against future hand-authored work is untested end to end** - the reasoning and
   the packager run are verified, but no pack mesh actually collides with a generated name today
   (0 collisions), so the "hand-authored wins" path has not been exercised with real data.

---

## Re-running the sync

Validator (`meshcheck`) - rebuild and run; it takes ~9 s for the whole 1.7 GB set:

```powershell
dotnet build C:\Users\marvi\AppData\Local\Temp\opencode\meshcheck\meshcheck.csproj -c Release --nologo
& C:\Users\marvi\AppData\Local\Temp\opencode\meshcheck\bin\Release\net10.0\meshcheck.exe `
    D:\Simitone\GeneratedMeshes C:\Users\marvi\AppData\Local\Temp\opencode\runN
```

It writes `report.txt`, `files.csv` (per-file metrics), `valid.txt`, `invalid.txt` and
`normal_only.txt`. Then copy the hard-valid set across and rebuild:

```powershell
robocopy D:\Simitone\GeneratedMeshes `
        D:\Simitone\Client\Simitone\Simitone.Client\Content\MeshReplace `
        *.fsom /COPY:DAT /DCOPY:T /R:1 /W:1 /MT:8
dotnet build D:\Simitone\Client\Simitone\Simitone.Client\Simitone.Client.csproj -c Release --nologo
dotnet build D:\Simitone\Client\Simitone\Simitone.Windows\Simitone.Windows.csproj -c Release --nologo
```

To exclude the NaN-normal set instead, build the `/XF` list from `runN\invalid.txt` (entries
whose reason column is anything other than `non_finite_normal`; currently empty).
Once the generator has exited, the plain build command works and the `/p:BuildProjectReferences=false`
workaround is no longer needed.

---

## Final sync (2026-09-27 03:37)

**The generator has exited**, so this section supersedes the snapshot counts in §1, §5 and §6
(6,104 / 6,110 / 8,773). Everything above describes the state while the generator was still
writing; the numbers below are final and no longer a moving target.

### 1. Full re-validation of all 8,648 files

Reused the same `meshcheck` harness (rebuilt unchanged) over the whole directory in **18.5 s**:

| | |
|---|---|
| Files examined | **8,648** (2,273,387,789 B / 2.12 GB, 93,342,386 tris, 72,823,528 verts) |
| Hard-valid (zero findings) | **6,605** |
| `non_finite_normal` only (ingested anyway, per §4) | **2,043** (4,495 bad *vertices* of 72,823,528) |
| **Hard-invalid** (rejected) | **0** |
| Distinct IFF stems | 736, all ending in `_iff` |

The decisive check: grouping the 2,043 rejected files by their reason column yields exactly one
group, `non_finite_normal` — no file has any *other* finding, and none carries a secondary note.
So the entire 8,648-file set is loadable and structurally sound; 24.1 % carry the known §4 defect.
(The harness counts bad *vertices*; `handparse.ps1` counts non-finite *floats*, so 2 bad vertices
= 6 floats. Both agree.)

Distribution moved as expected with the extra 2.5 k files: `tris` min 2 / p50 4,624 / p95 26,057 /
max 540,996; `geoms` max 1,944; `groups` max 585; max bounds extent p50 2.23 / max 14.52 world units.
`PixelDir` 0..3 only, **0** files using the custom-texture slot (65535). Notes on valid files:
11 `sub_cm_extent`, 5 `stem_unusual_chars` (the same hyphened `control-*` / `strength-o-meter`
stems as before). 0 non-finite positions, 0 non-finite UVs, 0 zero-length normals, max normal
length 1.0000.

### 2. Ground truth for the new files

The §2 `dgrptruth` pass had only ever covered the 6,104-file snapshot, so the delta was put
through it too — **180 distinct stems, 180/180 resolved, 0 read/parse errors** across 81 FAR
archives, then joined against the per-file metrics:

* drawgroup id present as a real `DGRP.ChunkID` in that IFF: **2,538 / 2,538**
* stored `(PixelDir, PixelSPR)` in range for that rotation: **2,538 / 2,538**

Independently, `handparse.ps1` (the hand-rolled PowerShell reader, no shared code with the C#
harness) was re-run on two delta files and agrees with `meshcheck` to the vertex and triangle:
`alarmclock_iff_128.fsom` (1 group / 4 geoms / 2,767 verts / 3,654 tris / **0 bytes left over** /
0 non-finite / 0 out-of-range index) and `alarmclock_iff_134.fsom` (1 group / 4 geoms / 1,049 verts
/ 1,057 tris / 0 bytes left over / **exactly 6 non-finite floats, all normals** / 0 positions /
0 UVs / 0 out-of-range index).

### 3. Collision check and ingestion

| Check | Result |
|---|---|
| Files in `GeneratedMeshes` not already in `Content/MeshReplace/` (the delta) | **2,538** |
| — of those hard-valid | 2,019 |
| — of those `non_finite_normal` only | 519 |
| — of those hard-invalid | **0** |
| Delta ∩ community pack (`FSO.Remeshes-main\remeshes`, 2,877 `.fsom`) | **0** |
| Files in `Content/MeshReplace/` *not* from the generated set | **0** (that folder has never held a pack file, so nothing could be overwritten) |
| Case-insensitive name clashes inside the delta | 0 |

Ingested with `File.Copy(..., overwrite: false)` over the exact 2,538-name list, so the existing
6,110 could not be touched even in principle: 2,538 copied, **0 failed**.

```
D:\Simitone\GeneratedMeshes                 8,648  (2.117 GB)
Client\...\Content\MeshReplace\              8,648  (2.117 GB)
  in MeshReplace but not in GeneratedMeshes        0
  in GeneratedMeshes but not in MeshReplace        0
  SHA-256 of 60 randomly sampled delta files      60/60 byte-identical
```

The delta itself: 579,416,294 B (0.54 GB), 23,889,641 tris, 18,610,269 verts, 1,019 bad-normal
vertices, 1 `sub_cm_extent` note, 1,986 files containing degenerate triangles (18,958 of
23,889,641).

### 4. Build, tests, smoke

* `dotnet build Client\Simitone\Simitone.Windows\Simitone.Windows.csproj -c Release` -
  **plain build, 0 errors, 653 warnings** (all pre-existing: FreeSO `CS0169`/`CS0414` plus the
  `NU1701` restore noise). Because the generator has exited, the
  `-p:BuildProjectReferences=false` workaround from §6 is **no longer needed** - the first plain
  attempt succeeded.
* `dotnet test Client\Simitone\Simitone.Tests\Simitone.Tests.csproj -c Release` -
  **Passed! Failed: 0, Passed: 1974, Skipped: 0, Total: 1974**.
* Built output `bin\Release\net10.0-windows\Content\MeshReplace\`:

  ```
  11,311 .fsom  +  589 .png  +  .remeshpack.stamp
      8,648  from Content/MeshReplace (the generated set; all present, 0 missing)
      2,663  from the community pack   (0 overlap with the generated set)
          0  unexplained
  11,311 = 8,648 + 2,663
  ```

  40/40 sampled delta files and 20/20 sampled pre-existing files are SHA-256 identical between
  `GeneratedMeshes` and the built `bin` folder.
* **Game boot.** The shipping binary is `Simitone.exe` (the project is `Simitone.Windows`, the
  assembly is not). Ran `Simitone.exe -path:"D:\Program Files\Sims"` for 42 s (pid 20984, stayed
  alive the whole time, then closed). `%USERPROFILE%\Documents\Simitone\logs\simitone.log`
  gained **5 lines, all `[INFO]`**: `-path override in use`, graphics config
  (`mode=Full2D ... weather=on`), free will on, weather sounds loaded from the new `Content` dir,
  neighbourhood management seed path. **Zero** new lines matching
  `error|exception|fail|warn|crash|not found|missing`.

**The §6 caveat still stands and was not closed by this run:** `mode=Full2D` means 3D was never
entered, so `RCMeshProvider` was never constructed and no mesh was ever asked for. The run proves
the extra 2,538 files do not break startup or the content-directory scan; it is still not evidence
of a GPU upload. See §7.3 for what a real check would look like.

### 5. What this did *not* change

1. **The NaN-normal defect is still unfixed** and is now slightly larger in absolute terms:
   2,043 of 8,648 files (24.1 %), 4,495 bad vertices. The §4 reasoning and the §7.2 recommendation
   (sanitise in `Simitone.GenerateMeshes/Program.cs`'s `Convert()` before `WriteFSOM`) are
   unchanged. Nothing here regressed it; the delta added 519 such files and 1,019 bad vertices.
2. **No GPU-side load still observed** - unchanged from §7.3.
3. **Precedence against future hand-authored work is still untested end to end** - 0 collisions
   still, so the "hand-authored wins" path (§5.3) remains reasoning-plus-packager-verified only.
4. **The user-folder override caveat (§6) is now larger**: creating
   `%USERPROFILE%\Documents\Simitone\MeshReplace\` would ignore all **8,648** generated meshes, not
   6,110. It still does not exist, and the constructor only ever creates the *content* one.
