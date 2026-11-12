using System;
using System.Collections.Generic;
using System.Linq;

namespace SanctumHelper;

/// <summary>Beta-only test switches, shown on the Dev tab of aLogs' Sanctum Helper page. Each one turns on a proposed change, so it
/// can be tried alone. All off at every start, on purpose: a build can change what a switch does. The one exception is a switch a
/// tester made persistent (dev-persistent.json): that starts on, through updates, until they undo it or its Revision is raised.
/// Persistent says nothing about what goes into a release. With them all off, Sanctum Helper works as it did in 0.1.36, plus the
/// tested ones already made normal behaviour. Before a real release, the ones chosen become normal behaviour and the rest, with
/// this file, come out.</summary>
public static class Dev
{
    public const string NoRisk = "No known risk", LowRisk = "Low risk", HighRisk = "High risk";

    public sealed record Option(string Id, string Section, string Area, string Title, string What, string Gain, string Risk, string Tested, Action<bool> Set, int Revision = 0);

    // Read every frame or fix from several threads; a plain field read is all that's needed.
    public static bool IslandTile, IslandFastMatch, IslandNear, SanctumNear, FloorHere, WideThrottle, IslandArtThread, ArrivalSlow, TimerNearby,
        RedrawSkip, GlideReset, MisfitFix, OldPanFix, LogLayout, LogIsland, LogCandidates, ExactZoom, MinimapBox, ReleaseOnLock,
        CornerMargin, MarkerCoast,
        ChallengeAsync, SkipRepeat, ArrowRetry, StaleSearch, HostedIo, HanningCache, ResizeSkip, EscNearby, WiderDisc,
        LazyArt, FreeIdle, GrabTime, PictureAge, LogThread,
        HostedScan, CompactMasks, ConeCheck, FloorRooms, BossRooms, DecodeThread, FasterArrow, RetainedMap,
        ExitSameDoor, ConeLog, RedrawCap, Facing30, Track20, OneWorker, RockLog, Stats, ChestRooms, ChestSearch;

    const string Bench = "Bench tested", Compared = "Compared with the old pictures", Log = "Checked against a real game log", Untested = "Not tested yet";

