using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SanctumHelper;

/// <summary>Hosted mode (--hosted): Sanctum Helper runs as a module of the aLogs app, which shows
/// its options and status in its own window. No tray icon and no settings window here; instead:
///  - settings.json is still the one settings file: aLogs edits it and this reloads it live;
///  - hosted\info.json (once) lists what the options can be: styles, layers, difficulties,
///    languages, layouts;
///  - hosted\status.json (every second) says what Sanctum Helper sees right now;
///  - aLogs drops hosted\cmd-*.json files ({ "command": "...", ... }) for actions:
///    new-run, position-map, capture-key, capture-pad, clear-pad, render-layout, open-logs.
/// With --parent &lt;pid&gt; it quits when the aLogs app does.</summary>
public sealed class Hosted
{
    public static bool Active { get; private set; }
    public static string Dir => Path.Combine(Settings.Dir, "hosted");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly App app;
    readonly Settings s;
    readonly LayoutDb db;
    readonly Engine engine;
    readonly KeyHook hook;
    readonly DispatcherTimer timer;
    readonly FileSystemWatcher watcher;
    readonly Process? parent;
    DateTime settingsChangedAt = DateTime.MaxValue;
    string? capturing;
    DateTime captureStarted;
    DispatcherTimer? padTimer;

    public static void Enable() => Active = true;

    public Hosted(App app, Settings settings, LayoutDb db, Engine engine, KeyHook hook, int parentPid)
    {
        this.app = app; s = settings; this.db = db; this.engine = engine; this.hook = hook;
        Directory.CreateDirectory(Dir);
        foreach (var stale in Directory.GetFiles(Dir, "cmd-*.json")) TryDelete(stale);
        try { if (parentPid > 0) parent = Process.GetProcessById(parentPid); } catch { parent = null; }
        WriteInfo();

        watcher = new FileSystemWatcher(Settings.Dir, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
        watcher.Changed += (_, _) => settingsChangedAt = DateTime.UtcNow;
        watcher.Created += (_, _) => settingsChangedAt = DateTime.UtcNow;
        watcher.Renamed += (_, _) => settingsChangedAt = DateTime.UtcNow;
        watcher.EnableRaisingEvents = true;

        hook.AnyKeyPressed += OnAnyKey;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        int ticks = 0;
        timer.Tick += (_, _) =>
        {
            if (parent != null && parent.HasExited) { app.ExitHosted(); return; }
            // Settings changed on disk (by aLogs): wait for the write to settle, then apply.
            if ((DateTime.UtcNow - settingsChangedAt).TotalMilliseconds > 200) { settingsChangedAt = DateTime.MaxValue; Reload(); }
            RunCommands();
            if (capturing == "key" && (DateTime.UtcNow - captureStarted).TotalSeconds > 15) capturing = null;
            if (++ticks % 4 == 0) WriteStatus();
        };
        timer.Start();
        WriteStatus();
    }

    // ---- Settings from aLogs

    void Reload()
    {
        string text;
        try { text = File.ReadAllText(Path.Combine(Settings.Dir, "settings.json")); } catch { return; }
        if (text == Settings.LastJson) return; // our own save
        var fresh = Settings.Load();
        string oldLang = s.Language, oldGame = s.GameLanguage, oldDiff = s.Difficulty;
        int oldLayout = s.ForcedLayout;
        foreach (var p in typeof(Settings).GetProperties().Where(p => p.CanRead && p.CanWrite))
            p.SetValue(s, p.GetValue(fresh));
        hook.Key = s.HoldKey;
        if (s.Language != oldLang || s.GameLanguage != oldGame) app.LanguageChanged();
        if (s.Difficulty != oldDiff || s.ForcedLayout != oldLayout) engine.ResetRun();
        app.SettingsChanged();
        Log.Write("hosted: settings reloaded");
        WriteInfo(); // names follow the app language
        WriteStatus();
    }

    // ---- Commands from aLogs

    void RunCommands()
    {
        string[] files;
        try { files = Directory.GetFiles(Dir, "cmd-*.json").OrderBy(f => f).ToArray(); } catch { return; }
        foreach (var file in files)
        {
            JsonElement cmd;
            try { cmd = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone(); }
            catch (IOException) { continue; } // still being written
            catch { TryDelete(file); continue; }
            TryDelete(file);
            try { Run(cmd); } catch (Exception e) { Log.Write("hosted command: " + e.Message); }
        }
    }

    void Run(JsonElement cmd)
    {
        switch (cmd.GetProperty("command").GetString())
        {
            case "new-run": engine.ResetRun(); break;
            // the app turns Sanctum Helper off: close the normal way (settings and logs saved), not killed
            case "quit": System.Windows.Application.Current.Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown()); break;
            case "position-map": app.BeginMiniMapPlacement(); break;
            case "capture-key": capturing = "key"; captureStarted = DateTime.UtcNow; break;
            case "capture-pad": CapturePad(); break;
            case "clear-pad": s.HoldButton = 0; s.Save(); break;
            case "open-logs":
                Directory.CreateDirectory(Settings.Dir);
                Process.Start(new ProcessStartInfo(Settings.Dir) { UseShellExecute = true });
                break;
            case "render-layout":
                RenderLayout(cmd.GetProperty("layout").GetInt32(),
                    cmd.TryGetProperty("width", out var w) ? w.GetInt32() : 900,
                    cmd.TryGetProperty("height", out var h) ? h.GetInt32() : 600);
                break;
        }
        WriteStatus();
    }

