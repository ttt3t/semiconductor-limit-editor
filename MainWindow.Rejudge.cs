using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;
namespace SemiconductorCsvAnalyzer;
public partial class MainWindow
{
    private RejudgeSession? _rejudge;
    private RejudgeSettings _rejudgeSettings = new();
    private readonly DispatcherTimer _rejudgeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _rejudgeCancellation;
    private int _rejudgeVersion;
    private bool _linkingRules;
    private Dictionary<TestItemSummary, (double? Low, double? High, bool Flags, string Text, string Detail)> _yieldPreviewCache = new();
    private HashSet<string> _yieldSelectedIds = new(), _yieldScopeIds = new();
    private static string RejudgeSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemiconductorLimitEditor", "rejudge.json");
    private void InitializeRejudge()
    {
        try { if (File.Exists(RejudgeSettingsPath)) _rejudgeSettings = JsonSerializer.Deserialize<RejudgeSettings>(File.ReadAllText(RejudgeSettingsPath)) ?? new(); _rejudgeSettings.Validate(); }
        catch (Exception ex) { MessageBox.Show("分 BIN 设置读取失败，使用默认值：" + ex.Message); _rejudgeSettings = new(); }
        _rejudgeTimer.Tick += async (_, _) => { _rejudgeTimer.Stop(); await RecalculateRejudgeAsync(); };
        Closed += (_, _) => { _rejudgeTimer.Stop(); _rejudgeCancellation?.Cancel(); };
    }
    private void AttachRejudge(RejudgeSession session)
    {
        if (!ReferenceEquals(_rejudge, session))
        {
            if (_rejudge != null) foreach (var rule in _rejudge.Rules.Values) rule.PropertyChanged -= RuleChanged;
            _rejudge = session;
            foreach (var rule in session.Rules.Values) rule.PropertyChanged += RuleChanged;
        }
        ScheduleRejudge();
    }
    private void RuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_linkingRules || _rejudge == null) return;
        if (sender is LimitRule rule && rule.SharedGroup.Length > 0 && e.PropertyName is nameof(LimitRule.NewLowText) or nameof(LimitRule.NewHighText))
        {
            _linkingRules = true;
            try { foreach (var other in _rejudge.Rules.Values.Where(r => r.SharedGroup == rule.SharedGroup)) { other.NewLowText = rule.NewLowText; other.NewHighText = rule.NewHighText; } }
            finally { _linkingRules = false; }
        }
        ScheduleRejudge();
    }
    private void ScheduleRejudge()
    {
        _rejudgeVersion++; _rejudgeCancellation?.Cancel(); _rejudgeTimer.Stop(); _rejudgeTimer.Start();
        if (TxtRejudge != null) TxtRejudge.Text = "正在重新判定…";
    }
    private async Task RecalculateRejudgeAsync()
    {
        if (_rejudge == null || _closing) return;
        var session = _rejudge; int version = _rejudgeVersion;
        var cancellation = new CancellationTokenSource(); _rejudgeCancellation = cancellation;
        try
        {
            var summaries = _summaries.ToArray();
            // Capture editable rules before leaving the dispatcher.
            var snapshots = session.Rules.ToDictionary(p => p.Key, p => (Limits: p.Value.Limits(), p.Value.LowChanged, p.Value.HighChanged));
            var scope = ActiveData.Records.Select(r => r.Id).ToHashSet();
            var previousCache = _yieldPreviewCache; var previousSelected = _yieldSelectedIds; var previousScope = _yieldScopeIds;
            var mode = _retestMode; bool prefer = _config.PreferGoodRetest;
            var computed = await Task.Run(() =>
            {
                var results = session.Evaluate(cancellation.Token);
                var selected = RejudgeSession.Select(session.Data, results, mode, prefer);
                var changes = new Dictionary<TestItemSummary, (string Text, string Detail)>();
                var selectedIds = selected.Select(r => r.RecordId).ToHashSet();
                bool sameRows = previousSelected.SetEquals(selectedIds) && previousScope.SetEquals(scope);
                foreach (var s in summaries)
                {
                    cancellation.Token.ThrowIfCancellationRequested(); var limits = snapshots[s.Data.Id].Limits;
                    bool flags = !snapshots[s.Data.Id].LowChanged && !snapshots[s.Data.Id].HighChanged;
                    if (sameRows && previousCache.TryGetValue(s, out var cached) && cached.Low == limits.Low && cached.High == limits.High && cached.Flags == flags)
                    { changes[s] = (cached.Text, cached.Detail); continue; }
                    int total = 0, oldPass = 0, newPass = 0;
                    int visited = 0;
                    foreach (var r in session.Data.Records)
                    {
                        if ((++visited & 255) == 0) cancellation.Token.ThrowIfCancellationRequested();
                        if (!scope.Contains(r.Id) || !selectedIds.Contains(r.Id) || !s.IsCombined && s.Site != r.Site || !s.IsWaferCombined && s.Wafer != r.WaferId) continue;
                        if (!r.Measurements.TryGetValue(s.Data.Id, out var text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)) continue;
                        total++;
                        bool old = !(v < s.Data.LowLimit || v > s.Data.HighLimit) && r.ExplicitTestFailures?.Contains(s.Data.Id) != true;
                        if (old) oldPass++;
                        if (!(v < limits.Low || v > limits.High) && (snapshots[s.Data.Id].LowChanged || snapshots[s.Data.Id].HighChanged || r.ExplicitTestFailures?.Contains(s.Data.Id) != true)) newPass++;
                    }
                    double oldYield = total == 0 ? 0 : 100.0 * oldPass / total, newYield = total == 0 ? 0 : 100.0 * newPass / total;
                    changes[s] = (total == 0 ? "—" : (newYield - oldYield).ToString("+0.00;-0.00;0.00") + " pp", $"同一统计范围内有效样本 {total}；原良率 {oldYield:F3}%，新良率 {newYield:F3}%。不等同整颗 DUT 良率。STDF 采用 R*4 实际可写限值。");
                }
                return (selected, changes, selectedIds);
            }, cancellation.Token);
            if (version != _rejudgeVersion || _closing) return;
            var rows = computed.selected;
            double Yield(bool original) => rows.Count == 0 ? 0 : 100.0 * rows.Count(r => (original ? r.Original : r.Grade) is RejudgeGrade.Pass or RejudgeGrade.PartialPass) / rows.Count;
            TxtRejudge.Text = $"DUT {rows.Count} · 原 {Yield(true):F2}% → 新 {Yield(false):F2}% · 恢复 {rows.Count(r => r.Changed && r.Original == RejudgeGrade.Fail)} · 新增不良 {rows.Count(r => r.Changed && r.Grade == RejudgeGrade.Fail)} · 待确认 {rows.Count(r => r.Grade == RejudgeGrade.Pending)}";
            if (rows.Any(r => r.Grade == RejudgeGrade.Disabled)) TxtRejudge.Text = "未启用判定规则，保留原 BIN";
            TxtRejudge.ToolTip = "整体统计使用全部导入来源的整条首/末次记录；良品优先按新结果选最新良品。导出保留所有测试记录。";
            _yieldSelectedIds = computed.selectedIds; _yieldScopeIds = scope;
            _yieldPreviewCache = computed.changes.ToDictionary(p => p.Key, p => (snapshots[p.Key.Data.Id].Limits.Low, snapshots[p.Key.Data.Id].Limits.High, !snapshots[p.Key.Data.Id].LowChanged && !snapshots[p.Key.Data.Id].HighChanged, p.Value.Text, p.Value.Detail));
            foreach (var pair in computed.changes) { pair.Key.YieldChange = pair.Value.Text; pair.Key.YieldDetail = pair.Value.Detail; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _rejudgeVersion) TxtRejudge.Text = "规则无效，禁止导出：" + ex.Message; }
        finally { if (ReferenceEquals(_rejudgeCancellation, cancellation)) _rejudgeCancellation = null; cancellation.Dispose(); }
    }
    private void RuleCategoryChanged(object sender, SelectionChangedEventArgs e) { if (DgTestItems != null) ApplySearchFilter(_currentItem?.DisplayName, _currentSite, _currentWafer); }
    private void EnableVisibleRules(object sender, RoutedEventArgs e)
    { foreach (var rule in DgTestItems.Items.OfType<TestItemSummary>().Select(s => s.Rule).OfType<LimitRule>().Distinct()) rule.Participates = true; }
    private void ResetLimitRules(object sender, RoutedEventArgs e) { if (_rejudge != null) foreach (var rule in _rejudge.Rules.Values) rule.Reset(); }
    private void AddRejudgeSettings(SettingsWindow settings)
    {
        if (settings.FindName("SettingsTabs") is not TabControl tabs) return;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "配置原始 Pass/PartialPass 识别、双 BIN 导出目标与恢复等级。待确认结果保留原 BIN。", TextWrapping = TextWrapping.Wrap });
        var button = new Button { Content = "编辑良率调整与分 BIN设置…", Margin = new Thickness(0, 15, 0, 0), Padding = new Thickness(10) }; panel.Children.Add(button);
        button.Click += async (_, _) =>
        {
            var dialog = new RejudgeSettingsWindow(_rejudgeSettings) { Owner = settings };
            if (dialog.ShowDialog() != true || dialog.Value == null) return;
            try
            {
                AtomicFile.WriteAllText(RejudgeSettingsPath, JsonSerializer.Serialize(dialog.Value, LimitExportService.Json));
                _rejudgeSettings = dialog.Value;
                if (_data.Items.Count > 0)
                {
                    var session = await Task.Run(() => new RejudgeSession(_data, _rejudgeSettings)); AttachRejudge(session);
                    await RefreshTestItemList();
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "分 BIN 设置失败"); }
        };
        tabs.Items.Insert(0, new TabItem { Header = "良率调整与分 BIN", Content = panel });
    }
    private void SaveLimitPlan(object sender, RoutedEventArgs e)
    {
        if (_rejudge == null) return;
        var dialog = new SaveFileDialog { Filter = "规则方案|*.json", FileName = "limits.rules.json" };
        if (dialog.ShowDialog() == true) try { foreach (var r in _rejudge.Rules.Values) r.Limits(); LimitExportService.SavePlan(dialog.FileName, _rejudge); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "保存规则失败"); }
    }
    private async void LoadLimitPlan(object sender, RoutedEventArgs e)
    {
        if (_rejudge == null) return;
        var dialog = new OpenFileDialog { Filter = "规则方案|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var plan = LimitExportService.ReadPlan(dialog.FileName);
            var session = await Task.Run(() => new RejudgeSession(_data, plan.Settings));
            LimitExportService.ApplyPlan(session, plan.Rules); _rejudgeSettings = plan.Settings; AttachRejudge(session); await RefreshTestItemList();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "加载规则失败"); }
    }
    private async void ExportRejudged(object sender, RoutedEventArgs e)
    {
        if (_rejudge == null || _sources.Count == 0 || _isBusy) return;
        var session = _rejudge;
        try
        {
            foreach (var rule in session.Rules.Values) rule.Limits();
            var dialog = new OpenFolderDialog { Title = "选择导出父目录（创建新的独立结果目录，不覆盖源文件）" };
            if (dialog.ShowDialog() != true) return;
            string output = Path.Combine(dialog.FolderName, "LimitEditor_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);
            await RunBusyAsync(async () => await Task.Run(() => LimitExportService.ExportBundle(output, _sources, _config, session, session.Fingerprints, AnalysisOperation.Token)));
            TxtStatus.Text = "已导出重判文件、规则与变更清单：" + output;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(ex.Message, "导出失败（未发布部分结果）", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
