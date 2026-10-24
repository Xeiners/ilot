using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyNotch.Composition;
using MyNotch.Modules.DropZone.Hosts;
using Windows.UI;
using MyNotch.Localization;

namespace MyNotch.Modules.DropZone;

/// <summary>
/// A tile of the Drop Zone. Hover, progress and confirmation are springs on the compositor, referenced as <c>c</c>
/// in expressions; the progress bar is scaled, never laid out again.
/// </summary>
public sealed partial class UploadCard : UserControl
{
    private const float TileWidth = 210;
    private const float TileHeight = 96;
    private const float TileRadius = 16;
    private const double ActionSize = 28;
    private const double ActionSpacing = 2;
    private static readonly TimeSpan ConfirmationTime = TimeSpan.FromMilliseconds(900);
    private static readonly Color Amber = Color.FromArgb(255, 0xFF, 0xC8, 0x57);
    private static readonly Color Red = Color.FromArgb(255, 0xFF, 0x6B, 0x6B);

    private readonly DropZoneViewModel _viewModel;
    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _state;
    private readonly SpringChannel _hover;
    private readonly SpringChannel _progress;
    private readonly SpringChannel _active;
    private readonly SpringChannel _confirmation;
    private readonly DispatcherQueueTimer _confirmationTimer;
    private readonly string _fileGlyph;

