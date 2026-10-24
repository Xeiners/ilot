using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using MyNotch.Composition;
using MyNotch.Interop;
using MyNotch.Localization;
using MyNotch.Mascot;
using MyNotch.Modules.Clipboard;
using MyNotch.Notch;
using MyNotch.Settings;
using Windows.Foundation;
using Windows.UI;

namespace MyNotch.Modules.Assistant;

/// <summary>
/// The drop under the bar, the notch's black with the mascot's light (see <see cref="AssistantGlass"/>), the conversation on the left
/// (each question in a light bubble, each answer formatted, see <see cref="MarkdownLite"/>, with a button to copy it;
/// bouncing dots while it is awaited), and the mascot on the right, large, acting it out: following the question as it
/// is typed, thinking, talking (see <see cref="Act"/>). Before the first question, a greeting.
/// </summary>
/// <remarks>
/// Everything that moves is on the compositor and bound to the drop itself (<c>n.BubbleHeight</c>), so it follows the
/// drop's spring as it grows rather than the layout, which jumps to the new size at once. The light drifts with the
/// notch's glow (<c>n.GlowTurn</c>), which only turns while the assistant is there: nothing runs otherwise.
/// </remarks>
public sealed partial class AssistantPanel : UserControl
{
    // As tall as the mascot's stage and a little more: it never spills out of the drop.
    private const double SmallestHeight = 190;
    private const double TallestHeight = 470;
    private const double StageSize = 150;
    private const double StageHeight = 176;

    /// <summary>The mascot's middle on its stage (its top margin and half its size).</summary>
    private const double FaceMiddle = 14 + 54;
    private const double PictureHeight = 92;

    /// <summary>Its width unless the user made it another (see <see cref="PanelWidth"/>).</summary>
    public const double DefaultWidth = 660;

    /// <summary>The smallest the user may make the drop.</summary>
    public static readonly Size Smallest = new(480, SmallestHeight);

