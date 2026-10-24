using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Ilot.Composition;
using Ilot.Controls;
using Windows.UI;
using Ilot.Localization;

namespace Ilot.Modules.Code;

/// <summary>
/// A tile of the Code tab, in its project's colour. Selection and confirmation are springs on the compositor,
/// referenced as <c>c</c>.
/// </summary>
public sealed partial class CodeCard : UserControl
{
    private const float TileWidth = 190;
    private const float TileHeight = 96;
    private const float TileRadius = 16;
    private static readonly TimeSpan ConfirmationTime = TimeSpan.FromMilliseconds(1100);

    private readonly CodeViewModel _viewModel;
    private readonly SpringChannel _confirmation;
    private readonly SpringChannel _selected;
    private readonly DispatcherQueueTimer _confirmationTimer;

    public CodeCard(RecentProject project, CodeViewModel viewModel)
    {
        Project = project;
        _viewModel = viewModel;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        NameText.Text = project.Name;
        PlaceText.Text = project.Place;
        RevealAction.Visibility = project.IsLocal ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(Body, project.Location);
        AutomationProperties.SetName(Body, Loc.F("Ouvrir {0} ({1}) dans VS Code", project.Name, project.Label));
        Paint(project.Color);

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var corners = compositor.CreateRoundedRectangleGeometry();
        corners.Size = new Vector2(TileWidth, TileHeight);
        corners.CornerRadius = new Vector2(TileRadius);
        ElementCompositionPreview.GetElementVisual(Face).Clip = compositor.CreateGeometricClip(corners);

        var state = compositor.CreatePropertySet();
        _confirmation = new SpringChannel(state, "Confirm", 0);
        _selected = new SpringChannel(state, "Selected", 0);
        Bind("Clamp(c.Confirm, 0, 1)").Start(ElementCompositionPreview.GetElementVisual(Confirmation), "Opacity");
        Bind("Clamp(c.Selected, 0, 1)").Start(ElementCompositionPreview.GetElementVisual(SelectionRing), "Opacity");
        var content = ElementCompositionPreview.GetElementVisual(ConfirmationContent);
        Bind("Vector3(Lerp(0.8, 1, c.Confirm), Lerp(0.8, 1, c.Confirm), 1)").Start(content, "Scale");
        ConfirmationContent.SizeChanged += (_, e) =>
            content.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);

        _confirmationTimer = DispatcherQueue.CreateTimer();
        _confirmationTimer.Interval = ConfirmationTime;
        _confirmationTimer.IsRepeating = false;
        _confirmationTimer.Tick += (_, _) => _confirmation.AnimateTo(0, Motion.Fade);

        CompositionExpression Bind(string expression) => new CompositionExpression(compositor, expression).With("c", state);
    }

    public RecentProject Project { get; }

    /// <summary>Opens the project in VS Code (the tile clicked, or Enter on it while searching).</summary>
    public void Open()
    {
        _viewModel.Open(Project);
        _confirmation.AnimateTo(1, Motion.Toggle);
        _confirmationTimer.Stop();
        _confirmationTimer.Start();
    }

    /// <summary>Chosen with the arrow keys: a bright ring, and the tile scrolls into view.</summary>
    public void SetSelected(bool selected)
    {
        _selected.AnimateTo(selected ? 1 : 0, Motion.Hover);
        if (selected)
        {
            StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, HorizontalAlignmentRatio = 0.5 });
        }
    }

    /// <summary>Lights up where the search words appear.</summary>
    public void Highlight(IReadOnlyList<string> words) => TextSearch.Highlight(words, NameText, PlaceText, BadgeText);

    /// <summary>
    /// The project's colour as tinted glass: a translucent wash from the top left corner over a light frost, a sheen
    /// of light across the top, a hairline edge bright where the light comes from and coloured along the sides; the
    /// icon in full colour, and a glass badge.
    /// </summary>
    private void Paint(Color color)
    {
        Body.Background = Gradient(0, 0, 1, 1, (Tint(color, 0x48), 0), (Tint(color, 0x1C), 0.55), (Frost(0x0C), 1));
        Sheen.Background = Gradient(0, 0, 0, 1, (Frost(0x1E), 0), (Frost(0x08), 0.35), (Frost(0), 0.6));
        Rim.BorderBrush = Gradient(0, 0, 1, 1, (Frost(0x5C), 0), (Tint(color, 0x40), 0.45), (Frost(0x0F), 1));

        KindIcon.Glyph = Project.Glyph;
        KindIcon.Foreground = new SolidColorBrush(color);
        BadgeText.Text = Project.Label;
        BadgeText.Foreground = new SolidColorBrush(color);
        Badge.Background = new SolidColorBrush(Tint(color, 0x24));
        Badge.BorderBrush = new SolidColorBrush(Tint(color, 0x4D));
        Badge.BorderThickness = new Thickness(1);

        static Color Tint(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

        static Color Frost(byte alpha) => Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);
    }

    private static LinearGradientBrush Gradient(double x0, double y0, double x1, double y1, params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(x0, y0),
            EndPoint = new Windows.Foundation.Point(x1, y1),
        };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop { Color = color, Offset = offset });
        }

        return brush;
    }

    private void OnBodyClick(object sender, RoutedEventArgs e) => Open();

    private void OnRevealClick(object sender, RoutedEventArgs e) => _viewModel.Reveal(Project);

    private async void OnCopyClick(object sender, RoutedEventArgs e) => await _viewModel.CopyPathAsync(Project);
}
