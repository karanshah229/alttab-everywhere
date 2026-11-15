// Draws the app icon at every size Windows asks for and writes it as a multi-size .ico.
// Each size is drawn on its own pixel grid (not scaled down from a big one) so 16 px stays crisp.
//
//   dotnet run --project tools/IconGen [output.ico] [preview.png]
//
// The design: a blue rounded square holding a 2 x 2 grid of window tiles, the first one selected.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

string output = args.Length > 0 ? args[0] : Path.Combine("src", "AltTabEverywhere", "Assets", "app.ico");
int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

var images = sizes.Select(Draw).ToList();
WriteIco(output, images);
Console.WriteLine($"wrote {output} ({string.Join(", ", sizes)} px)");

if (args.Length > 1)
{
    // A preview strip, each size shown at 4x with nearest-neighbour scaling, to check small sizes.
    int width = sizes.Where(s => s <= 64).Sum(s => s * 4 + 16);
    using var strip = new Bitmap(width, 64 * 4 + 16);
    using var g = Graphics.FromImage(strip);
    g.Clear(Color.FromArgb(0x20, 0x20, 0x20));
    g.InterpolationMode = InterpolationMode.NearestNeighbor;
    g.PixelOffsetMode = PixelOffsetMode.Half;
    int x = 8;
    foreach (var image in images.Where(i => i.Width <= 64))
    {
        g.DrawImage(image, x, 8, image.Width * 4, image.Height * 4);
        x += image.Width * 4 + 16;
    }
    strip.Save(args[1], ImageFormat.Png);
}

static Bitmap Draw(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    float s = size;
    // Background: a rounded square filling the canvas (1 px inset at small sizes for the edge).
    float inset = size <= 24 ? 0.5f : s * 0.03f;
    var bounds = new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset);
    using (var path = RoundedRect(bounds, s * 0.22f))
    using (var fill = new LinearGradientBrush(bounds, Color.FromArgb(0x3A, 0xA0, 0xFF), Color.FromArgb(0x00, 0x5F, 0xC8), 60f))
        g.FillPath(fill, path);

    // Tiles: snapped to whole pixels so their edges stay sharp at 16-32 px.
    float margin = MathF.Round(s * 0.22f);
    float gap = MathF.Max(1, MathF.Round(s * 0.09f));
    float tile = MathF.Floor((s - 2 * margin - gap) / 2);
    float start = MathF.Round((s - (2 * tile + gap)) / 2);
    float radius = MathF.Max(0.5f, s * 0.05f);
    for (int row = 0; row < 2; row++)
    for (int col = 0; col < 2; col++)
    {
        var rect = new RectangleF(start + col * (tile + gap), start + row * (tile + gap), tile, tile);
        bool selected = row == 0 && col == 0;
        using var path = RoundedRect(rect, radius);
        using var brush = new SolidBrush(selected ? Color.White : Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        g.FillPath(brush, path);
    }
    return bmp;
}

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
    var path = new GraphicsPath();
    path.AddArc(r.X, r.Y, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

// .ico: 256 px as PNG, smaller sizes as 32-bit DIBs (every Windows API and System.Drawing.Icon reads those).
static void WriteIco(string path, List<Bitmap> images)
{
    var blobs = images.Select(i => i.Width >= 256 ? Png(i) : Dib(i)).ToList();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    using var w = new BinaryWriter(File.Create(path));
    w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
    int offset = 6 + 16 * images.Count;
    for (int i = 0; i < images.Count; i++)
    {
        int size = images[i].Width;
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32);
        w.Write(blobs[i].Length); w.Write(offset);
        offset += blobs[i].Length;
    }
    foreach (var blob in blobs) w.Write(blob);
}

static byte[] Png(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static byte[] Dib(Bitmap bmp)
{
    int size = bmp.Width;
    int maskStride = (size + 31) / 32 * 4;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(size); w.Write(size * 2); // height covers colour + mask
    w.Write((short)1); w.Write((short)32); w.Write(0);
    w.Write(size * size * 4 + maskStride * size);
    w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = size - 1; y >= 0; y--) // bottom-up BGRA
    for (int x = 0; x < size; x++)
    {
        var c = bmp.GetPixel(x, y);
        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
    }
    w.Write(new byte[maskStride * size]); // AND mask unused: alpha decides
    return ms.ToArray();
}
