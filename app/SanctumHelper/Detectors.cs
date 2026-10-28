using System;
using System.Linq;
using System.Text.RegularExpressions;
using OpenCvSharp;

namespace SanctumHelper;

public enum Anchor { TopCenter, TopRight, TopLeft }

public static class Regions
{
    /// <summary>The game's UI area in a client of any size: a 16:9 box, as tall as the window and centered on wider
    /// screens (21:9 ultrawide: pillarboxed), as wide as the window and kept to the top on narrower ones (16:10). Its
    /// height / 1080 is the UI scale (1920x1200 is 1.0, not 1.11; 3440x1440 is 1.33 with the UI starting 440 px in).</summary>
    public static Rect Hud(int w, int h)
    {
        double u = Math.Min(w, h * 16.0 / 9);
        return new Rect((int)Math.Round((w - u) / 2), 0, (int)Math.Round(u), Math.Min(h, (int)Math.Round(u * 9 / 16)));
    }

    /// <summary>The game's UI scale: its UI area's height / 1080.</summary>
    public static double Ui(int w, int h) => Hud(w, h).Height / 1080.0;

    /// <summary>Maps a region given in 1920x1080 reference pixels onto a client of any size.
    /// The game's UI scales with its UI area (see Hud) and stays anchored to that area's edges.</summary>
    public static Rect ToClient(RegionF r, Anchor a, int w, int h)
    {
        var hud = Hud(w, h);
        double s = hud.Height / 1080.0;
        double x = a switch
        {
            Anchor.TopCenter => hud.X + hud.Width / 2.0 + (r.X - 960) * s,
            Anchor.TopRight => hud.Right - (1920 - r.X) * s,
            _ => hud.X + r.X * s,
        };
        var rect = new Rect((int)Math.Round(x), (int)Math.Round(hud.Y + r.Y * s), (int)Math.Round(r.W * s), (int)Math.Round(r.H * s));
        return rect.Intersect(new Rect(0, 0, w, h));
    }
}

/// <summary>Reads the party screen's goal line, e.g. "Operation: Egg Heist Team Mode Nightmare"
/// or (wrapped) "Operation: Egg Heist Team Mode Difficulty: Chaos".</summary>
public sealed class DifficultyDetector(OcrService ocr)
{
    static readonly (Regex re, string name)[] Words =
    [
        (new(@"chaos|cha0s", RegexOptions.IgnoreCase), "Chaos"),
        (new(@"nightm|n[il1]ghtmare", RegexOptions.IgnoreCase), "Nightmare"),
        (new(@"\bhard\b", RegexOptions.IgnoreCase), "Hard"),
        (new(@"\bnormal\b|beginner", RegexOptions.IgnoreCase), "Normal"),
    ];

    public string LastText { get; private set; } = "";

    /// <summary>Returns a difficulty when the frame shows the Egg Heist team-mode party screen.
    /// The frame may be just a band from the top of the game window: the region is placed by the
    /// window's own size (clientHeight), not the frame's. (Placing it by a 70 px band's height
    /// read a tiny spot near the corner, so the party screen was never read live.)</summary>
    public string? Detect(Mat frame, RegionF region, int clientWidth = 0, int clientHeight = 0)
    {
        var rc = Regions.ToClient(region, Anchor.TopCenter, clientWidth > 0 ? clientWidth : frame.Width, clientHeight > 0 ? clientHeight : frame.Height)
            .Intersect(new Rect(0, 0, frame.Width, frame.Height));
        if (rc.Width < 20 || rc.Height < 10) return null;
        using var crop = new Mat(frame, rc);
        // Light text on a dark translucent banner: keep only bright pixels so the aurora
        // background behind the banner cannot leak into the OCR.
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        // The same picture as last time (a menu or the party screen left open, the game in the background): the
        // same answer, without running OCR again (one or two OCR passes twice a second, all day while idle).
        if (lastGray != null && lastGray.Size() == gray.Size())
        {
            using var diff = new Mat();
            Cv2.Absdiff(gray, lastGray, diff);
            if (Cv2.Mean(diff).Val0 < 1.0) return lastResult;
        }
        lastGray?.Dispose();
        lastGray = gray.Clone();
        lastResult = null;
        using var bin = new Mat();
        Cv2.Threshold(gray, bin, 150, 255, ThresholdTypes.BinaryInv);
        using var bgr = new Mat();
        Cv2.CvtColor(bin, bgr, ColorConversionCodes.GRAY2BGR);
        var text = ocr.Read(bgr);
        if (string.IsNullOrWhiteSpace(text)) text = ocr.Read(crop);
        LastText = text;
        return lastResult = Parse(text, Lang.GameCode);
    }

    Mat? lastGray;
    string? lastResult;

    /// <summary>Accent-, case- and space-free form for comparing OCR text with the game's wording
    /// (OCR spaces out CJK characters and may drop Vietnamese tone marks).</summary>
    static string Norm(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var b = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark || char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || ch == '\u200b') continue;
            b.Append(char.ToLowerInvariant(ch));
        }
        return b.ToString();
    }

    /// <summary>The party line in the game's language: its Egg Heist team-mode title (any of the
    /// game's wordings) and a difficulty name. English keeps the OCR-tolerant patterns below.</summary>
    public static string? Parse(string text, string gameLang)
    {
        if (gameLang == "en") return Parse(text);
        var t = Norm(text);
        bool title = Lang.Variants(gameLang, "EggHeistTeamMode").Concat(Lang.Variants(gameLang, "EggHeist"))
            .Select(Norm).Where(v => v.Length >= 2).Any(v => t.Contains(v));
        if (!title) return Parse(text); // the game may still show English text
        foreach (var d in new[] { "Chaos", "Nightmare", "Hard", "Normal" })
            if (Lang.Variants(gameLang, d).Select(Norm).Where(v => v.Length >= 1).Any(v => t.Contains(v))) return d;
        return Parse(text);
    }

    public static string? Parse(string text)
    {
        var t = text.Replace('\n', ' ');
        if (!Regex.IsMatch(t, @"egg\s*he[il1]st", RegexOptions.IgnoreCase)) return null;
        if (!Regex.IsMatch(t, @"team", RegexOptions.IgnoreCase)) return null;
        foreach (var (re, name) in Words)
            if (re.IsMatch(t)) return name;
        return null;
    }
}
