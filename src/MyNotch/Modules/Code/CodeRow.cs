using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyNotch.Controls;
using MyNotch.Localization;
using Windows.UI;

namespace MyNotch.Modules.Code;

/// <summary>
/// A project in the search's list (« code »): the Code tiles' look on a line. Its colour as tinted glass (a wash from
/// the left over a light frost, a hairline edge), its kind's icon in full colour on a little tinted square, its name and
/// where it is, its stack as a glass badge; hovered, the buttons to show its folder and copy its path. A click opens it.
/// </summary>
public sealed partial class CodeRow : Grid
{
    public const double RowHeight = 56;

    private static readonly SolidColorBrush Text = new(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Faint = new(Color.FromArgb(0x8F, 0xFF, 0xFF, 0xFF));

    private readonly CodeViewModel _viewModel;
    private readonly TextBlock _name;
    private readonly TextBlock _place;
    private readonly TextBlock _badgeText;
    private readonly Border _ring;
    private readonly StackPanel _actions;
    private bool _selected;
    private bool _hovered;

    public CodeRow(RecentProject project, CodeViewModel viewModel)
    {
        Project = project;
        _viewModel = viewModel;
        Height = RowHeight;
        var color = project.Color;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

        // The glass: its colour washing in from the left, a frost under it, a sheen on top, a hairline edge.
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = Gradient(0, 0, 1, 0.6, (Tint(color, 0x40), 0), (Tint(color, 0x16), 0.5), (Frost(0x0A), 1)),
            BorderThickness = new Thickness(1),
            BorderBrush = Gradient(0, 0, 1, 1, (Frost(0x40), 0), (Tint(color, 0x38), 0.5), (Frost(0x0C), 1)),
        });
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            IsHitTestVisible = false,
            Background = Gradient(0, 0, 0, 1, (Frost(0x14), 0), (Frost(0x04), 0.45), (Frost(0), 0.7)),
        });

        var content = new Grid { Padding = new Thickness(12, 0, 12, 0), ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Its kind's icon, in its colour, on a little tinted square.
        var icon = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Tint(color, 0x26)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Tint(color, 0x45)),
            Child = new FontIcon { Glyph = project.Glyph, FontSize = 14, Foreground = new SolidColorBrush(color) },
        };
        content.Children.Add(icon);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        _name = new TextBlock { Text = project.Name, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Text, TextTrimming = TextTrimming.CharacterEllipsis };
        _place = new TextBlock { Text = project.Place, FontSize = 11.5, Foreground = Faint, TextTrimming = TextTrimming.CharacterEllipsis };
        words.Children.Add(_name);
        words.Children.Add(_place);
        Grid.SetColumn(words, 1);
        content.Children.Add(words);

        // Hovered or chosen: show its folder, copy its path.
        _actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
        if (project.IsLocal)
        {
            _actions.Children.Add(Action("", Loc.T("Ouvrir l'emplacement"), (_, _) => _viewModel.Reveal(Project)));
        }

        _actions.Children.Add(Action("", Loc.T("Copier le chemin"), async (_, _) => await _viewModel.CopyPathAsync(Project)));
        Grid.SetColumn(_actions, 2);
        content.Children.Add(_actions);

        // What it is made with, as a glass badge.
        _badgeText = new TextBlock { Text = project.Label, FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(color) };
        var badge = new Border
        {
            Height = 20,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Tint(color, 0x24)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Tint(color, 0x4D)),
            Child = _badgeText,
        };
        _badgeText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(badge, 3);
        content.Children.Add(badge);
        Children.Add(content);

        // Chosen with the arrows: a bright ring.
        _ring = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            IsHitTestVisible = false,
            Opacity = 0,
        };
        Children.Add(_ring);

        ToolTipService.SetToolTip(this, project.Location);
        AutomationProperties.SetName(this, Loc.F("Ouvrir {0} ({1}) dans VS Code", project.Name, project.Label));
        PointerEntered += (_, _) => Hover(true);
        PointerExited += (_, _) => Hover(false);
        Tapped += (_, e) =>
        {
            e.Handled = true;
            Open();
        };
    }

    public RecentProject Project { get; }

    /// <summary>Opens the project in VS Code (a click, or Enter on it).</summary>
    public void Open() => _viewModel.Open(Project);

    /// <summary>Chosen with the arrow keys: a bright ring, its buttons, and the list scrolls to it.</summary>
    public void SetSelected(bool selected)
    {
        _selected = selected;
        _ring.Opacity = selected ? 1 : 0;
        ShowActions();
        if (selected)
        {
            StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.5 });
        }
    }

    /// <summary>Lights up where the search words appear.</summary>
    public void Highlight(IReadOnlyList<string> words) => TextSearch.Highlight(words, _name, _place, _badgeText);

    private void Hover(bool hovered)
    {
        _hovered = hovered;
        ShowActions();
    }

    private void ShowActions() => _actions.Opacity = _hovered || _selected ? 1 : 0;

    private static Button Action(string glyph, string name, RoutedEventHandler click)
    {
        var button = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Content = new FontIcon { Glyph = glyph, FontSize = 12, Foreground = Text },
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        button.Click += click;
        button.Tapped += (_, e) => e.Handled = true; // Its own: not the row's click.
        return button;
    }

    private static Color Tint(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Frost(byte alpha) => Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);

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
}
