using System;
using System.Collections.Generic;
using Avalonia;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Deterministic synthetic sparkline generator used by the Sniper terminal.
/// Mirrors the reference mock: a seeded sine wave, gently biased by the
/// direction of the recent price move so up-trends visually drift upward.
/// Points are emitted in screen space (y grows downward) sized to the caller's box.
/// </summary>
public static class SniperSparklines
{
    /// <summary>Small inline row sparkline (default 90×22, 16 points) matching the mock table.</summary>
    public static IList<Point> Row(string seedText, double changePercent)
        => Build(seedText, changePercent, points: 16, stepX: 6d, height: 22d, amplitude: 4d);

    /// <summary>Larger micro-trend used in the detail panel (300×90, 16 points).</summary>
    public static IList<Point> Micro(string seedText, double changePercent)
        => Build(seedText, changePercent, points: 16, stepX: 20d, height: 90d, amplitude: 16d);

    private static IList<Point> Build(string seedText, double changePercent, int points, double stepX, double height, double amplitude)
    {
        int seed = 0;
        foreach (var ch in seedText ?? string.Empty)
        {
            seed = ((seed * 31) + ch) & 0x7fffffff;
        }

        var pts = new List<Point>(points);
        double mid = height / 2d;
        double v = mid + ((seed % 5) - 2) * (amplitude / 4d);
        double drift = changePercent >= 0 ? amplitude * 0.09d : -amplitude * 0.09d;

        for (int i = 0; i < points; i++)
        {
            v += Math.Sin((i * 0.7d) + seed) * (amplitude * 0.6d) + drift;
            v = Math.Max(2d, Math.Min(height - 2d, v));
            // Screen space: higher value = higher on screen = smaller Y.
            pts.Add(new Point(i * stepX, height - v));
        }

        return pts;
    }
}
