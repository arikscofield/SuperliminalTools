using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SuperliminalTools.TASMod.Demo;

/// <summary>
/// Wall-clock harness for demo playback, used to tell whether a performance
/// change actually helped.
///
/// A demo replays deterministically, so every run performs bit-identical work.
/// Run-to-run differences are therefore pure machine noise -- which is what lets
/// you separate a real win from a warm cache: measure the spread of a baseline
/// first, and treat any A/B difference smaller than that spread as unproven.
///
/// Inert unless the game was launched with --bench=N, so the instrumentation can
/// stay compiled into Release builds. That is deliberate: you want to measure the
/// build you actually ship, determinism patch and all.
///
/// See the "Benchmarking" section of README.md for how to drive it.
/// </summary>
internal static class Bench
{
    // Timed slots. Keep these at per-frame granularity: a GetTimestamp pair costs
    // roughly 25ns, so timing something called hundreds of times per frame would
    // measure the instrument rather than the work. Count those instead -- see
    // InputPolls.
    internal const int PathProjector = 0;
    internal const int Hud = 1;
    internal const int FileWatch = 2;
    internal const int CheckpointIdx = 3;
    internal const int LiveAdvance = 4;

    private static readonly string[] SlotNames =
        { "PathProjector", "HUD", "FileWatch", "CheckpointIdx", "LiveAdvance" };

    private static readonly long[] _ticks = new long[SlotNames.Length];
    private static readonly long[] _calls = new long[SlotNames.Length];

    /// <summary>
    /// Input polls this run. Far too cheap to time individually; measure the
    /// per-call cost of the lookup offline and multiply by this instead.
    /// </summary>
    internal static long InputPolls;

    /// <summary>True only between Begin() and EndRun() of a --bench run.</summary>
    internal static bool Active { get; private set; }

    /// <summary>Runs still wanted, including any currently in flight.</summary>
    internal static int RunsLeft { get; private set; }

    private static Stopwatch _wall;
    private static int _gcAtStart;
    private static long _memAtStart;

    private static Vector3 _lastPos;
    private static bool _havePos;

    private static readonly List<double> _runMs = new();
    private static readonly List<int> _runFrames = new();

    /// <summary>
    /// Reads --bench=N (or a bare --bench, meaning one run) off the command line.
    /// Called once at startup; does nothing if the arg isn't present.
    /// </summary>
    internal static void ArmFromCommandLine()
    {
        foreach (var arg in System.Environment.GetCommandLineArgs())
        {
            if (arg == "--bench")
            {
                RunsLeft = 1;
                Debug.Log("[bench] armed for 1 run. Open a demo (F11) and press F5.");
                return;
            }

            if (!arg.StartsWith("--bench=")) continue;

            if (int.TryParse(arg.Substring("--bench=".Length), out var n) && n > 0)
            {
                RunsLeft = n;
                Debug.Log($"[bench] armed for {n} run(s). Open a demo (F11) and press F5. " +
                          "The first run is a warmup -- ignore it.");
            }
            else
            {
                Debug.LogError($"[bench] could not parse '{arg}'; expected --bench=N.");
            }
            return;
        }
    }

    /// <summary>Starts timing a run. No-op unless --bench armed us.</summary>
    internal static void Begin()
    {
        if (RunsLeft <= 0) return;

        System.Array.Clear(_ticks, 0, _ticks.Length);
        System.Array.Clear(_calls, 0, _calls.Length);
        InputPolls = 0;

        _gcAtStart = System.GC.CollectionCount(0);
        _memAtStart = System.GC.GetTotalMemory(false);
        _havePos = false;

        Active = true;
        _wall = Stopwatch.StartNew();
    }

    /// <summary>
    /// Records where the player is this frame. Called once per playback frame,
    /// because a run that ends by completing the level has already destroyed the
    /// player by the time the run is closed out.
    /// </summary>
    internal static void Sample()
    {
        if (!Active) return;

        var player = GameManager.GM != null ? GameManager.GM.player : null;
        if (player == null) return;

        _lastPos = player.transform.position;
        _havePos = true;
    }

    /// <summary>Opens a timing scope. Pair with T1; free when not benchmarking.</summary>
    internal static long T0() => Active ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>Closes the scope opened by T0 and attributes it to a slot.</summary>
    internal static void T1(int slot, long t0)
    {
        if (!Active) return;
        _ticks[slot] += Stopwatch.GetTimestamp() - t0;
        _calls[slot]++;
    }

