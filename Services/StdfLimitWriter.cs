using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using SemiconductorCsvAnalyzer.Models;
namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Replay the original STDF stream; retain unknown records and patch fields by source offsets.</summary>
internal static class StdfLimitWriter
{
    private sealed class Counts
    {
        public uint Parts, Good;
        public Dictionary<ushort, uint> Hardware = new(), Software = new(), HardwareGood = new(), SoftwareGood = new();
        public void Add(byte[] p, bool le)
        {
            Parts++; if ((p[2] & 0x18) == 0) Good++;
            ushort h = U2(p, 5, le), s = U2(p, 7, le);
            Hardware[h] = Hardware.GetValueOrDefault(h) + 1; Software[s] = Software.GetValueOrDefault(s) + 1;
            if ((p[2] & 0x18) == 0) { HardwareGood[h] = HardwareGood.GetValueOrDefault(h) + 1; SoftwareGood[s] = SoftwareGood.GetValueOrDefault(s) + 1; }
        }
    }
    private static ushort U2(byte[] p, int i, bool le) => le ? BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(i)) : BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(i));
    private static uint U4(byte[] p, int i, bool le) => le ? BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(i)) : BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(i));
    private static void Put2(byte[] p, int i, ushort v, bool le) { if (le) BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(i), v); else BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(i), v); }
    private static void Put4(byte[] p, int i, uint v, bool le) { if (le) BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(i), v); else BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(i), v); }
    private static void Float(byte[] p, int i, double? v, bool le) => Put4(p, i, unchecked((uint)BitConverter.SingleToInt32Bits((float)(v ?? 0))), le);
    private static void Record(Stream output, byte type, byte sub, byte[] p, bool le)
    {
        if (p.Length > ushort.MaxValue) throw new InvalidDataException("STDF 记录超过 U*2 长度。");
        byte[] h = new byte[4]; Put2(h, 0, (ushort)p.Length, le); h[2] = type; h[3] = sub; output.Write(h); output.Write(p);
    }
    private static int SkipCn(byte[] p, int i) { if (i >= p.Length) return i + 1; int next = i + 1 + p[i]; if (next > p.Length) throw new InvalidDataException("STDF C*n 字段截断。"); return next; }
    private static byte[] PatchTest(byte[] original, byte sub, IReadOnlyList<TestItemData> items, Dictionary<string, LimitRule> rules, bool le)
    {
        if (items.Count == 0 || sub is not (10 or 15)) return original;
        var selected = items.Select(i => rules.GetValueOrDefault(i.SourceKey)).ToArray();
        if (selected.Any(r => r == null)) throw new InvalidDataException("STDF 稳定测项定位未匹配规则。");
        var rule = selected[0]!;
        if (selected.Any(r => r!.Limits() != rule.Limits())) throw new InvalidDataException("MPR 同一记录各通道限值不一致。");
        int count = sub == 15 ? U2(original, 10, le) : 1;
        int startResults = sub == 15 ? 12 + (U2(original, 8, le) + 1) / 2 : 8;
        int text = startResults + count * 4;
        int options = SkipCn(original, SkipCn(original, text));
        // Materialize inherited limits on every executed PTR/MPR. This prevents changed defaults leaking into unedited records.
        var p = new byte[Math.Max(original.Length, options + 12)]; original.CopyTo(p, 0);
        var limits = rule.Limits();
        byte oldOpt = options < original.Length ? original[options] : (byte)0;
        p[options] = (byte)((oldOpt & 0x0f) | (limits.Low == null ? 0x40 : 0) | (limits.High == null ? 0x80 : 0));
        Float(p, options + 4, limits.Low, le); Float(p, options + 8, limits.High, le);
        if (rule.LowChanged) p[7] |= 0x40;
        if (rule.HighChanged) p[7] |= 0x80;
        if ((rule.LowChanged || rule.HighChanged) && (p[6] & 0x7f) == 0 && (p[7] & 7) == 0)
        {
            bool failed = false;
            for (int i = 0; i < count; i++)
            {
                float value = BitConverter.Int32BitsToSingle(unchecked((int)U4(p, startResults + 4 * i, le)));
                if (!float.IsFinite(value)) return original;
                failed |= value < limits.Low || value > limits.High;
            }
            p[6] = (byte)((p[6] & ~0xc0) | (failed ? 0x80 : 0));
        }
        return p;
    }
    public static void Save(string output, string source, AnalyzerConfig config, StdfImportMode mode, RejudgeSession session, RejudgeResult[] results, CancellationToken token)
    {
        using var input = SourceFileReader.Open(source);
        bool le; using (var reader = new StdfReader(input)) le = reader.LittleEndian;
        var rules = session.Rules.Values.ToDictionary(r => r.Key);
        var decisions = session.Data.Records.Select((r, i) => (r.SourceOffset, Result: results[i])).ToDictionary(r => r.SourceOffset, r => r.Result);
        string temporary = output + ".staging";
        var totals = new Dictionary<(byte Head, byte Site), Counts>();
        Counts Total(byte h, byte s) { if (!totals.TryGetValue((h, s), out var c)) totals[(h, s)] = c = new(); return c; }
        var groups = new Dictionary<(byte, byte), byte>();
        var wafers = new Dictionary<(byte, byte), Counts>();
        var waferSummaries = new Dictionary<long, Counts>();
        var tests = new Dictionary<(byte Head, byte Site, byte Kind, uint Number), (uint Executed, uint Failed, uint Alarm)>();
        var binScopes = new HashSet<(byte Head, byte Site, byte Sub)>();
        var existingBins = new HashSet<(byte Head, byte Site, byte Sub, ushort Bin)>();
        try
        {
            using (var spool = File.Create(temporary))
            {
                Record(spool, 0, 10, new byte[] { le ? (byte)2 : (byte)1, 4 }, le);
                void Observe(long offset, byte type, byte sub, byte[] payload, IReadOnlyList<TestItemData> items)
                {
                    token.ThrowIfCancellationRequested(); byte[] p = payload;
                    if (type == 1 && sub == 80 && p.Length >= 3)
                        for (int i = 0; i < p[2]; i++) groups[(p[0], p[3 + i])] = p[1];
                    if (type == 2 && sub == 10 && p.Length >= 2) wafers[(p[0], p[1])] = new();
                    if (type == 15 && sub is 10 or 15) p = PatchTest(p, sub, items, rules, le);
                    if (type == 15 && sub is 10 or 15 or 20 && p.Length >= 7)
                    {
                        var key = (p[4], p[5], sub, U4(p, 0, le)); var old = tests.GetValueOrDefault(key);
                        bool executed = (p[6] & 0x10) == 0;
                        tests[key] = (old.Executed + (executed ? 1u : 0u), old.Failed + (executed && (p[6] & 0xc0) == 0x80 ? 1u : 0u), old.Alarm + ((p[6] & 1) != 0 ? 1u : 0u));
                    }
                    if (type == 5 && sub == 20)
                    {
                        if (p.Length < 9 || !decisions.TryGetValue(offset, out var decision)) throw new InvalidDataException("PRR 原始偏移未能匹配 DUT 判定。");
                        if (decision.Changed)
                        {
                            Put2(p, 5, ushort.Parse(decision.HBin), le); Put2(p, 7, ushort.Parse(decision.SBin), le);
                            p[2] = (byte)((p[2] & ~0x18) | (decision.Grade == RejudgeGrade.Fail ? 8 : 0));
                        }
                        Total(p[0], p[1]).Add(p, le);
                        Counts? wafer = null;
                        if (groups.TryGetValue((p[0], p[1]), out var group)) wafers.TryGetValue((p[0], group), out wafer);
                        if (wafer == null) wafers.TryGetValue((p[0], 255), out wafer);
                        if (wafer == null) { var candidates = wafers.Where(k => k.Key.Item1 == p[0]).Select(k => k.Value).ToArray(); if (candidates.Length == 1) wafer = candidates[0]; }
                        wafer?.Add(p, le);
                    }
                    if (type == 2 && sub == 20 && p.Length >= 2)
                        if (wafers.Remove((p[0], p[1]), out var wafer)) waferSummaries[offset] = wafer;
                    if (type == 1 && sub is 40 or 50 && p.Length >= 4)
                    { binScopes.Add((p[0], p[1], sub)); existingBins.Add((p[0], p[1], sub, U2(p, 2, le))); }
                    Record(spool, type, sub, p, le);
                }
                var replay = StdfParser.ParseCore(source, config, null, token, mode, true, Observe);
                using var lease = new DatasetLease(replay.Records);
                if (replay.ImportWarning.Length > 0) throw new InvalidDataException("STDF 结构警告阻止原格式写出：" + replay.ImportWarning);
            }
            IEnumerable<Counts> Matching(byte h, byte s) => totals.Where(kv => (h == 255 || kv.Key.Head == h) && (s is 0 or 255 || kv.Key.Site == s)).Select(kv => kv.Value);
            uint Parts(byte h, byte s) => (uint)Matching(h, s).Sum(c => (long)c.Parts);
            uint Good(byte h, byte s) => (uint)Matching(h, s).Sum(c => (long)c.Good);
            uint Bin(byte h, byte s, byte sub, ushort b) => (uint)Matching(h, s).Sum(c => (long)(sub == 40 ? c.Hardware : c.Software).GetValueOrDefault(b));
            byte BinFlag(byte h, byte s, byte sub, ushort bin)
            {
                uint count = Bin(h, s, sub, bin);
                long good = Matching(h, s).Sum(c => (long)(sub == 40 ? c.HardwareGood : c.SoftwareGood).GetValueOrDefault(bin));
                return count == 0 || good > 0 && good != count ? (byte)' ' : good == count ? (byte)'P' : (byte)'F';
            }
            using var raw = File.Create(output);
            using Stream target = output.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(raw, CompressionLevel.Optimal, true) : new BufferedStream(raw, 65536);
            using var temp = SourceFileReader.Open(temporary); using var stream = new StdfReader(temp);
            Record(target, 0, 10, new byte[] { le ? (byte)2 : (byte)1, 4 }, le);
            var originalWrr = new Queue<Counts>(waferSummaries.OrderBy(p => p.Key).Select(p => p.Value));
            while (stream.Next())
            {
                token.ThrowIfCancellationRequested(); byte type = stream.Type, sub = stream.Subtype; var p = stream.Payload;
                if (type == 1 && sub == 30 && p.Length >= 22) { Put4(p, 2, Parts(p[0], p[1]), le); Put4(p, 14, Good(p[0], p[1]), le); }
                if (type == 1 && sub is 40 or 50 && p.Length >= 9) { ushort bin = U2(p, 2, le); Put4(p, 4, Bin(p[0], p[1], sub, bin), le); p[8] = BinFlag(p[0], p[1], sub, bin); }
                if (type == 2 && sub == 20 && p.Length >= 26)
                {
                    if (originalWrr.Count == 0) throw new InvalidDataException("WRR 没有明确 WIR/Site Group 归属。");
                    var c = originalWrr.Dequeue(); Put4(p, 6, c.Parts, le); Put4(p, 18, c.Good, le);
                }
                if (type == 10 && sub == 30 && p.Length >= 19)
                {
                    byte kind = p[2] switch { (byte)'P' => 10, (byte)'M' => 15, (byte)'F' => 20, _ => 0 };
                    if (kind != 0)
                    {
                        var counts = tests.Where(k => (p[0] == 255 || k.Key.Head == p[0]) && (p[1] is 0 or 255 || k.Key.Site == p[1]) && k.Key.Kind == kind && k.Key.Number == U4(p, 3, le)).Select(k => k.Value).ToArray();
                        Put4(p, 7, (uint)counts.Sum(c => (long)c.Executed), le); Put4(p, 11, (uint)counts.Sum(c => (long)c.Failed), le); Put4(p, 15, (uint)counts.Sum(c => (long)c.Alarm), le);
                    }
                }
                if (type == 1 && sub == 20)
                {
                    // Emit new BIN definitions/counts in every existing summary scope.
                    foreach (var scope in binScopes)
                    {
                        var bins = Matching(scope.Head, scope.Site).SelectMany(c => (scope.Sub == 40 ? c.Hardware : c.Software).Keys).Distinct();
                        foreach (ushort bin in bins)
                            if (!existingBins.Contains((scope.Head, scope.Site, scope.Sub, bin)))
                            {
                                var b = new byte[10]; b[0] = scope.Head; b[1] = scope.Site; Put2(b, 2, bin, le); Put4(b, 4, Bin(scope.Head, scope.Site, scope.Sub, bin), le);
                                b[8] = BinFlag(scope.Head, scope.Site, scope.Sub, bin); Record(target, 1, scope.Sub, b, le);
                            }
                    }
                }
                Record(target, type, sub, p, le);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
