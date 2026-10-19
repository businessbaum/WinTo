using System.Globalization;
using System.Text;
using PDFtoImage;
using SkiaSharp;

namespace MultiTool.Modules.Files.Metadata;

/// <summary>A new, deliberately minimal PDF containing only raster page images.
/// No source PDF object, font, attachment, script, signature or revision is copied.</summary>
internal static class PdfCleaner
{
    public static void Clean(string source, string target, CancellationToken token)
    {
        using var input = File.OpenRead(source);
        var sizes = Conversion.GetPageSizes(input, leaveOpen: true).ToArray();
        if (sizes.Length is < 1 or > 1000) throw new InvalidDataException("PDF must have 1 to 1000 pages.");
        foreach (var size in sizes)
            if (!float.IsFinite(size.Width) || !float.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0 || size.Width * size.Height * 9 > 40_000_000)
                throw new InvalidDataException("PDF page exceeds the limit of 40 million pixels at 216 dpi.");
        input.Position = 0;
        using (var output = File.Create(target))
        {
            List<long> offsets = [0];
            void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
            void Begin(int id) { if (id != offsets.Count) throw new InvalidOperationException(); offsets.Add(output.Position); Write($"{id} 0 obj\n"); }
            Write("%PDF-1.4\n");
            Begin(1); Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            Begin(2); Write($"<< /Type /Pages /Count {sizes.Length} /Kids [{string.Join(' ', Enumerable.Range(0, sizes.Length).Select(i => $"{3 + i * 3} 0 R"))}] >>\nendobj\n");
            int page = 0;
            foreach (var bitmap in Conversion.ToImages(input, leaveOpen: true,
                         options: new RenderOptions(Dpi: 216, WithAnnotations: false, WithFormFill: true, BackgroundColor: SKColors.White)))
            {
                using (bitmap)
                {
                    token.ThrowIfCancellationRequested();
                    var size = sizes[page];
                    string width = size.Width.ToString("0.#####", CultureInfo.InvariantCulture), height = size.Height.ToString("0.#####", CultureInfo.InvariantCulture);
                    int id = 3 + page * 3;
                    Begin(id); Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /I {id + 1} 0 R >> >> /Contents {id + 2} 0 R >>\nendobj\n");
                    // A fresh encoder receives only the rendered pixels. Its JPEG
                    // wrapper is then sanitized like any other image in this module.
                    using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95) ?? throw new InvalidDataException("PDF page could not be encoded.");
                    var jpeg = ImageContainers.Clean(encoded.ToArray(), FileKind.Jpeg, token).Bytes;
                    Begin(id + 1); Write($"<< /Type /XObject /Subtype /Image /Width {bitmap.Width} /Height {bitmap.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
                    output.Write(jpeg); Write("\nendstream\nendobj\n");
                    string drawing = $"q {width} 0 0 {height} 0 0 cm /I Do Q\n";
                    Begin(id + 2); Write($"<< /Length {Encoding.ASCII.GetByteCount(drawing)} >>\nstream\n{drawing}endstream\nendobj\n");
                    page++;
                }
            }
            if (page != sizes.Length) throw new InvalidDataException("Not all PDF pages were rendered.");
            long xref = output.Position;
            Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
            foreach (long offset in offsets.Skip(1)) Write($"{offset:0000000000} 00000 n \n");
            Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }
        token.ThrowIfCancellationRequested();
        using var verified = File.OpenRead(target);
        if (Conversion.GetPageCount(verified) != sizes.Length) throw new InvalidDataException("Page check of the new PDF failed.");
    }
}
