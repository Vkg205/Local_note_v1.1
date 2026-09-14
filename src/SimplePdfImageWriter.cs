using System.IO;
using System.Globalization;
using System.Text;
using System.Windows.Media.Imaging;

namespace LocalNote.App.Services;

public static class SimplePdfImageWriter
{
    public static void Write(RenderTargetBitmap bitmap, string outputPath)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var imageStream = new MemoryStream();
        encoder.Save(imageStream);
        var jpeg = imageStream.ToArray();

        using var ms = new MemoryStream();
        var offsets = new List<long> { 0 };
        void Raw(string text) { var b = Encoding.ASCII.GetBytes(text); ms.Write(b); }
        Raw("%PDF-1.4\n");
        var pageW = 595d; var pageH = 842d;
        var scale = Math.Min(pageW / bitmap.PixelWidth, pageH / bitmap.PixelHeight);
        var w = bitmap.PixelWidth * scale; var h = bitmap.PixelHeight * scale;
        var x = (pageW - w) / 2; var y = (pageH - h) / 2;

        offsets.Add(ms.Position); Raw("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets.Add(ms.Position); Raw("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets.Add(ms.Position); Raw("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        offsets.Add(ms.Position); Raw($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {bitmap.PixelWidth} /Height {bitmap.PixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        ms.Write(jpeg); Raw("\nendstream\nendobj\n");
        var content = string.Create(CultureInfo.InvariantCulture, $"q\n{w:0.###} 0 0 {h:0.###} {x:0.###} {y:0.###} cm\n/Im0 Do\nQ\n");
        offsets.Add(ms.Position); Raw($"5 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream\nendobj\n");
        var xref = ms.Position;
        Raw("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++) Raw(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Raw($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(outputPath, ms.ToArray());
    }
}
