using System.IO;
using System.Windows;
using System.Windows.Controls;
using SemiconductorCsvAnalyzer.Models;
namespace SemiconductorCsvAnalyzer;
public sealed class RejudgeSettingsWindow : Window
{
    public RejudgeSettings? Value { get; private set; }
    private sealed class PairEditor : Grid
    {
        internal TextBox Software { get; } = new() { Padding = new Thickness(6), MinWidth = 110, Margin = new Thickness(0, 0, 12, 0), ToolTip = "SBIN：软件 BIN，范围 0～65534" };
        internal TextBox Hardware { get; } = new() { Padding = new Thickness(6), MinWidth = 110, ToolTip = "HBIN：硬件 BIN，范围 0～65534" };
        public PairEditor(BinPair pair)
        {
            Margin = new Thickness(0, 3, 0, 3); ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new());
            Software.Text = pair.SBin.ToString(); Hardware.Text = pair.HBin.ToString(); SetColumn(Hardware, 1); Children.Add(Software); Children.Add(Hardware);
        }
        internal BinPair Read()
        {
            ushort Number(TextBox box, string label)
            { if (!ushort.TryParse(box.Text.Trim(), out var value) || value == 65535) { box.Focus(); box.SelectAll(); throw new InvalidDataException(label + " 必须为 0～65534 的整数。"); } return value; }
            return new(Number(Software, "SBIN（软件 BIN）"), Number(Hardware, "HBIN（硬件 BIN）"));
        }
    }
    public RejudgeSettingsWindow(RejudgeSettings settings)
    {
        NameScope.SetNameScope(this, new NameScope());
        Title = "良率调整与分 BIN"; Width = 720; Height = 730; MinWidth = 550; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick }; footer.Children.Add(error);
        var save = new Button { Name = "ApplyBinSettings", Content = "应用并重建原始分类", Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 0), IsDefault = true }; footer.Children.Add(save); RegisterName("ApplyBinSettings", save);
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = "分别填写 SBIN 和 HBIN，无需输入冒号或记住顺序。\n例如：HBIN 1 对应 SBIN 1000，应在左侧填 1000，右侧填 1。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "应用后重新建立原始分类，重置手动限值和参与开关；原始识别与导出目标分别配置。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        void Heading(StackPanel parent)
        {
            var headers = new Grid(); headers.ColumnDefinitions.Add(new()); headers.ColumnDefinitions.Add(new());
            var s = new TextBlock { Text = "SBIN · 软件 BIN", FontWeight = FontWeights.SemiBold }; var h = new TextBlock { Text = "HBIN · 硬件 BIN", FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(h, 1); headers.Children.Add(s); headers.Children.Add(h); parent.Children.Add(headers);
        }
        List<PairEditor> Original(string title, IEnumerable<BinPair> pairs, string key)
        {
            var box = new GroupBox { Header = title, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(10) }; var contents = new StackPanel(); box.Content = contents; panel.Children.Add(box); Heading(contents);
            var rows = new StackPanel(); contents.Children.Add(rows); var editors = new List<PairEditor>(); int serial = 0;
            void Add(BinPair pair)
            {
                var line = new DockPanel(); var editor = new PairEditor(pair); var remove = new Button { Content = "移除", Margin = new Thickness(8, 3, 0, 3), Padding = new Thickness(8, 2, 8, 2) };
                DockPanel.SetDock(remove, Dock.Right); line.Children.Add(remove); line.Children.Add(editor); rows.Children.Add(line); editors.Add(editor);
                RegisterName(key + "SBin" + serial, editor.Software); RegisterName(key + "HBin" + serial++, editor.Hardware);
                remove.Click += (_, _) => { editors.Remove(editor); rows.Children.Remove(line); };
            }
            foreach (var pair in pairs) Add(pair);
            var add = new Button { Content = "+ 添加一组 BIN", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(8, 3, 8, 3) }; contents.Children.Add(add); add.Click += (_, _) => { Add(new(0, 0)); editors[^1].Software.Clear(); editors[^1].Hardware.Clear(); }; return editors;
        }
        var originalPass = Original("原始 Pass 识别（支持多组）", settings.OriginalPass, "OriginalPass");
        var originalPartial = Original("原始 PartialPass 识别（没有则保持空白）", settings.OriginalPartialPass, "OriginalPartial");
        PairEditor Target(string title, BinPair pair, string key)
        {
            var box = new GroupBox { Header = title, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(10) }; var body = new StackPanel(); Heading(body); var row = new PairEditor(pair); body.Children.Add(row); box.Content = body; panel.Children.Add(box);
            RegisterName(key + "SBin", row.Software); RegisterName(key + "HBin", row.Hardware); return row;
        }
        var pass = Target("导出 Pass 目标", settings.Pass, "Pass"); var partial = Target("导出 PartialPass 目标", settings.PartialPass, "PartialPass"); var fail = Target("新增 Fail 目标（统一目标）", settings.Fail, "Fail");
        var include = new CheckBox { Content = "原始良品参考池包含 PartialPass", IsChecked = settings.ReferenceIncludesPartialPass, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(include);
        var recovery = new CheckBox { Content = "原 Fail 恢复时写为 PartialPass（否则 Pass）", IsChecked = settings.RecoverAsPartialPass, Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(recovery);
        panel.Children.Add(new TextBlock { Text = "待确认结果保留原 BIN。不同目标等级须使用不同的完整 BIN 对。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        save.Click += (_, _) =>
        {
            try
            {
                var value = new RejudgeSettings { OriginalPass = originalPass.Select(p => p.Read()).ToList(), OriginalPartialPass = originalPartial.Select(p => p.Read()).ToList(),
                    Pass = pass.Read(), PartialPass = partial.Read(), Fail = fail.Read(), ReferenceIncludesPartialPass = include.IsChecked == true, RecoverAsPartialPass = recovery.IsChecked == true };
                value.Validate(); Value = value; DialogResult = true;
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
    }
}
