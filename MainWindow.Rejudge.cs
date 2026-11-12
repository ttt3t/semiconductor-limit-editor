using System.ComponentModel;
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
    private readonly DispatcherTimer _rejudgeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _rejudgeProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly SemaphoreSlim _rejudgeGate = new(1, 1);
    private CancellationTokenSource? _rejudgeCancellation;
    private RejudgeProgress? _rejudgeProgress;
    private RejudgePreviewResult? _lastPreview;
    private int _rejudgeVersion;
    private bool _linkingRules;
    private static string RejudgeSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemiconductorLimitEditor", "rejudge.json");
    private void InitializeRejudge()
    {
        try { if (File.Exists(RejudgeSettingsPath)) _rejudgeSettings = JsonSerializer.Deserialize<RejudgeSettings>(File.ReadAllText(RejudgeSettingsPath)) ?? new(); _rejudgeSettings.Validate(); }
        catch (Exception ex) { MessageBox.Show("分 BIN 设置读取失败，使用默认值：" + ex.Message); _rejudgeSettings = new(); }
        _rejudgeTimer.Tick += async (_, _) => { _rejudgeTimer.Stop(); await RecalculateRejudgeAsync(); };
        _rejudgeProgressTimer.Tick += (_, _) =>
        {
            if (_rejudgeProgress == null) return;
            var p = _rejudgeProgress.Latest;
            RejudgeProgressBar.Value = p.Fraction * 100; TxtRejudgeProgress.Text = $"{p.Stage} · {(int)(p.Fraction * 100)}%";
        };
        Closed += (_, _) => { StopRejudge(); _rejudgeProgressTimer.Stop(); _lastPreview = null; };
    }
    private void StopRejudge()
    {
        _rejudgeVersion++; _rejudgeTimer.Stop(); _rejudgeCancellation?.Cancel();
        _rejudgeProgress = null; _rejudgeProgressTimer.Stop();
        RejudgeProgressPanel.Visibility = Visibility.Collapsed;
    }
    private void AttachRejudge(RejudgeSession session)
    {
        if (!ReferenceEquals(_rejudge, session))
        {
            StopRejudge(); _lastPreview = null;
            if (_rejudge != null) foreach (var rule in _rejudge.Rules.Values) rule.PropertyChanged -= RuleChanged;
            _rejudge = session;
            foreach (var item in session.Data.Items) item.Rule = session.Rules[item.Id];
            foreach (var rule in session.Rules.Values) rule.PropertyChanged += RuleChanged;
        }
        ScheduleRejudge();
    }
    private void RuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_linkingRules || _rejudge == null || e.PropertyName is not (nameof(LimitRule.NewLowText) or nameof(LimitRule.NewHighText) or nameof(LimitRule.Participates))) return;
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
        if (_closing) return;
        _rejudgeVersion++; _rejudgeCancellation?.Cancel(); _rejudgeTimer.Stop(); _rejudgeTimer.Start();
        if (TxtRejudge != null) TxtRejudge.Text = "等待重算…（显示值尚未更新）";
        ExportRejudgedButton.IsEnabled = false;
    }
    private async void RecalculateNow_Click(object sender, RoutedEventArgs e)
    { _rejudgeTimer.Stop(); await RecalculateRejudgeAsync(); }
    private void CancelRejudge_Click(object sender, RoutedEventArgs e)
    { StopRejudge(); TxtRejudge.Text = "已取消良率重算；保留上次完成的预览，当前修改尚未计算。"; }
    private void LimitBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    { if (e.Column is Controls.LimitValueColumn && e.Row.Item is TestItemSummary { Rule: { Functional: true } }) e.Cancel = true; }
    private async Task RecalculateRejudgeAsync()
    {
        if (_rejudge == null || _closing || _isBusy) return;
        var session = _rejudge; int version = ++_rejudgeVersion;
        _rejudgeCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _rejudgeCancellation = cancellation;
        var progress = _rejudgeProgress = new RejudgeProgress();
        RejudgeProgressPanel.Visibility = Visibility.Visible; RejudgeProgressBar.Value = 0; TxtRejudgeProgress.Text = "准备重算… · 0%";
        _rejudgeProgressTimer.Start(); ExportRejudgedButton.IsEnabled = false;
        bool entered = false;
        try
        {
            // Pin only the existing store handles. Do not enumerate millions of records on the dispatcher.
            using var lease = _datasetLease?.Fork();
            var summaries = _summaries; var scope = ActiveData.Records; var edits = session.CaptureEdits();
            var mode = _retestMode; bool prefer = _config.PreferGoodRetest; var previous = _lastPreview;
            await _rejudgeGate.WaitAsync(cancellation.Token); entered = true;
            cancellation.Token.ThrowIfCancellationRequested();
            var computed = await Task.Run(() => RejudgePreview.Compute(session, edits, summaries, scope, mode, prefer, previous,
                cancellation.Token, progress.Report), cancellation.Token);
            if (version != _rejudgeVersion || _closing || cancellation.IsCancellationRequested) return;
            // Only realized rows have binding listeners; unchanged properties send no notifications.
            foreach (var pair in computed.Items) { pair.Key.YieldChange = pair.Value.Text; pair.Key.YieldDetail = pair.Value.Detail; }
            _lastPreview = computed;
            TxtRejudge.Text = computed.Summary;
            TxtRejudge.ToolTip = computed.Warning ?? "整体统计使用全部导入来源的整条首/末次记录；良品优先按新结果选择。";
            if (computed.Warning != null) TxtRejudge.Text += " · 警告：未识别到原始良品参考";
            ExportRejudgedButton.IsEnabled = true; progress.Complete();
            RejudgeProgressBar.Value = 100; TxtRejudgeProgress.Text = "良率重算完成 · 100%";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version != _rejudgeVersion || _closing) return;
            string message = ex is OutOfMemoryException ? "内存不足，已停止重算并保留上次结果。请减少同时打开的数据后重试。" : ex.Message;
            TxtRejudge.Text = "重算未完成，禁止导出：" + message;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemiconductorLimitEditor", "Logs", $"rejudge-failure-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
                AtomicFile.WriteAllText(path, ex.ToString()); TxtRejudge.ToolTip = "诊断日志：" + path;
            }
            catch (Exception) { TxtRejudge.ToolTip = message; }
        }
        finally
        {
            if (entered) _rejudgeGate.Release();
            if (ReferenceEquals(_rejudgeCancellation, cancellation))
            {
                _rejudgeCancellation = null; _rejudgeProgress = null; _rejudgeProgressTimer.Stop();
                RejudgeProgressPanel.Visibility = Visibility.Collapsed;
            }
        }
    }
    private void RuleCategoryChanged(object sender, SelectionChangedEventArgs e) { if (DgTestItems != null) ApplySearchFilter(_currentItem?.DisplayName, _currentSite, _currentWafer); }
    private void EnableVisibleRules(object sender, RoutedEventArgs e)
    {
        _linkingRules = true;
        try { foreach (var rule in DgTestItems.Items.OfType<TestItemSummary>().Select(s => s.Rule).OfType<LimitRule>().Distinct()) rule.Participates = true; }
        finally { _linkingRules = false; ScheduleRejudge(); }
    }
    private void ResetLimitRules(object sender, RoutedEventArgs e)
    {
        _linkingRules = true;
        try { if (_rejudge != null) foreach (var rule in _rejudge.Rules.Values) rule.Reset(); }
        finally { _linkingRules = false; ScheduleRejudge(); }
    }
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
            button.IsEnabled = false; settings.Close();
            try
            {
                await RunImportAsync(async () =>
                {
                    var value = dialog.Value;
                    var session = await Task.Run(() => new RejudgeSession(_data, value, AnalysisOperation.Token, attachRules: false));
                    var prepared = await PrepareAnalysisAsync(_data, _config, session);
                    AnalysisOperation.CheckCancellation(); _operationCommitting = true;
                    await Task.Run(() => AtomicFile.WriteAllText(RejudgeSettingsPath, JsonSerializer.Serialize(value, LimitExportService.Json)));
                    _rejudgeSettings = value; CommitAnalysis(_data, prepared);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { MessageBox.Show(ex.Message, "分 BIN 设置失败"); }
            finally { button.IsEnabled = true; }
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
            await RunImportAsync(async () =>
            {
                var session = await Task.Run(() =>
                {
                    var created = new RejudgeSession(_data, plan.Settings, AnalysisOperation.Token, attachRules: false);
                    LimitExportService.ApplyPlan(created, plan.Rules); return created;
                });
                var prepared = await PrepareAnalysisAsync(_data, _config, session);
                AnalysisOperation.CheckCancellation(); _rejudgeSettings = plan.Settings; CommitAnalysis(_data, prepared);
            });
        }
        catch (OperationCanceledException) { }
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
