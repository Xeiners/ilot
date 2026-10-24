using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyNotch.Localization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;

namespace MyNotch.Notch;

/// <summary>
/// The tour's card, in the bubble under the open notch: what is ringed, what it does, what to do to try it (then that
/// it is done), and the way on (back, next, or out of the tour; the last step may offer a way to try, see
/// <see cref="TourStep.Action"/>). For the drop, a sample file to drag onto the notch.
/// </summary>
public sealed partial class TourCard : UserControl
{
    /// <summary>Its width; its height follows what it says (see <see cref="Show"/>).</summary>
    public const double CardWidth = 420;

    /// <summary>Set in the data of the sample file dragged from the card: the notch takes it as the tour's, sends nothing.</summary>
    public const string SampleProperty = "MyNotch.TourSample";

    // Never less: a title, two lines, the buttons (should it be measured before its styles apply).
    private const double LeastHeight = 140;

    private static readonly Color DoneColor = Color.FromArgb(0xFF, 0x6C, 0xD9, 0x7E);

    public TourCard()
    {
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        Width = CardWidth;
    }

    public event EventHandler? NextRequested;

    public event EventHandler? BackRequested;

    public event EventHandler? SkipRequested;

    public event EventHandler? ActionRequested;

    /// <summary>Shows <paramref name="step"/>, the <paramref name="index"/>th of <paramref name="count"/>; <paramref name="done"/>: tried.</summary>
    /// <returns>The size it needs.</returns>
    public Size Show(TourStep step, int index, int count, bool done = false)
    {
        bool last = index == count - 1;
        Glyph.Glyph = step.Glyph;
        Title.Text = step.Title;
        Body.Text = step.Text;
        Counter.Text = $"{index + 1} / {count}";
        BackButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = Loc.T(last ? "Terminer" : "Suivant");
        ActionButton.Content = step.ActionLabel;
        ActionButton.Visibility = step.Action is null ? Visibility.Collapsed : Visibility.Visible;
        SampleFile.Visibility = step.Wait == TourWait.Drop && !done ? Visibility.Visible : Visibility.Collapsed;

        string? line = done ? step.Done : step.Try;
        TryRow.Visibility = line is not null ? Visibility.Visible : Visibility.Collapsed;
        TryText.Text = line ?? "";
        TryGlyph.Glyph = done ? "\uE73E" : "\uE7C9"; // Done: a check; else a hand, to do it.
        TryGlyph.Foreground = done ? new SolidColorBrush(DoneColor) : (Brush)Application.Current.Resources["NotchGlyphBrush"];
        TryRow.Background = new SolidColorBrush(done ? Color.FromArgb(0x24, DoneColor.R, DoneColor.G, DoneColor.B) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

        Measure(new Size(CardWidth, double.PositiveInfinity));
        return new Size(CardWidth, Math.Max(Math.Ceiling(DesiredSize.Height), LeastHeight));
    }

    /// <summary>The sample file, as a drag: only the tour's mark (the notch recognises it, nothing is sent).</summary>
    private void OnSampleDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        args.Data.Properties[SampleProperty] = true;
        args.Data.SetText("exemple.txt");
        args.Data.RequestedOperation = DataPackageOperation.Copy;
        args.AllowedOperations = DataPackageOperation.Copy;
    }

    private void OnNextClick(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void OnSkipClick(object sender, RoutedEventArgs e) => SkipRequested?.Invoke(this, EventArgs.Empty);

    private void OnActionClick(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(this, EventArgs.Empty);
}
