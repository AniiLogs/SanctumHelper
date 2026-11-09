using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SanctumHelper;

/// <summary>Where the game says the player is, from its own text log (Player.log in LocalLow,
/// which Unity writes for diagnostics). Read-only and shared, like opening it in Notepad: the game
/// process itself is never touched.
///
/// Each area the player loads into is logged as "createVoxelScene new>>>id:..." right after a
/// connect to that area's server: "singleworld" is the homeland, "line" the lobby, "dungeon" an
/// Egg Heist instance. Inside a run the island and the Sanctum keep their ids across visits
/// (measured: island at16yzk.. three times, Sanctum at168Dk.. twice), and a run starts outside,
/// so the first dungeon area of a run is the island and the other one the Sanctum. The log shows
/// each switch the moment loading starts, 3-11 s before the minimap does.</summary>
public sealed class GameLog
{
    public enum Place { Unknown, Elsewhere, Island, Sanctum, Homeland }

    public Place Where { get; private set; } = Place.Unknown;
    /// <summary>The island id of the current run (changes with every run).</summary>
    public string? RunId => islandId;
    public bool Available { get; private set; }

    /// <summary>Raised from Poll on each change seen live (not while catching up at startup):
    /// the new place, and whether it began a new run.</summary>
    public event Action<Place, bool>? Changed;

    static readonly Regex Connect = new(@"connect to worldx-[a-z]+-[a-z]+-([a-z]+)-", RegexOptions.Compiled);
    bool interlink;               // in Interlink's or Holo-Battle Sim's dungeon rooms (see Line)
    // A dungeon area that would start a new run waits here until the game has had time to name an Interlink or
    // Holo-Battle Sim scene (it does ~1 s after the area loads): each of their rooms used to count as an Egg Heist
    // island until then, and a whole Holo-Battle Sim visit for good.
    string? pendingId;
    DateTime pendingAt;
    static readonly TimeSpan PendingFor = TimeSpan.FromSeconds(3);
    static readonly Regex Scene = new(@"createVoxelScene new>>>id:(\S+)", RegexOptions.Compiled);
    // A homeland is "100-<player id>-550" (anyone's): nothing is read there for now (it is heavy on the game already)
    static readonly Regex HomelandId = new(@"^\d+-\d+-550$", RegexOptions.Compiled);

    readonly string path;
    long pos;
    readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    readonly StringBuilder partial = new();
    string? server, islandId, sanctumId;
    bool catchingUp = true;

