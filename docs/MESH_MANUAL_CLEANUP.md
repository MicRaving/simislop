# Manual mesh cleanup procedure

**Status:** v1, 2026-10-05. Derived while cleaning the Makin' Magic gargoyle
(`gargoyle_iff_100`); the procedure is meant to be followed by an agent one drawing group at a
time, with the game's own data as the only reference.

**Context:** `Simitone.GenerateMeshes` fills `Client/Simitone/Simitone.Client/Content/MeshReplace`
with image-based relief reconstructions for every drawing group that has no hand-authored remesh.
Each mesh is four depth-displaced sprite sheets (one per world rotation) drawn at once, so it
looks like the artist's sprite from far away but breaks down at close zoom: depth noise, fold
walls, sheet intersections, holes and floating debris. The ~2,800 meshes of the community pack
(referenced through `FSO.Remeshes-main/remeshes`) are the quality bar: sharp, coherent, one
surface instead of a net. This procedure is how a single mesh is brought up to that bar and
shipped without ever being overwritten by a regeneration.

---

## 1. The mesh and its texture contract

An `.fsom` is a gzip-compressed binary, not an OBJ:

- **Geometry groups** (`List<List<geometry>>`): group 0 is the base state of the object;
  higher groups are dynamic/animation sprites that the VM toggles.
