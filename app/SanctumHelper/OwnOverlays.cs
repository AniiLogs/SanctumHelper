using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace SanctumHelper;

/// <summary>Where Sanctum Helper's overlay windows are on screen (the map panel, the notification card), shared with
/// the aLogs app and meter (SharedCapture.PublishOverlays) so every reader of a picture of the screen covers them and
/// they can stay in screenshots and recordings. A spot just left is kept half a second: a picture being read can be a
/// little older than the move. Published on every change and once a second (a stale block means we closed).</summary>
public static class OwnOverlays
{
    static readonly object gate = new();
    static readonly Dictionary<string, Rect> current = new();
    static readonly List<(Rect Spot, long Until)> left = new();
    static long published;

    public static void Set(string who, Rect? spot)
    {
        lock (gate)
        {
            long now = Environment.TickCount64;
            current.TryGetValue(who, out var was);
            bool had = current.ContainsKey(who);
            if (spot is Rect s && s.Width > 0 && s.Height > 0)
            {
                if (had && was == s) return;
                if (had) left.Add((was, now + 500));
                current[who] = s;
            }
            else
            {
                if (!had) return;
                left.Add((was, now + 500));
                current.Remove(who);
            }
            Publish(now);
        }
    }

    /// <summary>Called often (each picture): publishes again once a second, and drops spots left long enough ago.</summary>
    public static void Tick()
    {
        lock (gate)
        {
            long now = Environment.TickCount64;
            if (now - published > 1000 || left.Any(l => l.Until < now)) Publish(now);
        }
    }

    static void Publish(long now)
    {
        left.RemoveAll(l => l.Until < now);
        published = now;
        if (!SharedCapture.AppPresent) return;
        SharedCapture.PublishOverlays(SharedCapture.OwnerSanctum, current.Values.Concat(left.Select(l => l.Spot)).ToList());
    }
}
