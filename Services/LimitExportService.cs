using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record LimitPlan(int Version, RejudgeSettings Settings, LimitRule[] Rules);
public static class LimitExportService
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Fingerprint(string path) { using var f = SourceFileReader.Open(path); return Convert.ToHexString(SHA256.HashData(f)); }
    public static void SavePlan(string path, RejudgeSession session) => AtomicFile.WriteAllText(path,
        JsonSerializer.Serialize(new LimitPlan(1, session.Settings, session.Rules.Values.ToArray()), Json));
    public static LimitPlan ReadPlan(string path)
    {
        var plan = JsonSerializer.Deserialize<LimitPlan>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("规则方案为空。");
        if (plan.Version != 1) throw new InvalidDataException("不支持的规则方案版本。");
        plan.Settings.Validate(); return plan;
    }
    public static void ApplyPlan(RejudgeSession session, IEnumerable<LimitRule> saved)
    {
        var byKey = saved.ToDictionary(r => r.Key);
        foreach (var rule in session.Rules.Values)
            if (byKey.TryGetValue(rule.Key, out var old))
            {
                if (old.OriginalLow != rule.OriginalLow || old.OriginalHigh != rule.OriginalHigh || old.Unit != rule.Unit) throw new InvalidDataException("方案规格/单位与当前源文件不一致。");
                rule.NewLowText = old.NewLowText; rule.NewHighText = old.NewHighText; rule.Participates = old.Participates; rule.Limits();
            }
        foreach (var group in session.Rules.Values.Where(r => r.SharedGroup.Length > 0).GroupBy(r => r.SharedGroup))
            if (group.Select(r => r.Limits()).Distinct().Count() > 1) throw new InvalidDataException("MPR 同组结果必须使用相同限值。");
    }
    public static void ExportBundle(string destination, IReadOnlyList<CsvImportSource> sources, AnalyzerConfig config,
        RejudgeSession session, IReadOnlyDictionary<string, string> fingerprints, CancellationToken token = default)
    {
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("导出目录已存在，请选择新的目录，避免覆盖原文件。");
        string stage = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(stage);
        var warnings = new List<string>();
        try
        {
            SavePlan(Path.Combine(stage, "rules.json"), session);
            for (int n = 0; n < sources.Count; n++)
            {
                token.ThrowIfCancellationRequested(); var source = sources[n]; string path = Path.GetFullPath(source.Path);
                if (!fingerprints.TryGetValue(path, out var hash) || Fingerprint(path) != hash) throw new IOException("源文件已变化，请重新导入：" + path);
                // Reuse original raw rows. No second measurement matrix is allocated for export.
                var data = new CsvParseResult { Items = session.Data.Items.Where(i => string.Equals(i.SourcePath, path, StringComparison.OrdinalIgnoreCase)).Select(DatasetService.CloneItem).ToList(),
                    Records = session.Data.Records.Where(r => string.Equals(r.SourcePath, path, StringComparison.OrdinalIgnoreCase)).ToList(), ImportWarning = session.Data.ImportWarning,
                    SourceHashes = session.Data.SourceHashes };
                if (data.Items.Count == 0 || data.Records.Count == 0) throw new InvalidDataException("源文件缺少可稳定定位的原始测项或 DUT 记录。");
                var layout = source.ConfigText.Length > 0 ? ConfigLoader.Parse(source.ConfigText) : config;
                using var lease = new DatasetLease(data.Records);
                var local = new RejudgeSession(data, session.Settings); ApplyPlan(local, session.Rules.Values);
                var results = local.Evaluate(token);
                string output = Path.Combine(stage, $"{n + 1:D3}_" + Path.GetFileName(path));
                bool stdf = DataFileService.IsStdf(path);
                if (stdf)
                {
                    if (data.ImportWarning.Length > 0) throw new InvalidDataException("STDF 可恢复预览，但结构存在警告，无法保证安全写回；请先核对源文件：\n" + data.ImportWarning);
                    StdfLimitWriter.Save(output, path, layout, source.StdfMode, local, results, token);
                }
                else
                {
                    bool preserve = !IntegratedCsvService.IsIntegrated(path) && layout.LowLimitRow > 0 && layout.HighLimitRow > 0 && layout.LowLimitRow != layout.HighLimitRow &&
                        layout.LowLimitRow < layout.DataStartRow && layout.HighLimitRow < layout.DataStartRow && layout.SBinColumn > 0 && layout.HBinColumn > 0;
                    if (preserve) CsvLimitWriter.Save(output, path, layout, local, results, token);
                    else
                    {
                        output = Path.ChangeExtension(output, ".normalized.csv");
                        CsvLimitWriter.SaveNormalized(output, local, results);
                        warnings.Add(Path.GetFileName(path) + "：缺少可写限值行或双 BIN 列/属于整合 CSV，已转换为标准化 CSV，附带 .ini 配置。");
                    }
                }
                if (Fingerprint(path) != hash) throw new IOException("导出期间源文件发生变化：" + path);
                string manifest = output + ".changes.json";
                using (var file = File.Create(manifest))
                using (var writer = new Utf8JsonWriter(file, new() { Indented = true }))
                {
                    writer.WriteStartObject(); writer.WriteString("source", path); writer.WriteString("sourceSha256", hash);
                    writer.WriteNumber("records", results.Length); writer.WriteNumber("changed", results.Count(r => r.Changed));
                    writer.WriteNumber("pending", results.Count(r => r.Grade == RejudgeGrade.Pending));
                    writer.WritePropertyName("changesAndPending"); writer.WriteStartArray();
                    for (int i = 0; i < results.Length; i++) if (results[i].Changed || results[i].Grade == RejudgeGrade.Pending)
                        JsonSerializer.Serialize(writer, new { SourceOffset = data.Records[i].SourceOffset, data.Records[i].SerialNumber, Result = results[i] });
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
            }
            File.WriteAllText(Path.Combine(stage, "变更说明.txt"), "逐条原始记录重判；保留全部测试历史。未启用规则及待确认结果保留原 BIN。\n规则与 BIN 策略见 rules.json；逐文件变更见 .changes.json。\n" + string.Join("\n", warnings), new UTF8Encoding(false));
            token.ThrowIfCancellationRequested(); Directory.Move(stage, destination);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
