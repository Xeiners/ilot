using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyNotch.Localization;
using Windows.UI;

namespace MyNotch.Modules.Study;

/// <summary>
/// A course in the Révisions row: what is due (or what Gemini is doing on it, or what went wrong), its title, its
/// cards, and a bar of how well they are known. Follows the course as it changes.
/// </summary>
public sealed partial class DeckTile : UserControl
{
    private const double BarWidth = 104;

    private static readonly Color CardBase = Color.FromArgb(0xFF, 0x16, 0x16, 0x18);
    private Color _due;
    private static readonly Color DoneColor = Color.FromArgb(0xFF, 0x6C, 0xD9, 0x7E);
    private static readonly Color BusyColor = Color.FromArgb(0xFF, 0xFF, 0xC8, 0x57);
    private static readonly Color FailedColor = Color.FromArgb(0xFF, 0xFF, 0x7A, 0x7A);

    /// <param name="accent">The revisions' colour: the tile is lit with it, and what is due is said in it.</param>
    public DeckTile(StudyDeck deck, Color accent)
    {
        Deck = deck;
        InitializeComponent();
        Loc.Apply(this);
        MasteryFill.Background = new SolidColorBrush(DoneColor);
        deck.PropertyChanged += OnDeckChanged;
        Tint(accent);
    }

    /// <summary>Lit with <paramref name="accent"/> (the revisions' colour): its glass, its ring, what is due.</summary>
    public void Tint(Color accent)
    {
        _due = Color.FromArgb(255, (byte)(accent.R + (255 - accent.R) * 0.4), (byte)(accent.G + (255 - accent.G) * 0.4), (byte)(accent.B + (255 - accent.B) * 0.4));
        var light = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0.6, 1) };
        light.GradientStops.Add(new GradientStop { Offset = 0, Color = Mix(CardBase, accent, 0.26) });
        light.GradientStops.Add(new GradientStop { Offset = 1, Color = Mix(CardBase, accent, 0.06) });
        Body.Background = light;
        Ring.BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, _due.R, _due.G, _due.B));
        Refresh(DateTimeOffset.Now);
    }

    public StudyDeck Deck { get; }

    public event EventHandler? Opened;

    public bool IsOpen
    {
        set => Ring.Opacity = value ? 1 : 0;
    }

    /// <summary>Shown again as it is now (what is due changes with the time).</summary>
    public void Refresh(DateTimeOffset now)
    {
        TitleText.Text = Deck.Title;
        int due = Deck.DueCount(now);
        (string glyph, string state, Color color) = Deck switch
        {
            { Busy: true } => ("\uE895", Loc.T("Préparation…"), BusyColor),
            { Failed: true } => ("\uE783", Loc.T("À réessayer"), FailedColor),
            _ when due > 0 => ("\uE7BE", Loc.Count(due, "carte à revoir", "cartes à revoir"), _due),
            _ => ("\uE73E", Loc.T("À jour"), DoneColor),
        };
        StateIcon.Glyph = glyph;
        StateIcon.Foreground = new SolidColorBrush(color);
        StateText.Text = state;
        StateText.Foreground = new SolidColorBrush(color);
        CountText.Text = Loc.Count(Deck.Cards.Count, "carte", "cartes");
        MasteryFill.Width = Math.Max(Deck.Mastery > 0 ? 4 : 0, BarWidth * Deck.Mastery);
        AutomationProperties.SetName(this, $"{Deck.Title}, {state}");
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromArgb(
        255,
        (byte)Math.Round(a.R + (b.R - a.R) * amount),
        (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));

    /// <summary>No longer shown: it stops following the course.</summary>
    public void Detach() => Deck.PropertyChanged -= OnDeckChanged;

    private void OnDeckChanged(object? sender, PropertyChangedEventArgs e) => Refresh(DateTimeOffset.Now);

    private void OnBodyClick(object sender, RoutedEventArgs e) => Opened?.Invoke(this, EventArgs.Empty);
}
