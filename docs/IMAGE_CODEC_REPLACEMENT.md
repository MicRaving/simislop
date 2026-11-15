# Replacing SixLabors.ImageSharp

ImageSharp 3.2+ needs a Six Labors licence key, and 3.1.12 carried six suppressed advisories, so it is gone.
Simitone's own projects now use two small in-repo codecs in `Simitone.Client\Utils`:

- `BmpCodec` - decode only: 1/4/8-bit palettised, 16/24/32-bit, bottom-up and top-down, RLE8/RLE4, BITFIELDS.
- `PngCodec` - decode every colour type/bit depth incl. Adam7 and tRNS; encode 8-bit RGBA or greyscale.

`BitmapDecoder.Read` dispatches on the magic bytes, so only BMP and PNG are ever parsed (TIFF/GIF/JPEG return null).
The TS1 cursor `bfOffBits` repair stays in `BitmapDecoder`.

Why not StbImageSharp: it failed to decode the 38 RLE8 BMPs that mods ship (and it does not handle every 1/4-bit
variant), so a BMP decoder would have been hand-rolled anyway. Writing the PNG half on `ZLibStream` costs about
150 lines and no dependency, which is a smaller supply-chain surface than a package.

Verification (throwaway harness, old ImageSharp 3.1.12 path vs the new codecs, RGBA compared byte for byte):
6,459 real BMP/PNG files (install, mods, Content, repo, FreeSO) - 1 difference, `easelpaintbrush.bmp`, where
ImageSharp left the last row of an RLE8 stream black because the stream ends with EOF instead of EOL; the new
decoder renders that row. Plus ~1,370 synthetic BMP/PNG variants: differences only in ImageSharp's own bugs
(Adam7 greyscale/palette on tiny images, over-wide RLE runs wrapping onto the next row) and the non-standard 2bpp BMP.
