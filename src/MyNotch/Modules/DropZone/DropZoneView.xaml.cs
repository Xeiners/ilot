using System.Collections.Specialized;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI;
using MyNotch.Composition;
using MyNotch.Controls;
using MyNotch.Modules.DropZone.Local;
using MyNotch.Notch;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Core;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using MyNotch.Localization;

namespace MyNotch.Modules.DropZone;

/// <summary>
/// Drop Zone tab, and the notch's drop target: whatever files are dragged onto the notch (even closed) land here.
/// Tiles are kept in step with the uploads, with the same implicit motion as the clipboard's.
/// </summary>
public sealed partial class DropZoneView : UserControl, INotchDropTarget
{
    private const float TileCenterX = 105;
    private const float TileCenterY = 48;

    // Where the colours may gather while dragging: away from the top corners, where the notch meets the screen edge.
    private static readonly Rect CursorRoom = new(90, 50, 480, 90);

    // Violet on the left, cyan on the right, pink along the bottom: small, each on its own slow loop near its own edge,
    // so the wash frames the tab instead of pooling in its middle (positions in the open notch, tab bar included).
    private static readonly Blob[] Blobs =
    [
        new(Color.FromArgb(0x66, 0x8B, 0x5C, 0xF6), 115, 0.95f, TimeSpan.FromSeconds(17),
            [new(95, 105), new(170, 88), new(140, 128), new(70, 122)]),
        new(Color.FromArgb(0x55, 0x22, 0xD3, 0xEE), 105, 0.80f, TimeSpan.FromSeconds(21),
            [new(575, 100), new(500, 126), new(545, 82), new(600, 118)]),
        new(Color.FromArgb(0x55, 0xF4, 0x72, 0xB6), 100, 0.65f, TimeSpan.FromSeconds(25),
            [new(330, 136), new(250, 126), new(410, 132), new(330, 114)]),
    ];

    private static readonly Size DefaultChatSize = new(660, 410);
    private static readonly Size SmallestChat = new(500, 280);

    private readonly NotchContext _context;
    private readonly SolidColorBrush _activeChatBrush = new(Color.FromArgb(0x38, 0x22, 0xD3, 0xEE));
    private readonly Brush _defaultPillBrush;
    private IncomingFileCard? _incomingCard;
    private IDisposable? _question;
    private NotchBubble? _bubble;
    private IDisposable? _keyboard;
    private bool _chatOpen = true;
    private readonly ImplicitAnimationCollection _reflow;
    private readonly CompositionAnimationGroup _show;
    private readonly CompositionAnimationGroup _hide;
    private readonly CompositionPropertySet _ambience;
    private readonly SpringChannel _dragHover;
    private readonly SpringChannel _localHover;
    private readonly SpringChannel _localOn;
    private readonly SpringChannel _cursorX;
    private readonly SpringChannel _cursorY;
    private readonly (string Property, Vector2KeyFrameAnimation Loop)[] _drifts = new (string, Vector2KeyFrameAnimation)[Blobs.Length];
    private bool _following;
    private bool _overLocal;
    private string? _qrUrl;
    private bool _drifting;
    private readonly RowScroller _row;

