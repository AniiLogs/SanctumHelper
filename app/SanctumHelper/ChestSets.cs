using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SanctumHelper;

/// <summary>Dev switch chest-sets: which of a room's chest sets a run can still have. The game's data gives a room one fixed set
/// of chests per grade (the G1 to G6 in a marker's group), and a run hands a room at most one grade, so the map's chest marks in
/// such a room are several sets on top of each other. Two things cut them down: the grades the difficulty hands out at all
/// (layouts.json "grades", from the game's reward table), and a chest seen at a spot, which only the sets with a chest there can
/// explain. Nothing here reads the screen: Saw is for a reader to call.</summary>
public static class ChestSets
{
    /// <summary>How far a chest seen may be from a set's chest and still be it, in metres.</summary>
    public const double Reach = 1.5;

    static readonly string[] Tiers = ["simple", "exquisite", "luxurious", "legendary"];
    static readonly Regex GradeIn = new(@"G([1-6])", RegexOptions.Compiled);

    /// <summary>Difficulty, then "room" or "hallway": the grades handed out. Set when layouts.json is loaded.</summary>
    public static Dictionary<string, Dictionary<string, List<int>>> Grades { get; set; } = [];

    /// <summary>The grade in a marker's group ("G4宝箱" is 4), or 0 for one that belongs to no graded set (key rooms, the boss room,
    /// nests, hidden-chest traps): those are always drawn.</summary>
    public static int Grade(string? group) => group != null && GradeIn.Match(group) is { Success: true } m ? m.Groups[1].Value[0] - '0' : 0;

    public static bool IsChest(Marker m) => Array.IndexOf(Tiers, m.Kind) >= 0;

    /// <summary>A layout's graded sets by room: room, then grade, then that set's chests.</summary>
    public static Dictionary<int, SortedDictionary<int, List<Marker>>> Of(Layout l)
    {
        var rooms = new Dictionary<int, SortedDictionary<int, List<Marker>>>();
        foreach (var m in l.Markers)
        {
            int g = Grade(m.Group);
            if (g == 0 || !IsChest(m)) continue;
            if (!rooms.TryGetValue(m.Room, out var sets)) rooms[m.Room] = sets = [];
            if (!sets.TryGetValue(g, out var chests)) sets[g] = chests = [];
            chests.Add(m);
        }
        return rooms;
    }

