using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using MyNotch.Notch;
using Windows.UI;

namespace MyNotch.Modules.Pomodoro;

/// <summary>
/// The card a phase's end is told with, presented by the closed notch with the mascot as its hero (see
/// <see cref="NotchContext.Present"/>): its left is left to the mascot, come out of its place; on the right, what
/// happened in large (« Temps écoulé ! ») and what comes next under it, with its glyph, in the phase's colour. It
/// follows <c>n.Card</c>: the text slides in behind the mascot.
/// </summary>
public sealed partial class PomodoroCard : Grid
{
    public static readonly Windows.Foundation.Size Size = new(330, 84);

    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly FontIcon _glyph;
    private readonly StackPanel _details;

    public PomodoroCard()
    {
        Width = Size.Width;
        Height = Size.Height;
        IsHitTestVisible = false;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NotchContext.HeroRoom) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _title = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 19,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _glyph = new FontIcon { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        _subtitle = new TextBlock
        {
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var next = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        next.Children.Add(_glyph);
        next.Children.Add(_subtitle);

        _details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3, Margin = new Thickness(4, 0, 18, 0) };
        _details.Children.Add(_title);
        _details.Children.Add(next);
        SetColumn(_details, 1);
        Children.Add(_details);
    }

    /// <summary>Binds the card's motion to the notch's <c>n.Card</c>.</summary>
    public void Attach(NotchContext context)
    {
        context.Bind("Clamp(n.Card * 1.6 - 0.5, 0, 1)").Start(ElementCompositionPreview.GetElementVisual(this), "Opacity");
        ElementCompositionPreview.SetIsTranslationEnabled(_details, true);
        context.Bind("Vector3((1 - Clamp(n.Card, 0, 1)) * -22, 0, 0)").Start(ElementCompositionPreview.GetElementVisual(_details), "Translation");
    }

    /// <summary>Says <paramref name="title"/>, then <paramref name="subtitle"/> after <paramref name="glyph"/> in <paramref name="color"/>.</summary>
    public void Show(string title, string glyph, string subtitle, Color color)
    {
        _title.Text = title;
        _glyph.Glyph = glyph;
        _glyph.Foreground = new SolidColorBrush(color);
        _subtitle.Text = subtitle;
    }
}
