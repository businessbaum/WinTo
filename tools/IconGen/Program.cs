// Draws the app icon: a switch that is on, white on a near-black plate.
//
//   dotnet run --project tools\IconGen -- [--preview <png>]
//
// Writes src\MultiTool.Host\Assets\app.ico (16 to 256 px: EXE, installer, tray and windows) and prints the mark as
// path data in 24 x 24 units: LogoShape in MultiTool.UI\Theme\Icons.axaml, so the logo in the app is the same
// shape. Chosen in 0.13.0 from three designs (Monogram, Switch, Fan); the plate went dark for contrast.
//
// Images are stored as classic bitmaps, not PNG. PNG in an .ico is smaller, but GDI+ can't read such entries
// (System.Drawing.Icon.ToBitmap() throws), and tools that touch the file use the same library.

using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

var repo = FindRepo();
var assets = Path.Combine(repo, "src", "MultiTool.Host", "Assets");
var preview = ValueAfter("--preview");

// Same stops as LogoGradientBrush in Palette.axaml.
SKColor[] plate = [Hex("#3A3A44"), Hex("#1C1C22"), Hex("#0A0A0D")];
var mark = Center(Switch(), 20f);

WriteIco(Path.Combine(assets, "app.ico"), plate, mark, [16, 20, 24, 32, 40, 48, 64, 128, 256]);
Console.WriteLine($"switch: {PathData(mark)}");

if (preview is not null)
{
    WritePreview(preview, plate, mark);
    Console.WriteLine($"preview: {preview}");
}

return;

// ---------------------------------------------------------------------------------------------------------
// The mark, in 24 x 24 units: the track filled, the knob on the right showing the plate.

static SKPath Switch()
{
    var track = new SKPath();
    track.AddRoundRect(new SKRect(1, 5.5f, 23, 18.5f), 6.5f, 6.5f);

    var knob = new SKPath();
    knob.AddCircle(16.5f, 12, 4.3f);

    return track.Op(knob, SKPathOp.Difference)!;
}

// ---------------------------------------------------------------------------------------------------------
// Drawing

static SKBitmap Render(SKColor[] plateColors, SKPath shape, int size)
{
    var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);

    // Half-pixel steps keep the plate edge crisp at small sizes; 16 px is nearly edge to edge.
    var pad = MathF.Max(0.5f, MathF.Round(size * 0.04f * 2) / 2);
    var side = size - 2 * pad;
    var plate = new SKRoundRect(new SKRect(pad, pad, pad + side, pad + side), side * 0.225f);

    using (var paint = new SKPaint { IsAntialias = true })
    {
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(pad, pad), new SKPoint(pad + side, pad + side), plateColors, [0, 0.5f, 1], SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(plate, paint);

        // Light from above, faint.
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, pad), new SKPoint(0, pad + side * 0.65f),
            [SKColors.White.WithAlpha(30), SKColors.White.WithAlpha(0)], [0, 1], SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(plate, paint);
    }

    // A light rim: a near-black plate would vanish on a dark taskbar and in the app's dark header.
    using (var rim = new SKPaint
           {
               IsAntialias = true,
               Style = SKPaintStyle.Stroke,
               StrokeWidth = MathF.Max(1, size / 64f),
               Color = SKColors.White.WithAlpha(size <= 24 ? (byte)70 : (byte)44),
           })
    {
        var inset = rim.StrokeWidth / 2;
        var edge = new SKRoundRect(new SKRect(pad + inset, pad + inset, pad + side - inset, pad + side - inset),
            side * 0.225f - inset);
        canvas.DrawRoundRect(edge, rim);
    }

    // Small sizes get a larger mark: margins that read at 256 px swallow it at 16.
    var inner = side * (size <= 24 ? 0.80f : size <= 40 ? 0.76f : 0.72f);
    var scale = inner / 24f;
    var origin = pad + (side - inner) / 2;
    using var placed = new SKPath(shape);
    placed.Transform(SKMatrix.CreateScaleTranslation(scale, scale, origin, origin));

    using (var white = new SKPaint { IsAntialias = true, Color = SKColors.White })
        canvas.DrawPath(placed, white);

    var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
    surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
    return bitmap;
}

static void WriteIco(string path, SKColor[] plate, SKPath shape, int[] sizes)
{
    var images = sizes.Select(size =>
    {
        using var bitmap = Render(plate, shape, size);
        return (Size: size, Bytes: IcoImage(bitmap));
    }).ToList();

    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)images.Count);

    var offset = 6 + 16 * images.Count;
    foreach (var (size, bytes) in images)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(bytes.Length);
        writer.Write(offset);
        offset += bytes.Length;
    }

    foreach (var (_, bytes) in images) writer.Write(bytes);
}