    public GameLog(string? logPath = null)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        path = logPath ?? Path.Combine(profile, "AppData", "LocalLow", "Aniimo", "Aniimo", "Player.log");
    }

    /// <summary>Reads what the game added since last time (the whole log the first time, to
    /// know where a run already in progress stands). Cheap: a few KB per call.</summary>
    public void Poll()
    {
        try
        {
            if (!File.Exists(path)) { Available = false; return; }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < pos)
            {
                // The game restarted and began a new log.
                pos = 0; partial.Clear(); decoder.Reset();
                server = islandId = sanctumId = pendingId = null;
                Set(Place.Unknown, false);
            }
            Available = true;
            if (pos == 0 && fs.Length > 8 << 20) pos = CatchUpStart(fs);
            if (fs.Length == pos) { catchingUp = false; Settle(); return; }
            fs.Seek(pos, SeekOrigin.Begin);
            var bytes = new byte[Math.Min(fs.Length - pos, 4 << 20)];
            int n = fs.Read(bytes, 0, bytes.Length);
            pos += n;
            var chars = new char[decoder.GetCharCount(bytes, 0, n)];
            decoder.GetChars(bytes, 0, n, chars, 0);
            partial.Append(chars);
            var text = partial.ToString();
            int last = text.LastIndexOf('\n');
            if (last < 0) return;
            partial.Clear().Append(text, last + 1, text.Length - last - 1);
            foreach (var line in text.AsSpan(0, last).ToString().Split('\n'))
                Line(line);
            if (pos >= fs.Length) catchingUp = false;
            Settle();
        }
        catch (IOException) { /* the game is writing; next time */ }
        catch (UnauthorizedAccessException) { Available = false; }
    }

    /// <summary>Where to start reading a long log (it reached 300 MB in a day's play): the last
    /// connect to a server that isn't an Egg Heist instance, i.e. where the current run (if any)
    /// began. Found by searching backwards from the end, at most 256 MB.</summary>
    static long CatchUpStart(FileStream fs)
    {
        const int Block = 4 << 20;
        var buf = new byte[Block + 256];
        var marker = Encoding.ASCII.GetBytes("connect to worldx-");
        long end = fs.Length;
        for (long stop = Math.Max(0, fs.Length - (256L << 20)); end > stop; end -= Block)
        {
            long start = Math.Max(stop, end - Block);
            int len = (int)Math.Min(buf.Length, fs.Length - start);
            fs.Seek(start, SeekOrigin.Begin);
            len = fs.Read(buf, 0, len);
            for (int i = len - marker.Length - 64; i >= 0; i--)
            {
                if (buf[i] != (byte)'c' || !buf.AsSpan(i, marker.Length).SequenceEqual(marker)) continue;
                var server = Encoding.ASCII.GetString(buf, i + marker.Length, 48);
                if (server.Contains("-dungeon-")) continue;
                // Back to the start of that line.
                int s = i;
                while (s > 0 && buf[s - 1] != (byte)'\n') s--;
                return start + s;
            }
        }
        return Math.Max(0, fs.Length - (16L << 20));
    }

    void Line(string line)
    {
        if (line.Contains("connect to worldx-") && Connect.Match(line) is { Success: true } c)
        {
            server = c.Groups[1].Value; // ms (login), line, singleworld, dungeon, ...
            // Any new server ends the rooms below: leaving them can log no connect at all, so the next one may be a dungeon
            // (8 Nov: an Interlink run, back with no connect, then a whole Egg Heist run read as elsewhere). If it's another
            // of those rooms, it names itself again before the wait for a new run is over.
            interlink = false;
            if (server != "dungeon") pendingId = null;
            return;
        }
        // Interlink rooms and Holo-Battle Sim are dungeon areas too: the game names their scene (".._fb_bossrush..",
        // ".._fb_roguelike..") about a second after the area loads. From then until the next server connect,
        // nothing is Egg Heist.
        if (line.Contains("fb_bossrush") || line.Contains("fb_roguelike"))
        {
            pendingId = null;
            if (!interlink) { interlink = true; islandId = sanctumId = null; Set(Place.Elsewhere, false); }
            return;
        }
        if (!line.Contains("createVoxelScene") || Scene.Match(line) is not { Success: true } m) return;
        string id = m.Groups[1].Value;
        if (server != "dungeon" || interlink)
        {
            islandId = sanctumId = pendingId = null;
            Set(HomelandId.IsMatch(id) ? Place.Homeland : Place.Elsewhere, false);
            return;
        }
        // Another area loaded with no Interlink or Holo-Battle Sim scene named in between (they're named within a
        // second): the waiting one was Egg Heist's island. (Reading the log back at startup, there's no waiting.)
        if (pendingId != null)
        {
            if (id == pendingId) return;
            Settle(force: true);
        }
        if (islandId == null || (id != islandId && id != sanctumId && sanctumId != null))
        {
            // First area of a run (runs start outside), or a third id: a new run, once no Interlink or Holo-Battle
            // Sim scene follows (Settle). Until then the player is elsewhere.
            if (pendingId != id) { pendingId = id; pendingAt = DateTime.UtcNow; }
            Set(Place.Elsewhere, false);
        }
        else if (id == islandId) Set(Place.Island, false);
        else { sanctumId = id; Set(Place.Sanctum, false); }
    }

    /// <summary>The screen showed what Egg Heist never has (a challenge's "Victory Conditions" panel): this dungeon area
    /// isn't Egg Heist after all. A Pathfinder challenge loads like an island and names no scene in the log (7 Nov:
    /// the island map popped up over one). Like an Interlink room, nothing is Egg Heist until the next server
    /// connect.</summary>
    public void NotEggHeist()
    {
        pendingId = null;
        interlink = true;
        islandId = sanctumId = null;
        Set(Place.Elsewhere, false);
    }

    /// <summary>A waiting dungeon area that no Interlink or Holo-Battle Sim scene followed: an Egg Heist run's island.</summary>
    void Settle(bool force = false)
    {
        if (pendingId == null || (!force && DateTime.UtcNow - pendingAt < PendingFor)) return;
        islandId = pendingId; sanctumId = null; pendingId = null;
        Set(Place.Island, true);
    }

    void Set(Place p, bool newRun)
    {
        if (p == Where && !newRun) return;
        Where = p;
        if (!catchingUp) Changed?.Invoke(p, newRun);
    }
}
