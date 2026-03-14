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
    /// <summary>Number of frames kept in the rolling history buffer for graphs.</summary>
    public const int HistoryLength = 300;

    // Per-section stopwatches (restarted each Begin call).
    private static readonly Dictionary<string, Stopwatch> _active = new();

    // Smoothed millisecond values (EMA).
    private static readonly Dictionary<string, double> _smoothed = new();

    // Raw value from the last completed frame — unsmoothed, shows every spike.
    private static readonly Dictionary<string, double> _raw = new();

    // Peak value: set to raw when raw > peak, then decays slowly toward smoothed.
    private static readonly Dictionary<string, double> _peak = new();

    // Rolling history ring buffers for each section (used by ImPlot graphs).
    private static readonly Dictionary<string, float[]> _history = new();
    private static readonly Dictionary<string, int> _historyOffset = new();

    // Global FPS / frame-time rolling history (written by the game loop).
    private static readonly float[] _fpsHistory = new float[HistoryLength];
    private static readonly float[] _frameTimeHistory = new float[HistoryLength];
    private static int _globalOffset;

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

        // Record into rolling history ring buffer.
        if (!_history.ContainsKey(name))
        {
            _history[name] = new float[HistoryLength];
            _historyOffset[name] = 0;
        }
        var buf = _history[name];
        var off = _historyOffset[name];
        buf[off] = (float)ms;
        _historyOffset[name] = (off + 1) % HistoryLength;
    }

    /// <summary>
    /// Record global FPS and frame time. Call once per frame from the game loop.
    /// </summary>
    public static void RecordFrame(float fps, float frameTimeMs)
    {
        _fpsHistory[_globalOffset] = fps;
        _frameTimeHistory[_globalOffset] = frameTimeMs;
        _globalOffset = (_globalOffset + 1) % HistoryLength;
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

    /// <summary>Rolling history buffer for a named section (ring buffer, use with <see cref="GetHistoryOffset"/>).</summary>
    public static float[] GetHistory(string name) =>
        _history.TryGetValue(name, out var buf) ? buf : Array.Empty<float>();

    /// <summary>Current write offset into the ring buffer for a named section.</summary>
    public static int GetHistoryOffset(string name) =>
        _historyOffset.TryGetValue(name, out int off) ? off : 0;

    /// <summary>Global FPS history ring buffer.</summary>
    public static float[] FpsHistory => _fpsHistory;

    /// <summary>Global frame-time history ring buffer.</summary>
    public static float[] FrameTimeHistory => _frameTimeHistory;

    /// <summary>Current write offset for global FPS / frame-time ring buffers.</summary>
    public static int GlobalOffset => _globalOffset;
}
