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
        if (e.Args.Length >= 6 && e.Args[0] == "--styles")
        {
            try { Analyzer.Styles(e.Args, LayoutDb.Load(Path.Combine(AppContext.BaseDirectory, "Assets"))); }
            catch (Exception ex) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString()); }
            Shutdown();
            return;
        }
        single = new Mutex(true, @"Local\SanctumHelper.Single", out bool first);
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

    protected override void OnExit(ExitEventArgs e)
    {
        engine?.Dispose();
        hook?.Dispose();
        tray?.Dispose();
        single?.Dispose();
        base.OnExit(e);
    }
}
