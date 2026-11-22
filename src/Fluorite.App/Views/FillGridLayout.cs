using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Fluorite.App.Views;

/// <summary>
/// Equal cells that fill the width, for short lists. Unlike UniformGridLayout it doesn't virtualize: on a page that
/// stacks several of those in one scroller, scrolling made rows lose cells (only some columns stayed).
/// </summary>
public sealed partial class FillGridLayout : NonVirtualizingLayout
{
    public double MinItemWidth { get; set; } = 240;

    /// <summary>Cells are at least this tall, and as tall as the tallest item.</summary>
    public double MinItemHeight { get; set; }

    public double Spacing { get; set; } = 12;

    public int MaxColumns { get; set; } = int.MaxValue;

    private int _columns = 1;
    private double _itemWidth;
    private double _itemHeight;

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var children = context.Children;
        var maxColumns = Math.Max(1, MaxColumns);
        var width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : Math.Min(Math.Max(1, children.Count), maxColumns) * (MinItemWidth + Spacing) - Spacing;
        _columns = Math.Clamp((int)((width + Spacing) / (MinItemWidth + Spacing)), 1, maxColumns);
        _itemWidth = Math.Max(0, (width - (_columns - 1) * Spacing) / _columns);
        _itemHeight = MinItemHeight;
        foreach (var child in children)
        {
            child.Measure(new Size(_itemWidth, double.PositiveInfinity));
            _itemHeight = Math.Max(_itemHeight, child.DesiredSize.Height);
        }
        var rows = (children.Count + _columns - 1) / _columns;
        return new Size(width, rows == 0 ? 0 : rows * _itemHeight + (rows - 1) * Spacing);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var children = context.Children;
        for (var i = 0; i < children.Count; i++)
        {
            var (row, column) = Math.DivRem(i, _columns);
            children[i].Arrange(new Rect(column * (_itemWidth + Spacing), row * (_itemHeight + Spacing), _itemWidth, _itemHeight));
        }
        return finalSize;
    }
}
