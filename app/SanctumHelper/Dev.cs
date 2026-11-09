using System;
using System.Collections.Generic;
using System.Linq;

namespace SanctumHelper;

/// <summary>Beta-only test switches, shown on the Dev tab of aLogs' Sanctum Helper page. Each one turns on a proposed change, so it
/// can be tried alone. All off at every start, on purpose: a build can change what a switch does. The one exception is a switch
/// the player chose to keep (dev-keep.json): that starts on, through updates, until they unkeep it or it's marked ForcedOff here.
/// With them all off, Sanctum Helper works as it did in 0.1.36. Before a real release, the ones used become normal behaviour and
/// the rest, with this file, come out.</summary>
public static class Dev
{
    public const string NoRisk = "No known risk", LowRisk = "Low risk", HighRisk = "High risk";

    public sealed record Option(string Id, string Section, string Area, string Title, string What, string Gain, string Risk, string Tested, Action<bool> Set, bool ForcedOff = false);

    // Read every frame or fix from several threads; a plain field read is all that's needed.
    public static bool ArtStrips, GcThread, DuplicateTick, LabelCache,
        IslandTile, IslandFastMatch, IslandNear, SanctumNear, FloorHere, WideThrottle, IslandArtThread, ArrivalSlow, TimerNearby,
        RedrawSkip, GlideReset, MisfitFix, OldPanFix, LogLayout, LogIsland, LogCandidates, ExactZoom, MinimapBox, ReleaseOnLock,
        CornerMargin, MarkerCoast;

    const string Bench = "Bench tested", Compared = "Compared with the old pictures", Log = "Checked against a real game log", Untested = "Not tested yet";

