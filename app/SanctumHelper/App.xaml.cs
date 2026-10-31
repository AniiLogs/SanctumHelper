using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace SanctumHelper;

public partial class App : Application
{
    Mutex? single;
    WinForms.NotifyIcon? tray;
    Settings settings = new();
    LayoutDb? db;
    KeyHook? hook;
    Engine? engine;
    OverlayWindow? overlay;
    ToastWindow toast = null!;
    SettingsWindow? settingsWindow;
    Hosted? hosted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--uninstall"))
        {
            try { Uninstaller.Run(e.Args); } catch (Exception ex) { MessageBox.Show("Uninstall failed:\n" + ex.Message, "Sanctum Helper"); }
            Shutdown();
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--analyze")
        {
            try { Analyzer.Run(e.Args, LayoutDb.Load(Path.Combine(AppContext.BaseDirectory, "Assets")), new Settings()); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString()); }
            Shutdown();
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] is "--cone-record" or "--cone-replay")
        {
            try
            {
                if (e.Args[0] == "--cone-record") ConeRecord.Record(e.Args[1], e.Args.Length > 2 ? double.Parse(e.Args[2], System.Globalization.CultureInfo.InvariantCulture) : 10);
                else ConeRecord.Replay(e.Args[1]);
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString()); }
            Shutdown();
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--settings-shot")
        {
            try { SettingsShot.Run(this, e.Args[1], e.Args.Length > 2 ? e.Args[2] : "en"); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString()); }
            Shutdown();
            return;
        }
        if (e.Args.Contains("--place-test"))
        {
            // Developer check of placement mode without the engine (works beside a running copy).
            var ps = Settings.Load();
            var pov = new OverlayWindow(LayoutDb.Load(Path.Combine(AppContext.BaseDirectory, "Assets")), ps);
            pov.Show();
            pov.PlacementEnded += Shutdown;
            Dispatcher.BeginInvoke(pov.BeginPlacement, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--magnifier-test")
        {
            // Developer check: a picture of the screen without our overlay (Magnifier), read off a background thread
            // like the engine's. A see-through red overlay over a green window: without it, green; a plain copy, red.
            static Window Box(System.Windows.Media.Color c, bool overlay) => new()
            {
                Left = 300, Top = 300, Width = 400, Height = 300, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Topmost = overlay, ShowInTaskbar = false, AllowsTransparency = overlay,
                Background = new System.Windows.Media.SolidColorBrush(overlay ? System.Windows.Media.Color.FromArgb(160, c.R, c.G, c.B) : c),
            };
            var under = Box(System.Windows.Media.Colors.Lime, false); under.Show();
            var over = Box(System.Windows.Media.Colors.Red, true); over.Show();
            var h = new System.Windows.Interop.WindowInteropHelper(over).Handle;
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr((long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE) | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED));
            var dir = e.Args[1];
            new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(800);
                var lines = new System.Collections.Generic.List<string>();
                var r = new OpenCvSharp.Rect(0, 0, 100, 100);
                var pos = Dispatcher.Invoke(() => { var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(over); return ((int)(400 * dpi.DpiScaleX), (int)(400 * dpi.DpiScaleY)); });
                r.X = pos.Item1; r.Y = pos.Item2;
                for (int i = 0; i < 3; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var m = Magnifier.Grab(r, new[] { h });
                    double ms = sw.Elapsed.TotalMilliseconds;
                    using var plain = ScreenGrabber.Grab(r);
                    string Px(OpenCvSharp.Mat? x) => x == null ? "none" : x.At<OpenCvSharp.Vec3b>(50, 50).ToString();
                    lines.Add($"without overlay {Px(m)} ({ms:F1} ms, {m?.Width}x{m?.Height}) | plain screen copy {Px(plain)}");
                }
                lines.Add("magnifier failed: " + Magnifier.Failed);
                Directory.CreateDirectory(dir);
                File.WriteAllLines(Path.Combine(dir, "magnifier.txt"), lines);
                Dispatcher.Invoke(Shutdown);
            }) { IsBackground = true }.Start();
            return;
        }
        if (e.Args.Length >= 6 && e.Args[0] == "--styles")
        {
            try { Analyzer.Styles(e.Args, LayoutDb.Load(Path.Combine(AppContext.BaseDirectory, "Assets"))); }
            catch (Exception ex) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString()); }
            Shutdown();
            return;
        }
        bool hostedMode = e.Args.Contains("--hosted");
        if (hostedMode) Hosted.Enable();
        single = new Mutex(true, @"Local\SanctumHelper.Single", out bool first);
        if (!first && hostedMode)
        {
            // A standalone copy is running: tell aLogs instead of showing a message box.
            Log.Write("hosted: another Sanctum Helper is already running");
            Shutdown(3);
            return;
        }
        if (!first)
        {
            MessageBox.Show(Lang.T("Sanctum Helper is already running. Look for its icon in the system tray."), "Sanctum Helper");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, ex) => { Log.Write("ui: " + ex.Exception); ex.Handled = true; };

        settings = Settings.Load();
        try
        {
            db = LayoutDb.Load(Path.Combine(AppContext.BaseDirectory, "Assets"));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not load the layout data:\n" + ex.Message, "Sanctum Helper");
            Shutdown();
            return;
        }
        Lang.Init(Path.Combine(AppContext.BaseDirectory, "Assets"), settings.Language, settings.GameLanguage);
        Log.Write($"start v{typeof(App).Assembly.GetName().Version}, {db.Layouts.Count} layouts, key {KeyHook.Name(settings.HoldKey)}, difficulty {settings.Difficulty}, language {Lang.Code} (game {Lang.GameCode})");

        hook = new KeyHook(settings.HoldKey);
        overlay = new OverlayWindow(db, settings);
        // (the notification window is made once the engine exists, below)
        overlay.Show();
        engine = new Engine(db, settings, hook);
        engine.Frame += f => Dispatcher.BeginInvoke(() => overlay.Apply(f));
        engine.StatusChanged += () => Dispatcher.BeginInvoke(UpdateTray);
        engine.CaptureFallback += () => Dispatcher.BeginInvoke(() => overlay.ApplyCaptureMode(screenFallback: true));
        toast = new ToastWindow(settings);
        engine.LanguageDetected += () => Dispatcher.BeginInvoke(LanguageChanged);
        engine.Notify += (text, diff) => Dispatcher.BeginInvoke(() =>
            toast.Show(text, diff != null ? OverlayView.DifficultyColor(diff) : System.Windows.Media.Color.FromRgb(0x5C, 0xB8, 0xFF), engine.GameClient));
        engine.Start();

        if (hostedMode)
        {
            int pi = Array.IndexOf(e.Args, "--parent");
            int parentPid = pi >= 0 && pi + 1 < e.Args.Length && int.TryParse(e.Args[pi + 1], out var pp) ? pp : 0;
            hosted = new Hosted(this, settings, db, engine, hook, parentPid);
            Log.Write("hosted by the aLogs app");
            return;
        }

        tray = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "Sanctum Helper",
            ContextMenuStrip = BuildMenu(),
        };
        tray.DoubleClick += (_, _) => OpenSettings();
        UpdateTray();

        bool firstRun = !File.Exists(Path.Combine(Settings.Dir, "settings.json"));
        if (firstRun)
        {
            settings.Save();
            tray.ShowBalloonTip(6000, Lang.T("Sanctum Helper is running"),
                Lang.F("Open the map in the Lost Sanctum and hold {0}. Settings are in the tray icon.", KeyHook.Name(settings.HoldKey)), WinForms.ToolTipIcon.Info);
        }
        // Never pop a window over the game: only open settings on first run when it isn't in front.
        if (!e.Args.Contains("--tray") && firstRun && Native.ClassName(Native.GetForegroundWindow()) != "UnityWndClass") OpenSettings();
    }

    static System.Drawing.Icon LoadIcon()
    {
        var info = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        return info != null ? new System.Drawing.Icon(info.Stream) : System.Drawing.SystemIcons.Application;
    }

    WinForms.ContextMenuStrip BuildMenu()
    {
        var m = new WinForms.ContextMenuStrip();
        m.Items.Add(Lang.T("Settings…"), null, (_, _) => OpenSettings());
        m.Items.Add(Lang.T("Position mini map…"), null, (_, _) => BeginMiniMapPlacement());
        var diff = new WinForms.ToolStripMenuItem(Lang.T("Difficulty"));
        foreach (var d in new[] { "Auto" }.Concat(db!.Difficulties))
        {
            var item = new WinForms.ToolStripMenuItem(d == "Auto" ? Lang.T("Auto (read party screen)") : Lang.Difficulty(d)) { Tag = d };
            item.Click += (_, _) => { settings.Difficulty = d; settings.Save(); engine?.ResetRun(); UpdateTray(); };
            diff.DropDownItems.Add(item);
        }
        diff.DropDownOpening += (_, _) =>
        {
            foreach (WinForms.ToolStripMenuItem i in diff.DropDownItems) i.Checked = (string)i.Tag! == settings.Difficulty;
        };
        m.Items.Add(diff);
        m.Items.Add(Lang.T("New run (forget layout)"), null, (_, _) => engine?.ResetRun());
        m.Items.Add(new WinForms.ToolStripSeparator());
        m.Items.Add(Lang.T("Open log folder"), null, (_, _) =>
        {
            System.IO.Directory.CreateDirectory(Settings.Dir);
            Process.Start(new ProcessStartInfo(Settings.Dir) { UseShellExecute = true });
        });
        m.Items.Add(Lang.T("Exit"), null, (_, _) => ExitApp());
        return m;
    }

    /// <summary>Lets the user drag the on-screen map where they want it (tray menu, settings window).</summary>
    public void BeginMiniMapPlacement() => overlay?.BeginPlacement();

    /// <summary>The language was changed in the settings: everything shows it from now on.</summary>
    public void LanguageChanged()
    {
        Lang.Apply(settings.Language, settings.GameLanguage);
        engine?.GameLanguageChanged();
        if (tray != null) { tray.ContextMenuStrip?.Dispose(); tray.ContextMenuStrip = BuildMenu(); }
        UpdateTray();
        if (settingsWindow != null)
        {
            var w = settingsWindow;
            double left = w.Left, top = w.Top;
            w.Close();
            OpenSettings();
            if (settingsWindow != null) { settingsWindow.Left = left; settingsWindow.Top = top; }
        }
    }

    /// <summary>Called by the settings window after a change.</summary>
    public void SettingsChanged()
    {
        overlay?.ApplyCaptureMode();
        UpdateTray();
    }

    public void UpdateTray()
    {
        if (tray == null || engine == null) return;
        string game = engine.GameFound ? Lang.T("game found") : Lang.T("waiting for Aniimo");
        string d = engine.EffectiveDifficulty is string ed ? Lang.Difficulty(ed) : Lang.T("difficulty ?");
        string l = engine.CurrentLayout?.DisplayTitle ?? Lang.T("layout ?");
        var text = $"Sanctum Helper: {game}\n{d} · {l}";
        tray.Text = text.Length > 127 ? text[..127] : text;
        settingsWindow?.RefreshStatus();
    }

    void OpenSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(settings, db!, engine!, hook!);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    void ExitApp()
    {
        engine?.Dispose();
        hook?.Dispose();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        overlay?.Close();
        Shutdown();
    }

    /// <summary>Hosted mode: the aLogs app closed or asked us to stop.</summary>
    public void ExitHosted() => ExitApp();

    protected override void OnExit(ExitEventArgs e)
    {
        hosted?.Dispose();
        engine?.Dispose();
        hook?.Dispose();
        tray?.Dispose();
        single?.Dispose();
        base.OnExit(e);
    }
}