    void OnAnyKey(int vk)
    {
        if (capturing != "key") return;
        capturing = null;
        if (vk == 0x1B) return; // Esc cancels
        app.Dispatcher.BeginInvoke(() =>
        {
            s.HoldKey = vk;
            hook.Key = vk;
            s.Save();
            WriteStatus();
        });
    }

    /// <summary>Waits for a newly pressed controller button (Esc cancels, 15 s timeout).</summary>
    void CapturePad()
    {
        if (padTimer != null) return;
        var pad = new Gamepad();
        pad.Poll();
        int before = pad.Buttons;
        var started = DateTime.UtcNow;
        capturing = "pad";
        padTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        padTimer.Tick += (_, _) =>
        {
            pad.Poll();
            int fresh = pad.Buttons & ~before;
            before &= pad.Buttons;
            bool esc = (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (fresh == 0 && !esc && (DateTime.UtcNow - started).TotalSeconds < 15) return;
            if (fresh != 0)
            {
                int bit = fresh & -fresh;
                if ((fresh & Gamepad.LeftTrigger) != 0) bit = Gamepad.LeftTrigger;
                if ((fresh & Gamepad.RightTrigger) != 0) bit = Gamepad.RightTrigger;
                s.HoldButton = bit;
                s.Save();
            }
            padTimer!.Stop();
            padTimer = null;
            capturing = null;
            WriteStatus();
        };
        padTimer.Start();
    }

    /// <summary>The layout browser's picture of one layout, drawn exactly like the settings window does,
    /// saved as hosted\layout-&lt;n&gt;.png.</summary>
    void RenderLayout(int number, int width, int height)
    {
        var layout = db.ByNumber(number);
        if (layout == null) return;
        width = Math.Clamp(width, 200, 2000); height = Math.Clamp(height, 150, 1500);
        var view = new OverlayView(db, s) { Preview = true, Width = width, Height = height };
        view.Frame = new OverlayFrame(new OpenCvSharp.Rect(0, 0, width, height), true, layout, null, null, false, string.Join(" / ", layout.Difficulties), "");
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        var path = Path.Combine(Dir, $"layout-{number}.png");
        using (var f = File.Create(path + ".tmp")) encoder.Save(f);
        File.Move(path + ".tmp", path, true);
    }

    // ---- What aLogs shows

    void WriteInfo()
    {
        var v = typeof(App).Assembly.GetName().Version;
        var info = new
        {
            version = v?.ToString(3),
            gameDataVersion = db.GameDataVersion,
            difficulties = db.Difficulties.Select(d => new { value = d, name = Lang.Difficulty(d) }),
            styles = OverlayView.MiniMapStyles.Select(m => new { value = m.Value, name = Lang.T(m.Name), description = Lang.T(m.Description) }),
            languages = Lang.Languages.Select(l => new { code = l.Code, name = l.Name }),
            gameLanguage = Lang.NameOf(Lang.GameCode),
            layouts = db.Layouts.Where(l => l.Difficulties.Count > 0).OrderBy(l => l.Number).Select(l => new
            {
                number = l.Number,
                title = l.DisplayTitle,
                compass = Lang.T(l.Compass),
                difficulties = l.Difficulties.Select(Lang.Difficulty),
                keyRooms = l.KeyRooms.Count(),
                nests = l.NestCount,
            }),
        };
        WriteJson("info.json", info);
    }

    void WriteStatus()
    {
        var layout = engine.CurrentLayout;
        var status = new
        {
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            pid = Environment.ProcessId,
            gameFound = engine.GameFound,
            inRun = engine.GameFound && engine.InRun,
            difficulty = engine.EffectiveDifficulty is string d ? Lang.Difficulty(d) : null,
            difficultySource = s.Difficulty == "Auto" ? (engine.DetectedDifficulty != null ? "party screen" : "open the party screen once") : "set by hand",
            layout = layout == null ? null : new { number = layout.Number, title = layout.DisplayTitle, compass = Lang.T(layout.Compass), source = Lang.T(engine.LayoutSource) },
            keyName = KeyHook.Name(s.HoldKey),
            padName = Lang.T(Gamepad.Name(s.HoldButton)),
            capturing,
            ocrLanguage = engine.OcrLanguage,
            ocrMissingLanguage = engine.OcrMissingLanguage,
            windowCapture = engine.UsingWindowCapture,
            sharedCapture = SharedCapture.InUse,
            lastMatch = engine.LastMatchInfo,
        };
        WriteJson("status.json", status);
    }

    static void WriteJson(string name, object value)
    {
        try
        {
            var path = Path.Combine(Dir, name);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
            // The app may be reading the file at that moment ("Access to the path is denied"): try again shortly.
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(path + ".tmp", path, true); return; }
                catch (Exception) when (attempt < 5) { System.Threading.Thread.Sleep(20); }
            }
        }
        catch (Exception e) { Log.Write($"hosted {name}: {e.Message}"); }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* next tick */ }
    }

    public void Dispose()
    {
        timer.Stop();
        watcher.Dispose();
        hook.AnyKeyPressed -= OnAnyKey;
    }
}