// BITMAPINFOHEADER, pixels bottom-up, then a 1-bit mask. biHeight is double: image and mask together. The mask
// must be present although 32-bit alpha makes Windows ignore it; without it the entry is too short.
static byte[] IcoImage(SKBitmap bitmap)
{
    int w = bitmap.Width, h = bitmap.Height;
    var pixels = bitmap.Bytes;
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);

    writer.Write(40u);
    writer.Write(w);
    writer.Write(h * 2);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(0u);
    writer.Write((uint)(w * h * 4));
    writer.Write(0);
    writer.Write(0);
    writer.Write(0u);
    writer.Write(0u);

    for (var y = h - 1; y >= 0; y--) writer.Write(pixels, y * bitmap.RowBytes, w * 4);

    var maskStride = ((w + 7) / 8 + 3) & ~3;
    writer.Write(new byte[maskStride * h]);
    writer.Flush();
    return stream.ToArray();
}

// Large, at the sizes Windows uses, on a dark and a light taskbar, and 16 px enlarged.
static void WritePreview(string path, SKColor[] plate, SKPath shape)
{
    const int width = 480, height = 760;
    using var surface = SKSurface.Create(new SKImageInfo(width, height));
    var canvas = surface.Canvas;
    canvas.Clear(new SKColor(0xF6, 0xF6, 0xF8));

    using var label = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 15);
    using var muted = new SKPaint { IsAntialias = true, Color = new SKColor(0x70, 0x70, 0x7A) };
    const float x = 40;

    using (var big = Render(plate, shape, 256)) canvas.DrawBitmap(big, x + 52, 40);

    var sx = x;
    foreach (var size in new[] { 64, 48, 32, 24, 16 })
    {
        using var small = Render(plate, shape, size);
        canvas.DrawBitmap(small, sx, 330 + (64 - size));
        canvas.DrawText($"{size}", sx, 420, label, muted);
        sx += size + 22;
    }

    foreach (var (y, background, text) in new[]
             {
                 (450f, new SKColor(0x1F, 0x1F, 0x24), new SKColor(0xFF, 0xFF, 0xFF, 0xC0)),
                 (530f, new SKColor(0xEE, 0xEE, 0xF2), new SKColor(0x20, 0x20, 0x28, 0xC0)),
             })
    {
        using var bar = new SKPaint { Color = background };
        canvas.DrawRoundRect(new SKRect(x, y, x + 360, y + 56), 10, 10, bar);
        using (var icon = Render(plate, shape, 24)) canvas.DrawBitmap(icon, x + 20, y + 16);
        using (var icon = Render(plate, shape, 16)) canvas.DrawBitmap(icon, x + 290, y + 20);
        using var clock = new SKPaint { IsAntialias = true, Color = text };
        canvas.DrawText("12:04", x + 314, y + 33, label, clock);
    }

    using (var tray = Render(plate, shape, 16))
    {
        using var image = SKImage.FromBitmap(tray);
        canvas.DrawImage(image, new SKRect(x, 620, x + 96, 716), new SKSamplingOptions(SKFilterMode.Nearest));
    }
    canvas.DrawText("16 px, enlarged", x + 112, 674, label, muted);

    using var png = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
    using var file = File.Create(path);
    png.SaveTo(file);
}

// ---------------------------------------------------------------------------------------------------------
// Geometry helpers

// Scales the mark so its larger side is `extent` units and centers it in 24 x 24.
static SKPath Center(SKPath shape, float extent)
{
    var bounds = shape.TightBounds;
    var scale = extent / MathF.Max(bounds.Width, bounds.Height);
    var result = new SKPath(shape);
    result.Transform(SKMatrix.CreateTranslation(-bounds.MidX, -bounds.MidY)
        .PostConcat(SKMatrix.CreateScale(scale, scale))
        .PostConcat(SKMatrix.CreateTranslation(12, 12)));
    return result;
}

// Path data for Avalonia: two decimals are finer than any pixel at 24 units.
static string PathData(SKPath path) =>
    Regex.Replace(path.ToSvgPathData(), @"-?\d+\.\d+(e-?\d+)?",
        m => Math.Round(double.Parse(m.Value, CultureInfo.InvariantCulture), 2).ToString(CultureInfo.InvariantCulture));

static SKColor Hex(string value) => SKColor.Parse(value);

static string FindRepo()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MultiTool.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? throw new DirectoryNotFoundException("MultiTool.slnx not found above the tool.");
}

static string? ValueAfter(string name)
{
    var args = Environment.GetCommandLineArgs();
    var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
