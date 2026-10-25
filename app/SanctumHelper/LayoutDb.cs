using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SanctumHelper;

public sealed class Room
{
    public int Id { get; set; }
    public int Type { get; set; }
    public string Kind { get; set; } = "";
    public string Prefab { get; set; } = "";
    /// <summary>Key rooms: side in metres, the square in map-image px (x0, y0, x1, y1) and the locked door.</summary>
    public int? Size { get; set; }
    public double[]? ImgRect { get; set; }
    public Door? Door { get; set; }
    public double[] World { get; set; } = [];
    public double[] Img { get; set; } = [];
}

public sealed class Door
{
    public double[] World { get; set; } = [];
    public double[] Img { get; set; } = [];
    /// <summary>Which key opens it: golden, purple or blue.</summary>
    public string Color { get; set; } = "";
}

public sealed class Marker
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public int Room { get; set; }
    public double[] World { get; set; } = [];
    public double[] Img { get; set; } = [];
}

public sealed class WorldToImage
{
    public double Ox { get; set; }
    public double Oy { get; set; }
    public double Sx { get; set; }
    public double Sz { get; set; }
}

public sealed class DoorVector
{
    public double Angle { get; set; }
    public double Distance { get; set; }
}

public sealed class Layout
{
    public int Scene { get; set; }
    [JsonPropertyName("layout")] public int Number { get; set; }
    /// <summary>"island" for the team-mode island around the Sanctum; empty for Sanctum layouts.</summary>
    public string Kind { get; set; } = "";
    [JsonIgnore] public bool IsIsland => Kind == "island";
    public List<string> Difficulties { get; set; } = [];
    public string Image { get; set; } = "";
    public List<string> Layers { get; set; } = [];
    public int[] ImageSize { get; set; } = [2048, 2048];
    public WorldToImage WorldToImage { get; set; } = new();
    public DoorVector DoorVector { get; set; } = new();
    public List<Room> Rooms { get; set; } = [];
    public List<Marker> Markers { get; set; } = [];

    public Room Orange => Rooms.First(r => r.Kind == "orange");
    public Room Blue => Rooms.First(r => r.Kind == "blue");
    public bool HasDoors => Rooms.Any(r => r.Kind == "orange") && Rooms.Any(r => r.Kind == "blue");
    /// <summary>Map-art pixels per metre (6.1 in the Sanctum, 4.0 on the island).</summary>
    public double PxPerMetre => Math.Abs(WorldToImage.Sx);
    public IEnumerable<Room> KeyRooms => Rooms.Where(r => r.Kind == "key");
    public int NestCount => Markers.Count(m => m.Kind == "nest");

    public string Title => IsIsland ? "Egg Heist island" : $"Layout {Number}";
    /// <summary>The title in the app's language, for the UI.</summary>
    public string DisplayTitle => IsIsland ? Lang.T("Egg Heist island") : Lang.F("Sanctum {0}", Number);
    public string Compass
    {
        get
        {
            string[] d = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
            return d[(int)Math.Floor((DoorVector.Angle + 22.5) / 45) % 8];
        }
    }
}

public sealed class LayoutDb
{
    public string GameDataVersion { get; set; } = "";
    public List<string> Difficulties { get; set; } = [];
    public List<Layout> Layouts { get; set; } = [];

    [JsonIgnore] public string Root { get; private set; } = "";

    public static LayoutDb Load(string assetsDir)
    {
        var json = File.ReadAllText(Path.Combine(assetsDir, "layouts.json"));
        var db = JsonSerializer.Deserialize<LayoutDb>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                 ?? throw new InvalidDataException("layouts.json is empty");
        db.Root = assetsDir;
        return db;
    }

    public string ImagePath(Layout l) => Path.Combine(Root, l.Image.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Floor 0 = the ground floor image, 1.. = upper-floor images (same coordinates).</summary>
    public string FloorPath(Layout l, int floor) =>
        floor == 0 ? ImagePath(l) : Path.Combine(Root, l.Layers[floor - 1].Replace('/', Path.DirectorySeparatorChar));

    public static int FloorCount(Layout l) => 1 + l.Layers.Count;

    /// <summary>Layouts the game can draw for a difficulty (all team-mode layouts when unknown).</summary>
    public List<Layout> Candidates(string? difficulty) =>
        Layouts.Where(l => l.Difficulties.Count > 0 && (difficulty == null || l.Difficulties.Contains(difficulty))).ToList();

    public Layout? ByNumber(int n) => Layouts.FirstOrDefault(l => l.Number == n);

    public Layout? Island => Layouts.FirstOrDefault(l => l.IsIsland);
}
