using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenCvSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SanctumHelper;

/// <summary>Text recognition with the OCR engine built into Windows 10/11.</summary>
public sealed class OcrService
{
    readonly OcrEngine? engine;
    public string Language { get; } = "none";

    /// <summary>OCR for a language (BCP-47 tag) when Windows has it installed, else English, else
    /// the user's own languages. MissingLanguage says which was wanted but not there.</summary>
    public OcrService(string? languageTag = null)
    {
        if (languageTag != null && languageTag != "en-US")
        {
            try
            {
                var want = new Language(languageTag);
                if (OcrEngine.IsLanguageSupported(want)) engine = OcrEngine.TryCreateFromLanguage(want);
            }
            catch { /* not a language Windows knows */ }
            if (engine == null) MissingLanguage = languageTag;
        }
        var en = new Language("en-US");
        engine ??= OcrEngine.IsLanguageSupported(en) ? OcrEngine.TryCreateFromLanguage(en) : null;
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine != null) Language = engine.RecognizerLanguage.LanguageTag;
    }

    public bool Available => engine != null;
    public string? MissingLanguage { get; }

    /// <summary>Reads text from a BGR image. Small crops are upscaled first; the
    /// Windows engine is much more accurate on text at least ~20 px tall.</summary>
    public string Read(Mat bgr)
    {
        if (engine == null || bgr.Empty()) return "";
        using var big = new Mat();
        double f = bgr.Height < 80 ? 2.5 : 1.0;
        Cv2.Resize(bgr, big, new Size(), f, f, InterpolationFlags.Cubic);
        using var bgra = new Mat();
        Cv2.CvtColor(big, bgra, ColorConversionCodes.BGR2BGRA);
        var bytes = new byte[bgra.Total() * 4];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
        using var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height, BitmapAlphaMode.Premultiplied);
        sb.CopyFromBuffer(bytes.AsBuffer());
        var result = engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
        return string.Join(" ", result.Lines.Select(l => l.Text));
    }
}
