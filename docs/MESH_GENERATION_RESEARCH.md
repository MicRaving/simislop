# Higher-fidelity 3D mesh generation for Simitone

**Status:** investigation + implemented. Date: 2026-09-26 (implementation same day).
**Scope:** how to auto-generate object meshes for the 3D mode that are visibly
better than the current sprite-derived ones and free of the "vertex displacement"
artefacts, for the ~66% of drawing groups that have no hand-authored remesh.

**Implemented (see §10):** the calibrated reconstructor
(`FreeSO/TSOClient/tso.files/RC/DGRP3DFusion.cs`), an offline generator tool
(`Client/Simitone/Simitone.GenerateMeshes/`), tests, and the FSOM writer. The
runtime generator is intentionally left unchanged; the supported deployment is
an offline pack, exactly as the plan recommends.

This document is the result of reading the generation path end-to-end and then
probing the real TS1 install (`D:\Program Files\Sims`) with a throwaway harness
(`meshtool`, see §7). It is written so the next session can start implementing,
not so it can be read as a survey.

---

## 0. TL;DR

- The current generator does **not** reconstruct a surface. For each of the 4
  rotations it displaces one sprite's pixels along a fixed, hand-tuned camera
  axis by the 8-bit z-buffer, produces a grid "relief sheet", and then the
  renderer draws **all four sheets at once** (`DGRPRenderer.Draw3D`). It is a
  depth-displaced billboard cross, not a mesh.
- The misplacement the author complains about is **structural**, and I
  reproduced it: the four per-rotation sheets do not co-register in world space
  under the generator's own transform. A naive volumetric intersection of them
  collapses to a sliver (see §7), so no amount of post-smoothing fixes it.
- The highest-fidelity *automatic* route is a **volumetric reconstruction**:
  first recover one consistent camera/placement per view (calibration, ~5-10
  parameters solved once), then **space-carve** a voxel volume with the exact
  silhouettes and the z-buffers, extract a single watertight surface, remesh and
  finally texture it by projecting the original sprites. This is classical,
  deterministic, needs no ML, and fits the existing FSOM/`RCP` pipeline with
  little or no format change.
- It should run **offline** as a batch tool that emits an FSOM pack only for
  drawing groups with no existing `MeshReplace` file — exactly the strategy
  `Simitone.PackageRemeshes` already uses for the community pack.
- A learned/example-based path is possible (there are ~760 paired
  auto-vs-human meshes in the remesh pack) but is a later, optional stage and
  has licensing/policy caveats (§6).

---

## 1. How the current generator actually works

Entry points (`FreeSO/TSOClient/tso.content/RCMeshProvider.cs`):

1. `RCMeshProvider.Get(dgrp, obj)` tries, in order:
   `MeshReplace/<iff>_<dgrp>.fsom` → an `FSOM` chunk inside the IFF →
   `MeshCache/<iff>_<dgrp>.fsom` → **generate** (`new DGRP3DMesh(dgrp, obj, gd, dir)`)
   and save to `MeshCache` (`RCMeshProvider.cs:46-108`).
2. Generation lives in `DGRP3DMesh(DGRP, OBJD, GraphicsDevice, string)`
   (`FreeSO/TSOClient/tso.files/RC/DGRP3DMesh.cs:340-725`).

Generation, per object:

- For `rotation = 0..3` it fetches `dgrp.GetImage(1, 3, rotation)` — i.e. one
  fixed sprite *direction* viewed from the four world rotations, **highest zoom
  only** (`DGRP3DMesh.cs:356-369`).
- A fixed matrix maps image pixels to world (`DGRP3DMesh.cs:371-380`):
  ```
  mat  = T(-72,-344,-55) * S(1.43/128) * S(1,-1,1) * Rx(-30°) * Ry(45°·(1+2·rot))
  mat *= T(objOffset.X/16, objOffset.Z/5, objOffset.Y/16)   // per sprite
  ```
  `1.43`, `-55`, `-30°`, and `factor = 0.39` are empirical constants; a handful
  of IFFs get overrides (`DGRPRCParams` / `ParamsByIff`, `DGRP3DMesh.cs:28-68`).
- Each **opaque** sprite pixel `(x,y)` with z-value `d` becomes a world vertex
  `world = tl + x·xInc + y·yInc + (1-d)·dFactor` (`DGRP3DMesh.cs:447-489`).
  So the z-buffer is read as "how far to push this pixel along the view axis";
  `d==255` (the empty sentinel) is skipped (`if (d < 0.999f)`).
- A regular grid is triangulated, but quads whose diagonal exceeds
  `MaxAllowedSq = 0.065²` are dropped (`DGRP3DMesh.cs:491-547`) — this is a
  hole-punching heuristic, not a surface rule.
- Optional `CounterFix` clamps/grafts side vertices (`549-636`), and
  `Simplify=true` (default) runs quadric decimation, which **moves** surviving
  vertices (`640-697`).
- One `DGRP3DGeometry` is produced per `(dynamic-sprite, texture)`; for a static
  object all four rotations' geometry lands in the **same** dictionary
  (`397-405`).

Rendering (`FreeSO/TSOClient/tso.world/Utils/DGRPRenderer.cs`):

- `Draw3D` iterates every dynamic-sprite group and every geometry inside it and
  draws them all (`DGRPRenderer.cs:317-344`). `PixelDir` (the rotation tag) is
  **not** consulted. So all 4 relief sheets are drawn simultaneously; there is
  no hidden-surface fusion. The result is a 4-plane impostor cross, and any
  disagreement between the sheets shows up as poking-through/space-between
  geometry — the "vertex displacement".