    /// <summary>The grades a difficulty hands this room, of the ones it has sets for; all of them when the difficulty isn't known.
    /// A room whose sets stop at G3 is a hallway (rooms have sets up to G6, and only hallways are handed G1). The difficulty may
    /// be several joined by " / " (a preview of a layout two difficulties share).</summary>
    public static List<int> Allowed(IEnumerable<int> has, string? difficulty)
    {
        var all = has.OrderBy(g => g).ToList();
        if (difficulty == null || all.Count == 0) return all;
        string kind = all[^1] <= 3 ? "hallway" : "room";
        var handed = new HashSet<int>();
        foreach (var name in difficulty.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Grades.TryGetValue(name, out var by) || !by.TryGetValue(kind, out var grades)) return all;   // a name the table lacks: nothing ruled out
            handed.UnionWith(grades);
        }
        var left = all.Where(handed.Contains).ToList();
        return left.Count > 0 ? left : all;
    }

    /// <summary>One run of one layout: what is still possible in each room.</summary>
    public sealed class Run
    {
        public int Layout { get; }
        public string? Difficulty { get; }
        /// <summary>Goes up when a sighting ruled something out, so a drawn map knows to redraw.</summary>
        public int Version { get; private set; }

        readonly Dictionary<int, SortedDictionary<int, List<Marker>>> sets;
        readonly Dictionary<int, List<int>> possible = [];
        readonly HashSet<int> seen = [];   // rooms where a chest was seen

        public Run(Layout l, string? difficulty)
        {
            Layout = l.Number; Difficulty = difficulty;
            sets = Of(l);
            foreach (var (room, by) in sets) possible[room] = Allowed(by.Keys, difficulty);
        }

        /// <summary>The grades a room can still have; empty for a room with no graded sets.</summary>
        public IReadOnlyList<int> Possible(int room) { lock (possible) return possible.TryGetValue(room, out var p) ? p.ToArray() : []; }

        /// <summary>One set left, and a chest of it was seen: that is the room's set.</summary>
        public bool Confirmed(int room) { lock (possible) return seen.Contains(room) && possible.TryGetValue(room, out var p) && p.Count == 1; }

        /// <summary>Should this marker be drawn? Yes unless it belongs to a graded set that is ruled out.</summary>
        public bool Shows(Marker m)
        {
            int g = Grade(m.Group);
            if (g == 0) return true;
            lock (possible) return !possible.TryGetValue(m.Room, out var p) || p.Contains(g);
        }

        /// <summary>A chest was seen at a spot (world x, z in metres), of a tier if that was read (simple, exquisite, luxurious,
        /// legendary). In each room with a possible set that has such a chest there, the sets without one are ruled out. False
        /// when no possible set has a chest there: then nothing changes, since the sighting or the position was wrong.</summary>
        public bool Saw(double x, double z, string? tier = null)
        {
            lock (possible)
            {
                bool any = false;
                foreach (var (room, by) in sets)
                {
                    var p = possible[room];
                    var fit = p.Where(g => by[g].Any(c => (tier == null || c.Kind == tier) && Math.Sqrt((c.World[0] - x) * (c.World[0] - x) + (c.World[1] - z) * (c.World[1] - z)) <= Reach)).ToList();
                    if (fit.Count == 0) continue;
                    any = true;
                    seen.Add(room);
                    if (fit.Count < p.Count) possible[room] = fit;
                    Version++;
                }
                return any;
            }
        }

        /// <summary>A chest's prompt was up with the player at this spot of the layout's picture: the chest is the nearest one
        /// of a set still possible, within 4 m (the prompt's reach plus the position's error). Null, and nothing changes, when
        /// none is that near or another spot is about as near: a second spot of the possible sets, or one of the chests in
        /// "others" (the layout's chests outside the graded sets), so it can't be told which one the prompt is for.</summary>
        public Marker? SawNear(double imgX, double imgY, double pxPerMetre, IEnumerable<Marker> others, double within = 4.0, double clear = 1.5)
        {
            double ppm = Math.Max(1e-6, pxPerMetre);
            double Away(Marker c) => Math.Sqrt((c.Img[0] - imgX) * (c.Img[0] - imgX) + (c.Img[1] - imgY) * (c.Img[1] - imgY)) / ppm;
            static double Apart(Marker a, Marker b) => Math.Sqrt((a.World[0] - b.World[0]) * (a.World[0] - b.World[0]) + (a.World[1] - b.World[1]) * (a.World[1] - b.World[1]));
            lock (possible)
            {
                var near = new List<(Marker m, double d)>();
                foreach (var (room, by) in sets)
                    foreach (int g in possible[room])
                        foreach (var c in by[g])
                            if (Away(c) is double d && d <= within + clear) near.Add((c, d));
                if (near.Count == 0) return null;
                var (best, bestD) = near.MinBy(n => n.d);
                if (bestD > within) return null;
                if (near.Any(n => n.d < bestD + clear && Apart(n.m, best) > Reach)) return null;
                if (others.Any(o => Away(o) < bestD + clear && Apart(o, best) > Reach)) return null;
                return Saw(best.World[0], best.World[1]) ? best : null;
            }
        }
    }

    static Run? current;

    /// <summary>The run for this layout and difficulty: the one going, or a new one if either differs.</summary>
    public static Run For(Layout l, string? difficulty)
    {
        var run = current;
        if (run == null || run.Layout != l.Number || run.Difficulty != difficulty) current = run = new Run(l, difficulty);
        return run;
    }

    /// <summary>A run ended or a layout was named: what was seen no longer counts.</summary>
    public static void Reset() => current = null;
}
