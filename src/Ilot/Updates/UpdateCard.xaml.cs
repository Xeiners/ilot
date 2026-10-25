using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Ilot.Localization;
using Ilot.Notch;

namespace Ilot.Updates;

/// <summary>
/// The question the closed notch asks when a new version is downloaded (<see cref="NotchContext.Ask"/>): install it now,
/// or later (the next start). It follows <c>n.Card</c>: the icon pops in, the text and the answers slide in behind.
/// </summary>
public sealed partial class UpdateCard : UserControl
{
    public UpdateCard(NotchContext context)
    {
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        context.Bind("Clamp(n.Card * 1.6 - 0.5, 0, 1)").Start(ElementCompositionPreview.GetElementVisual(this), "Opacity");
        var stage = ElementCompositionPreview.GetElementVisual(IconStage);
        stage.CenterPoint = new Vector3(22, 22, 0);
        context.Bind("Vector3(Lerp(0.5, 1, n.Card), Lerp(0.5, 1, n.Card), 1)").Start(stage, "Scale");
        foreach (var element in new UIElement[] { Details, Answers })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            context.Bind("Vector3((1 - Clamp(n.Card, 0, 1)) * 16, 0, 0)").Start(ElementCompositionPreview.GetElementVisual(element), "Translation");
        }
    }

    public Windows.Foundation.Size CardSize => new(Width, Height);

    /// <summary>Install it now.</summary>
    public event EventHandler? InstallRequested;

    /// <summary>Not now: it goes in the next time Ilot starts.</summary>
    public event EventHandler? Later;

    /// <summary>« Ilot 1.0.1 est prête ».</summary>
    public void Show(Version version)
    {
        TitleText.Text = Loc.F("Ilot {0} est prête", version.ToString(3));
        AutomationProperties.SetName(this, TitleText.Text);
    }

    private void OnInstallClick(object sender, RoutedEventArgs e) => InstallRequested?.Invoke(this, EventArgs.Empty);

    private void OnLaterClick(object sender, RoutedEventArgs e) => Later?.Invoke(this, EventArgs.Empty);
}
