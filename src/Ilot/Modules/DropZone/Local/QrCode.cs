using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using QRCoder;
using Windows.Foundation;

namespace Ilot.Modules.DropZone.Local;

/// <summary>
/// A QR code as vector shapes, one unit per module (to be scaled by a <see cref="Viewbox"/>): sharp at any size and
/// any display scale. The three finder squares are drawn rounded; the data modules stay square, for the scanner.
/// </summary>
public static class QrCode
{
    private const int QuietZone = 4; // What QRCoder puts around the code: the tile around it plays that part.
    private const int Finder = 7;

    public static FrameworkElement Build(string text, Brush ink)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        int size = matrix.Count - 2 * QuietZone;
        bool Dark(int x, int y) => matrix[y + QuietZone][x + QuietZone];

        // Data modules, a rectangle per horizontal run, all in one geometry (filled at once: no seams between them).
        var modules = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size;)
            {
                if (!Dark(x, y) || InFinder(x, y, size))
                {
                    x++;
                    continue;
                }

                int start = x;
                while (x < size && Dark(x, y) && !InFinder(x, y, size))
                {
                    x++;
                }

                modules.Children.Add(new RectangleGeometry { Rect = new Rect(start, y, x - start, 1) });
            }
        }

        var code = new Grid { Width = size, Height = size };
        code.Children.Add(new Path { Data = modules, Fill = ink });
        foreach (var (x, y) in new[] { (0, 0), (size - Finder, 0), (0, size - Finder) })
        {
            code.Children.Add(new Border
            {
                Width = Finder,
                Height = Finder,
                Margin = new Thickness(x, y, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                BorderBrush = ink,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2.2),
                Background = new SolidColorBrush(Colors.Transparent),
            });
            code.Children.Add(new Border
            {
                Width = 3,
                Height = 3,
                Margin = new Thickness(x + 2, y + 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = ink,
                CornerRadius = new CornerRadius(0.9),
            });
        }

        return code;
    }

    private static bool InFinder(int x, int y, int size) =>
        (x < Finder && y < Finder) || (x >= size - Finder && y < Finder) || (x < Finder && y >= size - Finder);
}
