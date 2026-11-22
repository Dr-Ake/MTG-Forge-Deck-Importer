using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

// Deterministic, local icon artwork. No external images or dependencies.
internal static class BuildImporterIcon
{
    private static GraphicsPath Rounded(float x, float y, float width, float height, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void Card(Graphics g, float centerX, float centerY, float angle, Color color)
    {
        GraphicsState saved = g.Save();
        g.TranslateTransform(centerX, centerY);
        g.RotateTransform(angle);
        using (GraphicsPath path = Rounded(-116, -157, 232, 314, 23))
        using (SolidBrush fill = new SolidBrush(color))
        using (Pen border = new Pen(Color.FromArgb(255, 27, 47, 70), 10))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        g.Restore(saved);
    }

    private static Bitmap Render()
    {
        Bitmap bitmap = new Bitmap(512, 512, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (GraphicsPath tile = Rounded(16, 16, 480, 480, 104))
            using (LinearGradientBrush navy = new LinearGradientBrush(new Point(40, 30), new Point(470, 500), Color.FromArgb(35, 65, 94), Color.FromArgb(14, 28, 49)))
            {
                g.FillPath(navy, tile);
            }
            Card(g, 208, 245, -14, Color.FromArgb(239, 178, 69));
            Card(g, 263, 243, 8, Color.FromArgb(40, 169, 164));
            Card(g, 247, 246, 0, Color.FromArgb(249, 246, 233));
            using (SolidBrush gold = new SolidBrush(Color.FromArgb(239, 178, 69)))
            using (GraphicsPath stripe = Rounded(153, 112, 188, 25, 9))
            {
                g.FillPath(gold, stripe);
            }
            using (SolidBrush navy = new SolidBrush(Color.FromArgb(35, 65, 94)))
            {
                PointF[] diamond = { new PointF(247, 167), new PointF(307, 239), new PointF(247, 311), new PointF(187, 239) };
                g.FillPolygon(navy, diamond);
            }
            using (SolidBrush teal = new SolidBrush(Color.FromArgb(12, 132, 139)))
            using (Pen rim = new Pen(Color.FromArgb(249, 246, 233), 10))
            {
                g.FillEllipse(teal, 282, 290, 190, 190);
                g.DrawEllipse(rim, 282, 290, 190, 190);
            }
            using (SolidBrush white = new SolidBrush(Color.White))
            {
                PointF[] arrow = { new PointF(357, 324), new PointF(397, 324), new PointF(397, 378), new PointF(426, 378), new PointF(377, 430), new PointF(328, 378), new PointF(357, 378) };
                g.FillPolygon(white, arrow);
                using (GraphicsPath tray = Rounded(328, 440, 98, 12, 5)) g.FillPath(white, tray);
            }
        }
        return bitmap;
    }

    private static Bitmap Resize(Bitmap source, int size)
    {
        Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(result))
        {
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, size, size), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        }
        return result;
    }

    private static void Main(string[] args)
    {
        string assetDirectory = args.Length == 1 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Assets"));
        Directory.CreateDirectory(assetDirectory);
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        byte[][] images = new byte[sizes.Length][];
        using (Bitmap artwork = Render())
        {
            artwork.Save(Path.Combine(assetDirectory, "forge-deck-importer-preview.png"), ImageFormat.Png);
            for (int i = 0; i < sizes.Length; i++)
            {
                using (Bitmap sized = Resize(artwork, sizes[i]))
                using (MemoryStream stream = new MemoryStream())
                {
                    sized.Save(stream, ImageFormat.Png);
                    images[i] = stream.ToArray();
                    if (sized.GetPixel(0, 0).A != 0) throw new InvalidOperationException("Icon corner must remain transparent.");
                }
            }
        }
        string iconPath = Path.Combine(assetDirectory, "Forge Deck Importer.ico");
        using (BinaryWriter writer = new BinaryWriter(File.Create(iconPath)))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(images[i].Length); writer.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] image in images) writer.Write(image);
        }
        using (Icon check = new Icon(iconPath, new Size(48, 48)))
        {
            if (check.Width != 48 || check.Height != 48) throw new InvalidOperationException("Windows icon could not be loaded at 48 pixels.");
        }
        Console.WriteLine("Created multi-resolution Windows icon: " + iconPath);
        Console.WriteLine("Sizes: " + String.Join(", ", sizes) + "; 32-bit PNG entries; transparent corners verified.");
    }
}
