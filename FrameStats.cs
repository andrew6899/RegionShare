using System.Diagnostics;

namespace RegionShare;

/// Frame pacing counters. Everything here is touched only from the capture thread, so no locking.
/// A line lands in the log every 10 s while a region is live — that's what tells you whether choppy
/// motion is us dropping frames or something downstream (Teams' encoder, the network).
sealed class FrameStats
{
    const double WindowSeconds = 10;

    long _arrived, _throttled, _busy, _presented;
    long _windowStart = Stopwatch.GetTimestamp();
    long _lastPresent;
    double _gapMin = double.MaxValue, _gapMax;
    int _gapCount;
    double _gapSum;

    public void Arrived() => _arrived++;
    public void Throttled() => _throttled++;
    public void Busy() => _busy++;

    public void Presented()
    {
        _presented++;
        long now = Stopwatch.GetTimestamp();
        if (_lastPresent != 0)
        {
            double ms = (now - _lastPresent) * 1000.0 / Stopwatch.Frequency;
            _gapMin = Math.Min(_gapMin, ms);
            _gapMax = Math.Max(_gapMax, ms);
            _gapSum += ms;
            _gapCount++;
        }
        _lastPresent = now;
    }

    public void MaybeLog()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (elapsed < WindowSeconds) return;
        if (_arrived > 0)
        {
            // Spacing tells you more than the average: steady 16.7 ms is smooth, alternating 16/33 is judder.
            string gaps = _gapCount > 0
                ? $"gap avg {_gapSum / _gapCount:F1} ms (min {_gapMin:F1}, max {_gapMax:F1})"
                : "no gaps measured";
            Log.Info($"frames: {_arrived / elapsed:F1}/s in, {_presented / elapsed:F1}/s out, " +
                     $"dropped {_throttled} throttle + {_busy} busy, {gaps}");
        }
        _arrived = _throttled = _busy = _presented = 0;
        _gapCount = 0; _gapSum = 0; _gapMin = double.MaxValue; _gapMax = 0;
        _windowStart = now;
    }
}
