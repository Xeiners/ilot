using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MyNotch.Composition;
using MyNotch.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using MyNotch.Localization;

namespace MyNotch.Modules.Clipboard;

/// <summary>
/// A tile of the clipboard history. Hover, pin and confirmation are springs on the compositor, referenced as
/// <c>c</c> in expressions.
/// </summary>
public sealed partial class ClipboardCard : UserControl
{
    private const float TileWidth = 140;
    private const float TileHeight = 96;
    private const float TileRadius = 16;
    private const int TooltipThreshold = 60;
    private static readonly TimeSpan ConfirmationTime = TimeSpan.FromMilliseconds(900);

    private readonly ClipboardViewModel _viewModel;
    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _state;
    private readonly SpringChannel _hover;
    private readonly SpringChannel _pinned;
    private readonly SpringChannel _confirmation;
    private readonly SpringChannel _selected;
    private readonly DispatcherQueueTimer _confirmationTimer;

    public ClipboardCard(ClipboardEntry entry, ClipboardViewModel viewModel)
    {
        Entry = entry;
        _viewModel = viewModel;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _state = _compositor.CreatePropertySet();
        _hover = new SpringChannel(_state, "Hover", 0);
        _pinned = new SpringChannel(_state, "Pinned", entry.IsPinned ? 1 : 0);
        _confirmation = new SpringChannel(_state, "Confirm", 0);
        _selected = new SpringChannel(_state, "Selected", 0);
        _confirmationTimer = DispatcherQueue.CreateTimer();
        _confirmationTimer.Interval = ConfirmationTime;
        _confirmationTimer.IsRepeating = false;
        _confirmationTimer.Tick += (_, _) => _confirmation.AnimateTo(0, Motion.Fade);

        ShowContent();
        Choreograph();
        ShowPin();

        // handledEventsToo: the buttons inside must not hide the pointer from the card.
        CardRoot.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => _hover.AnimateTo(1, Motion.Hover)), handledEventsToo: true);
        CardRoot.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => _hover.AnimateTo(0, Motion.Hover)), handledEventsToo: true);
        Loaded += (_, _) => Entry.PropertyChanged += OnEntryChanged;
        Unloaded += (_, _) => Entry.PropertyChanged -= OnEntryChanged;
    }

    public ClipboardEntry Entry { get; }

    /// <summary>Its preview is asked for (its eye), to look at it larger and touch it up.</summary>
    public event EventHandler? PreviewRequested;

    /// <summary>Raised once the entry is back on the clipboard (tile clicked, or Enter in the search).</summary>
    public event EventHandler? Copied;

    /// <summary>Lights up where the search words appear in what the tile shows (nothing when there are none).</summary>
    public void Highlight(IReadOnlyList<string> words) =>
        TextSearch.Highlight(words, HeadlineText, DetailText, HexText, RgbText, KindText);

    /// <summary>Chosen with the arrow keys: a bright ring, and the tile scrolls into view.</summary>
    public void SetSelected(bool selected)
    {
        _selected.AnimateTo(selected ? 1 : 0, Motion.Hover);
        if (selected)
        {
            StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, HorizontalAlignmentRatio = 0.5 });
        }
    }

    /// <summary>Brings "how long ago" up to date.</summary>
    public void Refresh(DateTimeOffset now) => AgeText.Text = Entry.AgeAt(now);

    /// <summary>Each kind has its own preview and its own action.</summary>
    private void ShowContent()
    {
        KindIcon.Glyph = Entry.KindGlyph;
        KindText.Text = Entry.KindLabel;
        Refresh(DateTimeOffset.Now);

        switch (Entry.Kind)
        {
            case ClipboardKind.Link:
                HeadlineText.Text = Entry.Domain;
                HeadlineText.FontSize = 14;
                HeadlineText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                HeadlineText.MaxLines = 1;
                DetailText.Text = Entry.Detail;
                DetailText.Visibility = Visibility.Visible;
                SetAction(new FontIcon { Glyph = "", FontSize = 12 }, Loc.T("Ouvrir le lien"));
                ToolTipService.SetToolTip(Body, Entry.Link!.AbsoluteUri);
                break;

            case ClipboardKind.Color:
                TextContent.Visibility = Visibility.Collapsed;
                ColorContent.Visibility = Visibility.Visible;
                Swatch.Background = new SolidColorBrush(Entry.Color);
                HexText.Text = Entry.Headline;
                RgbText.Text = Entry.Rgb;
                SetAction(
                    new TextBlock { Text = Loc.T("RVB"), FontSize = 8.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                    Loc.T("Copier en RVB"));
                break;

            case ClipboardKind.Image:
                TextContent.Visibility = Visibility.Collapsed;
                Thumbnail.Visibility = Visibility.Visible;
                ImageShade.Visibility = Visibility.Visible;
                KindText.Text = Entry.Headline;
                SetAction(new FontIcon { Glyph = "", FontSize = 12 }, Loc.T("Enregistrer dans Images"));
                LoadThumbnail();
                break;

            default:
                HeadlineText.Text = Entry.Headline;
                if (Entry.Text!.Length > TooltipThreshold)
                {
                    ToolTipService.SetToolTip(Body, Entry.Headline);
                }

                break;
        }

        AutomationProperties.SetName(Body, Loc.F("Copier : {0}, {1}", Entry.KindLabel, Entry.Headline));
    }

    private void SetAction(UIElement content, string name)
    {
        KindAction.Content = content;
        KindAction.Visibility = Visibility.Visible;
        AutomationProperties.SetName(KindAction, name);
        ToolTipService.SetToolTip(KindAction, name);
    }

    /// <summary>Decoded at the size of the tile, from a stream: the file is not held open afterwards.</summary>
    private async void LoadThumbnail()
    {
        try
        {
            double aspect = Entry.ImageWidth > 0 ? (double)Entry.ImageHeight / Entry.ImageWidth : 1;
            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelHeight = (int)Math.Ceiling(Math.Max(TileHeight, TileWidth * aspect)),
            };
            using var stream = File.OpenRead(Entry.ImagePath!).AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream);
            Thumbnail.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Debug.WriteLine($"[Clipboard] Thumbnail unavailable: {ex.Message}");
        }
    }

    private void Choreograph()
    {
        // Rounded tile: the thumbnail and the ring follow its corners.
        var corners = _compositor.CreateRoundedRectangleGeometry();
        corners.Size = new Vector2(TileWidth, TileHeight);
        corners.CornerRadius = new Vector2(TileRadius);
        VisualOf(Face).Clip = _compositor.CreateGeometricClip(corners);

        // Under the pointer, the kind label gives way to the actions; the pin stays while pinned.
        Bind("1 - Clamp(c.Hover, 0, 1)").Start(VisualOf(KindText), "Opacity");
        Bind("Clamp(c.Hover, 0, 1)").Start(VisualOf(KindAction), "Opacity");
        Bind("Clamp(c.Hover, 0, 1)").Start(VisualOf(RemoveButton), "Opacity");
        Bind("Clamp(c.Hover, 0, 1)").Start(VisualOf(PreviewButton), "Opacity");
        Bind("Clamp(c.Hover, 0, 1)").Start(VisualOf(ActionsBack), "Opacity");
        var back = VisualOf(ActionsBack);
        ActionsBack.SizeChanged += (_, e) => back.CenterPoint = new Vector3((float)e.NewSize.Width, 0, 0);
        Bind("Vector3(Lerp(0.9, 1, Clamp(c.Hover, 0, 1)), Lerp(0.9, 1, Clamp(c.Hover, 0, 1)), 1)").Start(back, "Scale"); // It grows out of its corner.
        Bind("Max(Clamp(c.Hover, 0, 1), Clamp(c.Pinned, 0, 1))").Start(VisualOf(PinButton), "Opacity");
        Bind("Clamp(c.Pinned, 0, 1) * 0.6").Start(VisualOf(PinRing), "Opacity");
        Bind("Clamp(c.Selected, 0, 1)").Start(VisualOf(SelectionRing), "Opacity");

        // Confirmation: the veil fades in while its check mark springs up.
        Bind("Clamp(c.Confirm, 0, 1)").Start(VisualOf(Confirmation), "Opacity");
        var content = VisualOf(ConfirmationContent);
        Bind("Vector3(Lerp(0.8, 1, c.Confirm), Lerp(0.8, 1, c.Confirm), 1)").Start(content, "Scale");
        ConfirmationContent.SizeChanged += (_, e) =>
            content.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
    }

    private void ShowPin(bool animate = false)
    {
        bool pinned = Entry.IsPinned;
        PinIcon.Glyph = pinned ? "" : "";
        PinIcon.Foreground = (Brush)Application.Current.Resources[pinned ? "NotchPinBrush" : "NotchPrimaryTextBrush"];
        AutomationProperties.SetName(PinButton, Loc.T(pinned ? "Détacher" : "Épingler"));
        ToolTipService.SetToolTip(PinButton, Loc.T(pinned ? "Détacher" : "Épingler"));
        if (animate)
        {
            _pinned.AnimateTo(pinned ? 1 : 0, Motion.Toggle);
        }
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ClipboardEntry.IsPinned):
                ShowPin(animate: true);
                break;

            case nameof(ClipboardEntry.CopiedAt):
                Refresh(DateTimeOffset.Now);
                break;
        }
    }

    private void Confirm(string message)
    {
        ConfirmationText.Text = message;
        _confirmation.AnimateTo(1, Motion.Toggle);
        _confirmationTimer.Stop();
        _confirmationTimer.Start();
    }

    /// <summary>Puts the entry back on the clipboard, with its "Copié" confirmation.</summary>
    public async Task<bool> CopyAsync()
    {
        if (!await _viewModel.CopyAsync(Entry))
        {
            return false;
        }

        Confirm(Loc.T("Copié"));
        Copied?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async void OnBodyClick(object sender, RoutedEventArgs e) => await CopyAsync();

    private async void OnKindActionClick(object sender, RoutedEventArgs e)
    {
        switch (Entry.Kind)
        {
            case ClipboardKind.Link:
                await _viewModel.OpenLinkAsync(Entry);
                break;

            case ClipboardKind.Color:
                if (await _viewModel.CopyRgbAsync(Entry))
                {
                    Confirm(Loc.T("RVB copié"));
                }

                break;

            case ClipboardKind.Image:
                if (await _viewModel.SaveImageAsync(Entry))
                {
                    Confirm(Loc.T("Enregistrée"));
                }

                break;
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e) => _viewModel.Remove(Entry);

    private void OnPreviewClick(object sender, RoutedEventArgs e) => PreviewRequested?.Invoke(this, EventArgs.Empty);

    private void OnPinClick(object sender, RoutedEventArgs e) => _viewModel.TogglePin(Entry);

    /// <summary>Drag the tile out of the notch: the image as a file (and a bitmap), or the text (and the link).</summary>
    private async void OnDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        args.AllowedOperations = DataPackageOperation.Copy;
        args.Data.Properties[MyNotch.Notch.NotchContext.InternalDragProperty] = true;
        if (Entry.Kind != ClipboardKind.Image)
        {
            args.Data.SetText(Entry.Text!);
            if (Entry.Link is { } link)
            {
                args.Data.SetWebLink(link);
            }

            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            // Dropped files keep their name: give it a readable one rather than the history's internal id.
            string folder = Path.Combine(Path.GetTempPath(), "MyNotch");
            string path = Path.Combine(folder, $"Image {Entry.CopiedAt.LocalDateTime:yyyy-MM-dd HH.mm.ss}.png");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(folder);
                File.Copy(Entry.ImagePath!, path);
            }

            var file = await StorageFile.GetFileFromPathAsync(path);
            args.Data.SetStorageItems([file]);
            args.Data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Debug.WriteLine($"[Clipboard] Image not dragged: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private CompositionExpression Bind(string expression) =>
        new CompositionExpression(_compositor, expression).With("c", _state);

    private static Visual VisualOf(UIElement element) => ElementCompositionPreview.GetElementVisual(element);
}
