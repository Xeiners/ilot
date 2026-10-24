using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using MyNotch.Localization;

namespace MyNotch.Modules.Search;

/// <summary>A line of the search's results (the panel keeps a few and fills them again as the search changes).</summary>
public sealed partial class SearchResultRow : UserControl
{
    private static readonly Color Amber = Color.FromArgb(255, 0xFF, 0xC8, 0x57);
    private static readonly Brush Matched = new SolidColorBrush(Microsoft.UI.Colors.White);
    private static readonly Brush ConfirmBrush = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0x8A, 0x80));
    private static readonly Brush SubtitleBrush = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));

    public SearchResultRow()
    {
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        ElementCompositionPreview.SetIsTranslationEnabled(LayoutRoot, true);
    }

    internal SearchResult? Result { get; private set; }

    /// <summary>Fills the line with <paramref name="result"/>; <c>true</c> if it shows something else than before.</summary>
    public bool Show(SearchResult result, ShellIcons icons, bool confirming)
    {
        bool changed = Result?.Key != result.Key;
        Result = result;

        TitleText.Inlines.Clear();
        int at = 0;
        foreach (var (start, length) in result.Matches.Where(match => match.Start >= 0 && match.Start + match.Length <= result.Title.Length))
        {
            if (start > at)
            {
                TitleText.Inlines.Add(new Run { Text = result.Title[at..start] });
            }

            TitleText.Inlines.Add(new Run { Text = result.Title.Substring(start, length), Foreground = Matched, FontWeight = FontWeights.SemiBold });
            at = start + length;
        }

        if (at < result.Title.Length)
        {
            TitleText.Inlines.Add(new Run { Text = result.Title[at..] });
        }

        // A calculation's result reads whole, in white.
        TitleText.Foreground = result.Kind == SearchKind.Calculation ? Matched : (Brush)new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
        TitleText.FontWeight = result.Kind == SearchKind.Calculation ? FontWeights.SemiBold : FontWeights.Normal;

        SubtitleText.Text = confirming ? Loc.T("Appuyez encore sur Entrée pour confirmer") : result.Subtitle;
        SubtitleText.Foreground = confirming ? ConfirmBrush : SubtitleBrush;
        KindText.Text = result.KindLabel;

        if (changed)
        {
            ShowIcon(result, icons);
        }

        return changed;
    }

    /// <summary>The line chosen: Enter would open it.</summary>
    public void Select(bool selected) => EnterKey.Opacity = selected ? 1 : 0;

    private async void ShowIcon(SearchResult result, ShellIcons icons)
    {
        var tint = result.Tint ?? KindColor(result);
        GlyphIcon.Glyph = result.Glyph;
        GlyphIcon.Foreground = new SolidColorBrush(tint);
        Tile.Background = new SolidColorBrush(Color.FromArgb(0x30, tint.R, tint.G, tint.B));
        GlyphIcon.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        Tile.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        IconImage.Source = null;
        if (result.IconSource is not { } source)
        {
            return;
        }

        var icon = await icons.GetAsync(source, result.IconKey ?? source);
        if (Result != result || icon is null)
        {
            return; // Shows something else by now, or the shell has no icon for it.
        }

        IconImage.Source = icon;
        GlyphIcon.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        Tile.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private static Color KindColor(SearchResult result) => result.Kind switch
    {
        SearchKind.Calculation => Amber,
        SearchKind.Setting => Color.FromArgb(255, 0x6A, 0xB0, 0xFF),
        SearchKind.Command => Color.FromArgb(255, 0xFF, 0x7A, 0x70),
        SearchKind.Web => Color.FromArgb(255, 0x7A, 0xD7, 0xF0),
        SearchKind.Folder => Amber,
        _ => Color.FromArgb(255, 0xA8, 0xA8, 0xB0),
    };
}
