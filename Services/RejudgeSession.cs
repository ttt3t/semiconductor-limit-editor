using System.Buffers;
using System.Globalization;
using SemiconductorCsvAnalyzer.Models;
namespace SemiconductorCsvAnalyzer.Services;

public sealed record RuleEdit(string LowText, string HighText, bool Participates);
public sealed record RuleState(double? Low, double? High, bool Participates, bool OriginalFlags);
/// <summary>Frozen references, numeric-column comparisons, and cancellation-safe incremental counters.</summary>
public sealed class RejudgeSession
{
    public CsvParseResult Data { get; }
    public RejudgeSettings Settings { get; }
    public IReadOnlyDictionary<string, string> Fingerprints { get; }
    public Dictionary<string, LimitRule> Rules { get; } = new();
    private readonly HashSet<string> _numericColumns = new();
    private readonly RejudgeGrade[] _original;
    public RejudgeSession(CsvParseResult data, RejudgeSettings settings, CancellationToken token = default,
        Action<double, string>? progress = null, bool attachRules = true)
    {
        settings.Validate(); Data = data; Settings = settings;
        Fingerprints = data.Records.Select(r => r.SourcePath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p,
            p => data.SourceHashes.TryGetValue(p, out var hash) ? hash : LimitExportService.Fingerprint(p), StringComparer.OrdinalIgnoreCase);
        _original = new RejudgeGrade[data.Records.Count];
        var references = new List<int>();
        for (int n = 0; n < data.Records.Count; n++)
        {
            if ((n & 1023) == 0) { token.ThrowIfCancellationRequested(); AnalysisOperation.CheckCancellation(); }
            var grade = _original[n] = settings.Original(data.Records[n]);
            if (grade == RejudgeGrade.Pass || settings.ReferenceIncludesPartialPass && grade == RejudgeGrade.PartialPass) references.Add(n);
        }
        // Identity preparation may clone records while sharing their numeric columns.
        // Verify alignment once per shared record table, rather than once per column or cell.
        var alignments = new Dictionary<IReadOnlyList<CsvRecord>, bool>(ReferenceEqualityComparer.Instance);
        byte[]? buffer = null;
        try
        {
            for (int i = 0; i < data.Items.Count; i++)
            {
                token.ThrowIfCancellationRequested(); AnalysisOperation.CheckCancellation();
                var item = data.Items[i]; var rows = item.Values.RowRecords;
                if (!alignments.TryGetValue(rows, out bool numeric))
                {
                    numeric = ReferenceEquals(rows, data.Records) || rows.Count == data.Records.Count && rows.Select(r => r.Id).SequenceEqual(data.Records.Select(r => r.Id));
                    alignments.Add(rows, numeric);
                }
                if (numeric) _numericColumns.Add(item.Id);
                var rule = new LimitRule { Key = item.SourceKey, Name = item.DisplayName, Unit = item.ShownUnit, SharedGroup = item.StdfLimitGroup,
                    OriginalLow = item.LowLimit, OriginalHigh = item.HighLimit, IsStdf = item.IsStdfMeasurement, Functional = item.StdfKind == 20, DisplayFactor = item.DisplayFactor };
                int visited = 0;
                foreach (int n in references)
                {
                    if ((++visited & 1023) == 0) { token.ThrowIfCancellationRequested(); AnalysisOperation.CheckCancellation(); }
                    int state = State(item, n, item.LowLimit, item.HighLimit, true, numeric, ref buffer);
                    if (state == 3) continue;
                    rule.ReferenceCount++; if (state == 1) rule.ReferenceFailures++; if (state == 2) rule.ReferenceMissing++;
                }
                rule.Category = rule.ReferenceFailures > 0 ? RuleCategory.Conflict : rule.ReferenceMissing > 0 ? RuleCategory.Invalid : RuleCategory.Effective;
                rule.Participates = rule.Category == RuleCategory.Effective; rule.Reset();
                Rules.Add(item.Id, rule); if (attachRules) item.Rule = rule;
                double fraction = (double)(i + 1) / Math.Max(1, data.Items.Count);
                progress?.Invoke(fraction, "建立原始良品分类"); ImportProgress.Report(fraction);
            }
        }
        finally { if (buffer != null) ArrayPool<byte>.Shared.Return(buffer); }
    }
    public bool UsesNumericColumn(TestItemData item) => _numericColumns.Contains(item.Id);
    public IReadOnlyDictionary<string, RuleEdit> CaptureEdits() => Rules.ToDictionary(p => p.Key,
        p => new RuleEdit(p.Value.NewLowText, p.Value.NewHighText, p.Value.Participates));
    public IReadOnlyDictionary<string, RuleState> ResolveEdits(IReadOnlyDictionary<string, RuleEdit> edits) => Rules.ToDictionary(p => p.Key, p =>
    {
        var edit = edits[p.Key]; var limits = p.Value.Limits(edit.LowText, edit.HighText);
        bool flags = edit.LowText == LimitRule.Format(p.Value.OriginalLow * p.Value.DisplayFactor) && edit.HighText == LimitRule.Format(p.Value.OriginalHigh * p.Value.DisplayFactor);
        return new RuleState(limits.Low, limits.High, edit.Participates, flags);
    });
    // 0 pass; 1 failure; 2 unknown; 3 explicitly inapplicable.
    private int State(TestItemData item, int n, double? low, double? high, bool original, bool numeric, ref byte[]? buffer)
    {
        var row = Data.Records[n];
        if (item.SourcePath.Length > 0 && !string.Equals(item.SourcePath, row.SourcePath, StringComparison.OrdinalIgnoreCase)) return 3;
        double value = numeric ? item.Values.GetSourceRowValue(n) : double.NaN;
        if (!double.IsFinite(value))
        {
            var raw = row.Measurements;
            if (!raw.ContainsKey(item.Id)) return 2;
            if (raw.IsOnDisk && raw.Schema is { } schema && schema.Indices.TryGetValue(item.Id, out int index))
            {
                var rawRow = raw.ReadRow(ref buffer);
                if (rawRow.IsNumeric(index)) value = rawRow.ReadNumeric(index);
                else
                {
                    var text = rawRow.RawTextSpan(index);
                    if (text.SequenceEqual(StdfParser.NotApplicableValue)) return 3;
                    if (!DatasetService.TryValue(text, out value)) return 2;
                }
            }
            else
            {
                if (!raw.TryGetText(item.Id, out var text)) return 2;
                if (text.SequenceEqual(StdfParser.NotApplicableValue)) return 3;
                if (!DatasetService.TryValue(text, out value)) return 2;
            }
        }
        return value < low || value > high || original && row.ExplicitTestFailures?.Contains(item.Id) == true ? 1 : 0;
    }
    private readonly object _evaluationGate = new();
    private IReadOnlyDictionary<string, RuleState> _previousRules = new Dictionary<string, RuleState>();
    private int[]? _failed, _unknown, _applicable;
    public RejudgeResult[] Evaluate(CancellationToken token = default, Action<double, string>? progress = null,
        IReadOnlyDictionary<string, RuleState>? snapshots = null)
    {
        lock (_evaluationGate)
        {
            token.ThrowIfCancellationRequested(); snapshots ??= ResolveEdits(CaptureEdits());
            int count = Data.Records.Count;
            int[] failed = _failed?.ToArray() ?? new int[count], unknown = _unknown?.ToArray() ?? new int[count], applicable = _applicable?.ToArray() ?? new int[count];
            var changed = Data.Items.Where(i => !_previousRules.TryGetValue(i.Id, out var old) || snapshots[i.Id] != old).ToArray();
            byte[]? buffer = null;
            try
            {
                for (int i = 0; i < changed.Length; i++)
                {
                    var item = changed[i]; var next = snapshots[item.Id]; _previousRules.TryGetValue(item.Id, out var old);
                    bool numeric = UsesNumericColumn(item);
                    for (int n = 0; n < count; n++)
                    {
                        if ((n & 1023) == 0) { token.ThrowIfCancellationRequested(); progress?.Invoke(.8 * (i + (double)n / Math.Max(1, count)) / Math.Max(1, changed.Length), "比较参与测项"); }
                        int previous = old?.Participates == true ? State(item, n, old.Low, old.High, old.OriginalFlags, numeric, ref buffer) : 3;
                        int current = next.Participates ? State(item, n, next.Low, next.High, next.OriginalFlags, numeric, ref buffer) : 3;
                        failed[n] += (current == 1 ? 1 : 0) - (previous == 1 ? 1 : 0);
                        unknown[n] += (current == 2 ? 1 : 0) - (previous == 2 ? 1 : 0);
                        applicable[n] += (current != 3 ? 1 : 0) - (previous != 3 ? 1 : 0);
                    }
                }
            }
            finally { if (buffer != null) ArrayPool<byte>.Shared.Return(buffer); }
            bool enabled = snapshots.Values.Any(r => r.Participates);
            var output = new RejudgeResult[count];
            string passS = Settings.Pass.SBin.ToString(), passH = Settings.Pass.HBin.ToString(), partialS = Settings.PartialPass.SBin.ToString(), partialH = Settings.PartialPass.HBin.ToString(), failS = Settings.Fail.SBin.ToString(), failH = Settings.Fail.HBin.ToString();
            for (int n = 0; n < count; n++)
            {
                if ((n & 1023) == 0) { token.ThrowIfCancellationRequested(); progress?.Invoke(.8 + .2 * n / Math.Max(1.0, count), "汇总 DUT 判定"); }
                var row = Data.Records[n]; var original = _original[n]; RejudgeGrade grade; string reason = "";
                if (!enabled) { grade = RejudgeGrade.Disabled; reason = "未启用判定规则，保留原 BIN"; }
                else if (failed[n] > 0) { grade = RejudgeGrade.Fail; reason = "参与项明确失败"; }
                else if (unknown[n] > 0 || applicable[n] == 0 || row.Incomplete || original == RejudgeGrade.Pending ||
                    (!(row.X.HasValue && row.Y.HasValue) && string.IsNullOrWhiteSpace(row.ChipSerialNumber) && string.IsNullOrWhiteSpace(row.RetestUuid)))
                { grade = RejudgeGrade.Pending; reason = "参与项缺测/无效、身份/BIN 或 DUT 记录不完整"; }
                else grade = original is RejudgeGrade.Pass or RejudgeGrade.PartialPass ? original : Settings.RecoverAsPartialPass ? RejudgeGrade.PartialPass : RejudgeGrade.Pass;
                bool change = (grade is RejudgeGrade.Pass or RejudgeGrade.PartialPass or RejudgeGrade.Fail) && grade != original && original != RejudgeGrade.Pending;
                output[n] = new(row.Id, original, grade, !change ? row.SBin : grade == RejudgeGrade.Pass ? passS : grade == RejudgeGrade.PartialPass ? partialS : failS,
                    !change ? row.HBin : grade == RejudgeGrade.Pass ? passH : grade == RejudgeGrade.PartialPass ? partialH : failH, change, reason);
            }
            token.ThrowIfCancellationRequested();
            _failed = failed; _unknown = unknown; _applicable = applicable; _previousRules = snapshots;
            progress?.Invoke(1, "DUT 判定完成"); return output;
        }
    }
    public static int[] SelectIndices(CsvParseResult data, IReadOnlyList<RejudgeResult> results, RetestMode mode, bool preferGood, CancellationToken token = default)
    {
        if (mode == RetestMode.All) return Enumerable.Range(0, results.Count).ToArray();
        var seen = mode == RetestMode.OnlyRetests ? new HashSet<(string, int?, int?, string)>() : null;
        var chosen = new Dictionary<(string, int?, int?, string), int>(); var repeated = new List<int>();
        for (int i = 0; i < data.Records.Count; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var key = DatasetService.ChipKey(data.Records[i]);
            if (seen != null) { if (!seen.Add(key)) repeated.Add(i); }
            else if (!chosen.TryGetValue(key, out var old) || RetestPreference.Replace(Good(results[old]), Good(results[i]), preferGood, mode)) chosen[key] = i;
        }
        return seen != null ? repeated.ToArray() : chosen.Values.Order().ToArray();
    }
    public static IReadOnlyList<RejudgeResult> Select(CsvParseResult data, IReadOnlyList<RejudgeResult> results, RetestMode mode, bool preferGood) => SelectIndices(data, results, mode, preferGood).Select(i => results[i]).ToArray();
    private static bool Good(RejudgeResult r) => r.Grade is RejudgeGrade.Pass or RejudgeGrade.PartialPass;
}