    public DropZoneView(DropZoneViewModel viewModel, NotchContext context)
    {
        ViewModel = viewModel;
        _context = context;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        _defaultPillBrush = (Brush)Application.Current.Resources["NotchPillBrush"];

        // Somewhere to put the focus once done typing in the chat box.
        IsTabStop = true;
        UseSystemFocusVisuals = false;

        // Below the tab bar, lined up with the clipboard tiles and the media artwork.
        DropArea.Margin = new Thickness(DropArea.Margin.Left, context.ContentTop + 6, 0, 0);
        LocalArea.Margin = new Thickness(LocalArea.Margin.Left, context.ContentTop + 6, 0, 0);
        SharePanel.Margin = new Thickness(SharePanel.Margin.Left, context.ContentTop + 4, 0, 0);
        Scroller.Margin = new Thickness(Scroller.Margin.Left, context.ContentTop + 2, 0, 0);
        EmptyHint.Margin = new Thickness(EmptyHint.Margin.Left, context.ContentTop + 34, 0, 0);

        // The chat sheet goes into the bubble under the notch when Wi-Fi sharing is open.
        ChatHolder.Children.Remove(ChatSheet);
        ChatBox.GotFocus += (_, _) => TakeKeyboard();
        ChatBox.TextChanged += (_, _) => ChatBoxHint.Visibility = ChatBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatBox.KeyDown += OnChatBoxKeyDown;
        ChatBox.Paste += OnChatBoxPaste;
        KeyDown += OnViewKeyDown;

        var compositor = context.Motion.Compositor;
        _reflow = ImplicitMotion.Reflow(compositor, Motion.Reflow);
        _show = ImplicitMotion.Show(compositor, Motion.Pop, fromScale: 0.82f);
        _hide = ImplicitMotion.Hide(compositor, toScale: 0.86f);
        foreach (var item in viewModel.Items)
        {
            Cards.Children.Add(CreateCard(item));
        }

        ((INotifyCollectionChanged)viewModel.Items).CollectionChanged += OnItemsChanged;

        // Opens with the notch, after the tab bar; slides and fades with the tab selection.
        ElementCompositionPreview.SetIsTranslationEnabled(LayoutRoot, true);
        var root = ElementCompositionPreview.GetElementVisual(LayoutRoot);
        context.Bind($"Clamp((n.Expansion - 0.45) * 2.4, 0, 1) * {NotchContext.TabOpacity}").Start(root, "Opacity");
        context.Bind($"Vector3({NotchContext.TabOffset}, (1 - n.Progress) * -8, 0)").Start(root, "Translation");

        // While something is dragged over the notch, the drop area under it lights up (the uploads' unless it is the
        // Wi-Fi one) and its icon rises to meet it.
        _ambience = compositor.CreatePropertySet();
        _dragHover = new SpringChannel(_ambience, "Over", 0);
        _localHover = new SpringChannel(_ambience, "Local", 0);
        _localOn = new SpringChannel(_ambience, "On", 0);
        _cursorX = new SpringChannel(_ambience, "X", 0);
        _cursorY = new SpringChannel(_ambience, "Y", 0);
        LightUp(DropArea, DropHighlight, DropIcon, "d.Over * (1 - d.Local)");
        LightUp(LocalArea, LocalHighlight, LocalIcon, "d.Over * d.Local");
        Hover("Clamp(d.On, 0, 1) * (1 - Clamp(d.Over * d.Local, 0, 1))").Start(ElementCompositionPreview.GetElementVisual(LocalOn), "Opacity");

        context.AcceptDrops(this);
        context.ExpandedChanged += (_, _) => RefreshWhenVisible();
        context.SelectionChanged += (_, _) => RefreshWhenVisible();
        viewModel.Attach(context);

        _row = new RowScroller(Scroller, UploadsPane); // Arrows at its ends, and the wheel, for a mouse.

        BindAmbience(compositor);

        // The QR code takes the uploads' place while sharing on the Wi-Fi, and gives it back after.
        ElementCompositionPreview.SetImplicitShowAnimation(SharePanel, ImplicitMotion.Show(compositor, Motion.Pop, fromScale: 0.9f));
        ElementCompositionPreview.SetImplicitHideAnimation(SharePanel, _hide);
        ElementCompositionPreview.SetImplicitShowAnimation(UploadsPane, ImplicitMotion.Show(compositor, Motion.Pop, fromScale: 0.96f));
        ElementCompositionPreview.SetImplicitHideAnimation(UploadsPane, ImplicitMotion.Hide(compositor, toScale: 0.96f));
        ElementCompositionPreview.GetElementVisual(SharePanel).CenterPoint = new Vector3((float)SharePanel.Width / 2, (float)SharePanel.Height / 2, 0);
        ElementCompositionPreview.GetElementVisual(UploadsPane).CenterPoint = new Vector3(448, (float)context.ContentTop + 52, 0);
        viewModel.Local.PropertyChanged += (_, e) =>
        {
            // Progress comes a few times a second: only its own line is brought up to date then.
            if (e.PropertyName != nameof(LocalShareViewModel.TransferText))
            {
                ShowShare();
            }

            if (e.PropertyName is nameof(LocalShareViewModel.Incoming) or "")
            {
                AskIncoming();
            }

            ShowTransfer();
        };
        viewModel.Local.MessagesChanged += OnLocalMessagesChanged;
        ShowShare();

        // The transfer bar glides between the progress reports (a tenth of a second apart) rather than stepping.
        var glide = compositor.CreateVector3KeyFrameAnimation();
        glide.Target = "Scale";
        glide.InsertExpressionKeyFrame(1, "this.FinalValue", compositor.CreateLinearEasingFunction());
        glide.Duration = TimeSpan.FromMilliseconds(120);
        var fill = compositor.CreateImplicitAnimationCollection();
        fill["Scale"] = glide;
        ElementCompositionPreview.GetElementVisual(TransferFill).ImplicitAnimations = fill;

        CompositionExpression Hover(string expression) => new CompositionExpression(compositor, expression).With("d", _ambience);

        // Swells a little, lights its rim up, and its icon rises, as the drag comes over it (`over`, from 0 to 1).
        void LightUp(FrameworkElement area, UIElement highlight, FrameworkElement icon, string over)
        {
            var visual = ElementCompositionPreview.GetElementVisual(area);
            visual.CenterPoint = new Vector3((float)area.Width / 2, (float)area.Height / 2, 0);
            Hover($"Vector3(1 + 0.04 * {over}, 1 + 0.04 * {over}, 1)").Start(visual, "Scale");
            Hover($"Clamp({over}, 0, 1)").Start(ElementCompositionPreview.GetElementVisual(highlight), "Opacity");
            ElementCompositionPreview.SetIsTranslationEnabled(icon, true);
            var glyph = ElementCompositionPreview.GetElementVisual(icon);
            icon.SizeChanged += (_, e) => glyph.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            Hover($"Vector3(0, -4 * {over}, 0)").Start(glyph, "Translation");
            Hover($"Vector3(1 + 0.18 * {over}, 1 + 0.18 * {over}, 1)").Start(glyph, "Scale");
        }
    }