    public static readonly IReadOnlyList<Option> Options =
    [
        new("art-strips", NoRisk, "Memory", "Load maps in strips", "Loads map images a strip at a time. Takes effect on the next map loaded.",
            "Island map load 827 to 445 ms; peak while loading all maps 722 to 482 MB.", "None found: all 51 images came out identical.", Bench, v => ArtStrips = v),
        new("gc-thread", NoRisk, "Smoothness", "Clean up memory off the tracking loop", "Frees memory on another thread, so tracking isn't held up meanwhile.",
            "Removes a pause; size unknown.", "None found.", Untested, v => GcThread = v),
        new("duplicate-tick", NoRisk, "CPU", "Ignore repeated display ticks", "The display clock can tick twice for one frame: the second is skipped.",
            "Unknown; may be nothing on some screens.", "None found.", Untested, v => DuplicateTick = v),
        new("label-cache", NoRisk, "CPU", "Build labels once", "Text on the map is laid out once instead of every frame.",
            "Unknown; probably the costliest part of a frame.", "Up to 16 pixels differ slightly, too little to see.", Compared, v => LabelCache = v),

        new("log-layout", LowRisk, "Accuracy", "Layout from the game's log", "The game's log names the Sanctum as it loads: used instead of working it out from pictures. Starts with the next Sanctum.",
            "Right at once, instead of 17-46 s, and no wrong layout (one run had the wrong one for about 4 minutes).", "If the log were ever wrong, it wouldn't correct itself until the run ended (not seen in 9 of 9). A patch could remove the line: then it works as with this off.", Log, v => LogLayout = v),
        new("log-candidates", LowRisk, "Memory", "Wait for the log before loading candidates", "Waits 1.5 s for the log's name before loading every possible layout. Use with Layout from the game's log.",
            "Avoids loading up to 30 layouts' maps when one is needed.", "If no name comes, identifying the layout starts its loading 1.5 s later.", Untested, v => LogCandidates = v),
        new("log-island", LowRisk, "Speed", "Island from the game's log", "The log names Egg Heist's island too, so a new run is known at once.",
            "About 3 s sooner.", "Low. A patch could remove the line: then it waits the 3 s as before.", Log, v => LogIsland = v),
        new("island-tile", LowRisk, "CPU", "Island: reuse a scaled piece of the map", "Scales a piece of the island once and reuses it, instead of for every read.",
            "With the next two: island reads about 20 to 2.2 ms.", "Low: positions could shift by under a pixel when a new piece is cut.", Bench, v => IslandTile = v),
        new("island-fast-match", LowRisk, "CPU", "Island: faster matching", "Uses the same faster match the Sanctum already has. Same answers within rounding.",
            "With the others: island reads about 20 to 2.2 ms.", "Low.", Bench, v => IslandFastMatch = v),
        new("island-near", LowRisk, "CPU", "Island: look near first", "Right after a good read, looks just around where you should be. Anything unclear looks wider as before.",
            "With the others: island reads about 20 to 2.2 ms.", "Could rarely take a look-alike spot nearby; no accuracy loss on the bench.", Bench, v => IslandNear = v),
        new("sanctum-near", LowRisk, "CPU", "Sanctum: look near first", "As above, inside a Sanctum.",
            "With Floors: Sanctum reads about 4.5 to 2.9 ms.", "Could rarely take a look-alike spot nearby; no accuracy loss on the bench.", Bench, v => SanctumNear = v),
        new("floor-here", LowRisk, "CPU", "Floors: compare at your spot only", "While your floor clearly matches, the other is compared only where you are. Floors share coordinates.",
            "With Sanctum look near: Sanctum reads about 4.5 to 2.9 ms. Floor changes still seen in about 100 ms.", "A floor change that also moves you far away is noticed once your floor stops matching.", Bench, v => FloorHere = v),
        new("wide-throttle", LowRisk, "Smoothness", "Limit long searches when lost", "Island: long searches when you're lost take at most a quarter of the time.",
            "Worst stall about 460 to 27 ms.", "A few more missed reads while lost (93 to 107, 85 to 87 of 480), and finding you could take slightly longer.", Bench, v => WideThrottle = v),
        new("island-art-thread", LowRisk, "Smoothness", "Load the island map off the tracking loop", "Tracking keeps running while the island map loads.",
            "Removes a pause of about a second, once a run.", "The first island position waits for the load.", Untested, v => IslandArtThread = v),
        new("arrival-slow", LowRisk, "CPU", "Check landing spots less often", "On arriving on the island, checks the landing spots about 20 times a second instead of about 38.",
            "About half the checks for about 2 s.", "Placed up to about 25 ms later.", Untested, v => ArrivalSlow = v),
        new("timer-nearby", LowRisk, "CPU", "Run timer: look where it was", "Checks the stopwatch's last spot first, then the whole area if it isn't there.",
            "Unknown; most in a run's last 3 minutes.", "Low: a miss falls back to the full search.", Untested, v => TimerNearby = v),
        new("redraw-skip", LowRisk, "CPU", "Fewer redraws standing still", "New readings that change nothing shown don't redraw the map.",
            "About 38 to about 4 redraws a second standing still; nothing while walking.", "Settings can take up to 250 ms to show.", Untested, v => RedrawSkip = v),
        new("glide-reset", LowRisk, "Smoothness", "No glide between maps", "Going between the island and a Sanctum, the marker starts from the new spot.",
            "Removes a short wrong glide.", "None found.", Untested, v => GlideReset = v),
        new("misfit-fix", LowRisk, "Accuracy", "Menu time isn't a layout mismatch", "Time in menus, on the map screen or on the island no longer counts toward \"hasn't matched for 30 s\".",
            "No lost layout after a long menu.", "A real mismatch that keeps being interrupted takes longer to catch.", Untested, v => MisfitFix = v),
        new("old-pan-fix", LowRisk, "Accuracy", "No old layout from the map screen", "The map screen's last picture is dropped with its run, so an old layout can't come back.",
            "Avoids a wrong map; how often is unknown.", "None found.", Untested, v => OldPanFix = v),
        new("exact-zoom", LowRisk, "Accuracy", "Exact minimap zoom", "Uses the zoom in the game's data for the first search, instead of trying 5 near it.",
            "A faster first search with fewer wrong matches.", "If the game's zoom differs on some screen, the first search could fail. Worst case: turn it off.", Untested, v => ExactZoom = v),
        new("minimap-box", LowRisk, "Accuracy", "Exact minimap position", "Moves the box read by 1.4 px at 1080p, to where the game's files put it.",
            "Removes up to about 2.3 map pixels of error.", "The value isn't checked on a real frame yet, so it could be worse. Watch whether the marker sits off.", Untested, v => MinimapBox = v),
        new("release-on-lock", LowRisk, "Memory", "Free other layouts once one is found", "Lets go of the candidates' memory as soon as the layout is locked by any route.",
            "Unknown.", "None found.", Untested, v => ReleaseOnLock = v),

        new("corner-margin", HighRisk, "CPU", "Smaller Corner map window", "Trims the transparent window around the Corner map.",
            "21% fewer pixels copied each frame at 1080p.", "A long caption could get clipped.", Untested, v => CornerMargin = v),
        new("marker-coast", HighRisk, "Smoothness", "Keep the marker moving after a missed read", "One missed read no longer pulls the marker back.",
            "Removes a pull-back of about 2.5 screen pixels.", "It may coast up to 0.15 s after you really stop.", Untested, v => MarkerCoast = v),
    ];