    public static readonly IReadOnlyList<Option> Options =
    [
        new("rock-log", NoRisk, "Accuracy", "Note the rocks the game names at each arrival", "After each area loads in an Egg Heist run, the game's own log names the rocks and props near the player. This writes those names to rocks.txt next to the helper's log, with the position the helper then settles on. Nothing shown changes and nothing reads it back yet.",
            "None yet: enough runs of this make a table of which rocks mean which landing or door, so an arrival could be placed from the game's log instead of from the screen.", "None found; a few short lines a run.", Untested, v => RockLog = v),
        new("dev-stats", NoRisk, "Accuracy", "Count what the readers did, once a minute", "Once a minute the log gets one line of counts and timings: pictures taken or skipped, position fixes and how many were lost, coasted or jumped, how often a look-nearby switch settled it, run timer and Esc button checks, and redraws. Nothing shown changes. Leave it on with and without another switch to compare the two.",
            "Shows from a log alone what a switch saved and whether it cost any reads.", "None found; one more log line a minute.", Untested, v => { Stats = v; Tally.Reset(); }),
        new("redraw-cap", LowRisk, "CPU", "Redraw the Corner map at most 60 times a second", "On a 120 Hz or faster screen the map was redrawn on every screen frame while you move or turn. With this on, every other frame is skipped.",
            "Unknown; about half the drawing work while moving on a 120 Hz screen. Nothing on a 60 Hz one.", "The map's glide may look less smooth on a fast screen.", Untested, v => RedrawCap = v),
        new("facing-30", LowRisk, "CPU", "Read facing 30 times a second", "The arrow and view cone are read from the minimap 30 times a second instead of 60, and the picture of the corner is asked for half as often. The map still turns smoothly between reads.",
            "Unknown; half the corner pictures taken from the game's screen, and half the arrow and cone reads.", "The arrow and cone may trail a fast camera turn by a few degrees more.", Untested, v => Facing30 = v),
        new("track-20", LowRisk, "CPU", "Fix the position 20 times a second", "The position is worked out from the minimap 20 times a second instead of about 40. The map still glides between fixes.",
            "Unknown; about half of the tracking's work (it measured about a third of one core).", "The marker may trail a sprint or a sharp turn slightly more.", Untested, v => Track20 = v),
        new("one-worker", LowRisk, "CPU", "Match pictures on one thread", "Picture matching stays on the helper's own low-priority thread instead of spreading over four cores at once.",
            "Unknown; no more short bursts on four cores at the same moment as the game.", "Naming a layout from the minimap and finding yourself on the whole island may take longer.", Untested, v => { OneWorker = v; try { OpenCvSharp.Cv2.SetNumThreads(v ? 1 : 4); } catch { /* no OpenCV in this mode */ } }),
        new("cone-log", NoRisk, "Accuracy", "Say how the view cone reads went", "Every 5 s in a run, the log says how many view cone reads were kept or dropped, how sure they were, how often the cone leapt, and how far the drawn cone sits from the latest reading. Nothing shown changes.",
            "Shows whether the view cone is right, and which cone switch helps, from a real run.", "None found; one more log line every 5 s in a run.", Untested, v => ConeLog = v),

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
            "A faster first search with fewer wrong matches.", "If the game's zoom differs on some screen, the first search could fail. Worst case: turn it off. On simulated stairs, 1 of 24 layouts missed a floor change with this on.", Untested, v => ExactZoom = v),
        new("minimap-box", LowRisk, "Accuracy", "Exact minimap position", "Moves the box read by 1.4 px at 1080p, to where the game's files put it.",
            "Removes up to about 2.3 map pixels of error.", "The value isn't checked on a real frame yet, so it could be worse. Watch whether the marker sits off.", Untested, v => MinimapBox = v),
        new("release-on-lock", LowRisk, "Memory", "Free other layouts once one is found", "Lets go of the candidates' memory as soon as the layout is locked by any route.",
            "Unknown.", "None found.", Untested, v => ReleaseOnLock = v),

