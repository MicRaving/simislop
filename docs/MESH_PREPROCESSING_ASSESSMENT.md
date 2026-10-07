# Sprite preprocessing assessment: upscaling, and whether learned SR helps

**Status:** 2026-10-06 (second revision; learned-SR experiment added). Measured on
`gargoyle_iff_100` (the Makin' Magic statue) against the two cleanup candidates from the
2026-10-05 competition. Companion to `docs/MESH_MANUAL_CLEANUP.md`; evidence bundles live in
`.meshcleanup/gargoyle/`.

**Question.** Both shipped candidates were criticised for the same class of defect (DeepSeek: lost
detail, very low-res texture; Space Bunny: grainy, not cleaned up). Would preprocessing the
source sprites - upscaling first, including a real learned upscaler like RealSR/Real-ESRGAN -
produce better results?

**Answer, in two parts.**

1. **Classical resampling (nearest/bicubic/Lanczos, with or without sharpening) is a filter
   choice, not a fidelity gain** - measured below; it changes the image about as much as swapping
   the preview's nearest sampling for the GPU's bilinear, and recovers nothing.
2. **A real learned SR model (Real-ESRGAN x4plus; the current RealSR-class standard) genuinely
   does remaster the sprite** - clean edges, no pixel blocks, plausible stone structure, no
   Lanczos ringing. But on the *carved hull* most of that gain is destroyed by the geometry (voxel
   staircase) and the oblique UV projection: at mesh level the SR texture is a modest improvement,
   not a transformation. SR pays off in strategies where the sprite is shown at or near its own
   resolution - a hybrid sprite shell, corrected UVs, or a billboard - not as a standalone pass
   over the current hull.

---

## 1. What the source actually is (measured)

`Gargoyle.iff` drawgroup 100 carries all three zoom levels, all four directions:

| zoom | sprite size | note |
|---|---|---|
| 3 (used by the generator) | **29x52 / 31x52 / 29x51 / 31x51** | the largest source that exists |
| 2 | 15x27 / 16x26 (x2) | smaller render of the same view |
| 1 | 8x13 / 8x14 / 9x13 | smallest |

There is no higher-resolution copy anywhere in the object data. Alpha edges are anti-aliased
(179-195 partial-alpha pixels per rotation) and depth has 30-32 levels. At a 1024-pixel preview
panel one source texel covers ~20 render pixels, so any upscaling is an interpolation decision
over a 20x blow-up.

## 2. Classical upscaling, measured

4x Lanczos4 (premultiplied) and 4x + unsharp, baked as `_TEX_` PNGs onto the winner's geometry
unchanged; in-game-like bilinear previews:

| comparison | mean abs diff | p95 | pixels differing > 16 |
|---|---|---|---|
| native sprite + bilinear (game) vs 4x Lanczos | 7.67 | 36 | 7.7 % |
| native + bilinear vs 4x Lanczos + unsharp | 9.01 | 37 | 9.6 % |
| native nearest preview vs native bilinear | 7.51 | 23 | 9.6 % |
| texture space: native + bilinear vs 4x Lanczos | 10.75 | - | - |

Upscaling changes the image by about the same amount as the *filter choice*. Visually the Lanczos
bakes add ringing (dark/bright contour fringes) at edges - see `sr_sprite_compare2.png`, panel 2.
No detail is recovered. This reproduces the conclusion of the first revision of this document.

## 3. Learned SR (Real-ESRGAN / RealSR class), measured

Ran Real-ESRGAN on CPU (torch 2.13; ~4 s for all four sprites) with two models - `x4plus`
(RRDBNet, the classic photo model) and `realesr-general-x4v3` (compact, real-world) - plus
`x4plus_anime_6B` for the flat-shaded case. Correct handling matters and each fix changed the
result:

- RGB under transparent texels is **edge-filled with the nearest opaque colour** (TELEA inpainting
  was tried first and diffuses contour rings into the input, which the model then amplifies as
  texture);
- alpha is upscaled **nearest + Gaussian**, not Lanczos: coverage-mask Lanczos rings and the
  ringing becomes a visible fringe;
- brightness: `x4plus` drifts **~17 % dark** on these sprites (visible-area mean 108.4 vs 130.5).
  A per-channel mean match to the native sprite restores it to within 0.5 of the source.

**Sprite level** (`sr_sprite_compare2.png`: native-nearest4x | Lanczos4x | x4plus | general):
the learned outputs are the only ones that look like a higher-resolution render of the statue;
the wing, torso folds and pedestal read as clean structure instead of blocks, with no ringing.
Laplacian variance is *lower* than Lanczos (e.g. rot1: 8798 vs ~650) - the model smooths and
regularises rather than adding edge energy, which is what removes the pixel blocks.

**Mesh level** (the winner's hull, textures swapped, bilinear preview, `sr_mesh_compare2_rot1.png`;
sharpness = Laplacian variance over the object, diff vs the native-sprite render):

| texture | sharpness | diff mean | p95 | >16 |
|---|---|---|---|---|
| native sprite, bilinear (current game look) | 406 | - | - | - |
| 4x Lanczos | 648 (ringing) | 5.5 | 16 | 5.0 % |
| x4plus | 515 | 10.3 | 27 | 19.2 % |
| x4plus + mean match | 515 | 11.8 | 29 | 22.9 % |
| anime_6B + mean match | 505 | 12.8 | 31 | 27.0 % |

The SR texture on the hull is a small improvement: some block-stone texture replaces flat mosaic,
a little more definition in the body, but the voxel staircase and the oblique UV sampling are
unchanged, and at panel scale the five variants are close (`sr_after_official.png` shows the
full-quality four-panel render of the mean-matched x4plus bake). `x4plus` and the others did not
differ enough to declare a winner; the anime model is not better here.

## 4. Why the hull dilutes it (and where SR would pay)

The hull's surface normals are axis-aligned voxel faces while the sprite is a diagonal plane, so
every face samples the texture obliquely (~20x magnification, docked at grazing angles). What the
SR model invented - clean, plausible *pixel-scale* structure - is exactly what the projection
smears. SR is a texture-side fix; the hull's visible defects are geometry-side. Both candidates'
reports named the same two candidates for the real fix (per-face planar UV unwrap / UV transfer
from the sheets; a thin sprite-accurate shell over the hull). In both, the sprite is displayed at
its own resolution, so an SR bake would show at full strength instead of being resampled again.

## 5. Ranked options, by expected quality per unit of work

1. **Per-face planar UV unwrap / UV transfer from the original sheets** (they carry exact
   per-pixel UVs). Removes the shear/chevron; the largest visual gain on the current candidates.
2. **Hybrid hull + sprite shell** (hull as volume/back, trimmed sheet surface on the visible
   regions). Restores the artist's per-pixel detail; this is where SR textures pay off, together
   with depth smoothing/coverage-alpha trimming.
3. **Visibility-weighted multi-view baking** (blend the four views per texel by front-facing and
   unoccluded weight) instead of per-face assignment - removes assignment seams and grazing-band
   streaks, and is the natural place to plug SR textures in.
4. **Learned SR bake** (`x4plus`, maybe `general-x4v3`) as the texture source for the above, with
   edge-fill + nearest/blur alpha + brightness match. Ship as custom `_TEX_*.png` (sprite
   materials always bind the original 29px atlas, so this is the only way it reaches the game).
   Costs 4x texture area and adds hallucinated structure.
5. **ML SR as a default, unlabelled, whole-pipeline pass** - not recommended: it invents detail,
   is model-dependent, and sits badly with the remesh community's stated position on generated
   content. Use it as an explicit, documented texture step in Simitone-owned content only.

## 6. Recommendation

Adopt **learned SR as the texture source** (option 4) *inside* a texture-first strategy
(option 2 or 3, made possible by option 1) - not as a standalone preprocessing pass. On the
current shipped hull, SR alone does not fix the "grainy, not cleaned up" complaint, because that
complaint is about geometry; but for a corrected-UV or shell mesh the SR bake is the difference
between "29-pixel mosaic" and "remastered stone".

Concrete next prototype: take the winner's hull, transfer UVs from the nearest sheet vertex (or
wrap per face along the assigned view axis), bake the four brightness-matched x4plus sprites into
custom textures, and re-render the four panels plus off-axis. Acceptance test: the pedestal
chevron and grazing-band streaks disappear, silhouette IoU is unchanged, and the SR structure is
visible instead of smeared.
