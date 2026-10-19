using System.Runtime.InteropServices;

namespace MultiTool.Modules.Capture.Screenshot;

/// <summary>What text recognition found.</summary>
/// <param name="Text">Line by line, separated by newlines. Empty if nothing was recognized.</param>
/// <param name="Language">The recognition language as Windows names it, e.g. "English (United States)".</param>
/// <param name="Problem">Set if recognition wasn't possible at all.</param>
public sealed record OcrOutcome(string Text, string? Language, string? Problem)
{
    public static OcrOutcome Failed(string problem) => new("", null, problem);
}

/// <summary>Text recognition via <c>Windows.Media.Ocr</c>, the OCR built into Windows.</summary>
/// <remarks>
/// <para>
/// <b>By hand over COM, not the Windows SDK projection:</b> targeting <c>net10.0-windows10.0.19041.0</c>
/// pulls <c>Microsoft.Windows.SDK.NET.dll</c>, about 25 MB of projection for five interfaces.
/// </para>
/// <para>
/// <b>Slot numbers aren't guessed.</b> Every WinRT interface starts with the six <c>IUnknown</c> and
/// <c>IInspectable</c> methods; its own follow from slot 6 in metadata order. IIDs and order were read
/// from the Windows metadata and checked against real recognition with rendered text.
/// </para>
/// <para>
/// <b>On its own thread:</b> WinRT wants an initialized apartment; a pool thread may already have
/// another, and waiting on the STA UI thread would freeze it. A fresh thread with
/// <c>RoInitialize(MTA)</c> avoids both.
/// </para>
/// </remarks>
internal static unsafe partial class WindowsOcr
{
    private static readonly Guid IidOcrEngineStatics = new("5bffa85a-3384-3540-9940-699120d428a8");
    private static readonly Guid IidSoftwareBitmapFactory = new("c99feb69-2d62-4d47-a6b3-4fdb6a07fdf8");
    private static readonly Guid IidMemoryBuffer = new("fbc4dd2a-245b-11e4-af98-689423260cf8");
    private static readonly Guid IidMemoryBufferByteAccess = new("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");
    private static readonly Guid IidClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    /// <summary><c>BitmapPixelFormat.Bgra8</c>.</summary>
    private const int Bgra8 = 87;

    /// <summary><c>BitmapAlphaMode.Ignore</c>; a screenshot has no transparency.</summary>
    private const int AlphaIgnore = 2;

    /// <summary><c>BitmapBufferAccessMode.Write</c>.</summary>
    private const int AccessWrite = 2;