        new("challenge-async", LowRisk, "Smoothness", "Read challenge text off the tracking loop", "The \"Victory Conditions\" check on the island's first 30 s runs on its own thread.",
            "Removes up to about 30 short pauses each run; size unknown.", "The map hides in a challenge one read later, about a second.", Untested, v => ChallengeAsync = v),
        // (Revision 1: the repeat is told apart before it is copied, not after. What gets read, and when, is as before.)
        new("skip-repeat", LowRisk, "CPU", "Skip pictures already read", "When the capture hands over the same picture again, it isn't copied again, and facing and position aren't worked out again.",
            "Most at low FPS: maybe 180 ms of CPU a second at 30 FPS; nothing at 60+. An estimate.", "Low: if a changed picture were ever seen as the same, an update would wait for the next one.", Untested, v => SkipRepeat = v, 1),
        new("arrow-retry", LowRisk, "Accuracy", "Retry a missed arrow read at once", "One missed arrow read no longer stops facing updates for half a second.",
            "Removes facing freezes of up to 0.5 s.", "Slightly more CPU right after a miss.", Untested, v => ArrowRetry = v),
        new("stale-search", LowRisk, "Accuracy", "Drop island searches from an old run", "A slow whole-island search that finishes after the run changed is thrown away.",
            "Avoids a wrong position; how often is unknown.", "None found.", Untested, v => StaleSearch = v),
        new("hosted-io", LowRisk, "Smoothness", "Write status files off the display thread", "Sanctum Helper's status files for aLogs are written on another thread, so the map's drawing doesn't wait.",
            "Removes hitches of up to about 80 ms.", "aLogs' status could show a moment later.", Untested, v => HostedIo = v),
        new("hanning-cache", LowRisk, "CPU", "Island: reuse the movement window", "A calculation window used for each island step is made once.",
            "Small; under 1 ms a read.", "None found.", Untested, v => HanningCache = v),
        new("resize-skip", LowRisk, "CPU", "Skip resizing at 1080p", "At 1080p the corner is already the right size, so it isn't copied to resize it.",
            "Small, and only at 1080p.", "None found.", Untested, v => ResizeSkip = v),
        new("esc-nearby", LowRisk, "CPU", "Esc button: look where it was", "Checks the Esc button's last spot first, then the whole corner if it isn't there.",
            "Small; only when the run timer isn't seen.", "Low: a miss falls back to the full search.", Untested, v => EscNearby = v),
        new("wider-disc", LowRisk, "Accuracy", "Read more of the minimap", "Uses a slightly larger circle of the Sanctum minimap, from the game's files. Use with Exact minimap position.",
            "More floor to match, so possibly better reads in corridors.", "The game's mask shape couldn't be read, so the edge could add noise and lower match scores.", Untested, v => WiderDisc = v),
        new("lazy-art", LowRisk, "Memory", "Load the island's extra images only if needed", "The island's gray and transparency images are made only if something asks for them. Takes effect on the next map loaded.",
            "About 50 MB held, and a lower peak while loading.", "A short load the first time the full island map needs them.", Untested, v => LazyArt = v),
        new("free-idle", LowRisk, "Memory", "Free map pictures when idle", "The overlay lets go of its map pictures 90 s after the map is hidden.",
            "About 40-60 MB while idle; an estimate.", "A short stall to reload them when the map next shows.", Untested, v => FreeIdle = v),
        new("hosted-scan", LowRisk, "Smoothness", "Look for aLogs' commands off the display thread", "The check 4 times a second for commands from aLogs looks in the folder on another thread.",
            "Removes a small wait for the disk from the map's drawing thread; size unknown.", "A command could run a moment later.", Untested, v => HostedScan = v),
        new("compact-masks", LowRisk, "Memory", "Keep one small mask per map floor", "Each floor keeps only the two yes/no answers matching uses, not its gray and transparency images. Takes effect on the next map loaded.",
            "8 to 4 MB per floor: all maps held 434 to 234 MB. The floors matched against are identical, byte for byte.", "When feature matching needs a layout's gray image, it's loaded again: a short stall, once a layout.", Bench, v => CompactMasks = v),
        new("cone-check", LowRisk, "Accuracy", "View cone: check its width", "Cone edges wider than the game's own cone (114 degrees, from its files) aren't trusted: the other reader is used.",
            "Unknown: there's no recording of real cones to measure on, and simulated ones showed no gain.", "If the width measured is off, a good reading could go to the less exact reader.", Untested, v => ConeCheck = v),
        new("floor-rooms", LowRisk, "CPU", "Floors: only where the game has them", "The upper floor is compared only in or near rooms that have one, from the game's files (the upper floor's art has none anywhere else).",
            "Outside those rooms, Sanctum reads 5.4 to 4.2 ms, and leaving one is seen at once, not after 100 ms. Nothing measurable on walks that stay in or near them.", "A position over 10 m off could miss a floor change until the read is lost; a patch moving rooms would need new data. Floor changes matched with this off on 24 simulated layouts.", Bench, v => FloorRooms = v),
        new("chest-sets", LowRisk, "Accuracy", "Chests: only the sets this run can have", "A room's chest marks are several fixed sets on top of each other, one per grade, and a run gives a room one of them at most. With this on, the sets the difficulty never hands out aren't drawn (Normal: rooms get grades 2 to 4, Hard 2 to 5), and the full map names the sets still possible in each room. Needs the difficulty to be known.",
            "Fewer chest marks that can't be there: no Legendary chance marks outside key rooms on Normal and Hard.", "The grades per difficulty are from the game's reward table and not checked against a real run: if it's read wrong, a chest that is there would have no mark.", Untested, v => ChestRooms = v),
        new("chest-prompt", LowRisk, "Accuracy", "Chests: confirm a set at the Search prompt", "Needs \"Chests: only the sets this run can have\". In a Sanctum, a small part right of the screen's centre is looked at about twice a second for the game's Search prompt. When it's up and you stand at a chest spot of the map, the room's sets without a chest there are taken off. English game text only.",
            "A room's chest marks drop to the one set it has once you reach its first chest: a chest's spot alone tells 64% of sets apart.", "One more small picture about twice a second in a Sanctum, and a text read each time a prompt comes up. The prompt was only seen saying Search with the character transformed. A position several metres off, or another floor's chest above or below, could take the wrong sets off: they come back next run. Not tried in a game.", Untested, v => ChestSearch = v),
        new("boss-rooms", LowRisk, "Accuracy", "Show boss rooms", "Boss rooms get a frame like key rooms (under the Challenge layer) and a mark on Radar and Compass, and their Legendary chest shows as certain, not as a chance. Layouts 31, 33, 38, 61, 62.",
            "The boss room can be seen, and that its chest is there.", "The game's data has that chest with no alternative, but spawned differently from every other Legendary: it may only appear once the boss is down. Not seen in a real run.", Compared, v => BossRooms = v),
        new("decode-thread", LowRisk, "Smoothness", "Load map pictures off the display thread", "A map's pictures load on another thread when its layout comes up. Until one is ready, only that picture is missing, for a frame or two.",
            "First draw of a map's see-through or Radar picture 62-137 to 5-21 ms, of the island 140 to 63 ms.", "The map's picture can appear a moment after its marks. Pictures came out identical once loaded; not tried in a game.", Compared, v => DecodeThread = v),
        new("faster-arrow", LowRisk, "CPU", "Faster arrow read", "Works out how well each turn of the arrow fits directly, around where it last was, instead of through frequency transforms. Wider if unsure, as before.",
            "Arrow reads 1.0 to 0.4 ms while it's being followed; the first read 2.9 to 2.3 ms.", "Same answers on all 4,800 simulated minimaps, but no real ones were recorded to try. An arrow that jumps over 2 px between reads could read a step off once.", Bench, v => FasterArrow = v),
        new("exit-same-door", LowRisk, "Accuracy", "Out by the door you went in by", "Remembers which island door you went into the Sanctum by (orange or blue; your spawn's orange door if that wasn't seen) and, when you come out, looks at all ten doors, not only the five blue ones: the door you went in by wins a near tie, and a weak match waits about a second for a better one. Going in by a blue door, the Sanctum map starts at its blue door too.",
            "Every exit in one player's logs (8 of 8) came out of the orange door they went in by and was placed at a blue door of another spawn, so the island map was wrong after each exit.", "Ten doors are looked at on arrival, not one, for up to a second longer. Revision 1: it used to look only at the door you went in by for the first second, and took an exit by another door on a weak match (0.44; real ones measure 0.55 to 0.78).", Log, v => ExitSameDoor = v, 1),

