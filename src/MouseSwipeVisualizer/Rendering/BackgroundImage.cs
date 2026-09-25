using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace MouseSwipeVisualizer.Rendering;

/// <summary>
/// Background picture for the glass look: loaded once per (file, size, blur) with GDI+, scaled to cover
/// the canvas, blurred and dimmed into a BGRA buffer. Runs on the engine thread only when the style or
/// size changes, never per frame.
/// </summary>
internal static class BackgroundImage
{
    /// <summary>Draws the image "cover"-fitted (fills the canvas, centred, cropped) into <paramref name="target"/>.</summary>
    public static bool TryLoadCover(string path, int width, int height, uint[] target, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = "file not found";
                return false;
            }

            using var source = LoadUnlocked(path);
            using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                double scale = Math.Max((double)width / source.Width, (double)height / source.Height);
                float w = (float)(source.Width * scale), h = (float)(source.Height * scale);
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark seams at the edges
                g.DrawImage(source, new Rectangle((int)Math.Floor((width - w) / 2), (int)Math.Floor((height - h) / 2),
                        (int)Math.Ceiling(w), (int)Math.Ceiling(h)),
                    0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }

            BitmapData data = canvas.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] row = new int[width];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, width);
                    for (int x = 0; x < width; x++)
                    {
                        target[y * width + x] = (uint)row[x] | 0xFF000000u;
                    }
                }
            }
            finally
            {
                canvas.UnlockBits(data);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or ExternalException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Reads the file into memory first so the image file is not kept locked.</summary>
    private static Bitmap LoadUnlocked(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    /// <summary>Approximate Gaussian blur (3 separable box passes), roughly <paramref name="radius"/> px sigma.</summary>
    public static void Blur(uint[] pixels, uint[] scratch, int width, int height, double radius)
    {
        int r = (int)Math.Round(radius / 2);
        if (r < 1 || width < 2 || height < 2)
        {
            return;
        }

        for (int pass = 0; pass < 3; pass++)
        {
            BoxHorizontal(pixels, scratch, width, height, r);
            BoxVertical(scratch, pixels, width, height, r);
        }
    }

    /// <summary>Darkens towards black by <paramref name="amount"/> (0-1).</summary>
    public static void Dim(uint[] pixels, int count, double amount)
    {
        if (amount <= 0)
        {
            return;
        }

        int keep = (int)Math.Round((1 - Math.Clamp(amount, 0, 1)) * 256);
        for (int i = 0; i < count; i++)
        {
            uint p = pixels[i];
            uint r = (((p >> 16) & 0xFF) * (uint)keep) >> 8;
            uint g = (((p >> 8) & 0xFF) * (uint)keep) >> 8;
            uint b = ((p & 0xFF) * (uint)keep) >> 8;
            pixels[i] = 0xFF000000u | (r << 16) | (g << 8) | b;
        }
    }

    private static void BoxHorizontal(uint[] src, uint[] dst, int width, int height, int r)
    {
        int div = 2 * r + 1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int sr = 0, sg = 0, sb = 0;
            for (int k = -r; k <= r; k++)
            {
                uint p = src[row + Math.Clamp(k, 0, width - 1)];
                sr += (int)((p >> 16) & 0xFF);
                sg += (int)((p >> 8) & 0xFF);
                sb += (int)(p & 0xFF);
            }

            for (int x = 0; x < width; x++)
            {
                dst[row + x] = 0xFF000000u | ((uint)(sr / div) << 16) | ((uint)(sg / div) << 8) | (uint)(sb / div);
                uint outP = src[row + Math.Max(x - r, 0)];
                uint inP = src[row + Math.Min(x + r + 1, width - 1)];
                sr += (int)((inP >> 16) & 0xFF) - (int)((outP >> 16) & 0xFF);
                sg += (int)((inP >> 8) & 0xFF) - (int)((outP >> 8) & 0xFF);
                sb += (int)(inP & 0xFF) - (int)(outP & 0xFF);
            }
        }
    }

    private static void BoxVertical(uint[] src, uint[] dst, int width, int height, int r)
    {
        int div = 2 * r + 1;
        for (int x = 0; x < width; x++)
        {
            int sr = 0, sg = 0, sb = 0;
            for (int k = -r; k <= r; k++)
            {
                uint p = src[Math.Clamp(k, 0, height - 1) * width + x];
                sr += (int)((p >> 16) & 0xFF);
                sg += (int)((p >> 8) & 0xFF);
                sb += (int)(p & 0xFF);
            }

            for (int y = 0; y < height; y++)
            {
                dst[y * width + x] = 0xFF000000u | ((uint)(sr / div) << 16) | ((uint)(sg / div) << 8) | (uint)(sb / div);
                uint outP = src[Math.Max(y - r, 0) * width + x];
                uint inP = src[Math.Min(y + r + 1, height - 1) * width + x];
                sr += (int)((inP >> 16) & 0xFF) - (int)((outP >> 16) & 0xFF);
                sg += (int)((inP >> 8) & 0xFF) - (int)((outP >> 8) & 0xFF);
                sb += (int)(inP & 0xFF) - (int)(outP & 0xFF);
            }
        }
    }
}
