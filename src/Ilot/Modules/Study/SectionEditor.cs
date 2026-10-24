using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Ilot.Controls;
using Ilot.Localization;
using Ilot.Mascot;
using Windows.UI;
using static Ilot.Modules.Study.StudyPaint;

namespace Ilot.Modules.Study;

/// <summary>
/// A section made or changed, in a bubble under the notch: its name, its colour (the mascot's palette), its tab as it
/// will look; « Créer » (or « Enregistrer »), « Annuler », and « Supprimer » for one that exists. Entrée saves, Échap gives up.
/// </summary>
internal sealed partial class SectionEditor : Grid
{
    /// <summary>The bubble's size.</summary>
    public static readonly Windows.Foundation.Size Size = new(480, 212);

    private readonly List<(MotionButton Button, Ellipse Ring, string Hex)> _swatches = [];
    private readonly MotionButton _save;
    private readonly Ellipse _previewDot;
    private readonly TextBlock _previewName;
    private readonly Border _preview;
    private string _color;

    /// <param name="section">The section changed; <c>null</c>: a new one, in <paramref name="color"/>.</param>
    public SectionEditor(StudySection? section, string color)
    {
        _color = section?.Color ?? color;
        Padding = new Thickness(24, 20, 24, 20);
        VerticalAlignment = VerticalAlignment.Center;
        RowSpacing = 14;
        for (int i = 0; i < 4; i++)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // The title, and the tab as it will look.
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = Loc.T(section is null ? "Nouvelle section" : "Modifier la section"),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["NotchPrimaryTextBrush"],
        });
        _previewDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        _previewName = new TextBlock
        {
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["NotchPrimaryTextBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        _preview = new Border
        {
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(14),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { _previewDot, _previewName } },
        };
        SetColumn(_preview, 1);
        head.Children.Add(_preview);
        Children.Add(head);

        // Its name.
        NameBox = new TextBox
        {
            Text = section?.Name ?? "",
            PlaceholderText = Loc.T("Nom de la section (Maths, Physique…)"),
            MaxLength = 40,
            FontSize = 14,
            RequestedTheme = ElementTheme.Dark,
        };
        NameBox.TextChanged += (_, _) => ShowPreview();
        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Save();
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                Cancelled?.Invoke(this, EventArgs.Empty);
            }
        };
        SetRow(NameBox, 1);
        Children.Add(NameBox);

        // Its colour.
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        colors.Children.Add(new TextBlock
        {
            Text = Loc.T("Couleur"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12.5,
            Foreground = (Brush)Application.Current.Resources["NotchSecondaryTextBrush"],
        });
        foreach (var shade in MascotPalette.Colors.Where(each => each.Id != MascotPalette.DefaultId))
        {
            string hex = Hex(shade.Tone);
            var ring = new Ellipse { Width = 26, Height = 26, StrokeThickness = 2 };
            var swatch = new MotionButton
            {
                Style = (Style)Application.Current.Resources["NotchPillButtonStyle"],
                Width = 30,
                Height = 30,
                MinWidth = 0,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Content = new Grid { Children = { ring, new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(shade.Tone) } } },
            };
            ToolTipService.SetToolTip(swatch, Loc.T(shade.Name));
            swatch.Click += (_, _) =>
            {
                _color = hex;
                ShowColor();
            };
            _swatches.Add((swatch, ring, hex));
            colors.Children.Add(swatch);
        }

        SetRow(colors, 2);
        Children.Add(colors);

        // Its buttons: « Supprimer » on the left (one that exists), « Annuler » and « Créer » on the right.
        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (section is not null)
        {
            var delete = Pill(Loc.T("Supprimer la section"), "\uE74D");
            delete.HorizontalAlignment = HorizontalAlignment.Left;
            delete.Click += (_, _) => Deleted?.Invoke(this, EventArgs.Empty);
            buttons.Children.Add(delete);
        }

        var cancel = Pill(Loc.T("Annuler"), null);
        cancel.Click += (_, _) => Cancelled?.Invoke(this, EventArgs.Empty);
        SetColumn(cancel, 1);
        buttons.Children.Add(cancel);
        _save = Pill(Loc.T(section is null ? "Créer" : "Enregistrer"), section is null ? "\uE710" : "\uE73E");
        _save.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        _save.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x11, 0x11, 0x14));
        _save.Click += (_, _) => Save();
        SetColumn(_save, 2);
        buttons.Children.Add(_save);
        SetRow(buttons, 3);
        Children.Add(buttons);

        ShowColor();
    }

    /// <summary>The name's field (focused as the bubble opens).</summary>
    public TextBox NameBox { get; }

    /// <summary>« Créer » / « Enregistrer » (or Entrée): its name and colour.</summary>
    public event EventHandler<(string Name, string Color)>? Saved;

    /// <summary>« Annuler » (or Échap).</summary>
    public event EventHandler? Cancelled;

    /// <summary>« Supprimer la section ».</summary>
    public event EventHandler? Deleted;

    private void Save()
    {
        string name = NameBox.Text.Trim();
        if (name.Length > 0)
        {
            Saved?.Invoke(this, (name, _color));
        }
    }

    private void ShowColor()
    {
        foreach (var (button, ring, hex) in _swatches)
        {
            bool chosen = hex == _color;
            ring.Stroke = chosen ? new SolidColorBrush(Microsoft.UI.Colors.White) : null;
            button.Background = new SolidColorBrush(chosen ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Microsoft.UI.Colors.Transparent);
        }

        ShowPreview();
    }

    /// <summary>The tab as it will look: its dot, its name, lit in its colour.</summary>
    private void ShowPreview()
    {
        var color = Parse(_color);
        _previewDot.Fill = new SolidColorBrush(color);
        _previewName.Text = NameBox.Text.Trim() is { Length: > 0 } name ? name : Loc.T("Section");
        _preview.Background = new SolidColorBrush(Alpha(color, 0x3A));
        _save.IsEnabled = NameBox.Text.Trim().Length > 0;
        _save.Opacity = _save.IsEnabled ? 1 : 0.4; // Its pill has no look of its own when it cannot be clicked.
    }

    private static MotionButton Pill(string text, string? glyph)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (glyph is not null)
        {
            label.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        }

        label.Children.Add(new TextBlock { Text = text });
        return new MotionButton { Style = (Style)Application.Current.Resources["StudyPillStyle"], Content = label };
    }

    public static string Hex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Color Parse(string hex) =>
        hex.Length == 9 && uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint argb)
            ? Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)
            : Microsoft.UI.Colors.White;
}