        new("grab-time", HighRisk, "Smoothness", "Time movement from when the picture was taken", "Your speed is worked out from when each picture was taken, not when its read finished.",
            "Steadier speed; size unknown.", "Changes how the marker feels.", Untested, v => GrabTime = v),
        new("picture-age", HighRisk, "Accuracy", "Use each picture's true age", "The marker leads by how old the picture really is. Up to 200 ms is counted.",
            "A more accurate marker; size unknown.", "Changes how the marker feels, and it could overshoot.", Untested, v => PictureAge = v),
        new("retained-map", HighRisk, "CPU", "Move the map without redrawing it", "Corner, Big and see-through maps: the map is drawn once and moved, and the arrow and cone turned, without recording the picture again. Round, Radar and Compass stay as they are.",
            "About 60 to 4 redraws a second while walking; how much CPU that saves in a game is unknown.", "A new way through the map's drawing, so something could stop updating; settings can take up to 250 ms to show. Pictures differ only in a few hundred faint pixels.", Compared, v => RetainedMap = v),
        new("log-thread", HighRisk, "Smoothness", "Write Sanctum Helper's log on its own thread", "Log lines are queued, so nothing waits on the file.",
            "Fewer small pauses; size unknown.", "The last lines could be lost in a crash, right when they're wanted.", Untested, v => LogThread = v),
        new("corner-margin", HighRisk, "CPU", "Smaller Corner map window", "Trims the transparent window around the Corner map.",
            "21% fewer pixels copied each frame at 1080p.", "A long caption could get clipped.", Untested, v => CornerMargin = v),
        new("marker-coast", HighRisk, "Smoothness", "Keep the marker moving after a missed read", "One missed read no longer pulls the marker back.",
            "Removes a pull-back of about 2.5 screen pixels.", "It may coast up to 0.15 s after you really stop.", Untested, v => MarkerCoast = v),
    ];

    /// <summary>Turns one on or off; false if there's no such switch.</summary>
    public static bool Set(string id, bool on)
    {
        var option = Options.FirstOrDefault(o => o.Id == id);
        if (option == null) return false;
        option.Set(on);
        lock (Lit) { if (on) Lit.Add(id); else Lit.Remove(id); }
        SanctumHelper.Log.Write($"dev: {id} {(on ? "on" : "off")}");
        return true;
    }

    static readonly HashSet<string> Lit = [];

    // ---- Persistent switches: a tester's choice to have one start on, so they needn't remember what they liked. Saved with the
    // switch's Revision. Changing what a switch does means raising its Revision, the one thing that turns a persistent switch off:
    // the tester can make it persistent again straight away. Only ever read on a beta (--dev from aLogs).
    static readonly Dictionary<string, int> Persistent = [];
    static string PersistFile => System.IO.Path.Combine(Settings.Dir, "dev-persistent.json");
    static string[] changed = [];

    /// <summary>At start: turns on what the tester made persistent. One that has changed since, or no longer exists, is let go.</summary>
    public static void LoadPersistent()
    {
        Dictionary<string, int> saved;
        try { saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(System.IO.File.ReadAllText(PersistFile)) ?? []; }
        catch { return; }   // none yet, or unreadable: all off
        var gone = new List<string>();
        foreach (var (id, revision) in saved)
        {
            var option = Options.FirstOrDefault(o => o.Id == id);
            if (option != null && option.Revision == revision) { lock (Persistent) Persistent[id] = revision; Set(id, true); }
            else { gone.Add(id); SanctumHelper.Log.Write($"dev: {id} no longer persistent: it changed in this build"); }
        }
        changed = gone.Where(id => Options.Any(o => o.Id == id)).ToArray();
        if (gone.Count > 0) SavePersistent();
    }

    /// <summary>Making one persistent turns it on now as well; undoing that leaves it as it is until the next start.</summary>
    public static bool SetPersistent(string id, bool persistent)
    {
        var option = Options.FirstOrDefault(o => o.Id == id);
        if (option == null) return false;
        lock (Persistent) { if (persistent) Persistent[id] = option.Revision; else Persistent.Remove(id); }
        if (persistent) Set(id, true);
        changed = changed.Where(x => x != id).ToArray();
        SavePersistent();
        SanctumHelper.Log.Write($"dev: {id} {(persistent ? "persistent" : "not persistent")}");
        return true;
    }

    public static string[] PersistentOnes() { lock (Persistent) return Persistent.Keys.OrderBy(x => x).ToArray(); }

    /// <summary>Those turned off at this start because they changed in this build, for the tab to say so.</summary>
    public static string[] ChangedOnes() => changed;

    static void SavePersistent()
    {
        try
        {
            string text;
            lock (Persistent) text = System.Text.Json.JsonSerializer.Serialize(Persistent);
            System.IO.File.WriteAllText(PersistFile + ".tmp", text);
            System.IO.File.Move(PersistFile + ".tmp", PersistFile, true);
        }
        catch (Exception e) { SanctumHelper.Log.Write("dev: saving persistent switches: " + e.Message); }
    }

    /// <summary>The switches on now.</summary>
    public static string[] On() { lock (Lit) return Lit.OrderBy(x => x).ToArray(); }

    /// <summary>The minimap's box, where the game's files put it if that switch is on.</summary>
    public static RegionF Region(RegionF saved) => MinimapBox ? saved with { Y = saved.Y + 1.4 } : saved;
}

