using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SekaiSync.Desktop.Controls;

/// <summary>
/// 自动换行的水平面板。WinUI 3 没有内置 WrapPanel，而工具栏在窄窗下必须换行——
/// 此前用横向 StackPanel，窗口变窄时控件被直接裁掉（实测 1000 dip 下语言选择器整个消失）。
///
/// 行为：按水平顺序排列子元素，放不下就换到下一行；行内高度取该行最高子元素，
/// 较矮的子元素在该行内垂直居中，保证同一行视觉基线一致。
/// 不参与虚拟化（工具栏只有十来个控件）。
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing),
        typeof(double),
        typeof(WrapPanel),
        new PropertyMetadata(8d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing),
        typeof(double),
        typeof(WrapPanel),
        new PropertyMetadata(8d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight),
        typeof(double),
        typeof(WrapPanel),
        new PropertyMetadata(double.NaN, OnLayoutPropertyChanged));

    /// <summary>行间距。</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>列间距。</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>统一的子元素高度（NaN = 各自用期望高度）。</summary>
    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WrapPanel)d).InvalidateMeasure();

    private sealed class Slot
    {
        public required UIElement Element { get; init; }
        public double X { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 父级若是 StackPanel，传下来的是无限宽——那就没有换行依据。
        // 退化策略：用本控件上一次的实际宽度（首帧为 0 时按不换行处理，Arrange 会立刻纠正）。
        var maxWidth = availableSize.Width;
        if (double.IsInfinity(maxWidth))
        {
            maxWidth = ActualWidth > 0 ? ActualWidth : double.PositiveInfinity;
        }

        var rowSpacing = RowSpacing;
        var columnSpacing = ColumnSpacing;
        var itemHeight = ItemHeight;
        var forcedHeight = !double.IsNaN(itemHeight);

        double rowWidth = 0;
        double rowHeight = 0;
        double widest = 0;
        double totalHeight = 0;
        var firstInRow = true;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(new Size(
                double.IsInfinity(maxWidth) ? double.PositiveInfinity : maxWidth,
                forcedHeight ? itemHeight : double.PositiveInfinity));

            var desired = child.DesiredSize;
            var childWidth = desired.Width;
            var childHeight = forcedHeight ? itemHeight : desired.Height;

            if (!firstInRow && rowWidth + columnSpacing + childWidth > maxWidth)
            {
                widest = Math.Max(widest, rowWidth);
                totalHeight += rowHeight + rowSpacing;
                rowWidth = 0;
                rowHeight = 0;
                firstInRow = true;
            }

            rowWidth += (firstInRow ? 0 : columnSpacing) + childWidth;
            rowHeight = Math.Max(rowHeight, childHeight);
            firstInRow = false;
        }

        widest = Math.Max(widest, rowWidth);
        totalHeight += rowHeight;

        // 返回的宽度不能是无限大，否则父级 StackPanel 也无从约束。
        return new Size(double.IsInfinity(widest) ? 0 : widest, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var maxWidth = finalSize.Width;
        var rowSpacing = RowSpacing;
        var columnSpacing = ColumnSpacing;
        var forcedHeight = !double.IsNaN(ItemHeight);

        var rows = new List<List<Slot>>();
        var current = new List<Slot>();
        double rowWidth = 0;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            var desired = child.DesiredSize;
            var width = desired.Width;
            var height = forcedHeight ? ItemHeight : desired.Height;

            if (current.Count > 0 && rowWidth + columnSpacing + width > maxWidth)
            {
                rows.Add(current);
                current = new List<Slot>();
                rowWidth = 0;
            }

            current.Add(new Slot { Element = child, X = rowWidth + (current.Count > 0 ? columnSpacing : 0), Width = width, Height = height });
            rowWidth = current[^1].X + width;
        }

        if (current.Count > 0)
        {
            rows.Add(current);
        }

        double y = 0;
        foreach (var row in rows)
        {
            double rowHeight = 0;
            foreach (var slot in row)
            {
                rowHeight = Math.Max(rowHeight, slot.Height);
            }

            foreach (var slot in row)
            {
                // 同一行内垂直居中：较矮的控件不会贴顶，视觉基线与最高控件一致。
                var top = y + (rowHeight - slot.Height) / 2;
                slot.Element.Arrange(new Rect(slot.X, top, slot.Width, slot.Height));
            }

            y += rowHeight + rowSpacing;
        }

        return finalSize;
    }
}