Data source: `DGRP.GetDepth()` → `SPR2Frame.ZBufferData`
(`tso.files/Formats/IFF/Chunks/DGRP.cs:272-283`), decoded in
`SPR2Frame.Decode` (`SPR2.cs:349-537`). 8-bit per pixel, `255` = empty/far.

---

## 2. What the real data looks like (measured)

Harness output against `D:\Program Files\Sims\GameData\Objects\Objects.far`
(base game only; expansion packs add more):

| Metric | Value |
|---|---|
| IFF objects in the FAR | 126 |
| DGRP drawing groups | **2,217** |
| SPR2 chunks | 2,128 |
| SPR2 frames | 17,511 |
| Frames carrying a z-buffer (`Flags & 0x02`) | **17,511 (100%)** |
| DGRPs with a matching human remesh `fsom` | 761 (**34.3%**) |
| **Uncovered DGRPs (no hand mesh)** | **1,456 (65.7%)** |

Depth per sprite is narrow-band, e.g. a sink sprite uses z∈[139,255] with 58
distinct levels; a chair sprite z∈[134,255] with 80 levels. So the effective
depth resolution of a sprite is roughly **50-80 quantisation steps for the whole
object**, and `255` doubles as the empty sentinel.

View availability per DGRP (distinct SPR2 frames across the 4 world rotations):
4.6% have 1, 16.9% have 2, 38.0% have 4, the rest are multiples (multi-sprite
objects). Concretely a dining chair only ships **two** distinct renders
(`dir 0x01/0x40` share a frame, `dir 0x04/0x10` share another); a sink likewise.
Asymmetric objects do get 4. So most objects have **2-4 genuine views**, which
bounds what any reconstruction can recover (§5).

---

## 3. Why the current result is misplaced (ranked root causes)

1. **Impostor cross, no fusion.** Four independent relief sheets are rendered
   together. Nothing enforces that they describe one surface.
2. **The per-view placement is not consistent.** This is the big one. Under the
   generator's own transform the four sheets do not co-register; a volumetric
   intersection of their silhouettes is a small sliver rather than the object
   (prototype, §7). The hand-tuned matrix plus per-rotation sprite offsets is an
   approximation that no single-object post-process can repair.
3. **8-bit, per-sprite, unknown-range depth.** `z/255` is treated as a
   normalised depth with the near/far mapping fixed by `factor=0.39`; the true
   near/far plane of each rendered sprite was never recovered.
4. **Hand-tuned camera constants** (`1.43`, `-55`, `0.39`, `-30°`) and per-IFF
   patches (`BlenderTweak`, `CounterFix`, door rotation masks) encode the fact
   that the model is not solved.
5. **Hole-punching and decimation.** The `MaxAllowedSq` quad rejection drops
   legitimate steps (thin legs, chair spindles), and quadric simplification
   moves vertices after the fact.
6. **No visibility reasoning.** Back-facing and hidden geometry is emitted and
   drawn; occluded surfaces are not culled or reconciled.
7. **Multi-part / dynamic objects are not stitched.** Sub-sprites with
   `ObjectOffset` and dynamic-sprite groups are handled group-locally.

---

## 4. Candidate approaches

Ranked by expected fidelity × feasibility for this data. "Views" = 2-4 genuine
renders per object.

### A. Calibrated volumetric fusion (recommended)
**Pipeline:** recover one projection/placement model → space-carve a voxel
volume from the silhouettes and z-buffers → extract one watertight surface →
remesh → project-texture the original sprites.

- Silhouettes are exact and noise-free, so carving is robust and *guarantees*
  the mesh matches every sprite contour; this is what removes displacement.
- Depth carving (voxel in front of the measured z ⇒ empty) recovers concavities
  that pure shape-from-silhouette cannot.
- Works with only 2-4 views; asymmetry is handled by the natural number of
  views. Symmetric objects can have their views mirrored as extra constraints.
- Deterministic, no ML, easy to QA and to run offline.
- Cost: one voxel pass per DGRP (~10⁶ voxels at 1-2 cm), once, cached.

### B. Shape-from-silhouette / visual hull only
Simpler subset of A (no z-buffer). Very robust but over-fills (no concavities,
fat thin parts). Good fallback where z is unusable.

### C. Depth-map / TSDF fusion + Poisson or marching cubes
KinectFusion-style signed-distance fusion of the depth maps, then a smooth
surface. Best-looking flat surfaces if the calibration is good; needs the same
calibration work as A and more care with the 8-bit depth. A and C can share a
calibrated camera and differ only in the extraction step.

### D. Primitive / template fitting (semi-automatic)
Fit boxes/cylinders/spheres and extruded profiles to the carved hull for the
many blocky objects (tables, counters, appliances). Gives CAD-clean topology and
exact planarity that voxel extraction lacks. Best used as an *optional pass on
top of A* (dominant-plane detection + snapping), not as the base method.

### E. Learned / exemplar-based
There are ~760 (auto mesh, human mesh) pairs. Options: (i) train a residual
corrector that maps the generated mesh + sprite/depth to the human mesh; or
(ii) retrieve a same-category human template and non-rigidly fit it to the
carved hull. Highest ceiling, but: needs the pairs, has training/QA cost, and
runs into the remesh pack's "**We will not accept contributions with AI
generated content**" stance and its per-author licensing (§6). Recommended only
as an optional research track, and only for *local* auto-generation.

### F. Incremental fixes to the existing generator
Cheap, no new geometry pipeline: bilateral/edge-preserving z-buffer denoising
before displacement, feature-preserving remeshing instead of quadric
decimation, symmetry completion, and using all 3 zooms. These reduce noise but
**cannot** fix the co-registration problem, so they are at best a stopgap.

---

