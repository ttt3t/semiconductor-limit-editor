using System.Globalization;
using SemiconductorCsvAnalyzer.Models;
namespace SemiconductorCsvAnalyzer.Services;
/// <summary>Frozen reference classification and deterministic whole-record adjudication.</summary>
public sealed class RejudgeSession
{
    public CsvParseResult Data { get; }
    public RejudgeSettings Settings { get; }
    public IReadOnlyDictionary<string, string> Fingerprints { get; }
    public Dictionary<string, LimitRule> Rules { get; } = new();
    public RejudgeSession(CsvParseResult data, RejudgeSettings settings)
    {
        settings.Validate(); Data = data; Settings = settings;
        Fingerprints = data.Records.Select(r => r.SourcePath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, p => data.SourceHashes.TryGetValue(p, out var hash) ? hash : LimitExportService.Fingerprint(p), StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.Items)
        {
            var rule = new LimitRule { Key = item.SourceKey, Name = item.DisplayName, Unit = item.ShownUnit, SharedGroup = item.StdfLimitGroup, OriginalLow = item.LowLimit, OriginalHigh = item.HighLimit, IsStdf = item.IsStdfMeasurement, Functional = item.StdfKind == 20, DisplayFactor = item.DisplayFactor };
            foreach (var row in data.Records)
            {
                var grade = settings.Original(row);
                if (grade != RejudgeGrade.Pass && !(settings.ReferenceIncludesPartialPass && grade == RejudgeGrade.PartialPass)) continue;
                var state = State(item, row, item.LowLimit, item.HighLimit, true);
                if (state == 3) continue;
                rule.ReferenceCount++;
                if (state == 1) rule.ReferenceFailures++;
                if (state == 2) rule.ReferenceMissing++;
            }
            rule.Category = rule.ReferenceFailures > 0 ? RuleCategory.Conflict : rule.ReferenceMissing > 0 ? RuleCategory.Invalid : RuleCategory.Effective;
            rule.Participates = rule.Category == RuleCategory.Effective; rule.Reset();
            Rules.Add(item.Id, rule); item.Rule = rule;
        }
    }
    private static int State(TestItemData item, CsvRecord row, double? low, double? high, bool original)
    {
        if (item.SourcePath.Length > 0 && !string.Equals(item.SourcePath, row.SourcePath, StringComparison.OrdinalIgnoreCase)) return 3;
        if (!row.Measurements.TryGetValue(item.Id, out string? raw) || string.IsNullOrWhiteSpace(raw)) return 2;
        if (raw == StdfParser.NotApplicableValue) return 3;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) return 2;
        return value < low || value > high || original && row.ExplicitTestFailures?.Contains(item.Id) == true ? 1 : 0;
    }
    private sealed record RuleState(double? Low, double? High, bool Participates, bool OriginalFlags);
    private readonly object _evaluationGate = new();
    private Dictionary<string, RuleState> _previousRules = new();
    private int[]? _failed, _unknown, _applicable;
    public RejudgeResult[] Evaluate(CancellationToken token = default)
    {
        lock (_evaluationGate)
        {
            token.ThrowIfCancellationRequested();
            int count = Data.Records.Count;
            var snapshots = Rules.ToDictionary(p => p.Key, p => { var limits = p.Value.Limits(); return new RuleState(limits.Low, limits.High, p.Value.Participates, !p.Value.LowChanged && !p.Value.HighChanged); });
            int[] failed = _failed?.ToArray() ?? new int[count], unknown = _unknown?.ToArray() ?? new int[count], applicable = _applicable?.ToArray() ?? new int[count];
            foreach (var item in Data.Items)
            {
                var next = snapshots[item.Id]; _previousRules.TryGetValue(item.Id, out var old);
                if (next == old) continue;
                for (int n = 0; n < count; n++)
                {
                    if ((n & 255) == 0) token.ThrowIfCancellationRequested();
                    int Contribution(RuleState? state) => state?.Participates == true ? State(item, Data.Records[n], state.Low, state.High, state.OriginalFlags) : 3;
                    int previous = Contribution(old), current = Contribution(next);
                    failed[n] += (current == 1 ? 1 : 0) - (previous == 1 ? 1 : 0);
                    unknown[n] += (current == 2 ? 1 : 0) - (previous == 2 ? 1 : 0);
                    applicable[n] += (current != 3 ? 1 : 0) - (previous != 3 ? 1 : 0);
                }
            }
            bool enabled = snapshots.Values.Any(r => r.Participates);
            var output = new RejudgeResult[count];
            for (int n = 0; n < count; n++)
            {
                token.ThrowIfCancellationRequested(); var row = Data.Records[n];
                var original = Settings.Original(row); RejudgeGrade grade; string reason = "";
                if (!enabled) { grade = RejudgeGrade.Disabled; reason = "未启用判定规则，保留原 BIN"; }
                else if (failed[n] > 0) { grade = RejudgeGrade.Fail; reason = $"{failed[n]} 个参与项明确失败"; }
                else if (unknown[n] > 0 || applicable[n] == 0 || row.Incomplete || original == RejudgeGrade.Pending ||
                    (!(row.X.HasValue && row.Y.HasValue) && string.IsNullOrWhiteSpace(row.ChipSerialNumber) && string.IsNullOrWhiteSpace(row.RetestUuid)))
                { grade = RejudgeGrade.Pending; reason = "参与项缺测/无效、身份/BIN 或 DUT 记录不完整"; }
                else grade = original is RejudgeGrade.Pass or RejudgeGrade.PartialPass ? original : Settings.RecoverAsPartialPass ? RejudgeGrade.PartialPass : RejudgeGrade.Pass;
                bool change = (grade is RejudgeGrade.Pass or RejudgeGrade.PartialPass or RejudgeGrade.Fail) && grade != original && original != RejudgeGrade.Pending;
                var pair = grade == RejudgeGrade.Pass ? Settings.Pass : grade == RejudgeGrade.PartialPass ? Settings.PartialPass : Settings.Fail;
                output[n] = new(row.Id, original, grade, change ? pair.SBin.ToString() : row.SBin, change ? pair.HBin.ToString() : row.HBin, change, reason);
            }
            // Commit atomically. Cancellation never leaves partially updated counters.
            _failed = failed; _unknown = unknown; _applicable = applicable; _previousRules = snapshots;
            return output;
        }
    }
    public static IReadOnlyList<RejudgeResult> Select(CsvParseResult data, IReadOnlyList<RejudgeResult> results, RetestMode mode, bool preferGood)
    {
        if (mode == RetestMode.All) return results.ToArray();
        if (mode == RetestMode.OnlyRetests)
        {
            var seen = new HashSet<(string, int?, int?, string)>();
            return results.Where((r, i) => !seen.Add(DatasetService.ChipKey(data.Records[i]))).ToArray();
        }
        var chosen = new Dictionary<(string, int?, int?, string), int>();
        for (int i = 0; i < data.Records.Count; i++)
        {
            var key = DatasetService.ChipKey(data.Records[i]);
            if (!chosen.TryGetValue(key, out var old) || RetestPreference.Replace(Good(results[old]), Good(results[i]), preferGood, mode)) chosen[key] = i;
        }
        return chosen.Values.Order().Select(i => results[i]).ToArray();
    }
    private static bool Good(RejudgeResult r) => r.Grade is RejudgeGrade.Pass or RejudgeGrade.PartialPass;
}
