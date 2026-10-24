using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using MyNotch.Composition;

namespace MyNotch.Controls;

/// <summary>
/// Button with physical feedback: on hover the content swells on a spring and a soft halo blooms behind it; on
/// press it squashes and springs back with a little bounce. All motion runs on the compositor.
/// </summary>
/// <remarks>
/// Round icon button by default. Other templates (tabs, cards) give the halo another shape and tone the motion down
/// through <see cref="HoverScale"/>, <see cref="PressScale"/> and <see cref="HaloRestScale"/>.
/// </remarks>
public sealed class MotionButton : Button
{
    private const string ContentScale = "(1 + hs * s.Hover) * (1 - ps * s.Press)";
    private const string HaloScale = "Lerp(hr, 1, s.Hover) - ps * 0.375 * s.Press";

    private SpringChannel? _hover;
    private SpringChannel? _press;
    private FrameworkElement? _content;
    private FrameworkElement? _halo;

    public MotionButton()
    {
        // Double-clicking "next" means "skip twice", never "pin the notch".
        IsDoubleTapEnabled = false;
    }

    /// <summary>How much the content grows under the pointer (0.08 = 8 %). Read when the template is applied.</summary>
    public double HoverScale { get; set; } = 0.08;

    /// <summary>How much the content squashes while pressed.</summary>
    public double PressScale { get; set; } = 0.16;

    /// <summary>Size of the halo when it starts to bloom, relative to its full size.</summary>
    public double HaloRestScale { get; set; } = 0.55;

    /// <summary>Opacity of the halo under the pointer.</summary>
    public double HaloOpacity { get; set; } = 0.12;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _content = GetTemplateChild("ContentPresenter") as FrameworkElement;
        _halo = GetTemplateChild("Halo") as FrameworkElement;
        if (_content is null || _halo is null)
        {
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var state = compositor.CreatePropertySet();
        _hover = new SpringChannel(state, "Hover", 0);
        _press = new SpringChannel(state, "Press", 0);

        Expression($"Vector3({ContentScale}, {ContentScale}, 1)").Start(ElementCompositionPreview.GetElementVisual(_content), "Scale");
        Expression($"Vector3({HaloScale}, {HaloScale}, 1)").Start(ElementCompositionPreview.GetElementVisual(_halo), "Scale");
        Expression("Clamp(s.Hover * ho + s.Press * 0.1, 0, 1)").Start(ElementCompositionPreview.GetElementVisual(_halo), "Opacity");

        _content.SizeChanged += (_, _) => CenterOrigin(_content);
        _halo.SizeChanged += (_, _) => CenterOrigin(_halo);

        CompositionExpression Expression(string expression) => new CompositionExpression(compositor, expression)
            .With("s", state)
            .With("hs", HoverScale).With("ps", PressScale).With("hr", HaloRestScale).With("ho", HaloOpacity);
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover?.AnimateTo(1, Motion.Hover);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        _hover?.AnimateTo(0, Motion.Hover);
        _press?.AnimateTo(0, Motion.Release);
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        _press?.AnimateTo(1, Motion.Press);
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        _press?.AnimateTo(0, Motion.Release);
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _press?.AnimateTo(0, Motion.Release);
    }

    private static void CenterOrigin(FrameworkElement element) =>
        ElementCompositionPreview.GetElementVisual(element).CenterPoint =
            new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
}