## 5. Recommended pipeline in detail

### Step 0 — calibrate once (the keystone)
The four views are renders of the same object from known azimuths. Recover, once
globally (or once per IFF family), the orthographic camera and the integer
sprite placement so that back-projected depth surfaces from all views agree.
This is a small bundle-adjustment over roughly {elevation, azimuth offset,
uniform scale, per-zoom image offset, per-view 2D offset}, solved by minimising
cross-view depth/silhouette disagreement on a few simple objects (a box-like
counter, a chair) where the answer is unambiguous. The existing constants
(`1.43`, `-30°`, `0.39`, `-55`) are the initial guess. The prototype's failure
to co-register (§7) is direct evidence this step is necessary; without it,
carving and TSDF both collapse.

### Step 1 — per-object volumetric reconstruction
For each DGRP and each dynamic-sprite group:

1. Gather all `(rotation, sprite)` with their alpha (silhouette) and z-buffer,
   plus `ObjectOffset`, using the calibrated projection.
2. Allocate a voxel grid over the union bounding box at ~1 cm (clamp to memory;
   objects are ~1 tile, so ~128³ typical).
3. **Space carve:** a voxel survives only if, for *every* view, it projects onto
   an opaque pixel *and* is not nearer than that pixel's measured surface
   (`z_voxel <= z_surface + tol`). Empty/background carves.
4. **Symmetry (optional):** for near-symmetric hulls, mirror one half onto the
   other to recover occluded backs and remove lopsidedness.

### Step 2 — surface extraction and remesh
- Convert occupancy to a smooth scalar field (narrow-band distance transform, or
  a couple of Gaussian passes), extract with marching cubes or surface nets.
- Snap near-coplanar regions (step D) for appliances/counters.
- Optional feature-preserving decimation to the game's triangle budget. Objects
  are small; 2-10k tris is plenty.
- Recompute normals; enforce consistent winding.

### Step 3 — texture
- For each extracted triangle, choose the view whose camera aligns best with the
  face normal *and* whose projected texels are unoccluded (use the voxel hull for
  visibility), then assign UVs into that view's sprite texture.
- **No FSOM format change is required:** the existing model is already "one
  `DGRP3DGeometry` per `(dynamic id, texture)`". Emit up to four disjoint
  geometries (one per source view texture) that together form the single fused
  surface. The renderer already draws all geometries of a group; because they
  are now disjoint faces of one surface, there is no overlap artefact.
- Optionally bake ambient occlusion as the remesh guidelines suggest.

### Step 4 — offline packaging (targets only uncovered DGRPs)
- Enumerate DGRPs, subtract the ones for which `MeshReplace/<iff>_<dgrp>.fsom`
  exists (reuse the alias/priority logic in
  `Simitone.Client/Utils/RemeshPackage.cs`), and generate only the remainder.
- Emit the flat `MeshReplace/` naming `RCMeshProvider` expects, then reuse the
  `Simitone.PackageRemeshes` tool/MSBuild target (`Simitone.Windows.csproj`,
  `PackageRemeshAddon`) to produce a single DBPF.
- Ship it as a separate optional pack, exactly like the community remesh pack,
  so it never has to overwrite or depend on hand-authored work.

### Step 5 — runtime wiring (fallback only)
If offline generation is not taken up immediately, the same code can run in the
existing background generation path: replace the body of the
`DGRP3DMesh(DGRP, OBJD, ...)` constructor with a call to a new
`DGRP3DFusion` class, keep the old path behind a `DGRPRCParams` flag, and bump
`DGRP3DMesh.CURRENT_RECONSTRUCT` (the format already invalidates stale caches on
reconstruct-version change, `DGRP3DMesh.cs:280`).

---

## 6. Validation

Per reconstructed DGRP, measure:

- **Silhouette IoU** per view (rasterise the mesh with the calibrated view
  projection vs the sprite alpha). The reconstruction should be near-exact by
  construction; anything below ~0.95 means the calibration or carving is wrong.
