using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OpenCvSharp;

namespace SanctumHelper;

/// <summary>Offline check of every detector on saved screenshots:
/// SanctumHelper.exe --analyze out_dir shot1.png [shot2.png ...] [--difficulty Hard]
/// Writes report.txt and, per screenshot, an image with the detections drawn on it.</summary>
public static class Analyzer
{
    public static void Run(string[] args, LayoutDb db, Settings settings)
    {
        // --lang de_DE: render the previews in another language.
        int li = Array.IndexOf(args, "--lang");
        Lang.Init(Path.Combine(db.Root), li >= 0 && li + 1 < args.Length ? args[li + 1] : "en", "Auto");
        var outDir = args[1];
        Directory.CreateDirectory(outDir);
        string? diff = null;
        int forcedLayout = 0;
        var files = new List<string>();
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--difficulty") diff = args[++i];
            else if (args[i] == "--layout") forcedLayout = int.Parse(args[++i]);
            else files.Add(args[i]);
        }
        var report = new StringBuilder();
        var ocr = new OcrService();
        var dd = new DifficultyDetector(ocr);
        var arts = new MapArtCache(db);
        var feats = new ArtFeatureCache(arts);
        var matcher = new MapMatcher(feats);
        var mini = new MinimapTracker(arts);
        var doorsLoc = new DoorLocator(arts, Cv2.ImRead(Path.Combine(db.Root, "icons", "map_header.png")),
            Cv2.ImRead(Path.Combine(db.Root, "icons", "screen_door_orange.png")), Cv2.ImRead(Path.Combine(db.Root, "icons", "screen_door_blue.png")));
        Layout? known = null;
        report.AppendLine($"OCR language: {ocr.Language}");

