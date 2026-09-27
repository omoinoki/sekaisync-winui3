using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using SekaiSync.Desktop.Services;

namespace SekaiSync.Desktop.Controls;

/// <summary>
/// 两栏之间的有界拖拽分栏：比例限界 + 按页持久化 + 键盘可调。
///
/// 指针坐标必须相对父网格取，不能相对手柄自身取：改比例会让手柄自己横向移动，
/// 以手柄为参照等于把位移反馈进位移里，拖拽时两栏会来回抽搐。
/// 几何（宽 14 + 左右 2 边距）与资讯页原有的分栏手柄保持一致。
/// </summary>
public sealed class ColumnSplitHandle : Button
{
    public static readonly DependencyProperty LeftProperty = DependencyProperty.Register(
        nameof(Left), typeof(ColumnDefinition), typeof(ColumnSplitHandle), new PropertyMetadata(null));

    public static readonly DependencyProperty RightProperty = DependencyProperty.Register(
        nameof(Right), typeof(ColumnDefinition), typeof(ColumnSplitHandle), new PropertyMetadata(null));

    public static readonly DependencyProperty MinRatioProperty = DependencyProperty.Register(
        nameof(MinRatio), typeof(double), typeof(ColumnSplitHandle), new PropertyMetadata(0.25));

    public static readonly DependencyProperty MaxRatioProperty = DependencyProperty.Register(
        nameof(MaxRatio), typeof(double), typeof(ColumnSplitHandle), new PropertyMetadata(0.60));

    public static readonly DependencyProperty SettingsKeyProperty = DependencyProperty.Register(
        nameof(SettingsKey), typeof(string), typeof(ColumnSplitHandle), new PropertyMetadata(string.Empty));

    private bool _dragging;
    private double _ratio = double.NaN;
    private double _pressX;
    private double _startRatio;

    public ColumnSplitHandle()
    {
        Width = 14;
        MinWidth = 0;
        Margin = new Thickness(2, 0, 2, 0);
        Padding = new Thickness(0);
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BorderThickness = new Thickness(0);
        VerticalAlignment = VerticalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        Content = new Rectangle
        {
            Width = 3,
            Height = 48,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SkHairlineBrush"],
        };
        AutomationProperties.SetName(this, "调整左右两栏宽度");
        AutomationProperties.SetHelpText(this, "拖动或使用左右方向键调整；Home 与 End 移到限界两端。");

        Loaded += (_, _) => ApplyStoredRatio();
        // 不能用 +=：Button 自己会把指针事件标记为已处理，实例委托收不到，必须 handledEventsToo。
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnMoved), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        PointerCaptureLost += (_, _) => { if (_dragging) { _dragging = false; Persist(); } };
        KeyDown += OnKeyDown;
    }

    public ColumnDefinition? Left { get => (ColumnDefinition?)GetValue(LeftProperty); set => SetValue(LeftProperty, value); }
    public ColumnDefinition? Right { get => (ColumnDefinition?)GetValue(RightProperty); set => SetValue(RightProperty, value); }
    public double MinRatio { get => (double)GetValue(MinRatioProperty); set => SetValue(MinRatioProperty, value); }
    public double MaxRatio { get => (double)GetValue(MaxRatioProperty); set => SetValue(MaxRatioProperty, value); }
    public string SettingsKey { get => (string)GetValue(SettingsKeyProperty); set => SetValue(SettingsKeyProperty, value); }

    /// <summary>原样读落盘值；页面不要直接用，走 <see cref="StoredRatioClamped"/>。</summary>
    private static double RatioFor(string key, double fallback) =>
        AppServices.Settings.ColumnSplitRatios.TryGetValue(key, out var value) ? value : fallback;

    /// <summary>
    /// 页面自己排布列宽时走这里，不要直接读落盘值：限界值是随页面内容调整的，
    /// 旧版落盘的比例可能已经越界，不夹就会把某一栏压成 0 宽。
    /// </summary>
    public double StoredRatioClamped(double fallback) =>
        Math.Clamp(RatioFor(SettingsKey, fallback), MinRatio, MaxRatio);

    private double StoredRatio => SettingsKey.Length > 0 ? RatioFor(SettingsKey, double.NaN) : double.NaN;

    private void ApplyStoredRatio()
    {
        var ratio = StoredRatio;
        // 限界值会随页面内容调整，旧落盘值可能已经越界，回填前先夹回来。
        if (!double.IsNaN(ratio)) SetRatio(Math.Clamp(ratio, MinRatio, MaxRatio));
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _pressX = PointerX(e);
        _startRatio = CurrentRatio;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var total = (Left?.ActualWidth ?? 0) + (Right?.ActualWidth ?? 0);
        if (total <= 0) return;

        // 以按下时的比例为基准换算总位移，不逐步回读列宽：布局刷新有一帧延迟，逐步累加会漂。
        SetRatio(_startRatio + (PointerX(e) - _pressX) / total);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        Persist();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ratio = e.Key switch
        {
            Windows.System.VirtualKey.Left => CurrentRatio - 0.05,
            Windows.System.VirtualKey.Right => CurrentRatio + 0.05,
            Windows.System.VirtualKey.Home => MinRatio,
            Windows.System.VirtualKey.End => MaxRatio,
            _ => double.NaN,
        };
        if (double.IsNaN(ratio)) return;

        SetRatio(ratio);
        Persist();
        e.Handled = true;
    }

    private double PointerX(PointerRoutedEventArgs e) =>
        e.GetCurrentPoint(Parent as UIElement ?? this).Position.X;

    /// <summary>
    /// 生效比例存在字段里；初值取两栏在 XAML 里声明的星号比例，绝不从 ActualWidth 反推。
    /// Loaded 时详情列可能还没量出宽度，Left/(Left+0) 会算成 1.0，于是每访问一页
    /// 都被夹到上限并写回配置，分栏从此卡死在上界；ActualWidth 也要等一帧布局才更新，
    /// 连按方向键会每步从同一个旧值出发。
    /// </summary>
    private double CurrentRatio => double.IsNaN(_ratio) ? DeclaredRatio() : _ratio;

    private double DeclaredRatio()
    {
        var left = Left?.Width ?? default;
        var right = Right?.Width ?? default;
        if (left.GridUnitType != GridUnitType.Star || right.GridUnitType != GridUnitType.Star) return 0.5;

        var total = left.Value + right.Value;
        return total > 0 ? left.Value / total : 0.5;
    }

    private void SetRatio(double ratio)
    {
        if (Left is null || Right is null) return;
        if (Visibility != Visibility.Visible) return;

        var clamped = Math.Clamp(ratio, MinRatio, MaxRatio);
        _ratio = clamped;
        Left.Width = new GridLength(clamped, GridUnitType.Star);
        Right.Width = new GridLength(1 - clamped, GridUnitType.Star);
    }

    private void Persist()
    {
        if (double.IsNaN(_ratio)) return;
        if (SettingsKey.Length == 0 || Visibility != Visibility.Visible) return;
        // 按下但没真的挪动时不写盘，避免把一次空交互固化成配置。
        if (AppServices.Settings.ColumnSplitRatios.TryGetValue(SettingsKey, out var stored) &&
            Math.Abs(stored - _ratio) < 0.001) return;

        AppServices.Settings.ColumnSplitRatios[SettingsKey] = _ratio;
        AppServices.Settings.Save();
    }
}
