using Avalonia;
using Avalonia.Media.Imaging;
using MultiTool.Modules.Capture.Screenshot;
using SkiaSharp;

namespace MultiTool.Modules.Photos;

/// <summary>One change to the picture's pixels, independent of resolution.</summary>
/// <remarks>
/// Edits are replayed: on the small preview while editing, on the full picture when saving. So every step
/// stores what it does relative to the picture (a crop as fractions, a markup with the size it was drawn
/// on), never pixels.
/// </remarks>
internal abstract record PhotoStep
{
    /// <summary>Applies the step. Disposes the input if a new bitmap comes back.</summary>
    public abstract SKBitmap Apply(SKBitmap input);

    /// <summary>Markup draws through Avalonia and must run on the UI thread.</summary>
    public virtual bool NeedsUiThread => false;
}

internal sealed record RotateStep(int Degrees) : PhotoStep
{
    public override SKBitmap Apply(SKBitmap input) => PhotoDecoder.Orient(input, Degrees switch
    {
        90 => SKEncodedOrigin.RightTop,
        180 => SKEncodedOrigin.BottomRight,
        270 => SKEncodedOrigin.LeftBottom,
        _ => SKEncodedOrigin.TopLeft,
    });
}

internal sealed record FlipStep(bool Horizontal) : PhotoStep
{
    public override SKBitmap Apply(SKBitmap input) =>
        PhotoDecoder.Orient(input, Horizontal ? SKEncodedOrigin.TopRight : SKEncodedOrigin.BottomLeft);
}

/// <param name="Area">Left, top, right, bottom as fractions of the picture.</param>
internal sealed record CropStep(SKRect Area) : PhotoStep
{
    public override SKBitmap Apply(SKBitmap input)
    {
        var rect = SKRectI.Round(new SKRect(Area.Left * input.Width, Area.Top * input.Height, Area.Right * input.Width, Area.Bottom * input.Height));
        rect.Intersect(new SKRectI(0, 0, input.Width, input.Height));
        if (rect.Width < 1 || rect.Height < 1) return input;

        var result = new SKBitmap(new SKImageInfo(rect.Width, rect.Height, input.ColorType, input.AlphaType));
        if (!input.ExtractSubset(result, rect))
        {
            result.Dispose();
            return input;
        }

        // ExtractSubset shares pixels with the input; a copy makes it independent.
        var copy = result.Copy();
        result.Dispose();
        input.Dispose();
        return copy;
    }
}

/// <summary>Drawings from the markup tools, replayed at any size.</summary>
/// <param name="Steps">As drawn, in pixels of a picture <paramref name="DrawnWidth"/> wide.</param>
internal sealed record MarkupStep(IReadOnlyList<EditStep> Steps, int DrawnWidth) : PhotoStep
{
    public override bool NeedsUiThread => true;

    public override SKBitmap Apply(SKBitmap input)
    {
        var scale = input.Width / (double)Math.Max(1, DrawnWidth);
        var image = ToCaptured(input);

        using var editor = new ImageEditor(image);
        foreach (var step in Steps)
        {
            var points = step.Points.Select(p => new Point(p.X * scale, p.Y * scale)).ToList();
            editor.Add(step with { Points = points });
        }

        var png = editor.RenderPng();
        var result = SKBitmap.Decode(png)?.Copy(SKColorType.Bgra8888);
        if (result is null) return input;

        input.Dispose();
        return result;
    }

    public static CapturedImage ToCaptured(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.Width * bitmap.Height * 4];
        using var copy = bitmap.ColorType == SKColorType.Bgra8888 && bitmap.AlphaType == SKAlphaType.Premul
            ? null
            : bitmap.Copy(SKColorType.Bgra8888);
        var source = copy ?? bitmap;

        var row = source.Width * 4;
        var pixels = source.GetPixelSpan();
        for (var y = 0; y < source.Height; y++)
            pixels.Slice(y * source.RowBytes, row).CopyTo(bytes.AsSpan(y * row, row));

        return new CapturedImage(bytes, source.Width, source.Height) { Premultiplied = source.AlphaType != SKAlphaType.Opaque };
    }
}

/// <summary>A preset look on top of the adjustments.</summary>
public enum PhotoFilter
{
    None,
    Vivid,
    Warm,
    Cool,
    BlackWhite,
    Noir,
    Sepia,
    Fade,
}

/// <summary>Light and color: sliders from -100 to 100 plus a filter. Applied last, as one color matrix.</summary>
public sealed record PhotoLook(int Brightness, int Contrast, int Saturation, int Warmth, PhotoFilter Filter)
{
    public static readonly PhotoLook Neutral = new(0, 0, 0, 0, PhotoFilter.None);

    public bool IsNeutral => this == Neutral;

