using System;
using System.Text.RegularExpressions;
using OpenCvSharp;

namespace SanctumHelper;

/// <summary>Dev switch chest-prompt: is the game's "Search" prompt up? Standing at a chest that can still be opened, the game shows
/// a dark pill with a white border right of the screen's centre, always at the same place (measured at 3840 x 2160: rows 0.500 to
/// 0.546 of the height, from 0.652 to 0.772 of the width). Everything that can be used shows the same pill with its own text
/// ("Egg Boat", a greeting with a player's name), so the pill alone says nothing: its text has to read "Search". The key box left
/// of it changes with the input device (F or Y) and isn't looked at. The text is never logged or kept: it can hold a player's name.</summary>
public static class ChestPrompt
{
    // The part looked at, in 16:9 fractions: x 0.60 to 0.80 of the width, y 0.48 to 0.57 of the height.
    const double X0 = 0.60, X1 = 0.80, Y0 = 0.48, Y1 = 0.57;

    /// <summary>Where the pill is in the game's picture (client coordinates). Placed from the centre by the height, like the rest
    /// of the game's centred HUD, so a wider window reads the same spot.</summary>
    public static Rect Area(int width, int height)
    {
        double wide = height * 16.0 / 9;
        int x = (int)Math.Round(width / 2.0 + (X0 - 0.5) * wide), y = (int)Math.Round(Y0 * height);
        var r = new Rect(x, y, (int)Math.Round((X1 - X0) * wide), (int)Math.Round((Y1 - Y0) * height));
        return r.Intersect(new Rect(0, 0, width, height));
    }

    static int Col(Mat m, double x) => Math.Clamp((int)((x - X0) / (X1 - X0) * m.Width), 0, m.Width);
    static int Row(Mat m, double y) => Math.Clamp((int)((y - Y0) / (Y1 - Y0) * m.Height), 0, m.Height);

    /// <summary>Is a one-line pill up in this picture of Area (BGR)? Its fill right of a short text is dark and nearly gray, and
    /// its white border runs along the top and the bottom. A long or two-line text fails this, which is fine: "Search" is short.</summary>
    public static bool Up(Mat area)
    {
        if (area.Empty() || area.Width < 60 || area.Height < 16 || area.Channels() < 3) return false;
        var px = area.GetGenericIndexer<Vec3b>();
        int dark = 0, all = 0;
        for (int y = Row(area, 0.512); y < Row(area, 0.535); y++)
            for (int x = Col(area, 0.735); x < Col(area, 0.76); x++)
            {
                var p = px[y, x];
                int max = Math.Max(p.Item0, Math.Max(p.Item1, p.Item2)), min = Math.Min(p.Item0, Math.Min(p.Item1, p.Item2));
                if (max > 45 && max < 125 && max - min < 45) dark++;
                all++;
            }
        if (all == 0 || dark < 0.9 * all) return false;
        return Border(area, px, 0.495, 0.507) && Border(area, px, 0.540, 0.552);
    }

    /// <summary>A row in this band that is white along at least 80% of the pill's middle.</summary>
    static bool Border(Mat area, Mat.Indexer<Vec3b> px, double y0, double y1)
    {
        int x0 = Col(area, 0.66), x1 = Col(area, 0.75);
        if (x1 <= x0) return false;
        for (int y = Row(area, y0); y < Row(area, y1); y++)
        {
            int white = 0;
            for (int x = x0; x < x1; x++)
            {
                var p = px[y, x];
                if (Math.Min(p.Item0, Math.Min(p.Item1, p.Item2)) > 200) white++;
            }
            if (white >= 0.8 * (x1 - x0)) return true;
        }
        return false;
    }

    /// <summary>The pill's text, as dark letters on white for the reader (a copy the caller disposes).</summary>
    public static Mat Text(Mat area)
    {
        int x0 = Col(area, 0.659), x1 = Col(area, 0.765), y0 = Row(area, 0.503), y1 = Row(area, 0.544);
        using var part = new Mat(area, new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)));
        using var gray = new Mat();
        Cv2.CvtColor(part, gray, ColorConversionCodes.BGR2GRAY);
        using var inverted = new Mat();
        Cv2.BitwiseNot(gray, inverted);
        var bgr = new Mat();
        Cv2.CvtColor(inverted, bgr, ColorConversionCodes.GRAY2BGR);
        return bgr;
    }

    // English only so far ("Search" was seen at a chest; "Open" is what the prompt was reported to say).
    // A first letter lost at the icon's edge, or a scrap of the icon read as a mark or two before the word, still counts.
    static readonly Regex Words = new(@"^(\S{1,2}\s+)?\W*(s?earch|open)\W*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Does the pill's text say it is something to search: a chest?</summary>
    public static bool IsChest(string text) => Words.IsMatch(text);
}
