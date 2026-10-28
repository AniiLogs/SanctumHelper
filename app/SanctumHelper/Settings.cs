using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SanctumHelper;

public sealed class Settings
{
    /// <summary>Bumped when a default changes in a way old settings files should pick up.</summary>
    public int Version { get; set; } = CurrentVersion;
    const int CurrentVersion = 5;

    /// <summary>Virtual-key code held to show the overlay (default: Left Ctrl; Caps Lock would
    /// flip the caps state every time).</summary>
    public int HoldKey { get; set; } = 0xA2;
    /// <summary>When true the key toggles the overlay instead of having to be held.</summary>
    public bool ToggleMode { get; set; } = false;
    public double MapOpacity { get; set; } = 0.55;
    public double MarkerScale { get; set; } = 1.0;
    public bool ShowMapImage { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    /// <summary>Draw the camera's view cone from the player on the on-screen maps, like the game's minimap.</summary>
    public bool ShowViewCone { get; set; } = true;
    /// <summary>Caption on the on-screen maps (all but see-through): "Below", "Above" or "Off",
    /// and what it says.</summary>
    public string CaptionPosition { get; set; } = "Below";
    public bool CaptionLayout { get; set; } = true;
    public bool CaptionDifficulty { get; set; } = true;
    /// <summary>Read the game's own log (Player.log) to know at once whether the player is on the
    /// island or in the Sanctum, and when a new run starts. The minimap is the fallback.</summary>
    public bool UseGameLog { get; set; } = true;
    /// <summary>Small notices in the top-right corner ("Hard Mode found.", "Sanctum found #32").</summary>
    public bool ShowNotifications { get; set; } = true;
    /// <summary>A controller button that works like the overlay key (XInput bit, 0 = none).</summary>
    public int HoldButton { get; set; }
    /// <summary>The language the game is played in (how the party screen is read): "Auto" reads
    /// the game's own language setting, else a code from Lang.Languages ("en", "de_DE", ...).</summary>
    public string GameLanguage { get; set; } = "Auto";
    /// <summary>The app's own language (overlay, notices, settings): "Auto" = the game's language.</summary>
    public string Language { get; set; } = "Auto";
    public bool ShowPlayer { get; set; } = true;
    public Dictionary<string, bool> Layers { get; set; } = new(DefaultLayers);

    static readonly Dictionary<string, bool> DefaultLayers = new()
    {
        ["doors"] = true, ["nest"] = true, ["key"] = true, ["challenge"] = true, ["legendary"] = true,
        ["luxurious"] = true, ["exquisite"] = false, ["simple"] = false, ["pot"] = false, ["pile"] = false,
    };

    /// <summary>Overlay visible in recordings/screenshots (reads the game window directly).</summary>
    public bool RecordableOverlay { get; set; } = true;

    // On-screen map while the full map is closed: one of OverlayView.MiniMapStyles ("Corner",
    // "Disc", "Radar", "Compass", "Overlay" (Diablo-style) or "Off").
    public string MiniMapMode { get; set; } = "Corner";
    /// <summary>"TopRight", "TopCenter", or "OverGame" (experimental): the corner or round map drawn exactly over the
    /// game's own minimap, opaque. The game's minimap is still read underneath: window capture never sees overlays,
    /// and with screen pictures (Desktop Duplication) the overlay is hidden from capture.</summary>
    public string MiniMapCorner { get; set; } = "TopRight";
    /// <summary>Where the user dragged the on-screen map: its center as a share of the game's
    /// width and height (so it survives resolution changes). Null = the default spot.</summary>
    public double? MiniMapX { get; set; }
    public double? MiniMapY { get; set; }
    public double MiniMapSize { get; set; } = 300;      // 1080p pixels
    public double MiniMapZoom { get; set; } = 0.33;     // screen px per map-art px at 1080p (~2 px per metre)
    public double MiniMapOpacity { get; set; } = 0.85;
    public double OverlayZoom { get; set; } = 0.55;
    /// <summary>Big map (key held, in-game map closed): zoom and size as a share of screen height.</summary>
    public double BigMapZoom { get; set; } = 0.8;
    public double BigMapSize { get; set; } = 0.8;
    public double OverlayOpacity { get; set; } = 0.65;
    /// <summary>Experimental: guess the layout from the minimap at the start of a run, before
    /// the full map has been opened.</summary>
    /// <summary>Island (outside the Sanctum) tracking. Off: it misfired too often; kept for later.</summary>
    public bool IslandMap { get; set; } = true;

    /// <summary>"Auto" reads the party screen; otherwise a fixed difficulty name.</summary>
    public string Difficulty { get; set; } = "Auto";
    /// <summary>0 = detect from the map; otherwise force a layout number.</summary>
    public int ForcedLayout { get; set; } = 0;
    public bool TrackMinimap { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public string WindowTitle { get; set; } = "Aniimo";
    public bool SaveDebugFrames { get; set; } = false;

    /// <summary>The current run's layout, so a restart mid-run (e.g. after an update) doesn't
    /// need the map opened again. Forgotten after 25 minutes or on the party screen.</summary>
    public int RunLayout { get; set; }
    /// <summary>The game log's id for the run that layout belongs to (see GameLog).</summary>
    public string? RunId { get; set; }
    public DateTime RunLayoutTime { get; set; }

    // Screen regions as fractions of a 16:9 reference frame (1920x1080), see Regions.cs.
    public RegionF PartyGoalRegion { get; set; } = new(740, 6, 400, 54);
    /// <summary>Square centered on the minimap's player arrow (top-left of the HUD).</summary>
    public RegionF MinimapRegion { get; set; } = new(75, 32, 210, 210);

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SanctumHelper");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    /// <summary>The settings file as this process last read or wrote it (hosted mode ignores its own saves).</summary>
    public static string? LastJson { get; private set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                LastJson = json;
                var loaded = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                if (!json.Contains("\"Version\"")) loaded.Version = 1;
                // v1 defaulted to Caps Lock, which flips caps every press: move to Left Ctrl.
                if (loaded.Version < 2 && loaded.HoldKey == 0x14) loaded.HoldKey = 0xA2;
                // v3 redesigned the see-through map; move users off the old (too heavy) defaults.
                if (loaded.Version < 3) { loaded.OverlayZoom = 0.55; loaded.OverlayOpacity = 0.65; }
                // v4: island tracking back on after the outside-detection fix.
                if (loaded.Version < 4) loaded.IslandMap = true;
                // v5: the easiest difficulty is called "Normal", as in the game.
                if (loaded.Difficulty == "Beginner") loaded.Difficulty = "Normal";
                loaded.Version = CurrentVersion;
                return loaded;
            }
        }
        catch (Exception e) { Log.Write("settings load failed: " + e.Message); }
        return new Settings();
    }

    static readonly object saving = new();

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        // Saved from the engine and the window's threads: one at a time, and written whole (temp file, then
        // replaced), so a crash or a second save can't leave a cut-off file that loads as the defaults.
        lock (saving)
        {
            var json = JsonSerializer.Serialize(this, Json);
            LastJson = json;
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        // Hosted by the aLogs app, which starts with Windows itself.
        if (!Hosted.Active) ApplyStartup();
    }

    /// <summary>Layers added in later versions fall back to their default.</summary>
    public bool Layer(string kind) => Layers.TryGetValue(kind, out var on) ? on : DefaultLayers.GetValueOrDefault(kind);

    void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (StartWithWindows) key.SetValue("SanctumHelper", $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue("SanctumHelper", false);
        }
        catch (Exception e) { Log.Write("startup registry: " + e.Message); }
    }
}

/// <summary>A rectangle in 1920x1080 reference pixels.</summary>
public sealed record RegionF(double X, double Y, double W, double H);

public static class Log
{
    static readonly object Gate = new();
    public static string FilePath => Path.Combine(Settings.Dir, "log.txt");

    public static void Write(string msg)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
            catch { /* logging must never crash the app */ }
        }
    }
}