    public UploadCard(UploadItem item, DropZoneViewModel viewModel)
    {
        Item = item;
        _viewModel = viewModel;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _state = _compositor.CreatePropertySet();
        _hover = new SpringChannel(_state, "Hover", 0);
        _progress = new SpringChannel(_state, "Progress", item.Progress);
        _active = new SpringChannel(_state, "Active", ShowsProgress ? 1 : 0);
        _confirmation = new SpringChannel(_state, "Confirm", 0);
        _confirmationTimer = DispatcherQueue.CreateTimer();
        _confirmationTimer.Interval = ConfirmationTime;
        _confirmationTimer.IsRepeating = false;
        _confirmationTimer.Tick += (_, _) => _confirmation.AnimateTo(0, Motion.Fade);

        NameText.Text = item.Name;
        _fileGlyph = item.IsArchive ? "" : "";
        ToolTipService.SetToolTip(NameText, item.Name);
        Choreograph();
        Show(animate: false);
        Refresh(DateTimeOffset.Now);

        // handledEventsToo: the buttons inside must not hide the pointer from the card.
        CardRoot.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => _hover.AnimateTo(1, Motion.Hover)), handledEventsToo: true);
        CardRoot.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => _hover.AnimateTo(0, Motion.Hover)), handledEventsToo: true);
        Loaded += (_, _) => Item.PropertyChanged += OnItemChanged;
        Unloaded += (_, _) => Item.PropertyChanged -= OnItemChanged;
    }

    public UploadItem Item { get; }

    private bool ShowsProgress => Item.State is UploadState.Preparing or UploadState.Uploading or UploadState.WaitingForNetwork;

    /// <summary>Brings "expires in" up to date.</summary>
    public void Refresh(DateTimeOffset now) =>
        DetailText.Text = $"{UploadItem.FormatSize(Item.Size)} · {Item.ExpiryTextAt(now)}";

    private void Choreograph()
    {
        var corners = _compositor.CreateRoundedRectangleGeometry();
        corners.Size = new Vector2(TileWidth, TileHeight);
        corners.CornerRadius = new Vector2(TileRadius);
        VisualOf(Face).Clip = _compositor.CreateGeometricClip(corners);

        // The bar fills on a spring between progress samples, and fades away once there is nothing to follow.
        var fill = VisualOf(ProgressFill);
        Bind("Vector3(Clamp(c.Progress, 0, 1), 1, 1)").Start(fill, "Scale");
        Bind("Clamp(c.Active, 0, 1)").Start(VisualOf(ProgressBar), "Opacity");

        Bind("Clamp(c.Confirm, 0, 1)").Start(VisualOf(Confirmation), "Opacity");
        var content = VisualOf(ConfirmationContent);
        Bind("Vector3(Lerp(0.8, 1, c.Confirm), Lerp(0.8, 1, c.Confirm), 1)").Start(content, "Scale");
        ConfirmationContent.SizeChanged += (_, e) =>
            content.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
    }

    /// <summary>The state of the upload, in words, colour and actions.</summary>
    private void Show(bool animate)
    {
        // A padlock when the file is encrypted end to end (Send): only the link can open it.
        bool encrypted = UploadHosts.Get(Item.Host).IsEncrypted;
        KindIcon.Glyph = encrypted ? "" : _fileGlyph;
        ToolTipService.SetToolTip(KindIcon, encrypted ? Loc.T("Chiffré de bout en bout") : null);

        StatusText.Text = Item.StatusText;
        StatusText.Foreground = Item.State switch
        {
            UploadState.Failed => new SolidColorBrush(Red),
            UploadState.WaitingForNetwork => new SolidColorBrush(Amber),
            UploadState.Done => (Brush)Application.Current.Resources["NotchPrimaryTextBrush"],
            _ => (Brush)Application.Current.Resources["NotchSecondaryTextBrush"],
        };
        ProgressFill.Background = Item.State == UploadState.WaitingForNetwork
            ? new SolidColorBrush(Amber)
            : (Brush)Application.Current.Resources["NotchTrackFillBrush"];

        bool done = Item.State == UploadState.Done;
        bool failed = Item.State == UploadState.Failed;
        PrimaryAction.Visibility = done || failed ? Visibility.Visible : Visibility.Collapsed;
        PrimaryIcon.Glyph = failed ? "" : "";
        SetName(PrimaryAction, Loc.T(failed ? "Réessayer" : "Copier le lien"));
        OpenAction.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        SetName(RemoveAction, Loc.T(Item.IsActive ? "Annuler l'envoi" : "Retirer de la liste"));
        RemoveIcon.Glyph = Item.IsActive ? "" : ""; // Cancel while sending, a bin afterwards.

        // The status runs up to the toolbar, whose width follows how many actions it shows.
        int actions = 1 + (done || failed ? 1 : 0) + (done ? 1 : 0);
        double toolbar = actions * ActionSize + (actions - 1) * ActionSpacing + 2 * Toolbar.Padding.Left;
        StatusText.Margin = new Thickness(12, 0, Toolbar.Margin.Right + toolbar + 8, Toolbar.Margin.Bottom);
        AutomationProperties.SetName(Body, done ? Loc.F("Copier le lien de {0}", Item.Name) : Item.Name);

        double progress = Item.State == UploadState.WaitingForNetwork ? 1 : Item.Progress;
        if (animate)
        {
            _progress.AnimateTo(progress, Motion.Fade);
            _active.AnimateTo(ShowsProgress ? 1 : 0, Motion.Fade);
        }
        else
        {
            _progress.Snap(progress);
            _active.Snap(ShowsProgress ? 1 : 0);
        }

        static void SetName(UIElement element, string name)
        {
            AutomationProperties.SetName(element, name);
            ToolTipService.SetToolTip(element, name);
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UploadItem.Size):
            case nameof(UploadItem.UploadedAt):
                Refresh(DateTimeOffset.Now);
                break;

            case nameof(UploadItem.StatusText):
            case nameof(UploadItem.State):
            case nameof(UploadItem.Progress):
                Show(animate: true);
                break;
        }
    }

    private async void OnBodyClick(object sender, RoutedEventArgs e)
    {
        switch (Item.State)
        {
            case UploadState.Done:
                await CopyAsync();
                break;

            case UploadState.Failed:
                _viewModel.Retry(Item);
                break;
        }
    }

    private void OnPrimaryActionClick(object sender, RoutedEventArgs e) => OnBodyClick(sender, e);

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await _viewModel.OpenLinkAsync(Item);

    private void OnRemoveClick(object sender, RoutedEventArgs e) => _viewModel.Remove(Item);

    private async Task CopyAsync()
    {
        if (await _viewModel.CopyLinkAsync(Item))
        {
            _confirmation.AnimateTo(1, Motion.Toggle);
            _confirmationTimer.Stop();
            _confirmationTimer.Start();
        }
    }

    private CompositionExpression Bind(string expression) =>
        new CompositionExpression(_compositor, expression).With("c", _state);

    private static Visual VisualOf(UIElement element) => ElementCompositionPreview.GetElementVisual(element);
}
