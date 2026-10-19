using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace MultiTool.Modules.Photos;

/// <summary>What a photo says about itself: when, with what, where.</summary>
public sealed record PhotoInfo(DateTime? Taken, string? Camera, string? Exposure, bool HasLocation);

/// <summary>A decoded picture, upright, as Skia pixels.</summary>
/// <param name="Decoder">Which way it was read: "Skia", "Windows codec" or "ffmpeg".</param>
public sealed record DecodedPhoto(SKBitmap Bitmap, string Decoder, PhotoInfo Info);

/// <summary>Reads every common picture format, three ways.</summary>
/// <remarks>
/// <para>
/// <b>Skia first</b> (JPEG, PNG, GIF, BMP, WebP, ICO): fast and already in the process. <b>Then Windows' own
/// codecs</b> through WIC: TIFF, JPEG XR and DDS always; HEIC, AVIF and camera RAW (CR2, CR3, NEF, ARW, DNG
/// and more) when the free extensions from the Microsoft Store are installed, as they are for the Photos
/// app. <b>Then ffmpeg</b>, if present: JPEG XL, TGA, QOI, EXR.
/// </para>
/// <para>
/// <b>Upright on arrival.</b> Phones store portrait photos sideways with an orientation tag; Skia reports it,
/// WIC doesn't apply it, so both are turned here. Editing and saving work on the upright picture.
/// </para>
/// <para>
/// <b>Info through WIC</b> for every format it can open, with the photo property names
/// (<c>System.Photo.DateTaken</c>), which map to the right place in JPEG, TIFF, HEIC and RAW alike.
/// </para>
/// </remarks>
internal static unsafe partial class PhotoDecoder
{
    /// <summary>Extensions offered in the open dialog and when browsing a folder.</summary>
    public static readonly string[] Extensions =
    [
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "webp", "ico",
        "tif", "tiff", "heic", "heif", "hif", "avif", "jxr", "wdp", "hdp", "dds",
        "dng", "cr2", "cr3", "nef", "nrw", "arw", "srf", "sr2", "orf", "rw2", "raf", "pef", "srw", "x3f", "3fr", "erf", "kdc", "mrw", "rwl", "iiq",
        "jxl", "tga", "qoi", "exr",
    ];

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    /// <returns>The picture, or <c>null</c> with the reason.</returns>
    public static (DecodedPhoto? Photo, string? Problem) Decode(string path, string? ffmpeg)
    {
        var info = ReadInfo(path, out var orientation);

        if (DecodeWithSkia(path) is { } skia) return (new DecodedPhoto(skia, "Skia", info), null);

        if (DecodeWithWic(path) is { } wic)
            return (new DecodedPhoto(Orient(wic, (SKEncodedOrigin)Math.Clamp((int)orientation, 1, 8)), "Windows codec", info), null);

        if (ffmpeg is not null && DecodeWithFfmpeg(path, ffmpeg) is { } ff) return (new DecodedPhoto(ff, "ffmpeg", info), null);

        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return (null, extension switch
        {
            "HEIC" or "HEIF" or "HIF" => "HEIC needs the free \"HEIF Image Extensions\" from the Microsoft Store.",
            "AVIF" => "AVIF needs the free \"AV1 Video Extension\" from the Microsoft Store, or ffmpeg.",
            "JXL" => "JPEG XL needs ffmpeg (winget install Gyan.FFmpeg).",
            _ when Extensions.Skip(20).Take(20).Contains(extension.ToLowerInvariant()) =>
                $"{extension} needs the free \"Raw Image Extension\" from the Microsoft Store.",
            _ => $"{Path.GetFileName(path)} could not be read.",
        });
    }

    // --------------------------------------------------------------- Skia

