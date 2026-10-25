using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SanctumHelper;

/// <summary>Developer check: renders every page of the settings window to PNG without showing it
/// (SanctumHelper.exe --settings-shot out_dir [language]). Works beside a running copy.</summary>
static class SettingsShot
{
    public static void Run(App app, string outDir, string language)
    {
        Directory.CreateDirectory(outDir);
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        var settings = new Settings { Language = language };
        var db = LayoutDb.Load(assets);
        Lang.Init(assets, settings.Language, settings.GameLanguage);
        var hook = new KeyHook(settings.HoldKey);
        var engine = new Engine(db, settings, hook);
        var w = new SettingsWindow(settings, db, engine, hook)
        {
            Left = -4000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false,
        };
        w.Show();
        foreach (var page in new[] { "Status", "Overlay", "Mini", "Markers", "Detection", "General", "Layouts", "About" })
        {
            if (w.FindName("Nav_" + page) is RadioButton rb) rb.IsChecked = true;
            DoEvents(); DoEvents();
            var root = (FrameworkElement)w.Content;
            var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((Brush)app.FindResource("Bg"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var f = File.Create(Path.Combine(outDir, $"settings_{language}_{page}.png"));
            enc.Save(f);
        }
        w.Close();
        hook.Dispose();
    }

    static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        System.Threading.Thread.Sleep(150);
    }
}
