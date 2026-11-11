using System.IO;
using System.Windows;
using System.Windows.Controls;
using SemiconductorCsvAnalyzer.Models;
namespace SemiconductorCsvAnalyzer;
public sealed class RejudgeSettingsWindow : Window
{
    public RejudgeSettings? Value { get; private set; }
    public RejudgeSettingsWindow(RejudgeSettings settings)
    {
        Title = "良率调整与分 BIN"; Width = 620; Height = 570; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = new ScrollViewer { Content = panel };
        panel.Children.Add(new TextBlock { Text = "原始识别采用 SBIN:HBIN 成对匹配；可填多对，以逗号分隔。\n应用后重新建立分类基准，清除当前手动规则；原始 BIN 与导出目标分开。", TextWrapping = TextWrapping.Wrap });
        TextBox Field(string title, string value)
        {
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 12, 0, 3) });
            var box = new TextBox { Text = value, Padding = new Thickness(5) }; panel.Children.Add(box); return box;
        }
        string P(BinPair pair) => $"{pair.SBin}:{pair.HBin}";
        var originalPass = Field("原始 Pass（SBIN:HBIN，可多对）", string.Join(',', settings.OriginalPass.Select(P)));
        var originalPartial = Field("原始 PartialPass（SBIN:HBIN，可多对）", string.Join(',', settings.OriginalPartialPass.Select(P)));
        var pass = Field("导出 Pass SBIN:HBIN", P(settings.Pass));
        var partial = Field("导出 PartialPass SBIN:HBIN", P(settings.PartialPass));
        var fail = Field("新增 Fail SBIN:HBIN（统一目标，不使用隐含测项优先级）", P(settings.Fail));
        var include = new CheckBox { Content = "原始良品参考池包含 PartialPass", IsChecked = settings.ReferenceIncludesPartialPass, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(include);
        var recovery = new CheckBox { Content = "原 Fail 恢复时写为 PartialPass（否则 Pass）", IsChecked = settings.RecoverAsPartialPass, Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(recovery);
        panel.Children.Add(new TextBlock { Text = "待确认：始终保留原 BIN 并列入清单。各等级 BIN 对必须不同。", Margin = new Thickness(0, 12, 0, 0) });
        var save = new Button { Content = "应用并重建原始分类", Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(8), IsDefault = true }; panel.Children.Add(save);
        BinPair Parse(string text)
        {
            var parts = text.Trim().Split(':');
            if (parts.Length != 2 || !ushort.TryParse(parts[0], out var s) || !ushort.TryParse(parts[1], out var h)) throw new InvalidDataException("BIN 对格式为 SBIN:HBIN，例如 1:1。");
            return new(s, h);
        }
        save.Click += (_, _) =>
        {
            try
            {
                var value = new RejudgeSettings { OriginalPass = originalPass.Text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToList(),
                    OriginalPartialPass = originalPartial.Text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToList(), Pass = Parse(pass.Text), PartialPass = Parse(partial.Text), Fail = Parse(fail.Text),
                    ReferenceIncludesPartialPass = include.IsChecked == true, RecoverAsPartialPass = recovery.IsChecked == true };
                value.Validate(); Value = value; DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "分 BIN 设置", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
    }
}