    /// <summary>
    /// Closes the run and logs its breakdown. Returns true if another run is
    /// wanted. Call this before anything else tears down the state it describes.
    /// </summary>
    internal static bool EndRun(int frames)
    {
        if (!Active) return false;

        Active = false;
        _wall.Stop();
        RunsLeft--;

        double totalMs = _wall.Elapsed.TotalMilliseconds;

        if (frames <= 0)
        {
            Debug.LogWarning($"[bench] run ended after {frames} frames ({totalMs:0.0}ms); " +
                             "nothing to measure, discarding it.");
            return RunsLeft > 0;
        }

        _runMs.Add(totalMs);
        _runFrames.Add(frames);

        double msPerFrame = totalMs / frames;
        double ticksToMs = 1000.0 / Stopwatch.Frequency;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[bench] run {_runMs.Count}/{_runMs.Count + RunsLeft}  frames={frames}  " +
                      $"wall={totalMs:0.0}ms  {msPerFrame:0.0000} ms/frame  ({1000.0 / msPerFrame:0} fps)");

        // Frame time is linear in cost, so these columns subtract cleanly: remove
        // something costing 0.18 ms/f and the run's ms/frame drops by 0.18. FPS
        // does not behave that way, which is why it isn't the headline number.
        double attributed = 0;
        for (int i = 0; i < SlotNames.Length; i++)
        {
            if (_calls[i] == 0) continue;
            double ms = _ticks[i] * ticksToMs;
            attributed += ms;
            sb.AppendLine($"    {SlotNames[i],-14} {ms,9:0.0}ms {ms / totalMs * 100,5:0.0}%  " +
                          $"{ms / frames:0.0000} ms/f  {_calls[i]} calls");
        }

        // The game itself, plus anything not instrumented. This should stay flat
        // across builds -- if it moves when you only touched the HUD, either the
        // measurement is bad or the change had a side effect you didn't intend.
        sb.AppendLine($"    {"unattributed",-14} {totalMs - attributed,9:0.0}ms " +
                      $"{(totalMs - attributed) / totalMs * 100,5:0.0}%");

        // Allocation churn shows up here rather than in mean frame time: Mono GC
        // pauses land unpredictably, so they inflate the spread, not the median.
        sb.AppendLine($"    gc0={System.GC.CollectionCount(0) - _gcAtStart}  " +
                      $"alloc={(System.GC.GetTotalMemory(false) - _memAtStart) / 1024}KB  " +
                      $"inputPolls={InputPolls} ({(double)InputPolls / frames:0.0}/frame)");

        sb.Append("    " + EndState(frames));

        Debug.Log(sb.ToString());
        return RunsLeft > 0;
    }

    /// <summary>
    /// Fingerprint of where the run actually finished. Every performance change
    /// here is supposed to be simulation-neutral, so this must not move between
    /// builds. If it does, the "optimization" is a desync bug, however good its
    /// timing looked. Round-trip formatted, same as DemoCSVSerializer.
    /// </summary>
    private static string EndState(int frames)
    {
        if (!_havePos) return $"endstate frames={frames} pos=<never sampled>";

        var p = _lastPos;
        return $"endstate frames={frames} " +
               $"pos={p.x.ToString("R")},{p.y.ToString("R")},{p.z.ToString("R")}";
    }

    /// <summary>Logs min/median/spread once the last run finishes.</summary>
    internal static void Summary()
    {
        if (_runMs.Count == 0) return;

        var sorted = new List<double>(_runMs);
        sorted.Sort();

        // Min is the least-contaminated sample: no OS scheduling, no GC landing
        // mid-run. Median guards against a single anomalously good outlier.
        double min = sorted[0];
        double median = sorted[sorted.Count / 2];
        double max = sorted[sorted.Count - 1];
        int frames = _runFrames[0];

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[bench] SUMMARY  runs={sorted.Count}  frames={frames}");
        sb.AppendLine($"    min    {min,9:0.0}ms  {min / frames:0.0000} ms/frame");
        sb.AppendLine($"    median {median,9:0.0}ms  {median / frames:0.0000} ms/frame");
        sb.AppendLine($"    max    {max,9:0.0}ms  {max / frames:0.0000} ms/frame");
        sb.AppendLine($"    spread {(max - min) / min * 100:0.0}%" +
                      "  <- an A/B win smaller than this is not yet proven");

        // A run that took a different number of frames did not do the same work,
        // so its time is not comparable with the others.
        for (int i = 1; i < _runFrames.Count; i++)
        {
            if (_runFrames[i] == frames) continue;
            sb.AppendLine($"    WARNING: run {i + 1} ran {_runFrames[i]} frames, not {frames}. " +
                          "The runs are not comparable -- the demo desynced or ended early.");
            break;
        }

        Debug.Log(sb.ToString());

        _runMs.Clear();
        _runFrames.Clear();
    }
}
