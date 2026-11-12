using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SemiconductorCsvAnalyzer.Models;
namespace SemiconductorCsvAnalyzer.Controls;
/// <summary>Plain virtualized text until editing; only the active cell owns a TextBox.</summary>
public sealed class LimitValueColumn : DataGridBoundColumn
{
    public bool IsLower { get; set; }
    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var text = new TextBlock { Padding = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "双击或按 F2 编辑；留空清除该侧限值。" };
        text.SetBinding(TextBlock.TextProperty, new Binding(IsLower ? "Rule.LowDisplayText" : "Rule.HighDisplayText") { Mode = BindingMode.OneWay });
        text.ToolTipOpening += ShowLimits; return text;
    }
    private static void ShowLimits(object sender, ToolTipEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TestItemSummary { Rule: { } rule } } element)
            element.ToolTip = rule.Functional ? "FTR 功能判定没有可编辑的数值限值；使用名称内的参与开关。" : rule.WritableLimitsText + " 双击或按 F2 编辑。";
    }
    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem)
    {
        var editor = new TextBox { Padding = new Thickness(2, 0, 2, 0), BorderThickness = new Thickness(0), MinWidth = 25 };
        editor.SetBinding(TextBox.TextProperty, new Binding(IsLower ? "Rule.NewLowText" : "Rule.NewHighText") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
        return editor;
    }
    protected override object PrepareCellForEdit(FrameworkElement element, RoutedEventArgs e)
    { var editor = (TextBox)element; editor.Focus(); editor.SelectAll(); return editor.Text; }
    protected override bool CommitCellEdit(FrameworkElement element)
    { ((TextBox)element).GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); return true; }
    protected override void CancelCellEdit(FrameworkElement element, object uneditedValue) => ((TextBox)element).Text = (string)uneditedValue;
}
