using System.Diagnostics;

namespace VintageVoxel;

/// <summary>
/// Lightweight frame profiler. Place <see cref="Begin"/> / <see cref="End"/> pairs
/// anywhere in the codebase to time named sections. Results are accumulated per frame
/// and displayed in the F3 debug overlay.
///
/// Usage:
///   Profiler.Begin("ChunkMeshing");
///   // ... code to measure ...
///   Profiler.End("ChunkMeshing");
/// </summary>
public static class Profiler
{
    // Per-section stopwatches (restarted each Begin call).
    private static readonly Dictionary<string, Stopwatch> _active = new();

    // Smoothed millisecond values (EMA).
    private static readonly Dictionary<string, double> _smoothed = new();

    // Raw value from the last completed frame — unsmoothed, shows every spike.
    private static readonly Dictionary<string, double> _raw = new();

    // Peak value: set to raw when raw > peak, then decays slowly toward smoothed.
    private static readonly Dictionary<string, double> _peak = new();

    // Ordered list of section names in first-seen order for stable display.
    private static readonly List<string> _order = new();

    private const double SmoothAlpha = 0.1;
    // Peak decays 5% per frame toward the smoothed value so it fades after a spike.
    private const double PeakDecayAlpha = 0.05;

    /// <summary>Start (or restart) the timer for <paramref name="name"/>.</summary>
    public static void Begin(string name)
    {
        if (!_active.TryGetValue(name, out var sw))
        {
            sw = new Stopwatch();
            _active[name] = sw;
            _order.Add(name);
        }
        sw.Restart();
    }

    /// <summary>Stop the timer for <paramref name="name"/> and record the elapsed time.</summary>
    public static void End(string name)
    {
        if (!_active.TryGetValue(name, out var sw)) return;
        sw.Stop();

        double ms = sw.Elapsed.TotalMilliseconds;
        _raw[name] = ms;

        _smoothed[name] = _smoothed.TryGetValue(name, out double prev)
            ? prev + (ms - prev) * SmoothAlpha
            : ms;

        // Peak: latch on new maximum, otherwise drift back toward smoothed.
        double smoothed = _smoothed[name];
        _peak[name] = _peak.TryGetValue(name, out double peak)
            ? ms > peak ? ms : peak + (smoothed - peak) * PeakDecayAlpha
            : ms;
    }

    /// <summary>
    /// Section names in first-seen order.
    /// Read by <see cref="DebugWindow"/> each frame.
    /// </summary>
    public static IReadOnlyList<string> Sections => _order;

    /// <summary>Smoothed (EMA) elapsed time in milliseconds.</summary>
    public static double GetMs(string name) =>
        _smoothed.TryGetValue(name, out double v) ? v : 0.0;

    /// <summary>Raw elapsed time from the last frame — shows every spike immediately.</summary>
    public static double GetRawMs(string name) =>
        _raw.TryGetValue(name, out double v) ? v : 0.0;

    /// <summary>Decaying peak: sticks at the highest seen value then slowly fades back.</summary>
    public static double GetPeakMs(string name) =>
        _peak.TryGetValue(name, out double v) ? v : 0.0;
}
