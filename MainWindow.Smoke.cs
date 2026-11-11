using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SemiconductorCsvAnalyzer.Models;
namespace SemiconductorCsvAnalyzer;
public partial class MainWindow
{
    // Opt-in release gate; runs on Windows with the real WPF dispatcher and templates.
    internal async Task RunRejudgeSmokeAsync(string logPath)
    {
        var lines = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); lines.Add("PASS " + message); }
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "LimitEditor-smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "smoke.csv");
            File.WriteAllText(path, "SN,X,Y,Site,SBIN,HBIN,Wafer,Voltage\nLSL,,,,,,,0\nUSL,,,,,,,2\nUNIT,,,,,,,V\nNUM,,,,,,,101\nA,0,0,1,1,1,W,1\nB,1,0,1,2,2,W,3\n", new UTF8Encoding(false));
            var config = new AnalyzerConfig { DataStartRow = 6, TestItemStartColumn = 8, TestNumberRow = 5, SBinColumn = 5, HBinColumn = 6, WaferColumn = 7 };
            await OpenManagedFileAsync(path, config, StdfImportMode.Standard, RetestMode.Last);
            Check(_rejudge != null && _rejudge.Rules.Count == 1, "real import attaches frozen rules");
            Check(DgTestItems.Columns.Count(c => c.Header is string header && header is "新 LSL" or "新 USL" or "良率变化") == 3, "exactly three new main-list columns");
            var summary = _summaries.First(); var column = DgTestItems.Columns.Single(c => Equals(c.Header, "新 USL"));
            DgTestItems.ScrollIntoView(summary, column); DgTestItems.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            IEnumerable<T> Descendants<T>(DependencyObject d) where T : DependencyObject
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                { var child = VisualTreeHelper.GetChild(d, i); if (child is T t) yield return t; foreach (var next in Descendants<T>(child)) yield return next; }
            }
            var box = Descendants<TextBox>(DgTestItems).First(b => ReferenceEquals(b.DataContext, summary) && b.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "Rule.NewHighText");
            box.Text = "4"; box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            await Task.Delay(800); await RecalculateRejudgeAsync();
            Check(summary.Rule?.NewHighText == "4", "virtualized TextBox updates the shared rule");
            Check(TxtRejudge.Text.Contains("恢复 1") && TxtRejudge.Text.Contains("100.00%"), "dispatcher preview displays recovered DUT and overall yield");
            Check(summary.YieldChange.Contains("50.00"), "single-item yield delta binding updates");
            box.Text = "NaN"; box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); await Task.Delay(400);
            Check(TxtRejudge.Text.Contains("规则无效"), "invalid edited bounds visibly block export");
            summary.Rule!.Reset(); summary.Rule.Participates = false; await Task.Delay(400);
            Check(TxtRejudge.Text.Contains("未启用判定规则"), "empty participation is visible instead of fake all-pass");
            var settings = new SettingsWindow(config, _ => { }); AddRejudgeSettings(settings);
            Check(((TabControl)settings.FindName("SettingsTabs")).Items.OfType<TabItem>().Any(t => Equals(t.Header, "良率调整与分 BIN")), "dedicated paired-bin settings tab is available"); settings.Close();
            lines.Add("ALL WPF REJUDGE SMOKE CHECKS PASSED"); File.WriteAllLines(logPath, lines); Application.Current.Shutdown(0);
        }
        catch (Exception ex) { lines.Add(ex.ToString()); File.WriteAllLines(logPath, lines); Application.Current.Shutdown(1); }
    }
}
