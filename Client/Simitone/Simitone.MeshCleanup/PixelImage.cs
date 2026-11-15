using Simitone.Client.Utils;
using System.IO;

namespace Simitone.MeshCleanup
{
    /// <summary>One straight-alpha RGBA pixel.</summary>
    public struct Rgba32
    {
        public byte R, G, B, A;

        public Rgba32(byte r, byte g, byte b, byte a = 255)
        {
            R = r; G = g; B = b; A = a;
        }
    }

    /// <summary>A plain top-down RGBA canvas that saves to PNG, for the preview renderer.</summary>
    public sealed class PixelImage
    {
        public readonly int Width;
        public readonly int Height;
        public readonly Rgba32[] Pixels;

        public PixelImage(int width, int height, Rgba32 fill = default)
        {
            Width = width;
            Height = height;
            Pixels = new Rgba32[width * height];
            if (fill.A != 0 || fill.R != 0 || fill.G != 0 || fill.B != 0)
                for (int i = 0; i < Pixels.Length; i++) Pixels[i] = fill;
        }

        /// <summary>Copies <paramref name="source"/> onto this canvas with its top-left at (x, y).</summary>
        public void Blit(PixelImage source, int x, int y)
        {
            for (int sy = 0; sy < source.Height; sy++)
                for (int sx = 0; sx < source.Width; sx++)
                    Pixels[(y + sy) * Width + x + sx] = source.Pixels[sy * source.Width + sx];
        }

        public void SavePng(string path)
        {
            var bytes = new byte[Pixels.Length * 4];
            for (int i = 0; i < Pixels.Length; i++)
            {
                bytes[i * 4] = Pixels[i].R;
                bytes[i * 4 + 1] = Pixels[i].G;
                bytes[i * 4 + 2] = Pixels[i].B;
                bytes[i * 4 + 3] = Pixels[i].A;
            }
            File.WriteAllBytes(path, PngCodec.EncodeRgba(Width, Height, bytes));
        }

        public static PixelImage LoadPng(string path)
        {
            var image = PngCodec.Decode(File.ReadAllBytes(path));
            var result = new PixelImage(image.Width, image.Height);
            for (int i = 0; i < result.Pixels.Length; i++)
                result.Pixels[i] = new Rgba32(image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2], image.Pixels[i * 4 + 3]);
            return result;
        }
    }
}
