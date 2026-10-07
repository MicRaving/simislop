# Merging animated object states into one 3D object

**Status:** assessed + implemented. Date: 2026-09-27.
**Related:** `docs/MESH_GENERATION_RESEARCH.md` (the reconstructor this builds on).

## The problem

The generator wrote **one `.fsom` per drawgroup**. But TS1 does not animate an object by
changing sprites inside one drawgroup — it swaps the whole drawgroup:

- `OBJD.BaseGraphicID` + `OBJD.NumGraphics` define a run of `NumGraphics` consecutive DGRP
  chunks.
- The object's `Graphic` VM variable selects one:
  `VMGameObject.RefreshGraphic()` does `Resource.Get<DGRP>(BaseGraphicID + Graphic)` and
  assigns it to `ObjectComponent.DGRP`; the renderer then loads the mesh for that drawgroup.

So an animated object is a *sequence of drawgroups*. Measured on the real install:

| Object | `NumGraphics` | sub-objects | files produced |
|---|---|---|---|
| `PetBirdCage.iff` | 25 | 4 | **100** |
| `AlarmClock.iff` | 7 | 1 | 7 |
| `Bars.iff` (sub 1) | 3 | — | 3 |
| `Volleyball.iff` | 60 | — | 60 |

Each frame is reconstructed independently, so a mostly-static birdcage is stored 25 times
over (≈3.6 MiB each — ~360 MiB for one object), and as the object animates the runtime
loads and caches every frame as a separate mesh. That is the "one new object per state"
duplication.

Additionally, many states are **recolours or static poses**: identical geometry, different
texture. Those are pure waste.

## Options assessed

**A. Keep one file per drawgroup (status quo).** Simple, already works, but N near-duplicate
files and N cached meshes per animating object.

**B. One container per object, sharing identical geometry blocks. (chosen)**
Pack all `NumGraphics` frames into a single file; store each distinct *block* (a frame's
geometry, without textures) once and let frames reference it, supplying per-frame texture
selectors. Frames that reconstruct identically — recolours, static poses, and the empty
frames at the ends of many animations — collapse to one block. No renderer change: each
frame is still handed to the existing draw loop as a normal mesh.

**C. Shared topology + per-vertex morph targets. (future)**
Resample every frame onto one topology and store per-frame vertex positions, giving true
interpolation between poses. This is the "most coherent" result but needs correspondence
across independently carved meshes and is a research-scale change; deferred.

**D. Split static vs moving geometry (base + deltas). (future)**
Reconstruct the parts that never move once and store only the moving parts per frame. The
biggest size win for objects like the birdcage, but again needs reliable cross-frame
correspondence. Deferred.

## What was implemented (option B)

1. **Format — `FreeSO/TSOClient/tso.files/RC/DGRP3DAnim.cs`** (`FSOa`, gzip, **version 2**):
   a table of **group blocks** (geometry only, one per dynamic group) plus a **frame table**
   (per frame, per group: block index + `(PixelDir, PixelSPR)` selectors), mask type and
   bounds. `FromFrames` dedupes blocks by a quantised-position + index SHA-1, so frames that
   share a dynamic group — recolours, static poses, empty tail frames — share that block. The
   dedupe granularity is the dynamic group, not the whole frame. **Version 1** (one block per
   whole frame) is still read and converted in memory, then re-emitted as version 2.

2. **Shared GPU buffers — `FreeSO/TSOClient/tso.files/RC/DGRP3DBuffer.cs`:** a geometry's
   vertex/index buffers are now refcounted and can be shared. `DGRP3DGeometry` holds a
   `DGRP3DBuffer` and exposes `Verts`/`Indices`/`PrimCount` as delegates, so the renderer is
   unchanged. The animated loader (`DGRP3DAnimCache`) uploads each block once and shares that
   buffer across every frame and recolour that uses it — a recoloured frame now costs a
   texture, not a copy of the vertices.

