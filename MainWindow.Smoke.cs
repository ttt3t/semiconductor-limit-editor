using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;
using SemiconductorCsvAnalyzer.Controls;
namespace SemiconductorCsvAnalyzer;
public partial class MainWindow
{
    private static IEnumerable<T> SmokeDescendants<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        { var child = VisualTreeHelper.GetChild(d, i); if (child is T t) yield return t; foreach (var next in SmokeDescendants<T>(child)) yield return next; }
    }
    internal async Task RunRejudgeSmokeAsync(string logPath)
    {
        var lines = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); lines.Add("PASS " + message); }
        TextBox BeginLimitEdit(TestItemSummary row, DataGridColumn column)
        {
            DgTestItems.ScrollIntoView(row, column); DgTestItems.SelectedItem = row; DgTestItems.CurrentCell = new(row, column); DgTestItems.UpdateLayout();
            if (!DgTestItems.BeginEdit()) throw new InvalidOperationException("Limit cell could not enter edit mode");
            DgTestItems.UpdateLayout();
            return SmokeDescendants<TextBox>(DgTestItems).First(b => ReferenceEquals(b.DataContext, row) && b.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "Rule.NewHighText");
        }
        void CommitLimit() { DgTestItems.CommitEdit(DataGridEditingUnit.Cell, true); DgTestItems.CommitEdit(DataGridEditingUnit.Row, true); }
        void Fixture(string path, int rows, int cols)
        {
            using var w = new StreamWriter(path, false, new UTF8Encoding(false));
            w.WriteLine("SN,X,Y,Site,SBIN,HBIN,Wafer," + string.Join(',', Enumerable.Range(1, cols).Select(i => "Test" + i)));
            void Header(string s, IEnumerable<string> cells) => w.WriteLine(s + new string(',', 7) + string.Join(',', cells));
            Header("LSL", Enumerable.Repeat("0", cols)); Header("USL", Enumerable.Repeat("2", cols)); Header("UNIT", Enumerable.Repeat("V", cols)); Header("NUM", Enumerable.Range(1, cols).Select(i => i.ToString()));
            string good = string.Join(',', Enumerable.Repeat("1", cols)); string bad = "3" + (cols > 1 ? "," + string.Join(',', Enumerable.Repeat("1", cols - 1)) : "");
            for (int i = 0; i < rows; i++) w.WriteLine($"R{i},{i},0,1,{(i % 20 == 0 ? 2 : 1)},{(i % 20 == 0 ? 2 : 1)},W," + (i % 20 == 0 ? bad : good));
        }
        try
        {
            _rejudgeSettings = new();
            string folder = Path.Combine(Path.GetTempPath(), "LimitEditor-smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "smoke.csv");
            File.WriteAllText(path, "SN,X,Y,Site,SBIN,HBIN,Wafer,Voltage\nLSL,,,,,,,0\nUSL,,,,,,,2\nUNIT,,,,,,,V\nNUM,,,,,,,101\nA,0,0,1,1,1,W,1\nB,1,0,1,2,2,W,3\n", new UTF8Encoding(false));
            var config = new AnalyzerConfig { DataStartRow = 6, TestItemStartColumn = 8, TestNumberRow = 5, SBinColumn = 5, HBinColumn = 6, WaferColumn = 7 };
            await OpenManagedFileAsync(path, config, StdfImportMode.Standard, RetestMode.Last);
            Check(_rejudge != null && _rejudge.Rules.Count == 1, "real import attaches frozen rules");
            Check(DgTestItems.Columns.Count(c => c.Header is string header && header is "新 LSL" or "新 USL" or "良率变化") == 3, "exactly three new main-list columns");
            var summary = _summaries.First(); var column = DgTestItems.Columns.Single(c => Equals(c.Header, "新 USL"));
            DgTestItems.ScrollIntoView(summary, column); DgTestItems.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!SmokeDescendants<TextBox>(DgTestItems).Any(b => b.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path?.StartsWith("Rule.New") == true), "idle limit cells contain no TextBox controls");
            var editor = BeginLimitEdit(summary, column); editor.Text = "4";
            Check(summary.Rule?.NewHighText == "2", "typing does not launch a calculation before commit");
            CommitLimit(); _rejudgeTimer.Stop(); await RecalculateRejudgeAsync();
            Check(summary.Rule?.NewHighText == "4", "committed edit updates the shared rule");
            Check(TxtRejudge.Text.Contains("恢复 1") && TxtRejudge.Text.Contains("100.00%"), "dispatcher preview displays recovered DUT and overall yield");
            Check(summary.YieldChange.Contains("50.00"), "single-item yield delta binding updates");
            editor = BeginLimitEdit(summary, column); editor.Text = "0"; DgTestItems.CancelEdit(DataGridEditingUnit.Cell); DgTestItems.CancelEdit(DataGridEditingUnit.Row);
            Check(summary.Rule!.NewHighText == "4", "Escape/cancel leaves committed limits unchanged");
            editor = BeginLimitEdit(summary, column); editor.Text = "NaN"; CommitLimit(); _rejudgeTimer.Stop(); await RecalculateRejudgeAsync();
            Check(TxtRejudge.Text.Contains("禁止导出") && !ExportRejudgedButton.IsEnabled, "invalid limits visibly block export without crashing");
            summary.Rule!.Reset(); summary.Rule.Participates = false; _rejudgeTimer.Stop(); await RecalculateRejudgeAsync();
            Check(TxtRejudge.Text.Contains("未启用判定规则"), "empty participation never produces fake all-pass");
            summary.Rule.Functional = true;
            DgTestItems.SelectedItem = summary; DgTestItems.CurrentCell = new(summary, column);
            Check(!DgTestItems.BeginEdit(), "FTR numeric limit cells reject editing"); summary.Rule.Functional = false;
            var settings = new SettingsWindow(config, _ => { }); AddRejudgeSettings(settings);
            Check(((TabControl)settings.FindName("SettingsTabs")).Items.OfType<TabItem>().Any(t => Equals(t.Header, "良率调整与分 BIN")), "dedicated paired-bin settings tab is available"); settings.Close();
            var bins = new RejudgeSettingsWindow(new()) { Owner = this };
            bins.Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                ((TextBox)bins.FindName("OriginalPassSBin0")).Text = "1000"; ((TextBox)bins.FindName("OriginalPassHBin0")).Text = "1";
                ((TextBox)bins.FindName("PassSBin")).Text = "1000"; ((TextBox)bins.FindName("PassHBin")).Text = "1";
                ((TextBox)bins.FindName("FailHBin")).Text = "65535";
                ((Button)bins.FindName("ApplyBinSettings")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(bins.Value == null && bins.IsVisible, "invalid numeric BIN remains in the editor with inline validation");
                ((TextBox)bins.FindName("FailHBin")).Text = "2";
                ((Button)bins.FindName("ApplyBinSettings")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Check(bins.ShowDialog() == true && bins.Value!.OriginalPass.Single() == new BinPair(1000, 1) && bins.Value.Pass == new BinPair(1000, 1), "separate SBIN/HBIN fields preserve asymmetric 1000 and 1 values");
            string wide = Path.Combine(folder, "wide.csv"); Fixture(wide, 8, 5000); await OpenManagedFileAsync(wide, config, StdfImportMode.Standard, RetestMode.Last);
            _rejudgeTimer.Stop(); DgTestItems.UpdateLayout();
            var scroll = SmokeDescendants<ScrollViewer>(DgTestItems).First(); var watch = Stopwatch.StartNew();
            for (int i = 0; i < 60; i++) { scroll.ScrollToVerticalOffset(i * 75); DgTestItems.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Background); }
            lines.Add($"PERF 5,000-item list, 60 virtualized scroll updates: {watch.Elapsed.TotalMilliseconds:F0} ms");
            Check(SmokeDescendants<DataGridRow>(DgTestItems).Count() < 150, "scrolling retains a bounded number of realized rows");
            Check(!SmokeDescendants<TextBox>(DgTestItems).Any(b => b.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path?.StartsWith("Rule.New") == true), "scrolling never instantiates inactive limit editors");
            var toggle = SmokeDescendants<RuleToggle>(DgTestItems).First(); bool was = toggle.Rule!.Participates; toggle.Toggle();
            Check(toggle.Rule.Participates != was && VisualTreeHelper.GetChildrenCount(toggle) == 0, "lightweight participation control toggles without a child visual tree");
            string large = Path.Combine(folder, "large.csv"); Fixture(large, 10000, 256); await OpenManagedFileAsync(large, config, StdfImportMode.Standard, RetestMode.Last);
            _rejudgeTimer.Stop(); var rule = _rejudge!.Rules[_data.Items[0].Id]; rule.NewHighText = "4"; _rejudgeTimer.Stop();
            int beats = 0; Task? calculation = null;
            var heartbeat = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(5) };
            heartbeat.Tick += (_, _) => { if (calculation is { IsCompleted: false }) beats++; }; heartbeat.Start();
            calculation = RecalculateRejudgeAsync();
            Check(RejudgeProgressPanel.IsVisible && MainContent.IsEnabled, "large recalculation shows progress without disabling the GUI");
            await calculation; heartbeat.Stop();
            Check(beats > 0, "dispatcher handles input-priority timer ticks while the real large worker calculates");
            Check(TxtRejudge.Text.Contains("恢复 500") && RejudgeProgressPanel.Visibility == Visibility.Collapsed, "large calculation commits correct results and dismisses progress");
            var completed = _lastPreview;
            rule.NewHighText = "0.5"; _rejudgeTimer.Stop(); var first = RecalculateRejudgeAsync(); CancelRejudge_Click(this, new()); await first;
            Check(ReferenceEquals(_lastPreview, completed) && !ExportRejudgedButton.IsEnabled, "cancellation preserves last completed preview and disables stale export");
            rule.NewHighText = "4"; _rejudgeTimer.Stop(); first = RecalculateRejudgeAsync(); rule.NewHighText = "0.5"; _rejudgeTimer.Stop(); var second = RecalculateRejudgeAsync();
            await Task.WhenAll(first, second);
            Check(TxtRejudge.Text.Contains("新 0.00%") && ExportRejudgedButton.IsEnabled, "rapid edits serialize workers and only the newest result is committed");
            lines.Add("ALL WPF REJUDGE SMOKE CHECKS PASSED"); File.WriteAllLines(logPath, lines); Application.Current.Shutdown(0);
        }
        catch (Exception ex) { lines.Add(ex.ToString()); File.WriteAllLines(logPath, lines); Application.Current.Shutdown(1); }
    }
}
