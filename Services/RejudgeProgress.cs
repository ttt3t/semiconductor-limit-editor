using System.Diagnostics;
namespace SemiconductorCsvAnalyzer.Services;
/// <summary>A worker-owned mailbox, polled by the dispatcher; never queues per-cell UI callbacks.</summary>
public sealed class RejudgeProgress
{
    private ImportProgressSnapshot _latest = new(0, "准备重算…");
    private long _updated;
    public ImportProgressSnapshot Latest => Volatile.Read(ref _latest);
    public void Report(double fraction, string stage)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _updated < Stopwatch.Frequency / 10 && stage == _latest.Stage) return;
        _updated = now;
        Volatile.Write(ref _latest, new(Math.Max(_latest.Fraction, Math.Clamp(fraction, 0, .999)), stage));
    }
    public void Complete() => Volatile.Write(ref _latest, new(1, "良率重算完成"));
}
