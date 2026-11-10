using System;
using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace SanctumHelper;

/// <summary>Hands this app's notices ("Chaos Mode chosen.") to the aLogs DPS meter, which shows every module's notices
/// in one style, in the spot and with the motion the player picked (General Settings › Notifications), one after
/// another instead of two cards on top of each other. Only while the meter's overlay runs (its status.json says
/// "notices": true and was written in the last few seconds); otherwise this app shows its own (ToastWindow).
/// Each notice is a small JSON file in %LOCALAPPDATA%\aLogs\meter\notices, which the meter reads and deletes.</summary>
public static class SharedNotices
{
    static readonly string MeterData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aLogs", "meter");
    static readonly string Status = Path.Combine(MeterData, "status.json");
    static readonly string Folder = Path.Combine(MeterData, "notices");
    static int sequence;

    /// <summary>True when the meter took the notice; false to show it here.</summary>
    public static bool TryHandOff(string text, Color accent, OpenCvSharp.Rect client)
    {
        try
        {
            var info = new FileInfo(Status);
            // the meter writes its status about once a second: an older one is a meter that's gone
            if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromSeconds(5)) return false;
            using (var doc = JsonDocument.Parse(File.ReadAllText(Status)))
                if (!doc.RootElement.TryGetProperty("notices", out var takes) || takes.ValueKind != JsonValueKind.True) return false;
            Directory.CreateDirectory(Folder);
            var notice = new
            {
                head = text,
                detail = "Sanctum Helper",
                accent = $"#{accent.R:x2}{accent.G:x2}{accent.B:x2}",
                screen = client.Width > 0 ? new[] { client.X, client.Y, client.Width, client.Height } : null,
                from = "sanctum",
            };
            // written whole, then renamed: the meter never reads half a file; the name keeps them in order
            string name = $"{DateTime.UtcNow.Ticks:D20}-{System.Threading.Interlocked.Increment(ref sequence):D4}-sanctum";
            string tmp = Path.Combine(Folder, name + ".tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(notice));
            File.Move(tmp, Path.Combine(Folder, name + ".json"));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
