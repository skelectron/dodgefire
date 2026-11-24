using System.Diagnostics;

namespace Crossfire.App;

/// <summary>Prints frame/sim timing and managed allocation stats to stdout every 5 seconds (--perf-log).</summary>
internal sealed class PerfLog
{
    private readonly Stopwatch _window = Stopwatch.StartNew();
    private int _frames, _simFrames;
    private double _frameSum, _frameMax, _simSum, _simMax;
    private long _allocSum, _allocMax;
    private int _gen0 = GC.CollectionCount(0);
    private int _report;

    public void Record(double frameDt, double simMs, long alloc, int bullets, int sprites, bool debugVisible)
    {
        _frames++;
        _frameSum += frameDt * 1000;
        _frameMax = Math.Max(_frameMax, frameDt * 1000);
        if (simMs >= 0)
        {
            _simFrames++;
            _simSum += simMs;
            _simMax = Math.Max(_simMax, simMs);
        }
        _allocSum += alloc;
        _allocMax = Math.Max(_allocMax, alloc);
        if (_window.Elapsed.TotalSeconds < 5) return;

        int gen0 = GC.CollectionCount(0) - _gen0;
        // The first report includes JIT/startup work; later ones show steady state.
        Console.WriteLine(
            $"[perf #{_report++}] fps {_frames / _window.Elapsed.TotalSeconds:0.0} | frame avg {_frameSum / _frames:0.00} max {_frameMax:0.00} ms | " +
            $"sim avg {(_simFrames > 0 ? _simSum / _simFrames : 0):0.000} max {_simMax:0.000} ms/tick | bullets {bullets} sprites {sprites} | " +
            $"alloc avg {_allocSum / Math.Max(1, _frames)} B/frame max {_allocMax} B | gen0 +{gen0} | debug ui {(debugVisible ? "on" : "off")}");
        _frames = _simFrames = 0;
        _frameSum = _frameMax = _simSum = _simMax = 0;
        _allocSum = _allocMax = 0;
        _gen0 = GC.CollectionCount(0);
        _window.Restart();
    }
}