    public DropZoneViewModel ViewModel { get; }

    // ── Drop target ─────────────────────────────────────────────────────────────────────────────────────────

    public string DropCaption => Loc.T(_overLocal ? "Partager en Wi-Fi local" : "Envoyer et copier le lien");

    public bool Accepts(DataPackageView data) => data.Contains(StandardDataFormats.StorageItems);

    public void OnDragHover(bool over)
    {
        _dragHover.AnimateTo(over ? 1 : 0, over ? Motion.Toggle : Motion.Fade);
        _following = false;
        if (!over)
        {
            _overLocal = false;
            _localHover.AnimateTo(0, Motion.Fade);
        }
    }

    /// <summary>
    /// The colours chase the cursor, a little behind it, on springs. Over the Wi-Fi area (or its QR code, while
    /// sharing), the drop is for the phone; anywhere else on the notch, closed included, for a link.
    /// </summary>
    public void OnDragMoved(Point position)
    {
        bool local = OverLocal(position);
        if (local != _overLocal)
        {
            _overLocal = local;
            _localHover.AnimateTo(local ? 1 : 0, Motion.Hover);
        }

        position = new Point(
            Math.Clamp(position.X, CursorRoom.Left, CursorRoom.Right),
            Math.Clamp(position.Y, CursorRoom.Top, CursorRoom.Bottom));
        if (!_following)
        {
            // First position of this drag: start from it rather than fly in from the last one.
            _following = true;
            _cursorX.Snap(position.X);
            _cursorY.Snap(position.Y);
            return;
        }

        _cursorX.AnimateTo(position.X, Motion.Hover);
        _cursorY.AnimateTo(position.Y, Motion.Hover);
    }

