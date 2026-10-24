using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MyNotch.Composition;
using Windows.UI;
using MyNotch.Localization;

namespace MyNotch.Controls;

/// <summary>
/// Makes a row that scrolls sideways (the clipboard's tiles, the projects…) easy to move with a plain mouse: an arrow
/// at each end, over a fade, that shows only while there is more that way (a click moves most of a page), and the
/// wheel turned anywhere over the row scrolls it.
/// </summary>
/// <remarks>
/// The arrows go into the row's parent panel, over its ends, placed from its margins. They fade in and out on the
/// compositor; nothing runs while the row stays still.
/// </remarks>
public sealed class RowScroller
{
    private const double ArrowSize = 28;
    private const double EdgeWidth = 64;
    private const double Page = 0.75;

    private readonly ScrollViewer _scroller;
    private readonly Edge _left;
    private readonly Edge _right;
    private double? _wheelTarget;

    /// <param name="host">The panel holding <paramref name="scroller"/>: the arrows go in it, over the row.</param>
    public RowScroller(ScrollViewer scroller, Panel host)
    {
        _scroller = scroller;
        var parent = host;
        var properties = ElementCompositionPreview.GetElementVisual(scroller).Compositor.CreatePropertySet();
        _left = new Edge(this, parent, properties, toRight: false);
        _right = new Edge(this, parent, properties, toRight: true);

        scroller.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate)
            {
                _wheelTarget = null;
            }

            Update();
        };
        scroller.SizeChanged += (_, _) => Update();
        if (scroller.Content is FrameworkElement content)
        {
            content.SizeChanged += (_, _) => Update();
        }

        // handledEventsToo: the scroller itself may mark the wheel as used even though it only scrolls sideways.
        scroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), handledEventsToo: true);
    }

    /// <summary>Back to the start (a new search, the list cleared…).</summary>
    public void ScrollToStart(bool animate = true)
    {
        _wheelTarget = null;
        _scroller.ChangeView(0, null, null, disableAnimation: !animate);
    }

    /// <summary>The wheel, turned up or down (or sideways), moves the row along.</summary>
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_scroller.ScrollableWidth <= 0)
        {
            return;
        }

        var properties = e.GetCurrentPoint(_scroller).Properties;
        int delta = properties.IsHorizontalMouseWheel ? properties.MouseWheelDelta : -properties.MouseWheelDelta;
        ScrollTo((_wheelTarget ?? _scroller.HorizontalOffset) + delta);
        e.Handled = true;
    }

    private void ScrollBy(int direction) =>
        ScrollTo((_wheelTarget ?? _scroller.HorizontalOffset) + direction * Math.Max(_scroller.ViewportWidth * Page, 120));

    private void ScrollTo(double offset)
    {
        double target = Math.Clamp(offset, 0, _scroller.ScrollableWidth);
        _wheelTarget = target;
        _scroller.ChangeView(target, null, null);
    }

    /// <summary>Each arrow shows while there is more its way (a few pixels at least).</summary>
    private void Update()
    {
        double offset = _wheelTarget ?? _scroller.HorizontalOffset;
        _left.Show(offset > 2);
        _right.Show(offset < _scroller.ScrollableWidth - 2);
    }

    /// <summary>One end of the row: a fade to the notch's black, and the arrow over it.</summary>
    private sealed class Edge
    {
        private readonly Button _arrow;
        private readonly SpringChannel _shown;
        private bool _visible;

        public Edge(RowScroller owner, Panel parent, Microsoft.UI.Composition.CompositionPropertySet properties, bool toRight)
        {
            var scroller = owner._scroller;
            var margin = scroller.Margin;
            double height = double.IsNaN(scroller.Height) ? scroller.ActualHeight : scroller.Height;

            var fade = new LinearGradientBrush { StartPoint = new(toRight ? 0 : 1, 0), EndPoint = new(toRight ? 1 : 0, 0) };
            fade.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0, 0, 0, 0) });
            fade.GradientStops.Add(new GradientStop { Offset = 0.7, Color = Color.FromArgb(0xD9, 0, 0, 0) });
            fade.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(0xF2, 0, 0, 0) });

            _arrow = new MotionButton
            {
                Style = (Style)Application.Current.Resources["NotchPillButtonStyle"],
                Width = ArrowSize,
                Height = ArrowSize,
                MinWidth = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(toRight ? 0 : 8, 0, toRight ? 8 : 0, 0),
                HorizontalAlignment = toRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x26, 0x26, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                HoverScale = 0.1,
                PressScale = 0.14,
                HaloOpacity = 0.12,
                HaloRestScale = 1,
                IsHitTestVisible = false,
                Content = new FontIcon { Glyph = toRight ? "" : "", FontSize = 11 },
            };
            AutomationProperties.SetName(_arrow, Loc.T(toRight ? "Faire défiler vers la droite" : "Faire défiler vers la gauche"));
            _arrow.Click += (_, _) => owner.ScrollBy(toRight ? 1 : -1);

            var edge = new Grid
            {
                Width = EdgeWidth,
                Height = height,
                HorizontalAlignment = toRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = scroller.VerticalAlignment,
                Margin = new Thickness(toRight ? 0 : margin.Left, margin.Top, toRight ? margin.Right : 0, margin.Bottom),
                Children = { new Rectangle { Fill = fade, IsHitTestVisible = false }, _arrow },
            };
            int index = parent.Children.IndexOf(scroller);
            parent.Children.Insert(index >= 0 ? index + 1 : parent.Children.Count, edge);

            // On the compositor, and with the row: when it steps back (the clipboard asking to clear…), so do they.
            string name = toRight ? "Right" : "Left";
            _shown = new SpringChannel(properties, name, 0);
            new CompositionExpression(properties.Compositor, $"Clamp(p.{name}, 0, 1) * row.Opacity").With("p", properties)
                .With("row", ElementCompositionPreview.GetElementVisual(scroller))
                .Start(ElementCompositionPreview.GetElementVisual(edge), "Opacity");
        }

        public void Show(bool visible)
        {
            if (visible == _visible)
            {
                return;
            }

            _visible = visible;
            _arrow.IsHitTestVisible = visible;
            _shown.AnimateTo(visible ? 1 : 0, Motion.Fade);
        }
    }
}
