using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Ilot.Composition;

namespace Ilot.Controls;

/// <summary>
/// Interactive progress bar. While the media plays the fill glides on the compositor (no work per frame); on hover
/// the bar thickens and a knob springs out; dragging previews the position (<see cref="Scrubbing"/>) and releasing
/// asks for a seek (<see cref="SeekRequested"/>).
/// </summary>
public sealed class Scrubber : Control
{
    private const string KnobScale = "Clamp(s.Hover, 0, 1.2) * (1 + 0.3 * s.Press)";

    private FrameworkElement? _bar;
    private FrameworkElement? _fill;
    private FrameworkElement? _knob;
    private Visual? _fillVisual;
    private CompositionPropertySet? _state;
    private SpringChannel? _hover;
    private SpringChannel? _press;
    private bool _pointerOver;
    private double _progress;
    private TimeSpan _remaining;
    private bool _gliding;

    public Scrubber()
    {
        // Double-clicking the bar seeks twice, it must not pin the notch.
        IsDoubleTapEnabled = false;
    }

    /// <summary>Raised continuously while the user drags, with the previewed fraction of the track.</summary>
    public event EventHandler<double>? Scrubbing;

    /// <summary>Raised when the user lets go, with the fraction of the track to jump to.</summary>
    public event EventHandler<double>? SeekRequested;

    public bool IsScrubbing { get; private set; }

    /// <summary>
    /// Shows <paramref name="progress"/> (0 to 1); while <paramref name="isPlaying"/>, glides to the end of the
    /// track in <paramref name="remaining"/>. Ignored while the user is dragging.
    /// </summary>
    public void ShowProgress(double progress, TimeSpan remaining, bool isPlaying)
    {
        _progress = Math.Clamp(progress, 0, 1);
        _remaining = remaining;
        _gliding = isPlaying && _progress < 1 && remaining > TimeSpan.Zero;
        if (!IsScrubbing)
        {
            ApplyProgress();
        }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _bar = GetTemplateChild("Bar") as FrameworkElement;
        _fill = GetTemplateChild("Fill") as FrameworkElement;
        _knob = GetTemplateChild("Knob") as FrameworkElement;
        if (_bar is null || _fill is null || _knob is null)
        {
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _state = compositor.CreatePropertySet();
        _hover = new SpringChannel(_state, "Hover", 0);
        _press = new SpringChannel(_state, "Press", 0);
        _fillVisual = ElementCompositionPreview.GetElementVisual(_fill);

        // Hover: the bar thickens and the knob springs out; pressing swells the knob a little more.
        Bind(compositor, "Vector3(1, Lerp(1, 1.75, s.Hover), 1)").Start(ElementCompositionPreview.GetElementVisual(_bar), "Scale");
        ElementCompositionPreview.SetIsTranslationEnabled(_knob, true);
        Bind(compositor, $"Vector3({KnobScale}, {KnobScale}, 1)").Start(ElementCompositionPreview.GetElementVisual(_knob), "Scale");

        _bar.SizeChanged += (_, _) => BindGeometry();
        _knob.SizeChanged += (_, _) => BindGeometry();
        ApplyProgress();
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        _pointerOver = true;
        _hover?.AnimateTo(1, Motion.Hover);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerOver = false;
        if (!IsScrubbing)
        {
            _hover?.AnimateTo(0, Motion.Hover);
        }
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_bar is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer))
        {
            return;
        }

        IsScrubbing = true;
        _hover?.AnimateTo(1, Motion.Hover);
        _press?.AnimateTo(1, Motion.Press);
        ScrubTo(e);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (IsScrubbing)
        {
            ScrubTo(e);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsScrubbing)
        {
            return;
        }

        ScrubTo(e);
        EndScrub();
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (IsScrubbing)
        {
            EndScrub();
        }
    }

    // Clicks belong to the scrubber: they must neither open nor pin the notch.
    protected override void OnTapped(TappedRoutedEventArgs e)
    {
        base.OnTapped(e);
        e.Handled = true;
    }

    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        base.OnDoubleTapped(e);
        e.Handled = true;
    }

    private void ScrubTo(PointerRoutedEventArgs e)
    {
        double width = _bar!.ActualWidth;
        _progress = width > 0 ? Math.Clamp(e.GetCurrentPoint(_bar).Position.X / width, 0, 1) : 0;
        _gliding = false;
        ApplyProgress();
        Scrubbing?.Invoke(this, _progress);
    }

    private void EndScrub()
    {
        IsScrubbing = false;
        _press?.AnimateTo(0, Motion.Release);
        if (!_pointerOver)
        {
            _hover?.AnimateTo(0, Motion.Hover);
        }

        SeekRequested?.Invoke(this, _progress);
    }

    private void ApplyProgress()
    {
        if (_fillVisual is null)
        {
            return;
        }

        _fillVisual.StopAnimation("Scale.X");
        _fillVisual.Scale = new Vector3((float)_progress, 1, 1);
        if (!_gliding)
        {
            return;
        }

        var compositor = _fillVisual.Compositor;
        var linear = compositor.CreateLinearEasingFunction();
        var glide = compositor.CreateScalarKeyFrameAnimation();
        glide.InsertKeyFrame(0, (float)_progress, linear);
        glide.InsertKeyFrame(1, 1, linear);
        glide.Duration = _remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : _remaining;
        _fillVisual.StartAnimation("Scale.X", glide);
    }

    /// <summary>Scale origins and the knob, glued to the end of the fill (even while it glides).</summary>
    private void BindGeometry()
    {
        if (_bar is null || _knob is null || _fillVisual is null || _state is null)
        {
            return;
        }

        float width = (float)_bar.ActualWidth;
        float knob = (float)_knob.ActualWidth;
        ElementCompositionPreview.GetElementVisual(_bar).CenterPoint = new Vector3(0, (float)_bar.ActualHeight / 2, 0);
        _fillVisual.CenterPoint = new Vector3(0, (float)_bar.ActualHeight / 2, 0);
        var knobVisual = ElementCompositionPreview.GetElementVisual(_knob);
        knobVisual.CenterPoint = new Vector3(knob / 2, (float)_knob.ActualHeight / 2, 0);
        new CompositionExpression(knobVisual.Compositor, "Vector3(f.Scale.X * w - k * 0.5, 0, 0)")
            .With("f", _fillVisual).With("w", width).With("k", knob)
            .Start(knobVisual, "Translation");
    }

    private CompositionExpression Bind(Compositor compositor, string expression) =>
        new CompositionExpression(compositor, expression).With("s", _state!);
}
