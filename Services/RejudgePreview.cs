using SemiconductorCsvAnalyzer.Models;
namespace SemiconductorCsvAnalyzer.Services;

public sealed record ItemYieldPreview(double? Low, double? High, bool OriginalFlags, string Text, string Detail);
public sealed record RejudgePreviewResult(string Summary, string? Warning, RejudgeSession Session,
    IReadOnlyList<CsvRecord> Scope, int[] Selection, IReadOnlyDictionary<TestItemSummary, ItemYieldPreview> Items);
/// <summary>All record enumeration and numeric work stays on the worker; the UI receives only display results.</summary>
public static class RejudgePreview
{
    private sealed class Counts { public int Total, Old, New; }
    public static RejudgePreviewResult Compute(RejudgeSession session, IReadOnlyDictionary<string, RuleEdit> edits,
        IReadOnlyList<TestItemSummary> summaries, IReadOnlyList<CsvRecord> scope, RetestMode mode, bool preferGood,
        RejudgePreviewResult? previous, CancellationToken token, Action<double, string>? progress = null)
    {
        var rules = session.ResolveEdits(edits);
        var results = session.Evaluate(token, (f, s) => progress?.Invoke(f * .6, s), rules);
        progress?.Invoke(.61, "选择整条复测记录");
        var selected = RejudgeSession.SelectIndices(session.Data, results, mode, preferGood, token);
        var selectedRows = new bool[results.Length]; foreach (int i in selected) selectedRows[i] = true;
        var scopeIds = scope.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var eligible = new bool[results.Length];
        for (int i = 0; i < eligible.Length; i++) { if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); eligible[i] = selectedRows[i] && scopeIds.Contains(session.Data.Records[i].Id); }
        bool reuse = ReferenceEquals(previous?.Session, session) && ReferenceEquals(previous.Scope, scope) && selected.AsSpan().SequenceEqual(previous.Selection);
        var output = new Dictionary<TestItemSummary, ItemYieldPreview>(summaries.Count);
        var sourceItems = session.Data.Items.ToDictionary(i => i.Id);
        var groups = summaries.GroupBy(s => s.Data.Id).ToArray();
        for (int g = 0; g < groups.Length; g++)
        {
            token.ThrowIfCancellationRequested(); progress?.Invoke(.65 + .34 * g / Math.Max(1.0, groups.Length), "计算单项良率");
            var group = groups[g]; var state = rules[group.Key]; var pending = new List<TestItemSummary>();
            foreach (var s in group)
            {
                if (reuse && previous!.Items.TryGetValue(s, out var cached) && cached.Low == state.Low && cached.High == state.High && cached.OriginalFlags == state.OriginalFlags) output[s] = cached;
                else pending.Add(s);
            }
            if (pending.Count == 0) continue;
            var source = sourceItems[group.Key]; var values = source.Values;
            if (!session.UsesNumericColumn(source)) throw new InvalidOperationException("预览数值列未与原始记录对齐，请重新导入。");
            var counters = pending.Select(s => (s.Site, Wafer: s.IsWaferCombined ? null : s.Wafer)).Distinct().ToDictionary(k => k, _ => new Counts());
            for (int v = 0; v < values.Count; v++)
            {
                if ((v & 1023) == 0) token.ThrowIfCancellationRequested();
                int row = values.SourceRowAt(v); if (!eligible[row]) continue;
                var record = session.Data.Records[row]; double number = values.GetValueAt(v);
                bool oldPass = !(number < source.LowLimit || number > source.HighLimit) && record.ExplicitTestFailures?.Contains(source.Id) != true;
                bool newPass = !(number < state.Low || number > state.High) && (!state.OriginalFlags || record.ExplicitTestFailures?.Contains(source.Id) != true);
                void Add(string site, string? wafer)
                {
                    if (!counters.TryGetValue((site, wafer), out var c)) return;
                    c.Total++; if (oldPass) c.Old++; if (newPass) c.New++;
                }
                Add(TestItemSummary.CombinedSite, null);
                if (record.Site != TestItemSummary.CombinedSite) Add(record.Site, null);
                Add(TestItemSummary.CombinedSite, record.WaferId);
                if (record.Site != TestItemSummary.CombinedSite) Add(record.Site, record.WaferId);
            }
            foreach (var s in pending)
            {
                var c = counters[(s.Site, s.IsWaferCombined ? null : s.Wafer)];
                double oldYield = c.Total == 0 ? 0 : 100.0 * c.Old / c.Total, newYield = c.Total == 0 ? 0 : 100.0 * c.New / c.Total;
                output[s] = new(state.Low, state.High, state.OriginalFlags,
                    c.Total == 0 ? "—" : (newYield - oldYield).ToString("+0.00;-0.00;0.00") + " pp",
                    $"同一统计范围内有效样本 {c.Total}；原良率 {oldYield:F3}%，新良率 {newYield:F3}%。不等同整颗 DUT 良率。STDF 采用 R*4 实际可写限值。");
            }
        }
        int originalGood = 0, newGood = 0, recovered = 0, added = 0, unknown = 0; bool disabled = false;
        foreach (int i in selected)
        {
            token.ThrowIfCancellationRequested(); var r = results[i];
            if (r.Original is RejudgeGrade.Pass or RejudgeGrade.PartialPass) originalGood++;
            if (r.Grade is RejudgeGrade.Pass or RejudgeGrade.PartialPass) newGood++;
            if (r.Changed && r.Original == RejudgeGrade.Fail) recovered++;
            if (r.Changed && r.Grade == RejudgeGrade.Fail) added++;
            if (r.Grade == RejudgeGrade.Pending) unknown++;
            disabled |= r.Grade == RejudgeGrade.Disabled;
        }
        double Percent(int count) => selected.Length == 0 ? 0 : 100.0 * count / selected.Length;
        string summary = disabled ? "未启用判定规则，保留原 BIN" : $"DUT {selected.Length} · 原 {Percent(originalGood):F2}% → 新 {Percent(newGood):F2}% · 恢复 {recovered} · 新增不良 {added} · 待确认 {unknown}";
        string? warning = session.Rules.Count > 0 && session.Rules.Values.All(r => r.ReferenceCount == 0)
            ? "未识别到原始良品参考，测项分类尚未验证。请在分 BIN 设置核对独立的 SBIN 与 HBIN 输入框。" : null;
        progress?.Invoke(.999, "更新良率预览");
        return new(summary, warning, session, scope, selected, output);
    }
}