/// <summary>Dev switch dev-stats: counts of what the readers did, for one line in the log a minute. Numbers only: how many and how
/// long. With the switch off nothing is counted and nothing is written.</summary>
public static class Tally
{
    public enum N
    {
        Picture, PictureNone, PictureSame, PictureSameCopied, Shrink, Timer, TimerNear, TimerLost, Esc, EscNear,
        Fix, FixNone, FixCoast, FixJump, FixJumpTaken, FixFloor, NearHit, NearMiss, Window, WindowKept, Island, IslandNone,
        MapOpen, BigMap, Frame, FrameNoDraw, Tick, TickTwice, TickCapped, Count
    }

    static readonly long[] counts = new long[(int)N.Count], ticks = new long[(int)N.Count];
    static long since = Environment.TickCount64;

    /// <summary>Now, for Time below; 0 with the switch off (and then Time does nothing).</summary>
    public static long Start => Dev.Stats ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

    public static void Add(N what) { if (Dev.Stats) System.Threading.Interlocked.Increment(ref counts[(int)what]); }

    public static void Time(N what, long start)
    {
        if (!Dev.Stats || start == 0) return;
        System.Threading.Interlocked.Increment(ref counts[(int)what]);
        System.Threading.Interlocked.Add(ref ticks[(int)what], System.Diagnostics.Stopwatch.GetTimestamp() - start);
    }