- **Geometry**: vertices (position, UV, normal), triangles and a **texture selector**.
  - `PixelDir` 0..3 + `PixelSPR` (index within that rotation's sprite list) = *use the object's
    own sprite from the game's DGRP*. No PNG ships with the mesh; the runtime binds the game
    sprite. **A cleaned mesh that keeps this selector keeps the artist's exact colours.**
  - `PixelDir` 65535 = the custom-texture slot; the runtime loads
    `<iff stem>_iff_TEX_<PixelSPR>.png` from the same folder as the `.fsom`
    (community-pack style, e.g. `sculptures2_iff_TEX_469.png`).
- **World coordinates**: XNA, Y up, one tile = 3 world units. The object sits on the floor at
  Y ≈ 0, its footprint is around the tile origin, and the four sheets are already in this frame.
  **Never rescale, rotate or recentre the model** - a replacement mesh that moves would jump when
  Hybrid 2D/3D mode switches.
- **UVs**: sprite-space V is measured from the top (0 = top row), because the generator builds
  each vertex from a pixel. The OBJ export/import flips V the same way Volcanic does, so keep
  using the tool rather than hand-writing OBJ coordinates.

## 2. Tooling

`Simitone.MeshCleanup` (repo: `Client/Simitone/Simitone.MeshCleanup`, headless, no game needed)
is the offline equivalent of Volcanic's FSOM editor plus a preview renderer:

```powershell
dotnet build D:\Simitone\Client\Simitone\Simitone.MeshCleanup\Simitone.MeshCleanup.csproj -c Release
$mc = 'D:\Simitone\Client\Simitone\Simitone.MeshCleanup\bin\Release\net10.0\Simitone.MeshCleanup.exe'
```

| Command | What it does |
|---|---|
| `sprites --game <install\|far\|iff> --iff <name> --dgrp <id> --out <dir> [--depth]` | Ground truth: one PNG per sprite plus `sprites.png` (four panels), optional depth maps |
| `export --fsom <file> --game ... --out <dir>` | `<mesh>.obj` + `.mtl` + one PNG per material + `.manifest.json` (bounds, hashes, UVs) |
| `render --fsom <file> --game ... --out <png> [--size N] [--az <deg>] [--el <deg>] [--bilinear]` | Preview: four panels = the game's rotations 0..3; with `--az` a single off-axis view; `--bilinear` matches the GPU's linear filtering (use it when judging pixelation - nearest overstates it) |
| `import --obj <file> --name <stem_iff_dgrp> --out <file\|dir> [--tex-out <dir>]` | Edited OBJ -> `.fsom`; copies custom texture PNGs under the runtime name |
| `check --fsom <file> [--json <file>]` | Structural report: indices, finiteness, bounds, components, spikes, winding, UV range |

`--game` may be the install folder (`D:\Program Files\Sims`), a `.far`, or an extracted `.iff`.
Blender 5.x is installed (`C:\Program Files\Blender Foundation\Blender 5.2\blender.exe`) and is
used headless with `--background --python script.py -- <args>`.

The material name in the OBJ is the contract: `<dyn>_SPR_rot<dir>_<spr>` or `<dyn>_TEX_<id>`.
**Renaming a material breaks the texture binding**; the importer rejects anything else.

## 3. The procedure

### Step 0 - scope and freeze

Pick one file: `<iff stem>_iff_<drawgroup>.fsom`. Record which IFF/FAR and drawgroup it is
(`export` names the source; `sprites` needs the IFF name). Work in a private folder:

```
.meshcleanup/<object>/<agent>/
    baseline/    original .fsom copy, its check JSON, before.png, sprites.png, check output
    export/      OBJ + MTL + PNGs + manifest
    work/        .blend files, scripts, scratch
    candidate/   the result: <mesh>.fsom (+ <stem>_iff_TEX_*.png if custom textures)
    renders/     after.png, offaxis-*.png, optional closeups
    report.md    what was done, commands run, gate results, self-assessment
```

Copy the **shipped** file (`Client/Simitone/Simitone.Client/Content/MeshReplace/<mesh>.fsom`),
not the scratch `GeneratedMeshes` copy - they can differ (the shipped set is older; for
`gargoyle_iff_100` the shipped mesh has ten components per sheet where the scratch has one).

### Step 1 - recon (never edit before this)

```powershell
& $mc check --fsom baseline\<mesh>.fsom --json baseline\check_before.json
& $mc render --fsom baseline\<mesh>.fsom --game "D:\Program Files\Sims" --out baseline\before.png --size 512
& $mc sprites --game "D:\Program Files\Sims" --iff <Name.iff> --dgrp <id> --out baseline\sprites --depth
```

Read the sprite panels against the before render. Write down, per rotation:
- the object's real shape (what is it? statue, appliance, plant, wall piece...);
- which parts of the mesh are wrong: spikes, fold walls, sheet poke-through, holes, debris,
  smeared texels, missing volume (back side), wrong proportion.

### Step 2 - choose a strategy (record it in the report)

| Strategy | Use when | Output |
|---|---|---|
| **A. Repair relief** | the sheets already read as the object; defects are local (spikes, debris, holes) | keep the four sheets, weld/clean/smooth them |
| **B. Clean billboard** | object is flat-ish, or colour fidelity matters more than volume (paintings, rugs, foliage cards) | one or two textured planes per rotation, placed where the sheet's silhouette is; delete the displaced sheets |
| **C. Rebuild volume** | blocky/primitive objects (tables, counters, appliances, simple statues) | low-poly boxes/cylinders fitted to the sprite silhouette, sprites projected as textures |
| **D. Hybrid** | complex organic objects (this gargoyle): a small clean volume carries the shape; the sprite planes carry the detail/colour | volume + trimmed sheets |

A cleaned mesh does not have to be manifold or closed to be accepted, but it must read as one
object from the game's four views and must not fall apart off-axis.

### Step 3 - export and edit

```powershell
& $mc export --fsom baseline\<mesh>.fsom --game "D:\Program Files\Sims" --out export
```

Rules for the Blender edit (headless or GUI, but scripts are reproducible):
1. **Import/export with matching axis settings**: Blender defaults (forward `NEGATIVE_Z`,
   up `Y`) round-trip the OBJ exactly; a no-op round trip is verified by
   `import` + `check` (same verts/tris/bounds). Change them only if you know what you are doing.
2. **Triangulate on export** (`export_triangulated_mesh=True`, or an explicit triangulate
   modifier): the importer fans any polygon, but triangles are what the runtime stores.
3. **Keep the world frame.** Bounds after import must match the baseline within a few
   centimetres (see gates). If Blender applies an axis change, the bounds check catches it.
4. **Stay grounded**: bottom of the mesh at Y ≈ 0.
5. Reuse the existing `o`/material names when you reuse the existing sheets; for new geometry
   assign an existing sprite material (e.g. `0_SPR_rot0_0`) or create `<dyn>_TEX_<id>` plus a
   `<material>.png` image next to the OBJ. Textures can be edited/extended (baked AO, cleaned
   edges) but the custom-texture route means shipping PNGs.
6. Apply all modifiers before export. Delete cameras/lights/empties or use
   `export_selected_objects`.

Useful Blender operations (all scriptable):
- `remove_doubles`, `normals_make_consistent`, `fill_holes`, `decimate`, `smooth`,
  `solidify`, `bisect`/`flatten` for sheets;
- `data_transfer` with `loop_data_types={'UV'}` to copy UVs from an original sheet onto new
  geometry;
- building the volume from the sheet silhouette bounds (fit boxes/cylinders to the opaque
  extents per view, then project UVs from the front-facing sheet).

### Step 4 - import and verify (gates)

```powershell
& $mc import --obj export\edited.obj --name <mesh> --out candidate --tex-out candidate
& $mc check --fsom candidate\<mesh>.fsom --json candidate\check_after.json
& $mc render --fsom candidate\<mesh>.fsom --game "D:\Program Files\Sims" --out renders\after.png --size 512
& $mc render --fsom candidate\<mesh>.fsom --game "D:\Program Files\Sims" --out renders\offaxis.png --size 512 --az 100 --el 30
```

Hard gates (all must pass; a fail means edit, not ship):

| Gate | Threshold |
|---|---|
| `check` errors | 0 |
| Non-finite data / index range | none |
| Bounds (min/max per axis) | within **0.05** world units of baseline - allows local fixes, catches axis/scaling mistakes and drifting |
| Bottom of mesh | Y within [-0.02, +0.10] |
| Components | <= 8 total across all geometries; no components under 4 triangles |
| Winding vs normals | not mixed (all-consistent is what matters; the engine's own meshes are 0% by this metric) |
| Triangles | <= 30,000 for a small object, <= 100,000 for a large one; no invisible geometry |

Visual gates (judged from the renders, against `sprites.png`):
- each of the four panels has the same silhouette as its sprite (shape and proportions);
- no spikes, no slabs, no stitched-in fold walls, no holes showing background through the body;
- colour/UV continuity: no smeared or mirrored texels, no speckle where a face flips sprite;
- the off-axis render reads as a solid object, not as intersecting paper sheets.

If a gate fails, the fix belongs in Blender; re-export and re-run step 4. Keep every iteration's
script so the final report can name the exact sequence.

### Step 5 - install and protect

1. Copy `candidate\<mesh>.fsom` (and any `<stem>_iff_TEX_*.png`) into
   `Client/Simitone/Simitone.Client/Content/MeshReplace/`.
2. Add the file name to
   `Client/Simitone/Simitone.Client/Content/MeshReplace/cleaned-meshes.txt`.
   `generate-meshes.ps1` reads that list and treats those names exactly like community meshes:
   never generated, never overwritten by `-InstallOutput`, never deleted as stale.
3. Build (`dotnet build Client\Simitone\Simitone.Windows\Simitone.Windows.csproj -c Release`) so
   the content copy and the built game's `Content\MeshReplace` pick it up. The community pack
   still wins any collision by design (hand-authored > cleaned > generated).
4. Confirm in `check` that the candidate is loadable and its name matches the file name; the
   runtime rejects `ReconstructVersion < 2`, NaN normals are tolerated but avoid them.

### Step 6 - report and scale

`report.md` per mesh: strategy, commands, before/after `check` numbers, before/after renders,
gate results, anything a reviewer should not have to rediscover. For a batch, keep a result table
(`cleaned-meshes.txt` is the inventory; the report is the evidence).

---

## 4. Pitfalls seen in practice

- **The shipped and scratch sets differ.** Always start from `Content/MeshReplace`.
- **Duplicate material names in one FSOM collapse on import** (the runtime keeps one geometry
  per texture in a group). The exporter warns; if you see it, merge the geometries instead.
- **Custom texture naming**: the runtime asks for `<iff stem>_iff_TEX_<id>.png` - not
  `_<drawgroup>_TEX_`. `import` writes the right name; do not rename afterwards.
- **The user folder replaces the content folder** (`%USERPROFILE%\Documents\Simitone\MeshReplace`
  shadows `Content\MeshReplace` entirely if it exists) - do not create it while testing.
- **`check` "components" counts vertex-disconnected islands, not visual overlap.** A sheet that
  folds back on itself can be one component and still be wrong; the renders are the arbiter.
- **Depth direction**: the preview's `larger t = nearer` matches the engine; if a render looks
  inside-out, do not "fix" the model, check the export axes first.
- **Blender object names**: if two `o` blocks collide, Blender silently suffixes `.001` and the
  import fails with a naming error. The exporter warns about duplicates for this reason.

## 5. Competition protocol

To compare agents/models on the same mesh fairly:

- freeze the tool revision (build once; competitors do not edit the tool or shared sources);
- give every competitor its own workspace (section 3, step 0) and the **same baseline
  evidence**;
- judged blind against the rubric below: originally the output is opened by the judge without
  seeing whose it is; the report is read only after scoring;
- the winner is installed (step 5), the loser's report is kept for the record.

Rubric (0-5 per row, weighted):

| Row | Weight | What is looked at |
|---|---|---|
| Silhouette match, four canonical views | x3 | renders vs `sprites.png` |
| Surface quality (spikes/slabs/holes/speckle) | x3 | close renders of before/after |
| Off-axis volume and form | x2 | `--az 100 --el 30` render |
| Topology and placement gates | x1 | `check_after.json`, bounds |
| Evidence quality (report, reproducibility) | x1 | `report.md`, scripts |

---

## 6. Worked example: `gargoyle_iff_100` (baseline)

- Source: `Gargoyle.iff`, drawgroup 100, in `ExpansionPack7.far`.
- Shipped mesh: 4 geometries, 3,787 verts / 6,577 tris, bounds
  `(-0.172,-0.002,-0.155)..(0.174,0.582,0.162)` (0.35 x 0.58 x 0.32 world units).
- `check` before: 4-10 components per sheet (fold holes), 237-262 boundary edges per sheet,
  0 spikes, winding 0% (engine convention).
- What the sprites show: a crouching winged gargoyle statue on a square/round pedestal.
- What the mesh gets wrong: the sheets intersect each other (dark slabs across the body), the
  pedestal is noisy, the face/wings are chewed up, silhouettes disagree between rotations.

Companions in the same file family: `gift_Gargoyle.iff` 100 is the same statue inside an
animated container (frame 0 identical geometry); `WorkbenchGargoyle.iff` 200/300 is a different
object (a crafting workbench). Animated containers are read/renderable with this tool but
`import` writes single-state FSOm files only - clean the static meshes first, and handle
animation by exporting/rebuilding per frame as a follow-up.

---

## 7. First run — gargoyles, 2026-10-05 (competition)

Two agents (`space-bunny-free`, `deepseek-v4.1-flash`) cleaned `gargoyle_iff_100` from the same
baseline with the frozen tool. Both independently chose **strategy C (rebuild volume)** and
implemented a visual-hull carve, because each measured that the four relief sheets are planes
crossing through the object (separation would need a ~2.7-unit slide on a 0.58-unit object) —
the measurement is in the winning report. Scores (silhouette x3, surface x3, off-axis x2, gates
x1, evidence x1) were **40.5 vs 36**; the carved hull that tracked the sprites (IoU 0.94 vs
0.90 baseline) and reused the game's own sprite materials won and is installed. Full scoring and
evidence: `.meshcleanup/gargoyle/results.md`.

Lessons carried back into this procedure:

1. **Keep the sprite materials if you can.** The winner ships no texture sidecars; the loser's
   baked custom textures produced the worst defect (dark projection streaks) and doubled the
   triangle budget.
2. **Texture projection, not geometry, is the remaining bottleneck.** A carve sampled from
   29-31 px sprites cannot out-resolve them. The two concrete upgrades, in order: per-face planar
   UV unwrapping against the source sprite triangle, or a thin sprite-accurate outer shell over
   the solid.
3. **Judge with images before reports**, as the rubric says: the two `check` outputs were nearly
   identical and neither predicted the visual gap.
4. **The sheets are individually registered.** Both runs measured each sheet's screen lattice as a
   uniform scale of its own sprite (uniform 0.337 px edit, plane-fit residual ~4e-7); the defect
   is the crossing planes, not a placement error. Objects whose sprites give the four views
   mutually consistent silhouettes are carve-safe; thin/animated ones are not automatically.
5. **Classical sprite upscaling is not the next lever; learned SR is, but only on a texture-first
   mesh.** The source has no higher resolution anywhere (zoom 3 is the largest; zoom 2/1 are
   smaller); Lanczos/sharpening bakes change the render about as much as swapping nearest for
   bilinear. A real SR model (Real-ESRGAN x4plus) does remaster the sprite cleanly, but on the
   carved hull the gain is diluted by the voxel geometry and oblique UVs. Use SR bakes where the
   sprite is shown near its own resolution (hybrid shell, corrected UVs), with edge-fill,
   nearest+blur alpha and a brightness match. Measured assessment:
   `docs/MESH_PREPROCESSING_ASSESSMENT.md`.

---

## Appendix: command cheat sheet

```powershell
$mc = 'D:\Simitone\Client\Simitone\Simitone.MeshCleanup\bin\Release\net10.0\Simitone.MeshCleanup.exe'
$game = 'D:\Program Files\Sims'

& $mc check    --fsom <file>.fsom --json <report>.json
& $mc sprites  --game $game --iff Gargoyle.iff --dgrp 100 --out <dir> --depth
& $mc export   --fsom <file>.fsom --game $game --out <dir>
& $mc render   --fsom <file>.fsom --game $game --out <png> --size 512 [--az 100 --el 30]
& $mc import   --obj <edited>.obj --name gargoyle_iff_100 --out <dir> --tex-out <dir>
```
