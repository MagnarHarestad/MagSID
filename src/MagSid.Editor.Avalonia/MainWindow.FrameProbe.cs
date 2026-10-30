using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using Avalonia.Threading;
using MagSid.Playback;
using NAudio.Wave;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The frame probe: a measurement of how smoothly the editor draws while a song plays, for checking
/// a change to the Retro grid, the DAW views or the drawing underneath them. Off unless
/// MAGSID_FRAMEPROBE names a log file; then the editor plays the song it was opened with from the
/// start, silently, in the view MAGSID_FRAMEPROBE_DAW=1 picks (the Retro view otherwise), writes
/// what it measured to that file and closes.
/// </summary>
/// <remarks>
/// The log holds the gaps between displayed frames, each control's paint time and the gaps between
/// its paints, the garbage collections and their pause, and what allocated the most. Compare paint
/// times and collections between runs rather than frame gaps alone: the display paces frames by
/// its own refresh, which can change between runs with nothing in the editor changed.
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>The file the probe writes to, or null when there is no probe.</summary>
    internal static string? FrameProbeLog =>
        Environment.GetEnvironmentVariable("MAGSID_FRAMEPROBE") is { Length: > 0 } log ? log : null;

    private const int FrameProbeFrames = 1500;

    private void StartFrameProbe(string log)
    {
        var frames = new List<double>();
        var paints = new List<(double At, string Who, double Ms)>();
        var clock = Stopwatch.StartNew();
        var allocations = new FrameProbeAllocations();
        System.Windows.Forms.Control.RenderTimed = (who, ms) =>
            paints.Add((clock.Elapsed.TotalMilliseconds, who.GetType().Name, ms));
        if (Environment.GetEnvironmentVariable("MAGSID_FRAMEPROBE_DAW") == "1")
            ToggleView();

        Dispatcher.UIThread.Post(() =>
        {
            StartFromBeginning();
            double last = -1;
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1);
            var pause = GC.GetTotalPauseDuration();

            void Frame(TimeSpan _)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                if (last >= 0)
                    frames.Add(now - last);
                last = now;
                if (frames.Count < FrameProbeFrames)
                {
                    RequestAnimationFrame(Frame);
                    return;
                }

                System.Windows.Forms.Control.RenderTimed = null;
                // The first second is the window settling and the song starting, not the drawing.
                var lines = new List<string> { "display frames: " + ProbeStats(frames.Skip(60)) };
                foreach (var who in paints.GroupBy(p => p.Who).OrderByDescending(g => g.Sum(p => p.Ms)))
                {
                    var at = who.Select(p => p.At).ToList();
                    lines.Add($"paint {who.Key}: total {ProbeMs(who.Sum(p => p.Ms))}ms  time {ProbeStats(who.Select(p => p.Ms))}");
                    lines.Add($"   gaps {ProbeStats(at.Zip(at.Skip(1), (a, b) => b - a))}");
                }
                lines.Add($"GC gen0 {GC.CollectionCount(0) - gen0} gen1 {GC.CollectionCount(1) - gen1} pause {ProbeMs((GC.GetTotalPauseDuration() - pause).TotalMilliseconds)}ms");
                lines.AddRange(allocations.Top(14));
                allocations.Dispose();
                File.WriteAllLines(log, lines);
                StopAny();
                Close();
            }

            RequestAnimationFrame(Frame);
        }, DispatcherPriority.Background);
    }

    private static string ProbeMs(double ms) => ms.ToString("F2", CultureInfo.InvariantCulture);

    private static string ProbeStats(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0
            ? "none"
            : $"n {sorted.Count} median {ProbeMs(sorted[sorted.Count / 2])} p90 {ProbeMs(sorted[(int)(sorted.Count * 0.9)])} "
              + $"p99 {ProbeMs(sorted[(int)(sorted.Count * 0.99)])} max {ProbeMs(sorted[^1])} "
              + $">10ms {sorted.Count(x => x > 10)} >20ms {sorted.Count(x => x > 20)}";
    }
}

/// <summary>The probe's audio: no device, but read from in real time, about 60 ms ahead of the
/// clock, so the playheads move as they do through a sound card.</summary>
internal sealed class FrameProbeOutput(IWaveProvider source) : IAudioOutput
{
    private const int SampleRate = 44100;
    private const int Ahead = SampleRate * 60 / 1000;
    private const int Block = 512;
    private readonly Stopwatch _clock = new();
    private long _produced;
    private Thread? _thread;
    private volatile bool _run;

    public void Play()
    {
        _run = true;
        _clock.Start();
        _thread ??= new Thread(() =>
        {
            var buffer = new byte[Block * 2];
            while (_run)
            {
                while (_produced < SamplesPlayed + Ahead)
                {
                    source.Read(buffer, 0, buffer.Length);
                    Interlocked.Add(ref _produced, Block);
                }
                Thread.Sleep(2);
            }
        }) { IsBackground = true, Name = "Frame probe audio" };
        if (!_thread.IsAlive)
            _thread.Start();
    }

    public void Pause() { }

    public void Stop() => _run = false;

    public bool IsPlaying => _run;

    public long SamplesPlayed => (long)(_clock.Elapsed.TotalSeconds * SampleRate);

    public event Action<Exception?>? Stopped { add { } remove { } }

    public void Dispose() => _run = false;
}

/// <summary>Counts the runtime's allocation samples - one per 100 KB allocated - by type.</summary>
internal sealed class FrameProbeAllocations : EventListener
{
    private readonly Dictionary<string, int> _counts = [];

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick") || e.Payload is null)
            return;
        int index = e.PayloadNames!.IndexOf("TypeName");
        string type = index >= 0 ? e.Payload[index]?.ToString() ?? "?" : "?";
        lock (_counts)
            _counts[type] = _counts.GetValueOrDefault(type) + 1;
    }

    public IEnumerable<string> Top(int count)
    {
        lock (_counts)
            return _counts.OrderByDescending(c => c.Value).Take(count).Select(c => $"  {c.Value,5} x100KB {c.Key}").ToList();
    }
}
