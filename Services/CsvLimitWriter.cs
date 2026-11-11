using System.Globalization;
using System.IO;
using System.Text;
using SemiconductorCsvAnalyzer.Models;
namespace SemiconductorCsvAnalyzer.Services;
internal static class CsvLimitWriter
{
    // Raw token replacement preserves quoting, delimiters, measurement text and multiline fields.
    private static List<string> Tokens(string text, char delimiter, bool quoted)
    {
        var cells = new List<string>(); bool inside = false; int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (quoted && text[i] == '"') { if (inside && i + 1 < text.Length && text[i + 1] == '"') i++; else inside = !inside; }
            if (!inside && text[i] == delimiter) { cells.Add(text[start..i]); start = i + 1; }
        }
        cells.Add(text[start..]); return cells;
    }
    private static IEnumerable<(string Text, string Ending)> Records(TextReader reader, bool quoted)
    {
        var b = new StringBuilder(); bool inside = false; int c;
        while ((c = reader.Read()) >= 0)
        {
            char ch = (char)c;
            if (quoted && ch == '"')
            {
                b.Append(ch);
                if (inside && reader.Peek() == '"') { b.Append((char)reader.Read()); continue; }
                inside = !inside; continue;
            }
            if (!inside && ch is '\r' or '\n')
            {
                string end = ch.ToString(); if (ch == '\r' && reader.Peek() == '\n') end += (char)reader.Read();
                yield return (b.ToString(), end); b.Clear();
            }
            else b.Append(ch);
        }
        if (inside) throw new InvalidDataException("CSV 引号未闭合。");
        if (b.Length > 0) yield return (b.ToString(), "");
    }
    public static void Save(string output, string source, AnalyzerConfig config, RejudgeSession session, RejudgeResult[] results, CancellationToken token)
    {
        using var f = SourceFileReader.Open(source);
        var prefix = new byte[4]; int size = f.Read(prefix); f.Position = 0;
        using var reader = new StreamReader(f, CsvText.EncodingFor(config.EncodingName), true);
        reader.Peek(); Encoding encoding = reader.CurrentEncoding;
        if (encoding.CodePage == 65001) encoding = new UTF8Encoding(size >= 3 && prefix[0] == 239 && prefix[1] == 187 && prefix[2] == 191);
        using var writer = new StreamWriter(output, false, encoding);
        var byRow = session.Data.Records.Select((r, i) => (r.SourceOffset, Result: results[i])).ToDictionary(r => r.SourceOffset, r => r.Result);
        int resultColumn = -1;
        int index = 0;
        var definitions = new Dictionary<ulong, int>();
        foreach (var record in Records(reader, config.HasQuotedFields))
        {
            token.ThrowIfCancellationRequested(); index++;
            var edits = new Dictionary<int, string>();
            ulong signature = CsvText.Signature(CsvParser.ParseLine(record.Text, config.Delimiter, config.HasQuotedFields));
            int definitionRow = index;
            if (index == config.TestItemRow)
            {
                var names = CsvParser.ParseLine(record.Text, config.Delimiter, config.HasQuotedFields);
                for (int i = 0; i < Math.Min(config.TestItemStartColumn - 1, names.Length); i++)
                    if (new[] { "RESULT", "P/F", "PF" }.Contains(names[i].Trim().ToUpperInvariant())) resultColumn = i;
            }
            if (index < config.DataStartRow) definitions.TryAdd(signature, index);
            else if (!byRow.ContainsKey(index) && definitions.TryGetValue(signature, out int originalRow)) definitionRow = originalRow;
            if (definitionRow == config.LowLimitRow || definitionRow == config.HighLimitRow)
                foreach (var item in session.Data.Items)
                {
                    var rule = session.Rules[item.Id];
                    if (definitionRow == config.LowLimitRow && rule.LowChanged) edits[item.ColumnIndex] = LimitRule.Format(rule.Limits().Low);
                    if (definitionRow == config.HighLimitRow && rule.HighChanged) edits[item.ColumnIndex] = LimitRule.Format(rule.Limits().High);
                }
            if (byRow.TryGetValue(index, out var result) && result.Changed)
            { edits[config.SBinColumn - 1] = result.SBin; edits[config.HBinColumn - 1] = result.HBin; if (resultColumn >= 0) edits[resultColumn] = result.Grade.ToString(); }
            if (edits.Count == 0) writer.Write(record.Text);
            else
            {
                var cells = Tokens(record.Text, config.Delimiter, config.HasQuotedFields);
                foreach (var pair in edits) { while (cells.Count <= pair.Key) cells.Add(""); cells[pair.Key] = pair.Value; }
                writer.Write(string.Join(config.Delimiter, cells));
            }
            writer.Write(record.Ending);
        }
    }
    public static void SaveNormalized(string output, RejudgeSession session, RejudgeResult[] results)
    {
        string Cell(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        using var writer = new StreamWriter(output, false, new UTF8Encoding(true));
        var items = session.Data.Items;
        void Row(IEnumerable<string> cells) => writer.WriteLine(string.Join(',', cells.Select(Cell)));
        string[] blanks = new string[12]; Array.Fill(blanks, "");
        Row(new[] { "SN", "X", "Y", "Site", "SBIN", "HBIN", "Wafer", "Product", "Program", "Lot", "Result", "OriginalSN" }.Concat(items.Select(i => i.Name)));
        Row(blanks.Concat(items.Select(i => LimitRule.Format(session.Rules[i.Id].Limits().Low))));
        Row(blanks.Concat(items.Select(i => LimitRule.Format(session.Rules[i.Id].Limits().High))));
        Row(blanks.Concat(items.Select(i => i.Unit)));
        Row(blanks.Concat(items.Select(i => i.TestNumber)));
        for (int i = 0; i < results.Length; i++)
        {
            var r = session.Data.Records[i]; var result = results[i];
            Row(new[] { r.SerialNumber, r.X?.ToString() ?? "", r.Y?.ToString() ?? "", r.Site, result.SBin, result.HBin, r.WaferId, r.Product, r.Program, r.Lot,
                result.Grade is RejudgeGrade.Pending or RejudgeGrade.Disabled ? r.Result : result.Grade.ToString(), r.ChipSerialNumber }.Concat(items.Select(t => r.Measurements.TryGetValue(t.Id, out var raw) ? raw : "")));
        }
        var c = new AnalyzerConfig { DataStartRow = 6, TestItemStartColumn = 13, SerialNumberColumn = 1, XColumn = 2, YColumn = 3, SiteColumn = 4, SBinColumn = 5, HBinColumn = 6,
            WaferColumn = 7, ProductColumn = 8, ProgramColumn = 9, LotColumn = 10, TestItemRow = 1, LowLimitRow = 2, HighLimitRow = 3, UnitRow = 4, TestNumberRow = 5 };
        ConfigLoader.Save(output + ".ini", c);
    }
}