    /// <summary>The 4×5 matrix Skia wants, with the offsets in the 0..1 range.</summary>
    public float[] Matrix()
    {
        var m = Identity();

        m = Multiply(Saturate(Saturation / 100.0 + 1), m);
        m = Multiply(ContrastMatrix(1 + Contrast / 100.0 * 0.6), m);
        m = Multiply(Offset(Brightness / 100.0 * 0.35), m);
        m = Multiply(WarmthMatrix(Warmth / 100.0), m);

        m = Filter switch
        {
            PhotoFilter.Vivid => Multiply(ContrastMatrix(1.08), Multiply(Saturate(1.35), m)),
            PhotoFilter.Warm => Multiply(WarmthMatrix(0.6), Multiply(Saturate(1.1), m)),
            PhotoFilter.Cool => Multiply(WarmthMatrix(-0.6), m),
            PhotoFilter.BlackWhite => Multiply(Saturate(0), m),
            PhotoFilter.Noir => Multiply(ContrastMatrix(1.35), Multiply(Saturate(0), m)),
            PhotoFilter.Sepia => Multiply(SepiaMatrix(), m),
            PhotoFilter.Fade => Multiply(Offset(0.06), Multiply(ContrastMatrix(0.82), Multiply(Saturate(0.75), m))),
            _ => m,
        };

        var result = new float[20];
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 5; column++)
                result[row * 5 + column] = (float)m[row, column];

        return result;
    }

    private static double[,] Identity()
    {
        var m = new double[5, 5];
        for (var i = 0; i < 5; i++) m[i, i] = 1;
        return m;
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var m = new double[5, 5];
        for (var r = 0; r < 5; r++)
            for (var c = 0; c < 5; c++)
                for (var k = 0; k < 5; k++)
                    m[r, c] += a[r, k] * b[k, c];
        return m;
    }

    private static double[,] Saturate(double s)
    {
        const double lr = 0.2126, lg = 0.7152, lb = 0.0722;
        var m = Identity();
        m[0, 0] = lr * (1 - s) + s; m[0, 1] = lg * (1 - s); m[0, 2] = lb * (1 - s);
        m[1, 0] = lr * (1 - s); m[1, 1] = lg * (1 - s) + s; m[1, 2] = lb * (1 - s);
        m[2, 0] = lr * (1 - s); m[2, 1] = lg * (1 - s); m[2, 2] = lb * (1 - s) + s;
        return m;
    }

    private static double[,] ContrastMatrix(double k)
    {
        var m = Identity();
        for (var i = 0; i < 3; i++)
        {
            m[i, i] = k;
            m[i, 4] = 0.5 * (1 - k);
        }
        return m;
    }

    private static double[,] Offset(double value)
    {
        var m = Identity();
        for (var i = 0; i < 3; i++) m[i, 4] = value;
        return m;
    }

    private static double[,] WarmthMatrix(double w)
    {
        var m = Identity();
        m[0, 0] = 1 + 0.2 * w;
        m[1, 1] = 1 + 0.04 * w;
        m[2, 2] = 1 - 0.2 * w;
        return m;
    }

    private static double[,] SepiaMatrix()
    {
        var m = Identity();
        m[0, 0] = 0.393; m[0, 1] = 0.769; m[0, 2] = 0.189;
        m[1, 0] = 0.349; m[1, 1] = 0.686; m[1, 2] = 0.168;
        m[2, 0] = 0.272; m[2, 1] = 0.534; m[2, 2] = 0.131;
        return m;
    }

    /// <summary>Draws the bitmap through the look into a new one. Disposes the input.</summary>
    public SKBitmap Apply(SKBitmap input)
    {
        if (IsNeutral) return input;

        var result = new SKBitmap(input.Info);
        using (var canvas = new SKCanvas(result))
        using (var paint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(Matrix()) })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(input, 0, 0, paint);
        }

        input.Dispose();
        return result;
    }
}

/// <summary>Conversions between Skia bitmaps and what Avalonia shows.</summary>
internal static class PhotoBitmaps
{
    public static WriteableBitmap ToAvalonia(SKBitmap bitmap)
    {
        using var copy = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        var source = copy ?? bitmap;

        var result = new WriteableBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            source.AlphaType == SKAlphaType.Opaque ? Avalonia.Platform.AlphaFormat.Opaque : Avalonia.Platform.AlphaFormat.Premul);

        using var buffer = result.Lock();
        var row = source.Width * 4;
        var pixels = source.GetPixels();

        unsafe
        {
            for (var y = 0; y < source.Height; y++)
                Buffer.MemoryCopy((byte*)pixels + y * source.RowBytes, (byte*)buffer.Address + y * buffer.RowBytes, buffer.RowBytes, row);
        }

        return result;
    }

    /// <summary>A copy no larger than <paramref name="longSide"/> on its long side, for quick previews.</summary>
    public static SKBitmap Shrink(SKBitmap bitmap, int longSide)
    {
        var scale = Math.Min(1, longSide / (double)Math.Max(bitmap.Width, bitmap.Height));
        if (scale >= 1) return bitmap.Copy();

        var info = new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)),
            SKColorType.Bgra8888, bitmap.AlphaType);
        return bitmap.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? bitmap.Copy();
    }

    public static SKBitmap Resize(SKBitmap bitmap, int width, int height)
    {
        if (width == bitmap.Width && height == bitmap.Height) return bitmap;

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, bitmap.AlphaType);
        var result = bitmap.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (result is null) return bitmap;

        bitmap.Dispose();
        return result;
    }
}