        foreach (var file in files)
        {
            report.AppendLine($"== {Path.GetFileName(file)}");
            using var frame = Cv2.ImRead(file, ImreadModes.Color);
            if (frame.Empty()) { report.AppendLine("  could not read"); continue; }
            var sw = Stopwatch.StartNew();
            var d = dd.Detect(frame, settings.PartyGoalRegion);
            report.AppendLine($"  party OCR ({sw.ElapsedMilliseconds} ms): \"{dd.LastText}\" -> {d ?? "none"}");
            var cands = db.Candidates(diff ?? d);

            sw.Restart();
            var pose = doorsLoc.Match(frame, cands, out var arrow);
            report.AppendLine($"  doors ({sw.ElapsedMilliseconds} ms): {(pose == null ? "none" : $"layout {pose.Layout.Number} scale {pose.Scale:0.000} t=({pose.Tx:0},{pose.Ty:0})")} arrow {arrow}");
            report.AppendLine($"    {doorsLoc.LastInfo}");
            if (pose != null)
            {
                var refiner = new FloorRefiner(arts);
                sw.Restart();
                var rp = refiner.Refine(frame, pose, 0.06, 40);
                report.AppendLine($"  refine narrow ({sw.ElapsedMilliseconds} ms): {(rp == null ? "none" : $"scale {rp.Scale:0.000} t=({rp.Tx:0},{rp.Ty:0})")} | {refiner.LastInfo}");
                sw.Restart();
                var bad = pose with { Scale = pose.Scale * 1.25, Tx = pose.Tx + 80, Ty = pose.Ty - 60 };
                var rb = refiner.Refine(frame, bad, 0.35, 160);
                report.AppendLine($"  refine from bad guess ({sw.ElapsedMilliseconds} ms): {(rb == null ? "none" : $"scale {rb.Scale:0.000} t=({rb.Tx:0},{rb.Ty:0})")} | {refiner.LastInfo}");
                if (rp != null) pose = rp;
            }
            sw.Restart();
            var fpose = matcher.Match(frame, pose != null ? [pose.Layout] : cands);
            pose ??= fpose;
            report.AppendLine($"  feature match ({sw.ElapsedMilliseconds} ms): {(fpose == null ? "none" : $"layout {fpose.Layout.Number} scale {fpose.Scale:0.000} t=({fpose.Tx:0},{fpose.Ty:0}) inliers {fpose.Inliers}")}");
            report.AppendLine($"    {matcher.LastInfo}");

            var rc = Regions.ToClient(settings.MinimapRegion, Anchor.TopLeft, frame.Width, frame.Height);
            using var mm = new Mat(frame, rc);
            sw.Restart();
            known = pose?.Layout ?? known ?? (forcedLayout != 0 ? db.ByNumber(forcedLayout) : null);
            mini.Reset(); // screenshots are far apart in time: always a full search
            Point2d? mpos = known != null ? mini.Locate(mm, known, Regions.Ui(frame.Width, frame.Height)) : null;
            var hit = mpos is Point2d mp ? ((Layout layout, Point2d pos, double score)?)(known!, mp, mini.LastScore) : null;
            report.AppendLine($"  minimap ({sw.ElapsedMilliseconds} ms): {(hit == null ? "none" : $"layout {hit.Value.layout.Number} at ({hit.Value.pos.X:0},{hit.Value.pos.Y:0}) score {hit.Value.score:0.00}")}");
            report.AppendLine($"    {mini.LastInfo}");

            if (diff != null)
            {
                // Experimental layout guess: score the minimap around each candidate's orange door.
                var scores = new List<string>();
                foreach (var l in db.Candidates(diff))
                {
                    var t = new MinimapTracker(arts);
                    t.Seed(new Point2d(l.Orange.Img[0], l.Orange.Img[1]));
                    t.Locate(mm, l, Regions.Ui(frame.Width, frame.Height));
                    scores.Add($"{l.Number}:{t.LastScore:0.00}");
                }
                report.AppendLine("  guess: " + string.Join(" ", scores));
            }
            if (db.Island is Layout isl)
            {
                var it = new IslandTracker(arts);
                sw.Restart();
                var ip = it.Locate(mm, isl, Regions.Ui(frame.Width, frame.Height), true);
                report.AppendLine($"  island ({sw.ElapsedMilliseconds} ms): {(ip == null ? "none" : $"({ip.Value.X:0},{ip.Value.Y:0})")} | {it.LastInfo}");
                if (ip is Point2d ipp)
                    foreach (var ms2 in new[] { "Corner" })
                        RenderOverlay(file, Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "_island_mini.png"), db, new Settings { MiniMapMode = ms2 },
                            new OverlayFrame(new Rect(0, 0, frame.Width, frame.Height), true, isl, null, ipp, true, null, "", Mini: true));
            }
            using var vis = frame.Clone();
            Cv2.Rectangle(vis, rc, Scalar.Cyan, 2);
            if (pose != null)
            {
                foreach (var m in pose.Layout.Markers.Where(m => m.Kind is "nest" or "orange" or "blue" or "legendary"))
                {
                    var p = pose.ToScreen(m.Img[0], m.Img[1]);
                    var col = m.Kind switch { "nest" => new Scalar(216, 79, 255), "orange" => new Scalar(30, 138, 255), "blue" => new Scalar(255, 139, 61), _ => new Scalar(58, 211, 255) };
                    Cv2.Circle(vis, new Point(p.X, p.Y), 9, col, 3);
                }
            }
            if (hit != null)
            {
                // Paste the art around the found position next to the minimap for eyeballing.
                var art = arts.Get(hit.Value.layout).Gray;
                int half = 160;
                var src = new Rect((int)hit.Value.pos.X - half, (int)hit.Value.pos.Y - half, half * 2, half * 2).Intersect(new Rect(0, 0, art.Width, art.Height));
                using var patch = new Mat(art, src);
                using var pc = new Mat();
                Cv2.CvtColor(patch, pc, ColorConversionCodes.GRAY2BGR);
                Cv2.Circle(pc, new Point(hit.Value.pos.X - src.X, hit.Value.pos.Y - src.Y), 6, Scalar.Red, -1);
                var dst = new Rect(rc.Right + 10, rc.Y, pc.Width, pc.Height).Intersect(new Rect(0, 0, vis.Width, vis.Height));
                pc[new Rect(0, 0, dst.Width, dst.Height)].CopyTo(vis[dst]);
            }
            Cv2.ImWrite(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "_analysis.jpg"), vis);
            double? hd = new HeadingDetector(Path.Combine(db.Root, "icons", "player_arrow.png")).Detect(mm, Regions.Ui(frame.Width, frame.Height));
            double? cam = HeadingDetector.FromViewCone(mm, Regions.Ui(frame.Width, frame.Height), out double camConf) is double cv && camConf >= 0.2 ? cv : null;
            report.AppendLine($"  heading: {(hd == null ? "none" : $"{hd:0} deg")}, camera (view cone): {(cam == null ? "none" : $"{cam:0} deg")} ({camConf:0.00})");
            double eggScore = HeadingDetector.EggScore(mm, Regions.Ui(frame.Width, frame.Height), Path.Combine(db.Root, "icons"));
            bool egg = (hd == null && eggScore >= 0.5) || Environment.GetEnvironmentVariable("SH_FORCE_EGG") == "1"; // (forcing: preview only)
            HeadingFeed.Publish(hd, cam, egg);
            report.AppendLine($"  egg form: {(egg ? "yes" : "no")} ({eggScore:0.00})");
            if (hit != null)
            {
                foreach (var mode in OverlayView.MiniMapStyles.Select(x => x.Value).Where(v => v != "Off"))
                {
                    var ms = new Settings { MiniMapMode = mode };
                    RenderOverlay(file, Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + $"_mini_{mode}.png"), db, ms,
                        new OverlayFrame(new Rect(0, 0, frame.Width, frame.Height), true, hit.Value.layout, null, hit.Value.pos, true, diff ?? d, "", Mini: true, Heading: hd, Camera: cam));
                }
                // Placement mode as the user sees it (corner map dragged to the bottom right).
                RenderOverlay(file, Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "_mini_placing.png"), db,
                    new Settings { MiniMapX = 0.8, MiniMapY = 0.72 },
                    new OverlayFrame(new Rect(0, 0, frame.Width, frame.Height), true, hit.Value.layout, null, hit.Value.pos, true, diff ?? d, "", Mini: true, Heading: hd, Camera: cam),
                    placing: true);
                RenderOverlay(file, Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "_big.png"), db, new Settings(),
                    new OverlayFrame(new Rect(0, 0, frame.Width, frame.Height), true, hit.Value.layout, null, hit.Value.pos, true, diff ?? d, "", Mini: true, Heading: hd, Big: true, Camera: cam));
            }
            if (pose != null)
            {
                Point2d? pl = arrow is Point2d a ? new Point2d((a.X - pose.Tx) / pose.Scale, (a.Y - pose.Ty) / pose.Scale) : null;
                RenderOverlay(file, Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "_overlay.png"), db, settings,
                    new OverlayFrame(new Rect(0, 0, frame.Width, frame.Height), true, pose.Layout, pose, pl, true, diff ?? d, ""));
            }
        }
        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
    }

    /// <summary>Every on-screen map style (and placement mode) drawn over one screenshot at a given
    /// spot: SanctumHelper.exe --styles out_dir shot.png layout x y [heading] [island]</summary>
    public static void Styles(string[] a, LayoutDb db)
    {
        var outDir = a[1];
        Directory.CreateDirectory(outDir);
        var shot = a[2];
        var layout = a.Length > 7 && a[7] == "island" ? db.Island! : db.ByNumber(int.Parse(a[3]))!;
        var at = new Point2d(double.Parse(a[4], System.Globalization.CultureInfo.InvariantCulture), double.Parse(a[5], System.Globalization.CultureInfo.InvariantCulture));
        double? heading = a.Length > 6 ? double.Parse(a[6], System.Globalization.CultureInfo.InvariantCulture) : null;
        var size = new System.Windows.Media.Imaging.BitmapImage(new Uri(Path.GetFullPath(shot)));
        var client = new Rect(0, 0, size.PixelWidth, size.PixelHeight);
        var f = new OverlayFrame(client, true, layout, null, at, true, null, "", Mini: true, Heading: heading);
        string tag = layout.IsIsland ? "island" : $"L{layout.Number}";
        foreach (var mode in OverlayView.MiniMapStyles.Select(x => x.Value).Where(v => v != "Off"))
            RenderOverlay(shot, Path.Combine(outDir, $"{tag}_{mode}.png"), db, new Settings { MiniMapMode = mode }, f);
        RenderOverlay(shot, Path.Combine(outDir, $"{tag}_Big.png"), db, new Settings(), f with { Big = true });
        RenderOverlay(shot, Path.Combine(outDir, $"{tag}_placing.png"), db, new Settings { MiniMapX = 0.8, MiniMapY = 0.7 }, f, placing: true);
    }

    /// <summary>Draws the real overlay over a screenshot, exactly as the game would show it.</summary>
    static void RenderOverlay(string shot, string outPath, LayoutDb db, Settings settings, OverlayFrame f, bool placing = false)
    {
        var bg = new System.Windows.Media.Imaging.BitmapImage(new Uri(Path.GetFullPath(shot)));
        var view = new OverlayView(db, settings) { Preview = true, PreviewUi = Regions.Ui(f.Client.Width, f.Client.Height), Frame = f, Placing = placing, PlacementClient = f.Client };
        var size = new System.Windows.Size(f.Client.Width, f.Client.Height);
        view.Measure(size); view.Arrange(new System.Windows.Rect(size));
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawImage(bg, new System.Windows.Rect(size));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(f.Client.Width, f.Client.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Render(view);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(outPath);
        enc.Save(fs);
    }
}