    /// <summary>Recognizes text in a BGRA image, 4 bytes per pixel, no row padding.</summary>
    public static Task<OcrOutcome> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<OcrOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            var init = RoInitialize(1);
            try
            {
                done.TrySetResult(Recognize(bgra, width, height, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                done.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                done.TrySetResult(OcrOutcome.Failed(ex.Message));
            }
            finally
            {
                // Only uninitialize what this call initialized.
                if (init >= 0) RoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "Screenshot text recognition",
        };

        thread.Start();
        return done.Task;
    }

    private static OcrOutcome Recognize(byte[] bgra, int width, int height, CancellationToken cancellationToken)
    {
        nint statics = 0, engine = 0, language = 0, bitmap = 0, operation = 0, result = 0, lines = 0;

        try
        {
            statics = Factory("Windows.Media.Ocr.OcrEngine", IidOcrEngineStatics);

            uint maxDimension;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(statics, 6))(statics, &maxDimension));

            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(statics, 10))(statics, &engine));
            if (engine == 0)
                return OcrOutcome.Failed("No recognition language installed. Windows Settings › Language › Options › Optical character recognition.");

            string? languageName = null;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(engine, 7))(engine, &language) >= 0 && language != 0)
                languageName = ReadString(language, 7);

            // Larger than allowed is rejected; shrink by whole divisors so no smoothing blurs the text.
            // Smaller images are doubled while allowed: at 11 and 13 px (typical UI font sizes) the
            // recognizer misread several words and "91c0" as "glco"; doubled, everything was right. Triple
            // brought nothing more.
            var (pixels, w, h) = width * 2 <= maxDimension && height * 2 <= maxDimension
                ? Double(bgra, width, height)
                : FitInto(bgra, width, height, (int)maxDimension);

            bitmap = CreateBitmap(pixels, w, h);

            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(engine, 6))(engine, bitmap, &operation));
            WaitFor(operation, cancellationToken);

            // IAsyncOperation<OcrResult>.GetResults, slot 8, after put_ and get_Completed.
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(operation, 8))(operation, &result));

            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(result, 6))(result, &lines));

            uint count;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(lines, 7))(lines, &count));

            var found = new List<Line>((int)count);
            for (uint i = 0; i < count; i++)
            {
                nint line = 0;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(lines, 6))(lines, i, &line));
                    if (ReadLine(line) is { } read) found.Add(read);
                }
                finally
                {
                    Release(line);
                }
            }

            return new OcrOutcome(string.Join(Environment.NewLine, InReadingOrder(found)), languageName, null);
        }
        finally
        {
            Release(lines);
            Release(result);
            Release(operation);
            Release(bitmap);
            Release(language);
            Release(engine);
            Release(statics);
        }
    }

    /// <summary>A recognized line with its position in the image.</summary>
    private sealed record Line(string Text, float Left, float Top, float Bottom)
    {
        public float Height => Bottom - Top;
    }

    /// <summary>Reads text and position of an <c>OcrLine</c>; the position from its word rectangles.</summary>
    private static Line? ReadLine(nint line)
    {
        var text = ReadString(line, 7);
        if (text.Length == 0) return null;

        nint words = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(line, 6))(line, &words));

            uint count;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(words, 7))(words, &count));

            float left = float.MaxValue, top = float.MaxValue, bottom = float.MinValue;

            for (uint i = 0; i < count; i++)
            {
                nint word = 0;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(words, 6))(words, i, &word));

                    // IOcrWord.BoundingRect, slot 6: Windows.Foundation.Rect, four floats.
                    WordRect rect;
                    Check(((delegate* unmanaged[Stdcall]<nint, WordRect*, int>)Slot(word, 6))(word, &rect));

                    left = Math.Min(left, rect.X);
                    top = Math.Min(top, rect.Y);
                    bottom = Math.Max(bottom, rect.Y + rect.Height);
                }
                finally
                {
                    Release(word);
                }
            }

            return count == 0 ? new Line(text, 0, 0, 0) : new Line(text, left, top, bottom);
        }
        finally
        {
            Release(words);
        }
    }

    /// <summary>Joins lines at the same height again, left to right.</summary>
    /// <remarks>
    /// The recognizer splits a screen line at a larger gap: "Line 1:" and the rest came out as two lines,
    /// not even in order (79 lines for 40 lines of text). On screens that's normal: label and value, table
    /// columns, a menu. Same height means the centers are less than half a line height apart.
    /// </remarks>
    private static IEnumerable<string> InReadingOrder(List<Line> lines)
    {
        var rows = new List<List<Line>>();

        foreach (var line in lines.OrderBy(l => (l.Top + l.Bottom) / 2))
        {
            var middle = (line.Top + line.Bottom) / 2;
            var row = rows.LastOrDefault();

            if (row is not null)
            {
                var anchor = row[0];
                var anchorMiddle = (anchor.Top + anchor.Bottom) / 2;
                var tolerance = Math.Max(1f, Math.Min(anchor.Height, line.Height)) / 2;

                if (Math.Abs(middle - anchorMiddle) <= tolerance)
                {
                    row.Add(line);
                    continue;
                }
            }

            rows.Add([line]);
        }

        return rows.Select(row => string.Join("  ", row.OrderBy(l => l.Left).Select(l => l.Text)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WordRect
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
    }

    /// <summary>Creates a SoftwareBitmap and fills it row by row.</summary>
    private static nint CreateBitmap(byte[] bgra, int width, int height)
    {
        nint factory = 0, bitmap = 0, buffer = 0, memory = 0, reference = 0, access = 0;

        try
        {
            factory = Factory("Windows.Graphics.Imaging.SoftwareBitmap", IidSoftwareBitmapFactory);

            // CreateWithAlpha, slot 7: format, width, height, alpha mode.
            Check(((delegate* unmanaged[Stdcall]<nint, int, int, int, int, nint*, int>)Slot(factory, 7))(
                factory, Bgra8, width, height, AlphaIgnore, &bitmap));

            // ISoftwareBitmap.LockBuffer, slot 15.
            Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(bitmap, 15))(bitmap, AccessWrite, &buffer));

            // IBitmapBuffer.GetPlaneDescription(0), slot 7. The buffer stride can exceed width × 4, so
            // it is filled row by row.
            PlaneDescription plane;
            Check(((delegate* unmanaged[Stdcall]<nint, int, PlaneDescription*, int>)Slot(buffer, 7))(buffer, 0, &plane));

            memory = Query(buffer, IidMemoryBuffer);
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(memory, 6))(memory, &reference));

            access = Query(reference, IidMemoryBufferByteAccess);

            byte* target;
            uint capacity;
            // IMemoryBufferByteAccess is classic COM: GetBuffer is slot 3.
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)Slot(access, 3))(access, &target, &capacity));

            var rowBytes = width * 4;
            fixed (byte* source = bgra)
            {
                for (var y = 0; y < height; y++)
                {
                    var offset = plane.StartIndex + (long)y * plane.Stride;
                    if (offset + rowBytes > capacity) break;
                    Buffer.MemoryCopy(source + (long)y * rowBytes, target + offset, capacity - offset, rowBytes);
                }
            }

            // Close first, then recognize: while the buffer is locked, the bitmap rejects any access.
            Close(reference);
            Close(buffer);

            var created = bitmap;
            bitmap = 0;
            return created;
        }
        finally
        {
            Release(access);
            Release(reference);
            Release(memory);
            Release(buffer);
            Release(bitmap);
            Release(factory);
        }
    }

    /// <summary>Waits for a WinRT operation without building a callback.</summary>
    /// <remarks>
    /// A <c>Completed</c> callback would be its own COM object; polling <c>IAsyncInfo</c> is enough for a wait
    /// on a dedicated thread, and recognition takes tenths of a second.
    /// </remarks>
    private static void WaitFor(nint operation, CancellationToken cancellationToken)
    {
        var info = Query(operation, IidAsyncInfo);

        try
        {
            while (true)
            {
                int status;
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 7))(info, &status));

                if (status == 1) return;                       // Completed
                if (status == 2) throw new OperationCanceledException(cancellationToken);
                if (status == 3)
                {
                    int error;
                    ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 8))(info, &error);
                    Marshal.ThrowExceptionForHR(error);
                }

                if (cancellationToken.IsCancellationRequested)
                    ((delegate* unmanaged[Stdcall]<nint, int>)Slot(info, 9))(info);

                Thread.Sleep(10);
            }
        }
        finally
        {
            Release(info);
        }
    }

    /// <summary>Upscales bilinearly to double edge length.</summary>
    private static (byte[] Pixels, int Width, int Height) Double(byte[] bgra, int width, int height)
    {
        var w = width * 2;
        var h = height * 2;
        var result = new byte[w * h * 4];

        Parallel.For(0, h, y =>
        {
            // Center of the target pixel back to the source: (y + 0.5) / 2 − 0.5.
            var sy = (y - 0.5) / 2;
            var y0 = Math.Clamp((int)Math.Floor(sy), 0, height - 1);
            var y1 = Math.Min(y0 + 1, height - 1);
            var fy = Math.Clamp(sy - y0, 0, 1);

            for (var x = 0; x < w; x++)
            {
                var sx = (x - 0.5) / 2;
                var x0 = Math.Clamp((int)Math.Floor(sx), 0, width - 1);
                var x1 = Math.Min(x0 + 1, width - 1);
                var fx = Math.Clamp(sx - x0, 0, 1);

                var a = (y0 * width + x0) * 4;
                var b = (y0 * width + x1) * 4;
                var c = (y1 * width + x0) * 4;
                var d = (y1 * width + x1) * 4;
                var o = (y * w + x) * 4;

                for (var k = 0; k < 3; k++)
                {
                    var top = bgra[a + k] * (1 - fx) + bgra[b + k] * fx;
                    var bottom = bgra[c + k] * (1 - fx) + bgra[d + k] * fx;
                    result[o + k] = (byte)(top * (1 - fy) + bottom * fy + 0.5);
                }

                result[o + 3] = 255;
            }
        });

        return (result, w, h);
    }

    /// <summary>Shrinks until both edges fit the allowed size.</summary>
    private static (byte[] Pixels, int Width, int Height) FitInto(byte[] bgra, int width, int height, int max)
    {
        if (max <= 0 || (width <= max && height <= max)) return (bgra, width, height);

        var factor = (int)Math.Ceiling(Math.Max(width, height) / (double)max);
        var w = width / factor;
        var h = height / factor;
        var result = new byte[w * h * 4];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // Per target pixel the average over the source square.
                int b = 0, g = 0, r = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = ((y * factor) + dy) * width * 4;
                    for (var dx = 0; dx < factor; dx++)
                    {
                        var i = row + ((x * factor) + dx) * 4;
                        b += bgra[i];
                        g += bgra[i + 1];
                        r += bgra[i + 2];
                    }
                }

                var n = factor * factor;
                var o = (y * w + x) * 4;
                result[o] = (byte)(b / n);
                result[o + 1] = (byte)(g / n);
                result[o + 2] = (byte)(r / n);
                result[o + 3] = 255;
            }
        }

        return (result, w, h);
    }

    // ------------------------------------------------------------------ COM helpers

    [StructLayout(LayoutKind.Sequential)]
    private struct PlaneDescription
    {
        public int StartIndex;
        public int Width;
        public int Height;
        public int Stride;
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static nint Query(nint instance, Guid iid)
    {
        nint result;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &result));
        return result;
    }

    private static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    private static void Close(nint instance)
    {
        var closable = Query(instance, IidClosable);
        try
        {
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(closable, 6))(closable);
        }
        finally
        {
            Release(closable);
        }
    }

    private static nint Factory(string className, Guid iid)
    {
        var name = CreateString(className);
        try
        {
            nint factory;
            Check(RoGetActivationFactory(name, &iid, &factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    /// <summary>Reads an HSTRING property at <paramref name="slot"/>.</summary>
    private static string ReadString(nint instance, int slot)
    {
        nint value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot))(instance, &value));

        try
        {
            uint length;
            var chars = WindowsGetStringRawBuffer(value, &length);
            return chars == null ? "" : new string(chars, 0, (int)length);
        }
        finally
        {
            WindowsDeleteString(value);
        }
    }

    private static nint CreateString(string text)
    {
        nint result;
        fixed (char* chars = text)
        {
            Check(WindowsCreateString(chars, (uint)text.Length, &result));
        }

        return result;
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int type);

    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateString(char* source, uint length, nint* result);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint value);

    [LibraryImport("combase.dll")]
    private static partial char* WindowsGetStringRawBuffer(nint value, uint* length);
}
