using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using MyNotch.Modules.Media.Audio;
using MyNotch.Notch;
using Windows.UI;
using MyNotch.Localization;

namespace MyNotch.Modules.Media;

/// <summary>
/// The card for an audio device connected or disconnected, presented by the closed notch (see
/// <see cref="NotchContext.Present"/>). It follows <c>n.Card</c>: the icon pops in, the text slides in behind; on a
/// connection two waves spread from the icon (played by the compositor, once each, then nothing runs).
/// </summary>
public sealed partial class AudioPopupView : UserControl
{
    private const float StageSize = 56;
    private const float DiscSize = 50;
    private static readonly TimeSpan WaveTime = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan WaveGap = TimeSpan.FromMilliseconds(420);
    private static readonly Color Connected = Color.FromArgb(255, 0x34, 0xC7, 0x59);
    private static readonly Color Disconnected = Color.FromArgb(255, 0x8E, 0x8E, 0x93);

    private readonly List<Visual> _waves = [];
    private CompositionAnimationGroup? _wave;
    private KeyFrameAnimation[] _waveParts = [];

    public AudioPopupView()
    {
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
    }

    public Windows.Foundation.Size CardSize => new(Width, Height);

    /// <summary>Binds the card's motion to the notch's <c>n.Card</c>.</summary>
    public void Attach(NotchContext context)
    {
        var compositor = context.Motion.Compositor;

        // Appears once the pill has grown enough to hold it, and leaves first.
        context.Bind("Clamp(n.Card * 1.6 - 0.5, 0, 1)").Start(ElementCompositionPreview.GetElementVisual(this), "Opacity");

        var stage = ElementCompositionPreview.GetElementVisual(IconStage);
        stage.CenterPoint = new Vector3(StageSize / 2, StageSize / 2, 0);
        context.Bind("Vector3(Lerp(0.5, 1, n.Card), Lerp(0.5, 1, n.Card), 1)").Start(stage, "Scale");

        ElementCompositionPreview.SetIsTranslationEnabled(Details, true);
        context.Bind("Vector3((1 - Clamp(n.Card, 0, 1)) * 16, 0, 0)").Start(ElementCompositionPreview.GetElementVisual(Details), "Translation");

        // Two rings the size of the disc, under it; each spreads out and fades.
        var ripples = compositor.CreateContainerVisual();
        ripples.Size = new Vector2(StageSize);
        for (int i = 0; i < 2; i++)
        {
            var ellipse = compositor.CreateEllipseGeometry();
            ellipse.Center = new Vector2(StageSize / 2);
            ellipse.Radius = new Vector2(DiscSize / 2 - 1);
            var ring = compositor.CreateSpriteShape(ellipse);
            ring.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
            ring.StrokeThickness = 1.5f;
            var wave = compositor.CreateShapeVisual();
            wave.Size = new Vector2(StageSize);
            wave.CenterPoint = new Vector3(StageSize / 2, StageSize / 2, 0);
            wave.Opacity = 0;
            wave.Shapes.Add(ring);
            ripples.Children.InsertAtTop(wave);
            _waves.Add(wave);
        }

        ElementCompositionPreview.SetElementChildVisual(Ripples, ripples);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.InsertKeyFrame(0, Vector3.One);
        scale.InsertKeyFrame(1, new Vector3(1.9f, 1.9f, 1), compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.6f), new Vector2(0.3f, 1)));
        scale.Duration = WaveTime;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, 0.9f);
        fade.InsertKeyFrame(1, 0);
        fade.Duration = WaveTime;
        _wave = compositor.CreateAnimationGroup();
        _wave.Add(scale);
        _wave.Add(fade);
        _waveParts = [scale, fade];
    }

    /// <summary>An accessory arrived: its name, "Connecté", and its volume; waves spread from the icon.</summary>
    public void ShowConnected(AudioDevice device, VolumeState volume)
    {
        Fill(device, Connected, device.Connection is { } connection ? Loc.F("Connecté · {0}", connection) : Loc.T("Connecté"));
        Icon.Opacity = 1;
        FooterIcon.Glyph = volume.Glyph;
        FooterText.Text = volume.IsAvailable ? volume.Text : "";
        VolumeBar.Visibility = Visibility.Visible;
        ElementCompositionPreview.GetElementVisual(VolumeFill).Scale = new Vector3((float)(volume.IsMuted ? 0 : volume.Level), 1, 1);
        SendWaves();
    }

    /// <summary>An accessory left: its name, "Déconnecté", and where the sound went.</summary>
    public void ShowDisconnected(AudioDevice device, AudioDevice? now)
    {
        Fill(device, Disconnected, Loc.T("Déconnecté"));
        Icon.Opacity = 0.55;
        FooterIcon.Glyph = now?.Glyph ?? "";
        FooterText.Text = now is null ? Loc.T("Aucune sortie audio") : Loc.F("Son sur : {0}", now.Name);
        VolumeBar.Visibility = Visibility.Collapsed;
    }

    private void Fill(AudioDevice device, Color status, string text)
    {
        Icon.Glyph = device.Glyph;
        NameText.Text = device.Name;
        StatusDot.Fill = new SolidColorBrush(status);
        StatusText.Text = text;
    }

    private void SendWaves()
    {
        if (_wave is null)
        {
            return;
        }

        for (int i = 0; i < _waves.Count; i++)
        {
            foreach (var animation in _waveParts)
            {
                animation.DelayTime = WaveGap * i;
                animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            }

            _waves[i].StartAnimationGroup(_wave);
        }
    }
}
