using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using SemiconductorCsvAnalyzer.Models;
namespace SemiconductorCsvAnalyzer.Controls;
/// <summary>A single drawn checkbox/category glyph, without a templated child visual tree.</summary>
public sealed class RuleToggle : FrameworkElement
{
    public static readonly DependencyProperty RuleProperty = DependencyProperty.Register(nameof(Rule), typeof(LimitRule), typeof(RuleToggle), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, RuleChanged));
    public LimitRule? Rule { get => (LimitRule?)GetValue(RuleProperty); set => SetValue(RuleProperty, value); }
    private LimitRule? _subscribed;
    private static readonly Pen Outline = MakePen(Brushes.SlateGray), Tick = MakePen(Brushes.White);
    private static Pen MakePen(Brush brush) { var p = new Pen(brush, 1.5); p.Freeze(); return p; }
    public RuleToggle()
    {
        Width = 32; Height = 20; Focusable = true; Cursor = Cursors.Hand;
        ToolTip = "参与 DUT 重新分 BIN（独立于关注）";
        Loaded += (_, _) => Subscribe(); Unloaded += (_, _) => Unsubscribe();
        ToolTipOpening += (_, _) => ToolTip = Rule?.Diagnosis ?? "尚未建立规则";
    }
    private static void RuleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { var t = (RuleToggle)d; t.Unsubscribe(); if (t.IsLoaded) t.Subscribe(); }
    private void Subscribe() { if (ReferenceEquals(_subscribed, Rule)) return; Unsubscribe(); _subscribed = Rule; if (_subscribed != null) _subscribed.PropertyChanged += Changed; InvalidateVisual(); }
    private void Unsubscribe() { if (_subscribed != null) _subscribed.PropertyChanged -= Changed; _subscribed = null; }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LimitRule.Participates)) return;
        InvalidateVisual();
        if (UIElementAutomationPeer.FromElement(this) is { } peer)
        {
            var next = Rule?.Participates == true ? ToggleState.On : ToggleState.Off;
            peer.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty, next == ToggleState.On ? ToggleState.Off : ToggleState.On, next);
        }
    }
    internal void Toggle() { if (IsEnabled && Rule != null) Rule.Participates = !Rule.Participates; }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { base.OnMouseLeftButtonDown(e); Focus(); Toggle(); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.Key == Key.Space) { Toggle(); e.Handled = true; } }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var check = new Rect(2, 3, 13, 13); dc.DrawRoundedRectangle(Rule?.Participates == true ? Brushes.RoyalBlue : Brushes.White, Outline, check, 2, 2);
        if (Rule?.Participates == true) { dc.DrawLine(Tick, new Point(4, 9), new Point(7, 12)); dc.DrawLine(Tick, new Point(7, 12), new Point(13, 6)); }
        Brush color = Rule?.ReferenceCount == 0 ? Brushes.Gray : Rule?.Category switch { RuleCategory.Conflict => Brushes.Firebrick, RuleCategory.Invalid => Brushes.DarkOrange, _ => Brushes.ForestGreen };
        dc.DrawEllipse(color, null, new Point(24, 10), 3, 3);
        if (IsKeyboardFocused) dc.DrawRectangle(null, Outline, new Rect(0, 0, 30, 19));
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new RulePeer(this);
    private sealed class RulePeer(RuleToggle owner) : FrameworkElementAutomationPeer(owner), IToggleProvider
    {
        protected override string GetClassNameCore() => nameof(RuleToggle);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;
        protected override string GetNameCore() => "参与重新分 BIN：" + owner.Rule?.Name;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Toggle ? this : base.GetPattern(patternInterface);
        public ToggleState ToggleState => owner.Rule?.Participates == true ? ToggleState.On : ToggleState.Off;
        public void Toggle() { if (!owner.IsEnabled) throw new ElementNotEnabledException(); owner.Toggle(); }
    }
}