- **Depth RMSE** per view (project mesh depth vs sprite z-buffer), and
  **cross-view consistency** (reproject view A's surface into view B).
- **Chamfer distance** to the human remesh where one exists (the ~760 pairs) —
  this is the closest thing to ground truth and also a way to tune parameters.
- Triangle/vertex budget and generation time per DGRP.
- Visual A/B in Blender or in-game from the four rotations in Hybrid mode
  (the remesh guidelines: must not jar when rotating between 2D and 3D).

The `meshtool` harness in §7 already provides silhouette IoU and OBJ export for
current vs prototype, and is the natural seed for this validation suite.

---

## 7. The probe harness and what it showed

I built a small throwaway .NET console tool (`meshtool`) that references
`FSO.Files` and reads `Objects.far` directly (`FAR1Archive` → `IffFile`). It
produced every number in §2 and can:

- `summary` — z-buffer coverage and per-file DGRP counts.
- `views` — distinct-frame histogram across the 4 rotations.
- `coverage` — remeshed vs uncovered DGRP counts.
- `ztext <iff> <dgrp>` — per-sprite size, offsets, z range, distinct levels.
- `fuse <iff> <dgrp>` — a **naive** prototype: replicate the current geometry in
  C#, then space-carve and extract a fused mesh, exporting `fused.obj` /
  `current.obj` and printing silhouette IoU.

Prototype result (the important part): using the generator's existing transform,
the four views' silhouettes intersect to only a sliver of the object —
**alpha-only intersection was 0.04% of the grid for a sink and 0.29% for a
chair**, versus the ~5-10% an object should occupy. That is the co-registration
failure of §3.2. Consequently the naive fused mesh scores *worse* silhouette IoU
(~0.7) than the current 4-sheet union (~0.84-0.89) — i.e. fusion without
calibration is not an improvement. It confirms the diagnosis and sets the order
of work: **calibration first, then fusion.**

(Harness location: `C:\Users\marvi\AppData\Local\Temp\opencode\meshtool`, if it
is still present. It is intentionally outside the repo; it can be promoted to a
`Simitone` tool project if wanted.)

---

## 8. Constraints and risks

- **Policy.** `FSO.Remeshes` explicitly refuses AI-generated contributions.
  This proposal is *not* that: it is a deterministic classical reconstruction
  that ships as a separate optional pack for objects that have no hand mesh. It
  should be described that way, and the community pack should remain untouched.
- **Licensing (only for approach E).** Human remeshes are "under full control of
  their authors"; using them as *training data* is legally murkier than using
  them as an evaluation benchmark. Prefer benchmark use unless authors grant
  more.
- **Dynamic sprites, depth masks, portals, doors, counters** are the hard 20%.
  Keep the existing `DGRPRCParams` overrides and be able to fall back to the
  current generator per DGRP.
- **Runtime cost.** Full carving is heavier than the current per-pixel code, but
  it is a one-time cached cost (grounds for doing it offline and shipping FSMs).
- **Only 2-4 views.** The back of an asymmetric object is genuinely
  unobserved; symmetry or a category prior must supply it. Visual hull is
  conservative where it cannot know.

---

## 9. Suggested next steps

1. Promote `meshtool` into the repo as a `Simitone.MeshResearch` tool (or extend
   the tests project) so the metrics are reproducible in CI.
2. Implement Step 0 calibration and prove cross-view agreement on 5-10 objects.
3. Implement Steps 1-3 for one object class (counters/appliances are the easiest
   and most numerous), A/B it against `current.obj` and the human remesh.
4. Wire the offline pack generation (Step 4) using `Simitone.PackageRemeshes`.
5. Only then consider approach D/E refinements.

---

## Appendix — key files

| Concern | File |
|---|---|
| Mesh generation | `FreeSO/TSOClient/tso.files/RC/DGRP3DMesh.cs` |
| Geometry/render holder | `FreeSO/TSOClient/tso.files/RC/DGRP3DGeometry.cs` |
| Reconstruction params | `FreeSO/TSOClient/tso.files/RC/DGRPRCParams.cs` |
| Provider/cache order | `FreeSO/TSOClient/tso.content/RCMeshProvider.cs` |
| 3D draw loop | `FreeSO/TSOClient/tso.world/Utils/DGRPRenderer.cs` |
| Z-buffer source | `FreeSO/TSOClient/tso.files/Formats/IFF/Chunks/DGRP.cs`, `SPR2.cs` |
| Remesh pack flattening | `Client/Simitone/Simitone.Client/Utils/RemeshPackage.cs` |
| Remesh pack builder | `Client/Simitone/Simitone.PackageRemeshes/Program.cs` |

---

## 10. Implementation (landed 2026-09-26)

### What was built

| Piece | Location |
|---|---|
| Calibrated reconstructor (camera, carving, extraction, decimation, UV split) | `FreeSO/TSOClient/tso.files/RC/DGRP3DFusion.cs` |
| FSOM writer usable without a `GraphicsDevice` | `DGRP3DMesh.WriteFSOM` |
| Quadric simplifier exposes its valid triangle count | `FSO.Common.MeshSimplify.Simplify.TriangleCount` |
| Offline generator CLI | `Client/Simitone/Simitone.GenerateMeshes/` |
| Build wiring (non-copying reference) | `Client/Simitone/Simitone.Windows/Simitone.Windows.csproj` |
| Camera-model tests | `Client/Simitone/Simitone.Tests/Utils/FusionTests.cs` |

### How it works (as implemented)

- **Camera (Step 0) is solved analytically, not optimised.** The exact isometric
  projection the 2D renderer already uses (`WorldSpace.GetScreenFromTile`, Near
  zoom: 128 px/tile, 3 world units/tile, height 78.383 px/unit, elevation 30°,
  azimuth 45°+90°r) is reproduced in `DGRP3DFusion.Screen`/`Inverse`/`DepthDir`.
  This is the key result of the probe: under this projection the four rotations
  co-register (27× more silhouette overlap for a chair than the legacy
  hand-tuned matrix), which is what removes the "vertex displacement" at the
  root. `FusionTests` pins `Screen`, the null-space property of `DepthDir`, and
  the `Inverse` round-trip.
- **Carving (Step 1).** A coarse pass over a generous box finds the real
  extents; a fine pass (default 128³ along the long axis) keeps a voxel only if
  it projects onto an opaque pixel in **every** view and is not clearly in front
  of that view's measured z-surface. A morphological close, a
  component-size filter, and Gaussian smoothing prepare the field.
- **Extraction + remesh (Step 2).** Marching tetrahedra, per-tet outward
  orientation (so the simplifier's flip test works), vertex welding, then the
  existing quadric simplifier (`FSO.Common.MeshSimplify`) to a per-object budget.
- **Texture (Step 3).** Each triangle is assigned to the view whose camera is
  most face-on; UVs are projected from that view's sprite. Geometry is split into
  one `DGRP3DGeometry` per source sprite, so the existing render path and FSOM
  format are unchanged and the game binds the original sprite textures.
- **Packaging (Step 4).** The tool flattens to `<iff>_iff_<dgrp>.fsom`, skips
  anything already covered by the community pack (reusing `RemeshPackage`'s
  aliases and priority rules), and validates every file by reading it back.
- **Dynamic sprites** are fused per dynamic group, mirroring the legacy grouper.
  Door/counter/blender overrides are left on the legacy path.

### Usage

```
Simitone.GenerateMeshes <ts1-path> <output-dir> [--pack <remesh-root>]
                        [--res N] [--no-depth] [--filter <substring>]
                        [--max N] [--verbose] [--verify]
```

Example (base game, uncovered groups only, validating output):

```
dotnet Client/Simitone/Simitone.GenerateMeshes/bin/Release/net10.0/Simitone.GenerateMeshes.dll \
  "C:\Program Files\Sims" .\GeneratedMeshes \
  --pack FSO.Remeshes-main\remeshes --verify
```

Copy the output into `Content/MeshReplace` at build time, or into
`<UserDir>/MeshReplace` (which `RCMeshProvider` prefers when present).

### Verified

- Builds green: `dotnet msbuild Client/Simitone/Simitone.sln /t:Simitone_Windows
  /p:Configuration=Release`, and `Simitone.Tests` is **860/860** (10 new camera
  tests).
- On the real install, base `Objects.far`: all 13 `Sculptures.iff` and 12/13
  `Stoves.iff` base groups generate; `DiningChairs` is correctly skipped because
  the community pack covers it. Every emitted FSOM re-parses cleanly.
- Generation is ~0.5 s per drawing group at res 128.

### Known limitations / next steps

- **Thin, complex shapes with few views stay rough.** A chair or a stove
  reconstructs into a correct, single, consistent surface; a thin statue built
  from only two distinct renders fragments into lobes. With 2-4 views that is
  fundamentally under-constrained; the fixes are symmetry completion, a TSDF /
  screened-Poisson smoother, or primitive/template fitting (§4-D), in that order.
- The view split can look speckled where the surface is bumpy, because adjacent
  triangles flip between source views. Region-coherent (per-patch) view
  assignment is the natural next refinement.
- The simplifier often stops somewhat above its target; tightening the threshold
  schedule or doing a final vertex-cluster pass would cut FSOM size further.
- **Runtime generation is intentionally not rewired.** The reconstructor runs in
  the offline tool only, so the default game behaviour is unchanged. Wiring it
  into `DGRP3DMesh` behind `DGRPRCParams` (Step 5) is still the follow-up if the
  pack is not built; the cache already invalidates on `CURRENT_RECONSTRUCT`.
- These meshes are classical/deterministic, not AI-generated, so they do not
  conflict with the community pack's content policy; they still ship as a
  separate, optional pack and never overwrite hand-authored work.
- **Animated objects** (a run of `NumGraphics` drawgroups stepped by the VM
  `Graphic` variable) are merged into one container per object instead of one
  file per state; see `docs/ANIMATION_MERGE.md`.

## 11. Session 2026-09-27 — z-buffer semantics, sparse shapes, cleanup

### What the renderer actually does with the z-buffer

Reading the engine's own consumer (`tso.world/Utils/_2DWorldBatch.cs` +
`tso.content/ContentSrc/Effects/2DWorldBatch.fx`) gives the exact depth model:

```
depthCalc:  depth = backDepth + (1 - z/255) / 0.4 * frontDepth
```

A pixel's surface lies `(1 - z/255) * DepthRange` along the view direction from
the far plane, and depth increases **toward** the camera. `frontDepth` is the
projected depth change over `dirToFront` (`(±3,0,±3)`) and the `0.4` matches the
generator's `factor = 0.39`. The far plane is exact (`Inverse(r, u, v, 0)`), so
the only unknown is the world depth range; the generator's constants give
`110.851 * 1.43/128 / 0.39 ≈ 3.18`.

Two further facts matter and were previously missed:

- The 2D renderer adds a **per-rotation screen offset** from `ObjectOffset`
  (`DGRPRenderer` lines 209-216: `GetScreenFromTile(RotateZ(ObjectOffset *
  (1/16,1/16,1/5), dir))`). For the objects inspected `ObjectOffset` is `(0,0,0)`
  so this is zero, but it must be honoured if non-zero data appears.
- `SpriteOffset` differs per rotation even for well-formed objects (the pool
  table's Y offsets are `-36,12,16,1`) because the object's screen footprint
  differs per rotation. This is expected, not a bug.

### Why thin/sparse shapes (trees) vanished

`FuseField` treated a direction with no opaque pixel at the projection as a veto,
so a single transparent gap killed the voxel. Worse, the raw alpha of a tree
canopy is mostly holes, so "opaque in all four directions" is nearly empty even
before depth carving. Fixes:

- **Filled silhouettes.** `MakeSilhouette` flood-fills the background and marks
  enclosed transparent holes as inside, so the visual-hull test uses the outline,
  not the raw per-pixel alpha.
- **Depth abstains instead of vetoing.** A direction with no depth sample at the
  projection contributes nothing; carving is still driven by the silhouette hull.
- **Silhouette fallback.** If depth carving empties the field, retry with the
  visual hull alone.
- **Single-view relief fallback.** Some objects (trees) ship four rotations that
  barely intersect at all - they are effectively independent billboards. When the
  hull is tiny the reconstructor builds a thin relief (`SingleViewRelief`,
  ±0.4 units) from the best-covered direction instead of returning nothing.

### Automatic mesh cleanup (added)

After marching: `RemoveNonManifold` (drop faces on >2-face edges), `FillHoles`
(cap boundary loops), `Taubin` (volume-preserving lambda/mu smoothing), then the
existing quadric `Decimate`, then `RemoveUnreferenced`.

### Registration: attempted, currently disabled

`RegisterRange` fits the depth range by ICP between the four back-projected point
sets (nearest-neighbour match, closed-form 1-D solve per iteration). It is
**unstable**: per-view fits drift to different values (e.g. `1.33/6.83/8.59/6.73`
for the pool table) and a shared global fit collapses objects toward each other
(it shrinks the bed to `0.65x0.59x0.78`). `UseRegistration` therefore defaults to
`false` and the engine-derived seed range is used. `AlignSilhouettes` (screen
-space coordinate descent that maximises the hull) runs only when the raw hull is
tiny and helps marginally.

### Point-cloud implicit surface: implemented, needs the depth scale

`FusePoints` back-projects every depth pixel, estimates a normal from the depth
gradient, and averages the signed distance to each point's tangent plane over the
grid (a simplified screened-Poisson). The intent was to replace the hull
intersection - which is what inflates - with a fit to the measured surfaces. With
the engine-derived range it **inflates** (pool table `4.4 x 3.3 x 6.0`): the four
views' points simply do not coincide, so the union is a blob. It is therefore off
by default (`UsePointFusion = false`) and kept for when the depth scale is solved.

### The depth scale is not recoverable from cross-view consistency (measured)

`Scan` (probe mode `scan`) sweeps the range `R` and prints the mean
nearest-neighbour distance between the four back-projected point sets. If a global
scale existed the curve would dip at it. It does not:

| object | argmin R | meanNN at argmin | behaviour |
|---|---|---|---|
| pool table | 8.0 | 0.26 | monotonically decreasing - wants a *large* range |
| fountain | 0.5 | 0.25 | monotonically increasing - wants a *small* range |
| tree | 6.0 | 0.43 | shallow minimum |

The metric is degenerate (as `R -> 0` each view collapses onto its own far plane,
so nearest-neighbour distance is not a well-posed objective). This is why every
calibration attempt - reprojection fit, per-view ICP, global ICP, mean-NN scan -
disagrees. The absolute depth scale is unobservable from the four views alone.

### Synthetic validation (why the carve, not the hull)

`hulltest` builds a known 2x1x3 box, projects its corners, and compares the visual hull
and the depth carve against the box:

| method | size | notes |
|---|---|---|
| ground truth box | 2.00 x 1.00 x 3.00 | |
| four-view visual hull | 3.08 x 2.54 x 4.03 | **inherently inflated** - the top face cannot be distinguished from the footprint without depth |
| depth carve (exact depth) | 2.50 x 0.96 x 2.91 | recovers the box to ~13% volume |

So the projection and carve math are correct; the residual error on real objects is the
depth scale plus inconsistent sprites, not the algorithm.

### Bounding-box calibration was tried and is unreliable

`calib2` reconstructs hundreds of covered drawing groups at a range of R and picks the R
whose bounding box best matches the community mesh. It does produce a minimum (R~1.5-2.5
depending on the sample) but the curve is within ~1% across R=1.5..2.5 and the community
meshes use a different orientation convention (a bed's human mesh is `2.19x1.24x0.96` while
the same object reconstructs as `1.08x1.26x2.16` - a 90-degree difference), so the metric is
comparing the wrong axes. The engine's own value (3.176) is kept.

### Final reconstruction settings

- `DepthRange = 110.851 * 1.43/128 / 0.39 = 3.176`, with an **adaptive growth**: if the carve
  comes out below `grid/2000` voxels it retries at 1.4x and 2x, so objects that the nominal
  range over-carves (a bed) still produce solid geometry.
- **Grounding**: the mesh is translated so its lowest vertex is at Y=0 (objects rest on the
  floor; the silhouettes often leave a small float).
- **Region-coherent view assignment**: per-triangle source direction is smoothed over triangle
  adjacency (3 passes) so neighbouring faces share a sprite - removes the speckle.
- **Fragmentation fallback**: a carve that shatters into more than 4 components means the four
  views disagree (trees); it is replaced by the single-view relief.
- **NaN normals fixed** in `DGRP3DVert.GenerateNormals` (a zero accumulated normal now falls
  back to UnitY instead of producing NaN).
- Dead morphological code removed.

### Honest status

- Solid objects (pool table, fountain, bed, sofa, bookcase, chair) reconstruct
  into coherent, grounded single surfaces; the pool table is `2.51x1.82x2.25`
  against the human remesh `1.88x0.94x2.94`.
- The four-view visual hull is still **inflated in Y and X** relative to the
  human meshes, and the depth (Z) is under-reconstructed at the seed range. This
  is the open problem: the absolute depth scale is not observable from
  cross-view consistency alone with a hard carve.
- Trees now produce a relief rather than nothing, but it is noisy.

### References consulted

- TSDF fusion: [andyzeng/tsdf-fusion](https://github.com/andyzeng/tsdf-fusion),
  [VDBFusion](https://pmc.ncbi.nlm.nih.gov/articles/PMC8838740/), Open3D
  [`ScalableTSDFVolume`](https://www.open3d.org/docs/latest/tutorial/pipelines/rgbd_integration.html).
- Visibility-consistent fusion: [Merrell et al. 2007](https://people.inf.ethz.ch/pomarc/pubs/MerrellICCV07subm.pdf).
- Space carving: [Kutulakos & Seitz 1998](https://members.loria.fr/MOBerger/CoursVision/spaceCarving.pdf).
- Shape-from-silhouette: [KKeishiro/Shape-from-Silhouettes](https://github.com/KKeishiro/Shape-from-Silhouettes).
- Poisson / screened Poisson: [Kazhdan et al.](https://github.com/mkazhdan/PoissonRecon),
  [ToG 2013](https://www.cs.jhu.edu/~misha/MyPapers/ToG13.pdf), Open3D
  [`create_from_point_cloud_poisson`](https://www.open3d.org/docs/latest/tutorial/geometry/surface_reconstruction.html)
  (+ density trimming / SurfaceTrimmer).
- Outlier removal: [Open3D](https://www.open3d.org/docs/latest/tutorial/geometry/pointcloud_outlier_removal.html).
- Thin structures: [CurveFusion](https://lingjie0206.github.io/papers/curvefusion/curvefusion.pdf).
- Watertight repair: [ManifoldPlus](https://arxiv.org/abs/2005.11621),
  [elalish/Manifold](https://github.com/elalish/manifold), [PyMeshLab](https://pymeshlab.readthedocs.io/en/latest/filter_list.html).
- Symmetry completion: [Rock et al. 2015](https://www.cv-foundation.org/openaccess/content_cvpr_2015/papers/Rock_Completing_3D_Object_2015_CVPR_paper.pdf).

### Next steps (in order)

1. **Back-project to oriented points and run screened Poisson** (Open3D or a
   C# port) instead of a hard carve; trim by density. This is the most robust
   route to "roughly fits the shape" and sidesteps the hard-carve inflation.
2. **Solve the depth scale with a well-posed objective** - e.g. ICP that
   registers whole clouds with a shared scale and a one-cloud anchor, rather than
   per-view free fits, or a global `(C,D)` fit with a symmetry prior.
3. Region-coherent per-patch view assignment to remove speckling.
4. Regenerate the pack and run the full test suite (currently blocked by a
   concurrent edit to `Simitone.Client`).

## 12. Session 2026-09-27 (later) — course correction: fix the original, don't replace it

### What went wrong

The whole volumetric direction above was aimed at the wrong property. The brief
said the existing meshes had "vertex displacements" and "misplacements", so it
was read as "produce a clean closed 3D mesh". But the property a player actually
sees is colour fidelity, and the volumetric rewrite threw it away:

- The original generator (`DGRP3DMesh`) is **image based**. Every sprite pixel
  becomes a vertex carrying *that pixel's own UV* (`(x/w, y/h)`), displaced along
  the view ray by its depth. Colours are exact by construction; the mesh *is*
  the sprite, pushed into depth. Rendered side by side, its pool table is a pool
  table - green felt, wood rails, legs.
- The volumetric reconstructor re-projects the sprite onto a reconstructed
  surface. Whenever that surface is not exactly where the sprite's surface is -
  which is most of the time, because the four renders do not co-register and the
  depth scale is unobservable (sec. 11) - every vertex samples the wrong texel.
  The result is a rounded, smeared blob.

So the original is better at looking like the artist's object; the volumetric
mesh is better only at being a closed surface. That trade was never worth it.

### The two real defects (both fixed)

1. **No sprite filtering.** A DGRP can carry dynamic/animation parts parked far
   from the object (`SpriteOffset.Y` of -1500, -3036). The original baked them
   all in, so the custom "Hungry Hamster" cage came out as a speck plus a cloud
   of fragments, 41 world units across. Fixed by clustering the sprites per
   rotation and keeping only the main cluster (largest, then nearest origin).
2. **Raw depth displacement.** Displacing the depth as stored throws spikes at
   every discontinuity - a table edge, a chair leg, a tree's leaves. That is what
   "vertex displacement" looked like. Fixed by blending the depth with its opaque
   3x3 neighbourhood mean before displacing; transparent pixels never contribute,
   so silhouette edges stay sharp.

### What landed

- `tso.files/RC/DGRP3DLegacy.cs` - the original algorithm, device-free (so the
  offline generator can use it), with both fixes. Emits the same
  `List<List<FusionGeometry>>` the pipeline already consumed.
- `DGRP3DMesh` (the runtime path) got the same two fixes: a
  `DGRP3DLegacy.MainCluster` pre-pass and `DGRP3DLegacy.SmoothDepth`. Its
  `totalSpr` counting was also corrected so a sprite with no depth no longer
  leaves `Complete()` unreachable.
- `Simitone.GenerateMeshes` now calls `DGRP3DLegacy.Build` instead of
  `DGRP3DFusion.Build`. `--res` / `--no-depth` are accepted but inert.
- `DGRP3DFusion` (the carve) is no longer on any generation path.

### Verified (renders, `FSOM_LEGACY` path = the real generator)

- Hungry Hamster cage: `1.00 x 2.81 x 1.04`, 8 parked sprites dropped; renders as
  the artist's cage - orange target wall, striped floor, the hamster on the floor.
- Tree (`Trees.iff` 100): `0.95 x 3.18 x 0.96`; renders as a trunk with a leafy
  crown in all four rotations. Foliage is as sparse as the sprite's alpha.

### Open

- Decide whether `DGRP3DFusion.cs` and `FusionTests` are deleted outright.
- Regenerate the pack and re-render a spread of objects to confirm the fixes hold
  beyond these two.

## 13. Session 2026-09-27 (later still) — can the "3d net" gaps be filled?

The meshes read as an unfolded box ("3d net"). Assessment, with the tests that
were run.

### The net is inherent to the approach

Each rotation becomes a *paper-thin 2.5D relief*: the sprite's pixels pushed into
depth along one view ray. Rendering a single sheet (`ham_rot0_all.png`) from four
angles shows it is a folded surface that looks exactly like the sprite from its
own angle and is a **sliver** from the side. The runtime draws all four at once -
`DGRPRenderer.Draw` iterates every geom in the group; `PixelDir` only picks the
texture, it is not a visibility selector. Four slivers at 90 degrees is the net.

### Three kinds of gap, and what filling each costs

| Gap | Cause | Fillable? | Cost |
| --- | --- | --- | --- |
| **Fold holes** | At a depth jump the original culls the connecting quad (`MaxAllowedSq`), so a table edge / wall-floor junction is a hole | **Yes, cleanly** | Connect the depths instead of culling. Tested (`FSOM_MAXQUAD=999`): holes close, but a raw connect stretches the edge texels across the whole depth. With the depth smoothing in place the walls are short, so this is cheap and safe. |
| **Open silhouette / back** | The sheet is single-sided and stops at the sprite's alpha boundary | **Yes** | The runtime culls back faces (no explicit `CullMode`, so the default), so a sheet is invisible from behind. Disabling culling for these meshes makes them read from both sides; it does not add volume. |
| **Inter-sheet seams** | The four sheets are separate surfaces; the wedges between them are empty | **Only with volume** | Tested giving each sheet a back layer + rim (`FSOM_SOLID`): the gaps close, but the object becomes a chunky self-intersecting block and the back/rim faces show stretched edge texels. Even a thin shell (`SolidBack` 0.15-0.35) bloats the object, because the relief's depth range (~3.2 units) is as large as the object (~2.8 units). |

### Verdict

The fold holes and the single-sided silhouette **can be filled cleanly and
cheaply**. The inter-sheet seams **can be closed, but not for free**: every route
that adds the volume they need either bloats/distorts the object (extruding the
sheets) or smears the texture (the volumetric route already rejected in sec. 12).
So "fill the gaps" is possible, but it trades away the property this approach was
chosen for - looking exactly like the artist's sprite.

### Options

- **A (cheap, safe).** Fill the fold holes and disable back-face culling for the
  generated meshes. Watertight within each sheet; the outer silhouette stays thin.
- **B (targeted).** A + a rim only where a sheet's silhouette runs alongside
  another sheet, so seams close without bloating the outline.
- **C (hybrid).** Relief sheets for colour + a coarse visual-hull backing to plug
  the seams; seams then show a smeared backing rather than the background.
- **D (no fill).** Draw only the sheet matching the current view rotation. The
  camera is iso and the rotation is discrete, so a matching sheet always exists -
  no net, exactly the sprite. Breaks down only if the 3D camera is moved off the
  iso axes.

`DGRP3DLegacy.SolidSheets` / `SolidBack` implement the volume experiment and are
off by default.

### Rendered results (hamster + tree, A/B/C)

All three were implemented and rendered (`FSOM_FOLDS`, `FSOM_STITCH`,
`FSOM_HYBRID`). What they showed:

- **The gaps really are minimal.** At a seam threshold of 0.12 world units the
  stitcher already found ~2,500 edges (hamster) / ~6,600 (tree) within reach of
  another sheet - the four silhouettes nearly coincide. That is why the front
  view barely changes.
- **A (fold fill):** closes the fold holes. The front view is unchanged; from an
  angle between two sheets the folds are filled by slanted walls (visible slabs
  across the cage seat) whose texture is stretched along the wall. Watertight,
  but the filled walls are the eye-catching part.
- **B (A + seam stitch):** adds bridges where the sheets run alongside. At 0.12
  the extra fill over A is modest; the bridges sample the edge texel, so they
  read as a continuation of the face.
- **C (hybrid):** as implemented it is a mess. The only volumetric backing to
  hand is the visual hull, which is inflated and misaligned (the hamster hull is
  `3.9 x 4.0 x 3.0` against the sheets' `1.0 x 2.8 x 1.0`), so it swamps the
  sheets. C needs a tight, well-registered backing, which the carve does not
  currently provide.

So A and B are the viable ones; C is blocked on a better hull.

## 14. Session 2026-09-28 - why regenerated meshes changed nothing in-game

After a regeneration the objects were still the old oversized ones. The
regeneration itself was correct; the meshes never reached the game.

### The chain

- The game reads `FSOEnvironment.ContentDir` = `Content/` (the working directory,
  i.e. the build output), and `RCMeshProvider` looks in `Content\MeshReplace`
  first (`FSOEnvironment.ContentDir/MeshReplace`, with a user-dir `MeshReplace`
  override).
- `Simitone.Client.csproj` copies `Content\**\*.fsom` into the build output, so
  the **committed** `Client\Simitone\Simitone.Client\Content\MeshReplace` is what
  ships.
- `Simitone.Windows.csproj` then runs `Simitone.PackageRemeshes`, which flattens
  the community pack over the top of that folder.

So the shipped pack is the source `Content\MeshReplace` plus the community pack.
That source folder still held the **old** generated meshes (`vrhelmet_iff_128`
was `2.83 x 3.67 x 2.91` against the new `0.97 x 0.83 x 0.96`;
`diningchairs2_iff_128` was `1.73 x 2.22 x 1.66` floating at Y=0.79 against the
new `0.56 x 1.08 x 0.65`). `GeneratedMeshes/` is gitignored scratch, and the
script's `-InstallOutput` skipped any destination that already existed, so it
could not replace the stale meshes either.

### The fix

`generate-meshes.ps1 -InstallOutput` now **syncs** the generated pack into the
shipped folder (and the build output):

- overwrite every generated mesh,
- delete stale generated meshes (those neither in the new pack nor owned by the
  community pack),
- never touch a file whose name the community pack owns.

The community pack's file names are collected from the pack root and used as a
protected set, so a hand-authored mesh can never be shadowed by a generated one.

### Result

- Shipped `Content\MeshReplace`: 5,916 generated meshes (4,240 stale removed).
- Build output: 5,916 generated + the 2,663 community meshes from
  `PackageRemeshes`, i.e. the community overlay is intact.
- `vrhelmet_iff_128` and `diningchairs2_iff_128` are now the correctly sized
  meshes in both places.

Regenerating without `-InstallOutput` still only fills the scratch folder and
changes nothing the game loads; the script now says so.




