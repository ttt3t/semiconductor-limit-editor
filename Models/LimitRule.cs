using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using System.IO;
namespace SemiconductorCsvAnalyzer.Models;
public enum RuleCategory { Effective, Conflict, Invalid }
public enum RejudgeGrade { Pass, PartialPass, Fail, Pending, Disabled }
public sealed class LimitRule : INotifyPropertyChanged
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public string SharedGroup { get; set; } = "";
    public double? OriginalLow { get; set; }
    public double? OriginalHigh { get; set; }
    public bool IsStdf { get; set; }
    public bool Functional { get; set; }
    public double DisplayFactor { get; set; } = 1;
    public RuleCategory Category { get; set; }
    public int ReferenceCount { get; set; }
    public int ReferenceFailures { get; set; }
    public int ReferenceMissing { get; set; }
    private string _low = "", _high = "";
    private bool _participates = true;
    public string NewLowText { get => _low; set { if (_low == value) return; _low = value; Changed(); } }
    public string NewHighText { get => _high; set { if (_high == value) return; _high = value; Changed(); } }
    public bool Participates { get => _participates; set { if (_participates == value) return; _participates = value; Changed(); } }
    [JsonIgnore] public string CategoryText => Category switch { RuleCategory.Conflict => "冲突", RuleCategory.Invalid => "无效", _ => "有效" };
    [JsonIgnore] public string CategoryColor => Category switch { RuleCategory.Conflict => "#DC2626", RuleCategory.Invalid => "#D97706", _ => "#16A34A" };
    [JsonIgnore] public string Diagnosis => $"{CategoryText}项；原始良品 {ReferenceCount}，失败 {ReferenceFailures}，缺测/无效 {ReferenceMissing}。" + (ReferenceCount == 0 ? "缺少良品样本，未验证分类。" : "分类依据原始阈值冻结。") + (SharedGroup.Length > 0 ? " MPR 共享上下限，同组编辑联动；参与状态可独立选择。" : "");
    [JsonIgnore] public bool LowChanged => NewLowText != Format(OriginalLow * DisplayFactor);
    [JsonIgnore] public bool HighChanged => NewHighText != Format(OriginalHigh * DisplayFactor);
    public static string Format(double? x) => x?.ToString("G17", CultureInfo.InvariantCulture) ?? "";
    public string LowDisplayText => Functional ? "—" : NewLowText;
    public string HighDisplayText => Functional ? "—" : NewHighText;
    public (double? Low, double? High) Limits() => Limits(NewLowText, NewHighText);
    public (double? Low, double? High) Limits(string lowText, string highText)
    {
        double? Read(string s, bool changed, double? original)
        {
            if (!changed) return original;
            if (Functional) throw new InvalidDataException("FTR 不支持修改数值限值；可以切换参与状态。");
            if (string.IsNullOrWhiteSpace(s) || s.Trim() == "-") return null;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v)) throw new InvalidDataException($"{Name}：限值须为有限数值（单位 {Unit}），留空清除该侧。");
            v /= DisplayFactor;
            if (IsStdf) v = (float)v;
            if (!double.IsFinite(v)) throw new InvalidDataException($"{Name}：超出 STDF R*4 可写范围。");
            return v;
        }
        var low = Read(lowText, lowText != Format(OriginalLow * DisplayFactor), OriginalLow); var high = Read(highText, highText != Format(OriginalHigh * DisplayFactor), OriginalHigh);
        if (low > high) throw new InvalidDataException($"{Name}：新 LSL 不能大于新 USL。");
        return (low, high);
    }
    [JsonIgnore] public string WritableLimitsText
    {
        get { try { var limits = Limits(); return $"实际判定/写出：LSL {Format(limits.Low * DisplayFactor)}，USL {Format(limits.High * DisplayFactor)} {Unit}。" + (IsStdf ? "STDF R*4 精度；空白表示无该侧限值。" : "空白清除该侧限值。"); } catch (Exception ex) { return ex.Message; } }
    }
    public void Reset() { NewLowText = Format(OriginalLow * DisplayFactor); NewHighText = Format(OriginalHigh * DisplayFactor); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new(name));
        if (name == nameof(NewLowText)) PropertyChanged?.Invoke(this, new(nameof(LowDisplayText)));
        if (name == nameof(NewHighText)) PropertyChanged?.Invoke(this, new(nameof(HighDisplayText)));
    }
}
public sealed record BinPair(ushort SBin, ushort HBin);
public sealed class RejudgeSettings
{
    public List<BinPair> OriginalPass { get; set; } = new() { new(1, 1) };
    public List<BinPair> OriginalPartialPass { get; set; } = new() { new(3, 3) };
    public BinPair Pass { get; set; } = new(1, 1);
    public BinPair PartialPass { get; set; } = new(3, 3);
    public BinPair Fail { get; set; } = new(2, 2);
    public bool ReferenceIncludesPartialPass { get; set; }
    public bool RecoverAsPartialPass { get; set; }
    public void Validate()
    {
        if (OriginalPass.Intersect(OriginalPartialPass).Any()) throw new InvalidDataException("原始 Pass 与 PartialPass BIN 对不能重叠。");
        if (Pass == PartialPass || Pass == Fail || PartialPass == Fail) throw new InvalidDataException("三个目标等级须使用不同的 SBIN + HBIN 对。");
        if (OriginalPass.Concat(OriginalPartialPass).Append(Pass).Append(PartialPass).Append(Fail).Any(p => p.SBin == 65535 || p.HBin == 65535)) throw new InvalidDataException("65535 是缺失 BIN 标记，不能作为目标或识别 BIN。");
    }
    public RejudgeGrade Original(CsvRecord r)
    {
        if (!ushort.TryParse(r.SBin, out var s) || !ushort.TryParse(r.HBin, out var h) || s == 65535 || h == 65535) return RejudgeGrade.Pending;
        var p = new BinPair(s, h);
        if (OriginalPass.Contains(p)) return RejudgeGrade.Pass;
        if (OriginalPartialPass.Contains(p)) return RejudgeGrade.PartialPass;
        return RejudgeGrade.Fail;
    }
}
public sealed record RejudgeResult(string RecordId, RejudgeGrade Original, RejudgeGrade Grade, string SBin, string HBin, bool Changed, string Reason);
