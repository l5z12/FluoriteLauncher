using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Fluorite.App.Views;

/// <summary>
/// A toolbar row that never runs off the window. Items marked <see cref="FarProperty"/> sit at the right end. When the
/// row is too narrow, buttons marked <see cref="ShrinkProperty"/> first drop their text (icon + text content; the
/// tooltip still names them), then the far items move to a line of their own, and any line wraps item by item.
/// </summary>
public sealed partial class ToolbarPanel : Panel
{
    public double Spacing { get; set; } = 8;
    public double LineSpacing { get; set; } = 8;

    public static readonly DependencyProperty FarProperty =
        DependencyProperty.RegisterAttached("Far", typeof(bool), typeof(ToolbarPanel), new PropertyMetadata(false));

    public static bool GetFar(UIElement element) => (bool)element.GetValue(FarProperty);
    public static void SetFar(UIElement element, bool value) => element.SetValue(FarProperty, value);

    public static readonly DependencyProperty ShrinkProperty =
        DependencyProperty.RegisterAttached("Shrink", typeof(bool), typeof(ToolbarPanel), new PropertyMetadata(false));

    public static bool GetShrink(UIElement element) => (bool)element.GetValue(ShrinkProperty);
    public static void SetShrink(UIElement element, bool value) => element.SetValue(ShrinkProperty, value);

    /// <summary>A near item (a search box) that takes the room left on its line, never less than it asks for.</summary>
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.RegisterAttached("Fill", typeof(bool), typeof(ToolbarPanel), new PropertyMetadata(false));

    public static bool GetFill(UIElement element) => (bool)element.GetValue(FillProperty);
    public static void SetFill(UIElement element, bool value) => element.SetValue(FillProperty, value);

    private bool _shrunk;

    /// <summary>The single-line width with every label shown, measured when they last were.</summary>
    private double _fullWidth;

    private readonly List<(UIElement Child, Rect Slot)> _slots = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var child = new Size(availableSize.Width, double.PositiveInfinity);
        MeasureAll(child);
        if (double.IsFinite(availableSize.Width))
        {
            if (!_shrunk)
            {
                var full = SingleLineWidth();
                if (full > availableSize.Width && SetLabels(Visibility.Collapsed))
                {
                    _fullWidth = full;
                    _shrunk = true;
                    MeasureAll(child);
                }
            }
            else if (availableSize.Width >= _fullWidth)
            {
                _shrunk = false;
                SetLabels(Visibility.Visible);
                MeasureAll(child);
            }
        }
        var height = Plan(availableSize.Width);
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : SingleLineWidth(), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Plan(finalSize.Width);
        foreach (var (child, slot) in _slots)
            child.Arrange(slot);
        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Visible)
                child.Arrange(default);
        }
        return finalSize;
    }

    private void MeasureAll(Size size)
    {
        foreach (var child in Children)
            child.Measure(size);
    }

    /// <summary>Hides or shows the text of the shrinkable buttons; false if there is none to hide.</summary>
    private bool SetLabels(Visibility visibility)
    {
        var any = false;
        foreach (var child in Children)
        {
            if (!GetShrink(child) || child is not ContentControl { Content: Panel content })
                continue;
            foreach (var label in content.Children.OfType<TextBlock>())
            {
                label.Visibility = visibility;
                any = true;
            }
        }
        return any;
    }

    /// <summary>The width needed to fit everything on one line.</summary>
    private double SingleLineWidth()
    {
        var (near, far) = Split();
        return Row(near) + (near.Count > 0 && far.Count > 0 ? Spacing : 0) + Row(far);
    }

    private (List<UIElement> Near, List<UIElement> Far) Split()
    {
        List<UIElement> near = [], far = [];
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Visible)
                (GetFar(child) ? far : near).Add(child);
        }
        return (near, far);
    }

    private double Row(List<UIElement> items) =>
        items.Count == 0 ? 0 : items.Sum(i => i.DesiredSize.Width) + Spacing * (items.Count - 1);

    private List<List<UIElement>> Flow(List<UIElement> items, double width)
    {
        List<List<UIElement>> lines = [];
        List<UIElement> line = [];
        foreach (var item in items)
        {
            if (line.Count > 0 && Row(line) + Spacing + item.DesiredSize.Width > width)
            {
                lines.Add(line);
                line = [];
            }
            line.Add(item);
        }
        if (line.Count > 0)
            lines.Add(line);
        return lines;
    }

    /// <summary>Fills <see cref="_slots"/> for this width; returns the height used.</summary>
    private double Plan(double width)
    {
        _slots.Clear();
        var (near, far) = Split();
        if (!double.IsFinite(width))
            width = SingleLineWidth();

        // Lines of (left-aligned items, right-aligned items).
        var lines = Flow(near, width).Select(l => (Left: l, Right: new List<UIElement>())).ToList();
        if (far.Count > 0)
        {
            if (lines.Count > 0 && Row(lines[^1].Left) + Spacing + Row(far) <= width)
                lines[^1].Right.AddRange(far);
            else
                lines.AddRange(Flow(far, width).Select(l => (Left: new List<UIElement>(), Right: l)));
        }

        double y = 0;
        foreach (var (left, right) in lines)
        {
            var height = left.Concat(right).Max(i => i.DesiredSize.Height);
            var room = Math.Max(0, width - Row(left) - (right.Count > 0 ? Spacing + Row(right) : 0));
            var fill = left.FirstOrDefault(GetFill);
            double x = 0;
            foreach (var item in left)
            {
                var w = item.DesiredSize.Width + (item == fill ? room : 0);
                Place(item, x, y, height, w);
                x += w + Spacing;
            }
            x = Math.Max(x, width - Row(right));
            foreach (var item in right)
            {
                Place(item, x, y, height);
                x += item.DesiredSize.Width + Spacing;
            }
            y += height + LineSpacing;
        }
        return lines.Count == 0 ? 0 : y - LineSpacing;
    }

    private void Place(UIElement item, double x, double y, double lineHeight, double? width = null) =>
        _slots.Add((item, new Rect(x, y + (lineHeight - item.DesiredSize.Height) / 2, width ?? item.DesiredSize.Width, item.DesiredSize.Height)));
}