    // ── Ambience ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Three soft blobs of colour (radial gradients), each drifting along its own slow loop while the tab is in view
    /// (and only then: nothing runs otherwise). While a file is dragged over the notch, they gather under the cursor
    /// and tighten, each with its own pull, so the colours swirl in rather than move as one.
    /// </summary>
    /// <remarks>
    /// Their loops stay clear of the notch's top corners, where the body meets the screen edge in pure black.
    /// </remarks>
    private void BindAmbience(Compositor compositor)
    {
        var size = new Vector2((float)_context.Motion.Layout.Expanded.Width, (float)_context.Motion.Layout.Expanded.Height);
        var container = compositor.CreateContainerVisual();
        container.Size = size;
        ElementCompositionPreview.SetElementChildVisual(AmbienceHost, container);

        // While dragging, a little brighter overall.
        new CompositionExpression(compositor, "Lerp(0.8, 1, Clamp(d.Over, 0, 1))").With("d", _ambience).Start(container, "Opacity");

        for (int i = 0; i < Blobs.Length; i++)
        {
            var blob = Blobs[i];
            string drift = $"B{i}";
            _ambience.InsertVector2(drift, blob.Path[0]);

            var brush = compositor.CreateRadialGradientBrush();
            brush.MappingMode = CompositionMappingMode.Absolute;
            brush.ExtendMode = CompositionGradientExtendMode.Clamp;
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0, blob.Color));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, Color.FromArgb((byte)(blob.Color.A * 0.4), blob.Color.R, blob.Color.G, blob.Color.B)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, blob.Color.R, blob.Color.G, blob.Color.B)));

            Bind($"Vector2(Lerp(d.{drift}.X, d.X, Clamp(d.Over, 0, 1) * p), Lerp(d.{drift}.Y, d.Y, Clamp(d.Over, 0, 1) * p))")
                .With("p", blob.Pull)
                .Start(brush, "EllipseCenter");
            Bind("Vector2(r * Lerp(1, 0.6, Clamp(d.Over, 0, 1)), r * 0.62 * Lerp(1, 0.7, Clamp(d.Over, 0, 1)))")
                .With("r", blob.Radius)
                .Start(brush, "EllipseRadius");

            var visual = compositor.CreateSpriteVisual();
            visual.Size = size;
            visual.Brush = brush;
            container.Children.InsertAtTop(visual);

            // The slow loop, played by the compositor; started and stopped with the tab's visibility.
            var loop = compositor.CreateVector2KeyFrameAnimation();
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.55f, 1));
            for (int k = 0; k < blob.Path.Length; k++)
            {
                loop.InsertKeyFrame((float)k / blob.Path.Length, blob.Path[k], ease);
            }

            loop.InsertKeyFrame(1, blob.Path[0], ease);
            loop.Duration = blob.Period;
            loop.IterationBehavior = AnimationIterationBehavior.Forever;
            _drifts[i] = (drift, loop);
        }

        CompositionExpression Bind(string expression) => new CompositionExpression(compositor, expression).With("d", _ambience);
    }

    private void Drift(bool on)
    {
        if (on == _drifting)
        {
            return;
        }

        _drifting = on;
        foreach (var (property, loop) in _drifts)
        {
            if (on)
            {
                _ambience.StartAnimation(property, loop);
            }
            else
            {
                _ambience.StopAnimation(property);
            }
        }
    }

    private sealed record Blob(Color Color, float Radius, float Pull, TimeSpan Period, Vector2[] Path);

    public async Task DropAsync(DataPackageView data)
    {
        bool local = _overLocal;
        var items = await data.GetStorageItemsAsync();
        if (local)
        {
            await ViewModel.Local.StartAsync(items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)).ToList());
            return;
        }

        _row.ScrollToStart();
        await ViewModel.DropAsync(items.ToList());
    }

    // ── Wi-Fi sharing ───────────────────────────────────────────────────────────────────────────────────────

    private bool OverLocal(Point position)
    {
        static bool Within(FrameworkElement element, Point point, double slack) =>
            point.X >= element.Margin.Left - slack && point.X <= element.Margin.Left + element.Width + slack
            && point.Y >= element.Margin.Top - slack && point.Y <= element.Margin.Top + element.Height + slack;

        return Within(LocalArea, position, 4)
            || (ViewModel.Local.IsActive && Within(SharePanel, position, 4))
            || (ViewModel.Local.IsActive && _bubble is not null && position.Y >= _context.Motion.Layout.Expanded.Height - 4);
    }

    /// <summary>Clicked: opens the sharing and the chat bubble to exchange with the other device.</summary>
    private async void OnLocalClick(object sender, RoutedEventArgs e)
    {
        _chatOpen = true;
        if (!ViewModel.Local.IsActive)
        {
            await ViewModel.Local.StartAsync([]);
        }
        else
        {
            SyncChatBubble();
        }
    }

    private void OnStopShareClick(object sender, RoutedEventArgs e) => ViewModel.Local.Stop();

    private async void OnPasteShareClick(object sender, RoutedEventArgs e)
    {
        _chatOpen = true;
        if (await ViewModel.Local.PasteAsync())
        {
            SyncChatBubble();
        }
    }

    private void OnToggleChatClick(object sender, RoutedEventArgs e)
    {
        _chatOpen = _bubble is null;
        if (!_chatOpen)
        {
            ReturnKeyboard();
        }

        SyncChatBubble();
    }

    private void OnCloseChatClick(object sender, RoutedEventArgs e)
    {
        _chatOpen = false;
        ReturnKeyboard();
        SyncChatBubble();
    }

    private void OnSendChatClick(object sender, RoutedEventArgs e) => SendTypedChat();

    private void OnOpenReceivedClick(object sender, RoutedEventArgs e) => ViewModel.Local.OpenReceived();

    private void OnAcceptIncomingClick(object sender, RoutedEventArgs e) => ViewModel.Local.AcceptAll();

    private void OnRefuseIncomingClick(object sender, RoutedEventArgs e) => ViewModel.Local.RefuseAll();

    /// <summary>
    /// Files from the phone wait for an answer: the closed notch asks with a card (like an incoming call) until it is
    /// given, here or there; the open Drop Zone asks beside its actions.
    /// </summary>
    private void AskIncoming()
    {
        var incoming = ViewModel.Local.Incoming;
        if (incoming.Count == 0)
        {
            _question?.Dispose();
            _question = null;
            return;
        }

        if (_incomingCard is null)
        {
            _incomingCard = new IncomingFileCard(_context);
            _incomingCard.Accepted += (_, _) => ViewModel.Local.AcceptAll();
            _incomingCard.Refused += (_, _) => ViewModel.Local.RefuseAll();
        }

        _incomingCard.Show(incoming);
        _question ??= _context.Ask(_incomingCard, _incomingCard.CardSize);
    }

    /// <summary>The QR code and what it is about, in place of the uploads while sharing.</summary>
    private void ShowShare()
    {
        var local = ViewModel.Local;
        bool active = local.IsActive;
        _localOn.AnimateTo(active ? 1 : 0, active ? Motion.Toggle : Motion.Fade);
        LocalCaption.Text = Loc.T(active ? "Partage actif" : "Sans Internet");
        SharePanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        UploadsPane.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        if (!active)
        {
            _chatOpen = true;
            ReturnKeyboard();
            SyncChatBubble();
            return;
        }

        if (local.Url != _qrUrl && local.Url is { } url)
        {
            _qrUrl = url;
            QrHost.Child = QrCode.Build(url, new SolidColorBrush(Colors.Black));
        }

        WhereText.Text = local.Where;
        TypedText.Text = local.TypedAddress ?? "";
        ChatAddressText.Text = local.TypedAddress ?? "";
        AlternateText.Text = local.AlternateAddress ?? "";
        AlternateText.Visibility = local.AlternateAddress is null ? Visibility.Collapsed : Visibility.Visible;
        SharedIcon.Glyph = local.SharedGlyph;
        SharedText.Text = local.SharedText;
        ReceivedText.Text = local.ReceivedText ?? "";
        ShowReceived();
        SyncChatBubble();
    }

    /// <summary>
    /// Beside the actions: files waiting for an answer ("Garder IMG_2034.jpg ?"), else the last one kept ("Reçu :
    /// photo.jpg"); a transfer under way takes the place first.
    /// </summary>
    private void ShowReceived()
    {
        var local = ViewModel.Local;
        bool pending = local.Incoming.Count > 0 && !local.IsTransferring;
        PendingBar.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        if (pending)
        {
            PendingText.Text = local.Incoming.Count == 1 ? Loc.F("Garder {0} ?", local.Incoming[0].Name) : Loc.F("Garder {0} fichiers ?", local.Incoming.Count);
        }

        ReceivedText.Visibility = local.ReceivedText is null || local.IsTransferring || pending ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What crosses the Wi-Fi, beside the actions: "42 % • 11,4 Mo/s" over a thin bar; only while it does.</summary>
    private void ShowTransfer()
    {
        var local = ViewModel.Local;
        TransferStatus.Visibility = local.IsTransferring ? Visibility.Visible : Visibility.Collapsed;
        ShowReceived();
        if (!local.IsTransferring)
        {
            return;
        }

        TransferIcon.Glyph = local.TransferGlyph;
        TransferText.Text = local.TransferText;
        ElementCompositionPreview.GetElementVisual(TransferFill).Scale = new Vector3((float)local.TransferProgress, 1, 1);
    }

    // ── Local Wi-Fi chat bubble ─────────────────────────────────────────────────────────────────────────────

    private void SyncChatBubble()
    {
        bool shouldShow = _context.IsVisible && ViewModel.Local.IsActive && _chatOpen;
        ToggleChatButton.Background = shouldShow ? _activeChatBrush : _defaultPillBrush;
        if (!shouldShow)
        {
            _bubble?.Dispose();
            _bubble = null;
            return;
        }

        if (_bubble is not null)
        {
            return;
        }

        var room = ChatRoom();
        var size = ViewModel.Local.ChatSize ?? new Size(Math.Min(DefaultChatSize.Width, room.Width), Math.Min(DefaultChatSize.Height, room.Height));
        var bubble = _context.ShowBubble(ChatSheet, size, SmallestChat, room);
        bubble.Resized += (_, resized) => ViewModel.Local.ChatSize = resized;
        _bubble = bubble;
        ScrollChatToEnd();
    }

    private Size ChatRoom()
    {
        var layout = _context.Motion.Layout;
        var room = layout.ExpandedMax;
        double width = room.Width;
        double bottom = room.Height;
        try
        {
            if (XamlRoot is { } root)
            {
                var screen = DisplayArea.Primary.WorkArea;
                width = Math.Min(width, screen.Width / root.RasterizationScale - 40);
                bottom = Math.Min(bottom, screen.Height / root.RasterizationScale - layout.TopInset - 20);
            }
        }
        catch (COMException)
        {
        }

        double height = bottom - layout.Expanded.Height - layout.BubbleGap;
        return new(Math.Max(width, SmallestChat.Width), Math.Max(height, SmallestChat.Height));
    }

    private void OnLocalMessagesChanged(object? sender, LocalMessage? added)
    {
        if (added is not null)
        {
            var card = new LocalMessageCard(added, ViewModel.Local);
            card.ImageClicked += OnCardImageClicked;
            var visual = ElementCompositionPreview.GetElementVisual(card);
            ElementCompositionPreview.SetImplicitShowAnimation(card, _show);
            card.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => visual.ImplicitAnimations = _reflow);
            ChatMessages.Children.Add(card);
            ChatEmptyHint.Visibility = Visibility.Collapsed;
            if (_context.IsVisible)
            {
                _chatOpen = true;
                SyncChatBubble();
            }

            ScrollChatToEnd();
            return;
        }

        if (ViewModel.Local.Messages.Count == 0)
        {
            ChatMessages.Children.Clear();
            ChatEmptyHint.Visibility = Visibility.Visible;
            ImagePreviewOverlay.Visibility = Visibility.Collapsed;
            FullPreviewImage.Source = null;
            return;
        }

        foreach (var child in ChatMessages.Children)
        {
            (child as LocalMessageCard)?.Refresh();
        }
    }

    private void ScrollChatToEnd()
    {
        if (ChatMessages.Children.Count == 0)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            ChatScroller.ChangeView(null, ChatScroller.ScrollableHeight + 400, null, disableAnimation: false));
    }

    private async void OnCardImageClicked(object? sender, LocalMessage message)
    {
        if (message.FilePath is not { } path || !File.Exists(path))
        {
            return;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            FullPreviewImage.Source = bitmap;
            PreviewTitleText.Text = message.FileName ?? Loc.T("Image");
            ImagePreviewOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
        }
    }

    private void OnClosePreviewClick(object sender, RoutedEventArgs e)
    {
        ImagePreviewOverlay.Visibility = Visibility.Collapsed;
        FullPreviewImage.Source = null;
    }

    private void OnClosePreviewPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        ImagePreviewOverlay.Visibility = Visibility.Collapsed;
        FullPreviewImage.Source = null;
    }

    private void SendTypedChat()
    {
        string text = ChatBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (ViewModel.Local.SendText(text))
        {
            ChatBox.Text = "";
            ChatBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnChatBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                SendTypedChat();
                break;

            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                ReturnKeyboard();
                break;
        }
    }

    /// <summary>
    /// Pasting in the chat field: if the clipboard holds files or an image, send them straight to the chat; if it
    /// holds text, let the field receive it so the user can edit or press Enter.
    /// </summary>
    private async void OnChatBoxPaste(object sender, TextControlPasteEventArgs e)
    {
        try
        {
            var view = WinClipboard.GetContent();
            if (view.Contains(StandardDataFormats.StorageItems) || (view.Contains(StandardDataFormats.Bitmap) && !view.Contains(StandardDataFormats.Text)))
            {
                e.Handled = true;
                await ViewModel.Local.PasteAsync();
            }
        }
        catch (Exception)
        {
        }
    }

    private async void OnViewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || ChatBox.FocusState != FocusState.Unfocused)
        {
            return;
        }

        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == Windows.System.VirtualKey.V && _context.IsVisible)
        {
            e.Handled = true;
            _chatOpen = true;
            if (await ViewModel.Local.PasteAsync())
            {
                SyncChatBubble();
            }
        }
    }

    private void TakeKeyboard()
    {
        if (_keyboard is not null)
        {
            return;
        }

        _keyboard = _context.TakeKeyboard(lost: () =>
        {
            _keyboard = null;
            if (ChatBox.FocusState != FocusState.Unfocused)
            {
                Focus(FocusState.Programmatic);
            }
        });
        DispatcherQueue.TryEnqueue(() => ChatBox.Focus(FocusState.Programmatic));
    }

    private void ReturnKeyboard()
    {
        var keyboard = _keyboard;
        _keyboard = null;
        keyboard?.Dispose();
        if (ChatBox.FocusState != FocusState.Unfocused)
        {
            Focus(FocusState.Programmatic);
        }
    }

    // ── Tiles ───────────────────────────────────────────────────────────────────────────────────────────────

    private UploadCard CreateCard(UploadItem item)
    {
        var card = new UploadCard(item, ViewModel);
        var visual = ElementCompositionPreview.GetElementVisual(card);
        visual.CenterPoint = new Vector3(TileCenterX, TileCenterY, 0);
        ElementCompositionPreview.SetImplicitShowAnimation(card, _show);
        ElementCompositionPreview.SetImplicitHideAnimation(card, _hide);

        // Glide on layout changes only once placed: attached any sooner, a new tile would fly in from the corner.
        card.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => visual.ImplicitAnimations = _reflow);
        return card;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (int i = 0; i < e.NewItems!.Count; i++)
                {
                    Cards.Children.Insert(e.NewStartingIndex + i, CreateCard((UploadItem)e.NewItems[i]!));
                }

                break;

            case NotifyCollectionChangedAction.Remove:
                for (int i = 0; i < e.OldItems!.Count; i++)
                {
                    Cards.Children.RemoveAt(e.OldStartingIndex);
                }

                break;

            case NotifyCollectionChangedAction.Move:
                Cards.Children.Move((uint)e.OldStartingIndex, (uint)e.NewStartingIndex);
                break;

            default:
                Cards.Children.Clear();
                foreach (var item in ViewModel.Items)
                {
                    Cards.Children.Add(CreateCard(item));
                }

                break;
        }
    }

    /// <summary>"Expires in" is brought up to date when the tab comes into view, not on a timer.</summary>
    private void RefreshWhenVisible()
    {
        Drift(_context.IsVisible);
        if (!_context.IsVisible)
        {
            ReturnKeyboard();
            _bubble?.Dispose();
            _bubble = null;
            return;
        }

        SyncChatBubble();
        var now = DateTimeOffset.Now;
        foreach (var child in Cards.Children)
        {
            (child as UploadCard)?.Refresh(now);
        }
    }
}