    private static readonly SolidColorBrush Question = new(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush QuestionEdge = new(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Text = new(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Faint = new(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Error = new(Color.FromArgb(0xFF, 0xFF, 0x9A, 0x90));

    private readonly NotchContext _context;
    private readonly AppSettings _settings;
    private readonly CompositionPropertySet _state;
    private readonly SpringChannel _thinking;
    private readonly SpringChannel _greeting;
    private bool _talking;
    private readonly AssistantHistoryView _historyView;
    private bool _historyShown;
    private bool _empty = true;
    private readonly AssistantGlass _glass;
    private readonly List<(ChatMessage Message, FrameworkElement View)> _shown = [];
    private bool _present;
    private double _width = DefaultWidth;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _pointerTimer;
    private NativeMethods.Point _cursor;
    private nint _window;

    /// <summary>How often the pointer is read while the drop shows (see <see cref="FollowPointer"/>).</summary>
    private static readonly TimeSpan PointerEvery = TimeSpan.FromMilliseconds(40);

    public AssistantPanel(NotchContext context, AppSettings settings, AssistantHistory history)
    {
        _context = context;
        _settings = settings;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        NewButton.Click += (_, _) => NewRequested?.Invoke(this, EventArgs.Empty);

        // The conversations kept: the history in place of this one, and back.
        _historyView = new AssistantHistoryView(history);
        HistoryHost.Children.Add(_historyView);
        HistoryButton.Click += (_, _) => ShowHistory(!_historyShown);
        _historyView.Closed += (_, _) => ShowHistory(false);
        _historyView.Opened += (_, conversation) =>
        {
            ShowHistory(false);
            HistoryOpened?.Invoke(this, conversation);
        };
        history.Changed += (_, _) =>
        {
            if (_historyShown)
            {
                LayoutChanged?.Invoke(this, EventArgs.Empty); // Fewer lines (one forgotten): the drop follows.
            }
        };

        var compositor = context.Motion.Compositor;
        _state = compositor.CreatePropertySet();
        _thinking = new SpringChannel(_state, "Thinking", 0);
        _greeting = new SpringChannel(_state, "Greet", 1);
        _state.InsertScalar("Laid", 0);
        _state.InsertScalar("Wander", 0);
        _glass = new AssistantGlass(compositor, Bind, MascotPalette.Find(settings.MascotColor));
        ElementCompositionPreview.SetElementChildVisual(Backdrop, _glass.Visual);
        BindStage();
        Scroller.ViewChanged += (_, _) => UpdateFades();
        ElementCompositionPreview.SetElementChildVisual(TopFade, CreateFade(compositor, top: true));
        ElementCompositionPreview.SetElementChildVisual(BottomFade, CreateFade(compositor, top: false));
        _pointerTimer = DispatcherQueue.CreateTimer();
        _pointerTimer.Interval = PointerEvery;
        _pointerTimer.Tick += (_, _) => FollowPointer();
        Scroller.SizeChanged += (_, _) => UpdateFades();
        SizeChanged += (_, e) => PlaceStage(e.NewSize.Height);

        // The mascot and the pill under it: a click talks with it aloud (or stops).
        Stage.Tapped += (_, e) =>
        {
            e.Handled = true; // Its own: the notch neither pins nor closes for it.
            TalkRequested?.Invoke(this, EventArgs.Empty);
        };
        Stage.DoubleTapped += (_, e) => e.Handled = true;
        Stage.PointerWheelChanged += (_, e) =>
        {
            e.Handled = true; // Not the notch's: its voice's volume.
            int delta = e.GetCurrentPoint(Stage).Properties.MouseWheelDelta;
            if (delta != 0)
            {
                VolumeWheel?.Invoke(this, Math.Sign(delta));
            }
        };
        Stage.PointerEntered += (_, _) => ShowPill(hovered: true);
        Stage.PointerExited += (_, _) => ShowPill(hovered: false);

        Face.Wear(MascotPalette.Find(settings.MascotColor), animate: false);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.MascotColor))
            {
                Face.Wear(MascotPalette.Find(settings.MascotColor));
                _glass.Tint(MascotPalette.Find(settings.MascotColor));
                ShowPill(hovered: false);
            }
            else if (e.PropertyName == nameof(AppSettings.ShowMascot))
            {
                Present(_present);
            }
        };
    }

    /// <summary>The drop's width (the user may resize it): the conversation is laid out, and measured, for it.</summary>
    public double PanelWidth
    {
        get => _width;
        set => _width = value;
    }

    // The text column: the drop, less its left padding, the column gap and the mascot's column.
    private double TextWidth => _width - 26 - 6 - StageSize;

    /// <summary>« Nouvelle conversation ».</summary>
    public event EventHandler? NewRequested;

    /// <summary>A conversation from the history, chosen to go on with it.</summary>
    public event EventHandler<SavedConversation>? HistoryOpened;

    /// <summary>What the drop shows changed on its own (the history shown, or hidden): its size is to be fitted again.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>The history shows, in place of the conversation.</summary>
    public bool IsHistoryShown => _historyShown;

    /// <summary>
    /// The conversations kept, in place of this one (<paramref name="shown"/>), or back to it: the « Historique » button,
    /// the arrow, Escape, or one chosen.
    /// </summary>
    public void ShowHistory(bool shown)
    {
        if (shown == _historyShown)
        {
            return;
        }

        _historyShown = shown;
        if (shown)
        {
            _historyView.Refresh();
        }

        ShowContent();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The greeting, the conversation, or the history: which one shows, and the buttons under them.</summary>
    private void ShowContent()
    {
        HistoryHost.Visibility = _historyShown ? Visibility.Visible : Visibility.Collapsed;
        Greeting.Visibility = !_historyShown && _empty ? Visibility.Visible : Visibility.Collapsed;
        Conversation.Visibility = !_historyShown && !_empty ? Visibility.Visible : Visibility.Collapsed;
        NewButton.Visibility = !_historyShown && !_empty ? Visibility.Visible : Visibility.Collapsed;
        HistoryButton.Visibility = _historyShown ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The mascot, or the pill under it, clicked: to talk with it aloud, or to stop.</summary>
    public event EventHandler? TalkRequested;

    /// <summary>The wheel turned on the mascot: its voice louder (1) or quieter (-1), a notch.</summary>
    public event EventHandler<int>? VolumeWheel;

    /// <summary>
    /// Talking aloud or not: the pill under the mascot says « Arrêter » in the mascot's colour while it is on,
    /// « Parler » otherwise.
    /// </summary>
    public void ShowTalking(bool talking)
    {
        _talking = talking;
        TalkPillText.Text = talking ? Loc.T("Arrêter") : Loc.T("Parler");
        TalkPillGlyph.Glyph = talking ? "\uE71A" : "\uE720";
        ShowPill(hovered: false);
    }

    private void ShowPill(bool hovered)
    {
        var accent = MascotPalette.Find(_settings.MascotColor).Accent;
        var ink = _talking ? Color.FromArgb(0xF0, 0, 0, 0) : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF);
        TalkPill.Background = new SolidColorBrush(_talking ? accent : Color.FromArgb(hovered ? (byte)0x30 : (byte)0x1A, 0xFF, 0xFF, 0xFF));
        TalkPill.Opacity = _talking && hovered ? 0.85 : 1;
        TalkPillText.Foreground = new SolidColorBrush(ink);
        TalkPillGlyph.Foreground = new SolidColorBrush(ink);
    }

    /// <summary>
    /// The stage, laid out where it rests in a drop <paramref name="height"/> tall (so its clicks land where it shows): in
    /// its middle. The compositor carries it from there as the drop springs (see AssistantGlass.StageTranslation).
    /// </summary>
    private void PlaceStage(double height)
    {
        Stage.Margin = new Thickness(0, AssistantGlass.StageTopFor(height, StageHeight), 0, 0);
        _state.InsertScalar("Laid", (float)height);
    }

    /// <summary>
    /// The drop is there (or gone): the mascot wakes up in it, with a little hello, or is put away (nothing of it runs
    /// meanwhile). Not there at all without the mascot (the setting).
    /// </summary>
    public void Present(bool present)
    {
        _present = present;
        bool shown = present && _settings.ShowMascot;
        Face.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown)
        {
            Face.IsAttentive = true; // First: shown, it wakes up at once, with its hello.
            Face.IsShown = true;
            NativeMethods.GetCursorPos(out _cursor); // Followed from its next move.
            _pointerTimer.Start();
            Wander(true);
            return;
        }

        _pointerTimer.Stop();
        Wander(false);
        _historyShown = false; // The drop gone: it opens on the conversation next time.
        ShowContent();

        Face.Act(MascotAct.Rest); // The orb's ribbons stop turning.
        Face.IsShown = false;
        Face.IsAttentive = false;
        _thinking.Snap(0);
    }

    /// <summary>A round of the wandering light (see AssistantGlass): slow, the drop being a quiet place.</summary>
    private static readonly TimeSpan WanderRound = TimeSpan.FromSeconds(18);

    /// <summary>The little light on the left wanders while the drop shows; still (nothing turning) once it is gone.</summary>
    private void Wander(bool on)
    {
        if (!on)
        {
            _state.StopAnimation("Wander");
            return;
        }

        var compositor = _state.Compositor;
        var round = compositor.CreateScalarKeyFrameAnimation();
        round.InsertExpressionKeyFrame(0, "this.StartingValue");
        round.InsertExpressionKeyFrame(1, "this.StartingValue + 1", compositor.CreateLinearEasingFunction());
        round.Duration = WanderRound;
        round.IterationBehavior = AnimationIterationBehavior.Forever;
        _state.StartAnimation("Wander", round);
    }

    /// <summary>The mascot acts the conversation out (see <see cref="MascotView.Act"/>); thinking, it floats in a stronger light.</summary>
    public void Act(MascotAct act)
    {
        Face.Act(act);
        _thinking.AnimateTo(act switch
        {
            MascotAct.Think => 1,
            MascotAct.Talk => 0.45,
            _ => 0,
        }, Motion.Fade);
    }

    /// <summary>Talking aloud: the mascot nods to the user's voice (<paramref name="microphone"/>) and bounces with its own (<paramref name="voice"/>).</summary>
    public void Voice(float microphone, float voice)
    {
        Face.Hear(microphone);
        Face.Voice(voice);
    }

    /// <summary>A key typed: the mascot's eyes go to <paramref name="caret"/> (in the window), where the text is written.</summary>
    public void Watch(Point caret)
    {
        if (ToFace(caret) is { } at)
        {
            Face.Watch(at);
        }
    }

    /// <summary>
    /// The pointer, wherever it is on the screen, read a few times a second while the drop shows (a timer, only
    /// meanwhile): when it moves, the mascot's eyes follow it; when it does not, they stay where the last key put them.
    /// </summary>
    private void FollowPointer()
    {
        if (!NativeMethods.GetCursorPos(out var cursor) || (cursor.X == _cursor.X && cursor.Y == _cursor.Y))
        {
            return;
        }

        _cursor = cursor;
        if (XamlRoot is not { } root)
        {
            return;
        }

        _window = _window != 0 ? _window : Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        var client = cursor;
        if (NativeMethods.ScreenToClient(_window, ref client) && ToFace(new Point(client.X / root.RasterizationScale, client.Y / root.RasterizationScale)) is { } at)
        {
            Face.Follow(at);
        }
    }

    /// <summary><paramref name="inWindow"/> (window coordinates) in the mascot's (its stage is laid out where it rests).</summary>
    private Point? ToFace(Point inWindow)
    {
        if (Face.Visibility != Visibility.Visible || Face.XamlRoot is null)
        {
            return null;
        }

        var origin = Face.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new Point(inWindow.X - origin.X, inWindow.Y - origin.Y);
    }

    /// <summary>
    /// Shows <paramref name="messages"/>: only the last one is redrawn when it is the one being written (an answer
    /// streams in many small pieces), all of them when they changed. None yet: the greeting.
    /// </summary>
    public void Show(IReadOnlyList<ChatMessage> messages)
    {
        bool same = messages.Count == _shown.Count && messages.Select((message, i) => _shown[i].Message == message).All(same => same);
        if (!same)
        {
            Messages.Children.Clear();
            _shown.Clear();
            foreach (var message in messages)
            {
                var view = Render(message);
                _shown.Add((message, view));
                Messages.Children.Add(view);
            }
        }
        else if (_shown.Count > 0)
        {
            var message = _shown[^1].Message;
            var view = Render(message);
            _shown[^1] = (message, view);
            Messages.Children[^1] = view;
        }

        bool empty = messages.Count == 0;
        _empty = empty;
        _greeting.AnimateTo(empty ? 1 : 0, Motion.Toggle); // Larger while it greets; its own size once the chat starts.
        ShowContent();

        // The end of the conversation in sight, as it is written.
        UpdateLayout();
        Scroller.ChangeView(null, Scroller.ScrollableHeight, null, disableAnimation: true);
        UpdateFades();
    }

    /// <summary>
    /// An end of the conversation fading into the black (from the <paramref name="top"/>, or the bottom), and itself
    /// fading out over its last stretch on the right: the mascot's glow reaches under the conversation, and a plain black
    /// band would end on it with an edge.
    /// </summary>
    private static SpriteVisual CreateFade(Compositor compositor, bool top)
    {
        var down = compositor.CreateLinearGradientBrush();
        down.StartPoint = new System.Numerics.Vector2(0, 0);
        down.EndPoint = new System.Numerics.Vector2(0, 1);
        down.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(top ? (byte)0xFF : (byte)0, 0, 0, 0)));
        down.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(top ? (byte)0 : (byte)0xFF, 0, 0, 0)));
        var across = compositor.CreateLinearGradientBrush();
        across.StartPoint = new System.Numerics.Vector2(0, 0);
        across.EndPoint = new System.Numerics.Vector2(1, 0);
        across.ColorStops.Add(compositor.CreateColorGradientStop(0.78f, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));
        across.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, 0xFF, 0xFF, 0xFF)));
        var mask = compositor.CreateMaskBrush();
        mask.Source = down;
        mask.Mask = across;
        var fade = compositor.CreateSpriteVisual();
        fade.Brush = mask;
        fade.RelativeSizeAdjustment = System.Numerics.Vector2.One;
        return fade;
    }

    /// <summary>An end of the conversation fades out where there is more past it.</summary>
    private void UpdateFades()
    {
        TopFade.Opacity = Scroller.VerticalOffset > 1 ? 1 : 0;
        BottomFade.Opacity = Scroller.VerticalOffset < Scroller.ScrollableHeight - 1 ? 1 : 0;
    }

    /// <summary>The drop's size for what it shows: as tall as the conversation (or the greeting), within its room.</summary>
    public Size Fit()
    {
        Footer.Measure(new Size(TextWidth, double.PositiveInfinity));
        double inside;
        if (_historyShown)
        {
            inside = _historyView.DesiredContentHeight;
        }
        else
        {
            var content = Conversation.Visibility == Visibility.Visible ? (FrameworkElement)Messages : Greeting;
            content.Measure(new Size(TextWidth, double.PositiveInfinity));
            inside = content.DesiredSize.Height;
        }

        double height = inside + Footer.DesiredSize.Height + 18 + 12 + 8;
        return new Size(_width, Math.Clamp(Math.Ceiling(height), SmallestHeight, TallestHeight));
    }

    /// <summary>The stage rides the drop's middle as it grows or shrinks, and floats gently while the mascot thinks.</summary>
    private void BindStage()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(Stage, true);
        Bind(AssistantGlass.StageTranslation).Start(ElementCompositionPreview.GetElementVisual(Stage), "Translation");

        // Larger while it greets (before the first question), from its middle: its own size once the chat starts.
        var face = ElementCompositionPreview.GetElementVisual(Face);
        face.CenterPoint = new System.Numerics.Vector3(54, 54, 0);
        Bind(AssistantGlass.FaceScale).Start(face, "Scale");
    }

    /// <summary>An expression on the notch (<c>n</c>), the panel's state (<c>a</c>), the stage's size (<c>s</c>, <c>h</c>) and the mascot's middle on it (<c>fc</c>).</summary>
    private CompositionExpression Bind(string expression) =>
        _context.Bind(expression).With("a", _state).With("s", (float)StageSize).With("h", (float)StageHeight).With("fc", (float)FaceMiddle);

    // ── The conversation ────────────────────────────────────────────────────────────────────────────────────

    private FrameworkElement Render(ChatMessage message)
    {
        if (message.FromUser)
        {
            var question = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 360,
                Padding = new Thickness(14, 8, 14, 9),
                CornerRadius = new CornerRadius(16, 16, 6, 16),
                Background = Question,
                BorderBrush = QuestionEdge,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = message.Text,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Segoe UI Variable Text"),
                    FontSize = 13.5,
                    Foreground = Text,
                    IsTextSelectionEnabled = true,
                },
            };
            if (message.Images.Count == 0)
            {
                return question;
            }

            // The images joined to it, above it, on its side.
            var asked = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            var pictures = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var image in message.Images)
            {
                pictures.Children.Add(new Border
                {
                    Height = PictureHeight,
                    Width = Math.Clamp(PictureHeight * image.Width / (double)image.Height, 48, 200),
                    CornerRadius = new CornerRadius(12),
                    BorderBrush = QuestionEdge,
                    BorderThickness = new Thickness(1),
                    Background = new ImageBrush { ImageSource = image.Preview, Stretch = Stretch.UniformToFill },
                });
            }

            asked.Children.Add(pictures);
            if (message.Text.Length > 0)
            {
                asked.Children.Add(question);
            }

            return asked;
        }

        if (message.IsWriting && message.Text.Length == 0)
        {
            return Dots();
        }

        if (message.IsError)
        {
            var error = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            error.Children.Add(new FontIcon { Glyph = "", FontSize = 13, Foreground = Error, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            error.Children.Add(new TextBlock
            {
                Text = message.Text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = TextWidth - 40,
                FontSize = 13.5,
                Foreground = Error,
            });
            return error;
        }

        var answer = new RichTextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 13.5,
            LineHeight = 20,
            Foreground = Text,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        MarkdownLite.Fill(answer, message.Text);
        if (message.IsWriting)
        {
            return answer;
        }

        // Done: a quiet button to copy the answer (as written, Markdown included).
        var copy = new Button
        {
            Padding = new Thickness(6, 3, 6, 3),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new FontIcon { Glyph = "", FontSize = 12, Foreground = Faint },
        };
        ToolTipService.SetToolTip(copy, Loc.T("Copier la réponse"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, Loc.T("Copier la réponse"));
        copy.Click += async (_, _) =>
        {
            if (await SystemClipboard.WriteTextAsync(message.Text))
            {
                copy.Content = new FontIcon { Glyph = "", FontSize = 12, Foreground = Faint }; // Copied.
            }
        };
        var block = new StackPanel { Spacing = 2 };
        block.Children.Add(answer);
        block.Children.Add(copy);
        return block;
    }

    /// <summary>The answer awaited: three dots bouncing one after the other, on the compositor (with the notch's glow).</summary>
    private StackPanel Dots()
    {
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(2, 8, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < 3; i++)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Fill = Text };
            dots.Children.Add(dot);
            ElementCompositionPreview.SetIsTranslationEnabled(dot, true);
            var visual = ElementCompositionPreview.GetElementVisual(dot);
            _context.Bind(AssistantGlass.DotTranslation).With("i", i).Start(visual, "Translation");
            _context.Bind(AssistantGlass.DotOpacity).With("i", i).Start(visual, "Opacity");
        }

        return dots;
    }
}
