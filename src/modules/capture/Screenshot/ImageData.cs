using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MultiTool.Modules.Capture.Screenshot;

// Shared with the Photos module, which compiles this file, ImageEditor.cs and ClipboardImage.cs into its own
// assembly (see its .csproj): the image editor and the clipboard writer exist once in source, twice in memory,
// each in its own module's load context.

/// <summary>A captured image: BGRA, 4 bytes per pixel, no row padding.</summary>
public sealed record CapturedImage(byte[] Pixels, int Width, int Height)
{
    /// <summary>Pixels carry premultiplied alpha (a photo with transparency); captures are opaque.</summary>
    public bool Premultiplied { get; init; }

    /// <summary>A crop in this image's pixels, clamped to the edge.</summary>
    public CapturedImage? Crop(int x, int y, int width, int height)
    {
        var left = Math.Clamp(x, 0, Width);
        var top = Math.Clamp(y, 0, Height);
        var right = Math.Clamp(x + width, 0, Width);
        var bottom = Math.Clamp(y + height, 0, Height);

        var w = right - left;
        var h = bottom - top;
        if (w <= 0 || h <= 0) return null;

        var result = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(Pixels, ((top + row) * Width + left) * 4, result, row * w * 4, w * 4);

        return new CapturedImage(result, w, h) { Premultiplied = Premultiplied };
    }
}

/// <summary>Conversions between the raw capture and Avalonia.</summary>
internal static class Images
{
    public static WriteableBitmap ToBitmap(CapturedImage image)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, image.Premultiplied ? AlphaFormat.Premul : AlphaFormat.Opaque);

        using var buffer = bitmap.Lock();
        var row = image.Width * 4;

        for (var y = 0; y < image.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(image.Pixels, y * row, buffer.Address + y * buffer.RowBytes, row);

        return bitmap;
    }

    /// <summary>Reads an image as BGRA, for OCR on a dropped file.</summary>
    public static CapturedImage FromBitmap(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];

        unsafe
        {
            fixed (byte* target = pixels)
            {
                bitmap.CopyPixels(new PixelRect(size), (nint)target, pixels.Length, stride);
            }
        }

        // CopyPixels returns the image's format. Common files come as BGRA; RGBA is swapped so the
        // recognizer doesn't swap colors.
        if (bitmap.Format is { } format && format == PixelFormat.Rgba8888)
        {
            for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }

        return new CapturedImage(pixels, size.Width, size.Height);
    }
}