    private static SKBitmap? DecodeWithSkia(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is null) return null;

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                bitmap.Dispose();
                return null;
            }

            return Orient(bitmap, codec.EncodedOrigin);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Turns a bitmap by an EXIF orientation. Disposes the input if a new one is made.</summary>
    /// <remarks>The same eight cases serve rotating and flipping while editing.</remarks>
    public static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or 0) return source;

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;

        var result = new SKBitmap(new SKImageInfo(width, height, source.ColorType, source.AlphaType));
        using (var canvas = new SKCanvas(result))
        {
            switch (origin)
            {
                case SKEncodedOrigin.TopRight:      // mirrored
                    canvas.Translate(width, 0);
                    canvas.Scale(-1, 1);
                    break;
                case SKEncodedOrigin.BottomRight:   // 180°
                    canvas.Translate(width, height);
                    canvas.RotateDegrees(180);
                    break;
                case SKEncodedOrigin.BottomLeft:    // upside down mirrored
                    canvas.Translate(0, height);
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.LeftTop:       // transposed
                    canvas.Scale(-1, 1);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.RightTop:      // 90° clockwise
                    canvas.Translate(width, 0);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.RightBottom:   // transverse
                    canvas.Translate(width, height);
                    canvas.RotateDegrees(90);
                    canvas.Scale(-1, 1);
                    break;
                case SKEncodedOrigin.LeftBottom:    // 90° counter-clockwise
                    canvas.Translate(0, height);
                    canvas.RotateDegrees(270);
                    break;
            }

            canvas.DrawBitmap(source, 0, 0);
        }

        source.Dispose();
        return result;
    }

    // --------------------------------------------------------------- ffmpeg

    private static SKBitmap? DecodeWithFfmpeg(string path, string ffmpeg)
    {
        try
        {
            var info = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-v", "error", "-i", path, "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "-" })
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return null;

            using var png = new MemoryStream();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.BaseStream.CopyTo(png);
            process.WaitForExit(30_000);
            _ = error.Result;

            return png.Length == 0 ? null : SKBitmap.Decode(png.ToArray())?.Copy(SKColorType.Bgra8888);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // --------------------------------------------------------------- WIC

    private static readonly Guid ClsidImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid IidImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid PixelFormatPbgra = new("6fddc324-4e03-4bfe-b185-3d77768dc910");

    private const uint GenericRead = 0x80000000;

    private static nint CreateFactory()
    {
        // Pool threads may come without COM; MTA suits WIC, and a second call is harmless.
        _ = CoInitializeEx(0, 0);

        var clsid = ClsidImagingFactory;
        var iid = IidImagingFactory;
        return CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, out var factory) == 0 ? factory : 0;
    }

    private static nint OpenFrame(nint factory, string path)
    {
        nint decoder = 0;
        fixed (char* name = path)
        {
            // IWICImagingFactory::CreateDecoderFromFilename
            if (((delegate* unmanaged[Stdcall]<nint, char*, Guid*, uint, int, nint*, int>)Vtable(factory)[3])(factory, name, null, GenericRead, 0, &decoder) != 0) return 0;
        }

        try
        {
            nint frame = 0;
            return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtable(decoder)[13])(decoder, 0, &frame) == 0 ? frame : 0;   // IWICBitmapDecoder::GetFrame
        }
        finally
        {
            Release(decoder);
        }
    }

    private static SKBitmap? DecodeWithWic(string path)
    {
        var factory = CreateFactory();
        if (factory == 0) return null;

        nint frame = 0, converter = 0;
        try
        {
            frame = OpenFrame(factory, path);
            if (frame == 0) return null;

            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtable(factory)[10])(factory, &converter) != 0) return null;    // CreateFormatConverter
            var format = PixelFormatPbgra;
            if (((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int, nint, double, int, int>)Vtable(converter)[8])(converter, frame, &format, 0, 0, 0, 0) != 0) return null; // Initialize

            uint width, height;
            if (((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Vtable(converter)[3])(converter, &width, &height) != 0 || width == 0 || height == 0) return null;  // GetSize

            var bitmap = new SKBitmap(new SKImageInfo((int)width, (int)height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var stride = (uint)bitmap.RowBytes;
            if (((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, nint, int>)Vtable(converter)[7])(converter, 0, stride, stride * height, bitmap.GetPixels()) != 0)    // CopyPixels
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Release(converter);
            Release(frame);
            Release(factory);
        }
    }

    /// <summary>Date, camera, exposure and whether there is a location, if Windows can read them.</summary>
    public static PhotoInfo ReadInfo(string path, out ushort orientation)
    {
        orientation = 1;
        var factory = CreateFactory();
        if (factory == 0) return new PhotoInfo(null, null, null, false);

        nint frame = 0, reader = 0;
        try
        {
            frame = OpenFrame(factory, path);
            if (frame == 0 || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtable(frame)[8])(frame, &reader) != 0 || reader == 0)   // GetMetadataQueryReader
                return new PhotoInfo(null, null, null, false);

            if (Query(reader, "System.Photo.Orientation") is ushort o) orientation = o;
            else if (Query(reader, "System.Photo.Orientation") is uint ou) orientation = (ushort)ou;

            DateTime? taken = Query(reader, "System.Photo.DateTaken") is long filetime ? DateTime.FromFileTimeUtc(filetime).ToLocalTime() : null;
            if (taken is null && Query(reader, "/app1/ifd/exif/{ushort=36867}") is string exifDate
                && DateTime.TryParseExact(exifDate.Trim('\0'), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                taken = parsed;

            var make = (Query(reader, "System.Photo.CameraManufacturer") as string)?.Trim('\0', ' ');
            var model = (Query(reader, "System.Photo.CameraModel") as string)?.Trim('\0', ' ');
            var camera = model is { Length: > 0 }
                ? make is { Length: > 0 } && !model.StartsWith(make.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? $"{make} {model}" : model
                : make;

            var parts = new List<string>();
            if (Number(Query(reader, "System.Photo.FNumber")) is { } f && f > 0) parts.Add($"f/{f:0.#}");
            if (Number(Query(reader, "System.Photo.ExposureTime")) is { } t && t > 0)
                parts.Add(t >= 1 ? $"{t:0.#} s" : $"1/{Math.Round(1 / t):0} s");
            if (Number(Query(reader, "System.Photo.ISOSpeed")) is { } iso && iso > 0) parts.Add($"ISO {iso:0}");
            if (Number(Query(reader, "System.Photo.FocalLength")) is { } focal && focal > 0) parts.Add($"{focal:0} mm");

            var hasLocation = Query(reader, "System.GPS.Latitude") is not null;

            return new PhotoInfo(taken, camera, parts.Count > 0 ? string.Join(" · ", parts) : null, hasLocation);
        }
        catch (Exception)
        {
            return new PhotoInfo(null, null, null, false);
        }
        finally
        {
            Release(reader);
            Release(frame);
            Release(factory);
        }
    }

    private static double? Number(object? value) => value switch
    {
        double d => d,
        float f => f,
        ushort u => u,
        uint u => u,
        short s => s,
        int i => i,
        ulong rational => (rational >> 32) is var den and > 0 ? (rational & 0xFFFFFFFF) / (double)den : null,
        _ => null,
    };

    /// <summary>One metadata value as a .NET value; <c>null</c> if missing. A block (GPS) comes back as <c>true</c>.</summary>
    private static object? Query(nint reader, string name)
    {
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();

        fixed (char* query = name)
        {
            if (((delegate* unmanaged[Stdcall]<nint, char*, byte*, int>)Vtable(reader)[5])(reader, query, variant) != 0) return null;    // GetMetadataByName
        }

        try
        {
            var data = variant + 8;
            return *(ushort*)variant switch
            {
                2 => *(short*)data,                                     // VT_I2
                3 => *(int*)data,                                       // VT_I4
                4 => *(float*)data,                                     // VT_R4
                5 => *(double*)data,                                    // VT_R8
                17 => (ushort)*data,                                    // VT_UI1
                18 => *(ushort*)data,                                   // VT_UI2
                19 => *(uint*)data,                                     // VT_UI4
                21 => *(ulong*)data,                                    // VT_UI8 (EXIF rational)
                30 => Marshal.PtrToStringAnsi(*(nint*)data),            // VT_LPSTR
                31 => Marshal.PtrToStringUni(*(nint*)data),             // VT_LPWSTR
                64 => *(long*)data,                                     // VT_FILETIME
                0 or 1 => null,
                _ => true,                                              // a block, an array: present
            };
        }
        finally
        {
            PropVariantClear(variant);
        }
    }

    // --------------------------------------------------------------- COM plumbing

    private static nint* Vtable(nint instance) => *(nint**)instance;

    private static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vtable(instance)[2])(instance);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint concurrency);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(byte* variant);
}