    /// <summary>Turns one on or off; false if there's no such switch.</summary>
    public static bool Set(string id, bool on)
    {
        var option = Options.FirstOrDefault(o => o.Id == id);
        if (option == null || (on && option.ForcedOff)) return false;
        option.Set(on);
        lock (Lit) { if (on) Lit.Add(id); else Lit.Remove(id); }
        SanctumHelper.Log.Write($"dev: {id} {(on ? "on" : "off")}");
        return true;
    }

    static readonly HashSet<string> Lit = [];

    // ---- Kept switches: the player's own choice to start one on. Only ever read on a beta (--dev from aLogs), so no real release
    // can run a switch from an earlier beta's choice.
    static readonly HashSet<string> Keeping = [];
    static string KeepFile => System.IO.Path.Combine(Settings.Dir, "dev-keep.json");

    /// <summary>At start: turns on what the player kept. A switch that's gone, or that the developer has forced off, is forgotten, so
    /// it takes a new choice to keep it again.</summary>
    public static void LoadKept()
    {
        string[] saved;
        try { saved = System.Text.Json.JsonSerializer.Deserialize<string[]>(System.IO.File.ReadAllText(KeepFile)) ?? []; }
        catch { return; }   // none yet, or unreadable: all off
        lock (Keeping) foreach (var id in saved) if (Options.Any(o => o.Id == id && !o.ForcedOff)) Keeping.Add(id);
        foreach (var id in Kept()) Set(id, true);
        if (Kept().Length != saved.Length) SaveKept();
    }

    /// <summary>Keeping turns it on now as well; unkeeping leaves it as it is until the next start.</summary>
    public static bool Keep(string id, bool keep)
    {
        var option = Options.FirstOrDefault(o => o.Id == id);
        if (option == null || (keep && option.ForcedOff)) return false;
        lock (Keeping) { if (keep) Keeping.Add(id); else Keeping.Remove(id); }
        if (keep) Set(id, true);
        SaveKept();
        SanctumHelper.Log.Write($"dev: {id} {(keep ? "kept" : "no longer kept")}");
        return true;
    }

    public static string[] Kept() { lock (Keeping) return Keeping.OrderBy(x => x).ToArray(); }

    static void SaveKept()
    {
        try
        {
            System.IO.File.WriteAllText(KeepFile + ".tmp", System.Text.Json.JsonSerializer.Serialize(Kept()));
            System.IO.File.Move(KeepFile + ".tmp", KeepFile, true);
        }
        catch (Exception e) { SanctumHelper.Log.Write("dev: saving kept switches: " + e.Message); }
    }

    /// <summary>The switches on now.</summary>
    public static string[] On() { lock (Lit) return Lit.OrderBy(x => x).ToArray(); }

    /// <summary>The minimap's box, where the game's files put it if that switch is on.</summary>
    public static RegionF Region(RegionF saved) => MinimapBox ? saved with { Y = saved.Y + 1.4 } : saved;
}
