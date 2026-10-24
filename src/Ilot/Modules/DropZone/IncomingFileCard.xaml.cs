using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Ilot.Modules.DropZone.Local;
using Ilot.Notch;
using Ilot.Localization;

namespace Ilot.Modules.DropZone;

/// <summary>
/// The question the closed notch asks when the phone has sent files (<see cref="NotchContext.Ask"/>): keep them or
/// not. It follows <c>n.Card</c>: the icon pops in, the text and the answers slide in behind.
/// </summary>
public sealed partial class IncomingFileCard : UserControl
{
    public IncomingFileCard(NotchContext context)
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

    /// <summary>Keep them.</summary>
    public event EventHandler? Accepted;

    /// <summary>Delete them.</summary>
    public event EventHandler? Refused;

    /// <summary>"IMG_2034.jpg", "3,2 Mo · depuis le téléphone"; or "4 fichiers", "12 Mo · depuis le téléphone".</summary>
    public void Show(IReadOnlyList<IncomingFile> files)
    {
        long size = files.Sum(file => file.Size);
        TitleText.Text = files.Count == 1 ? files[0].Name : Loc.F("{0} fichiers", files.Count);
        DetailText.Text = Loc.F("{0} · depuis le téléphone", UploadItem.FormatSize(size));
        KindIcon.Glyph = files.Count == 1 ? KindGlyph(files[0].Name) : "";
        AutomationProperties.SetName(this, Loc.F("Reçu du téléphone : {0}", TitleText.Text));
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e) => Accepted?.Invoke(this, EventArgs.Empty);

    private void OnRefuseClick(object sender, RoutedEventArgs e) => Refused?.Invoke(this, EventArgs.Empty);

    /// <summary>A photo, a video, a sound, or any other file.</summary>
    private static string KindGlyph(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".heic" or ".heif" or ".gif" or ".webp" or ".bmp" => "",
        ".mp4" or ".mov" or ".m4v" or ".mkv" or ".avi" or ".webm" or ".3gp" => "",
        ".mp3" or ".m4a" or ".aac" or ".wav" or ".flac" or ".ogg" or ".opus" => "",
        ".zip" or ".rar" or ".7z" => "",
        _ => "",
    };
}