3. **Generator — `Client/Simitone/Simitone.GenerateMeshes/Program.cs`:**
   builds a `drawgroup → (OBJD, frame)` map from every OBJD, and when it reaches the first
   frame of a multi-graphic object, reconstructs **all** frames and writes **one**
   `<iff>_iff_<BaseGraphicID>.fsom`; the individual frame files are skipped. If **any**
   frame is covered by the community pack, the merge is abandoned for that object and the
   ordinary per-frame path is used, so hand-authored meshes are never shadowed. `--verify`
   understands both formats.

4. **Tests — `Client/Simitone/Simitone.Tests/Utils/AnimMeshTests.cs`:**
   whole-frame recolour dedupe, per-dynamic-group dedupe, container round-trip (frames,
   blocks, selectors, geometry, bounds) and magic detection. The loader/format is also
   self-tested end-to-end from the probe harness (v2 round-trip and v1→v2 conversion).

5. **Runtime — `FreeSO/TSOClient/tso.content/RCMeshProvider.cs`:**
   for an object with `NumGraphics > 1`, a drawgroup in `[BaseGraphicID, +NumGraphics)`
   is served from the container, with `frame = dgrp.ChunkID - BaseGraphicID`. Animated
   files are detected by magic so the plain `FSOm` loader never mis-parses one; if there is
   no container the existing per-drawgroup lookup runs unchanged. Frames are built lazily and
   cached per frame; each group block is uploaded once and its GPU buffer shared across the
   frames and recolours that use it.

### Re-packing an existing folder (`--merge-only`)

Merging normally happens during generation, but a pack produced before this change can be
converted in place without reconstructing anything:

```
Simitone.GenerateMeshes "<Sims>" .\GeneratedMeshes --pack FSO.Remeshes-main\remeshes --merge-only
```

It scans the game data for the object-to-frame mapping, reads each object's existing per-frame
files, writes the container, and deletes the now-redundant frame files. It is idempotent
(already-merged objects are skipped) and only touches multi-graphic objects. Missing frame
files are treated as empty frames, so run it on a complete pack. `--no-merge` produces the old
one-file-per-state output when it is wanted.

## Results

- `AlarmClock.iff`: 7 frames → **one** `alarmclock_iff_128.fsom` (7 frames, **5 blocks**,
  240 KiB).
- `Bars.iff` sub 1 (3 frames, dynamic sprites): → **one** `bars_iff_356.fsom`
  (3 frames, **2 blocks**).
- `verify` reads each container back: `1 ok / 0 bad`.

### Applied to the real pack

`--merge-only` on the 8,648-file generated pack (dry run first) merged **1,104** animation
objects, removed **4,253** redundant per-frame files, and left no failures:

| | before | after |
|---|---|---|
| `.fsom` files | 8,648 | **4,503** |
| size | 2,168 MiB | **1,756 MiB** |
| verify | — | **4,503 ok / 0 bad** |

Re-running the pass reports `merged: 0, already containers: 1,104` — it is idempotent.
(630 multi-state objects are skipped: their frames are all empty or hand-authored, so there
is nothing to merge.)

The 1,040 containers written by the earlier (v1) merge were then **upgraded to v2** in place
(atomic temp+move): per-group dedupe turned **7,593 frames into 3,454 blocks**. The runtime
shares one GPU buffer per block, so a recoloured frame costs only its texture.

## Limits / follow-ups

- Dedupe is **per dynamic group**: it removes duplicate groups across frames (recolours,
  static/empty poses) but not the duplicated *static sub-parts* inside a group that genuinely
  moves (option D, e.g. the birdcage body while the bird moves). That needs cross-frame
  correspondence and is the remaining size item.
- Recolours now **share GPU buffers** (one `DGRP3DBuffer` per block, refcounted); each frame
  still has its own `DGRP3DGeometry` for its texture, which is just a texture binding.
- Frames are swapped, not interpolated (option C would interpolate). At the game's animation
  rates the swap matches how 2D mode already animates.
- The runtime change is additive and falls back cleanly, but it has **not been play-tested**;
  the format, generator and loader are covered by tests and a real-data round-trip.

## Verification command

```
Simitone.GenerateMeshes "<Sims>" .\GeneratedMeshes --pack FSO.Remeshes-main\remeshes --verify
```

Every animated object now appears once; the console line reads
`<name>  N frames, M block(s), T tris`.