    /// <summary>A fresh start (the switch was just turned on or off).</summary>
    public static void Reset()
    {
        for (int i = 0; i < counts.Length; i++)
        {
            System.Threading.Interlocked.Exchange(ref counts[i], 0);
            System.Threading.Interlocked.Exchange(ref ticks[i], 0);
        }
        System.Threading.Interlocked.Exchange(ref since, Environment.TickCount64);
    }

    /// <summary>The line for the time since the last one, and a fresh start; null when nothing was read in it.</summary>
    public static string? Report()
    {
        long now = Environment.TickCount64;
        long seconds = Math.Max(1, (now - System.Threading.Interlocked.Exchange(ref since, now) + 500) / 1000);
        var c = new long[counts.Length];
        var ms = new double[counts.Length];
        long all = 0;
        for (int i = 0; i < c.Length; i++)
        {
            c[i] = System.Threading.Interlocked.Exchange(ref counts[i], 0);
            long t = System.Threading.Interlocked.Exchange(ref ticks[i], 0);
            ms[i] = c[i] > 0 ? t * 1000.0 / System.Diagnostics.Stopwatch.Frequency / c[i] : 0;
            all += c[i];
        }
        if (all == 0) return null;
        long C(N what) => c[(int)what];
        string T(N what) => C(what) == 0 ? "0" : FormattableString.Invariant($"{C(what)} x {ms[(int)what]:0.00} ms");
        return $"stats {seconds} s: pictures {C(N.Picture)} (none {C(N.PictureNone)}, same {C(N.PictureSame)}, same but copied {C(N.PictureSameCopied)})"
            + $" · shrink {T(N.Shrink)} · timer {T(N.Timer)} (nearby {C(N.TimerNear)}, lost {C(N.TimerLost)}) · Esc {T(N.Esc)} (nearby {C(N.EscNear)})"
            + $" · fixes {C(N.Fix)} (none {C(N.FixNone)}, coasted {C(N.FixCoast)}, jumps waited {C(N.FixJump)}, taken {C(N.FixJumpTaken)}, floor changes {C(N.FixFloor)})"
            + $" · near settled {C(N.NearHit)}, not {C(N.NearMiss)} · window kept {C(N.WindowKept)} of {C(N.Window)}"
            + $" · island {C(N.Island)} (none {C(N.IslandNone)}) · map open {T(N.MapOpen)} · big map {T(N.BigMap)}"
            + $" · frames drawn {C(N.Frame)}, left {C(N.FrameNoDraw)} · ticks {C(N.Tick)} (twice {C(N.TickTwice)}, capped {C(N.TickCapped)})";
    }
}
