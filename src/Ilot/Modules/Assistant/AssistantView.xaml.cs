using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Ilot.Localization;
using Ilot.Modules.Assistant.Voice;
using Ilot.Notch;
using Ilot.Settings;
using Windows.System;
using Windows.UI.Core;

namespace Ilot.Modules.Assistant;

/// <summary>
/// The bar the notch becomes to ask the assistant (the mascot clicked, or its shortcut), taller with each line of the
/// question (Shift + Enter), and the drop falling under it as it opens: the mascot, large, on the right of it, acting the
/// conversation out (see <see cref="MascotAct"/>), and the conversation, the drop stretching as the answer is written.
/// Both are drawn back in when the bar closes.
/// </summary>
/// <remarks>
/// The keyboard is taken for as long as the bar shows, as for the search: Escape or a click elsewhere hands it back.
/// The conversation stays when the bar closes, and is there again when it opens.
/// </remarks>
public sealed partial class AssistantView : UserControl
{
    /// <summary>The answer streams in many small pieces: the drop is redrawn at most this often.</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMilliseconds(60);

    private readonly AssistantConversation _conversation;
    private readonly NotchContext _context;
    private readonly AssistantPanel _panel;
    private readonly AppSettings _settings;
    private readonly AssistantVoice _voice;
    private readonly ScreenCaptureService _capture;
    private bool _capturing;
    private readonly Quick.MixerSlider _voiceVolume;
    private readonly string _hint;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refresh;
    private NotchShape _shape = AssistantModule.Bar;
    private readonly List<ChatImage> _images = [];
    private NotchBubble? _bubble;
    private IDisposable? _keyboard;
    private bool _open;

    public AssistantView(AssistantConversation conversation, AssistantVoice voice, ScreenCaptureService capture, NotchContext context, AppSettings settings)
    {
        _conversation = conversation;
        _voice = voice;
        _capture = capture;
        _context = context;
        _settings = settings;
        _panel = new AssistantPanel(context, settings, conversation.History);
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        _hint = Hint.Text;

        // The bar blooms in as the notch opens, as the search bar does.
        ElementCompositionPreview.SetIsTranslationEnabled(LayoutRoot, true);
        var root = ElementCompositionPreview.GetElementVisual(LayoutRoot);
        context.Bind($"Clamp((n.Expansion - 0.4) * 2.2, 0, 1) * {NotchContext.TabOpacity}").Start(root, "Opacity");
        context.Bind("Vector3(0, (1 - n.Progress) * -6, 0)").Start(root, "Translation");
        var glyph = ElementCompositionPreview.GetElementVisual(AskGlyph);
        glyph.CenterPoint = new Vector3(10, 10, 0);
        context.Bind("Vector3(Lerp(0.55, 1, n.Progress), Lerp(0.55, 1, n.Progress), 1)").Start(glyph, "Scale");

        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = RefreshEvery;
        _refresh.IsRepeating = false;
        _refresh.Tick += (_, _) => ShowConversation();

        QuestionBox.TextChanged += (_, _) =>
        {
            bool typing = QuestionBox.Text.Length > 0;
            Hint.Visibility = typing ? Visibility.Collapsed : Visibility.Visible;
            ShowReady();
            if (typing && !_conversation.IsBusy && !_voice.IsOn)
            {
                Act(MascotAct.Listen);
                WatchCaret();
            }
        };

        // Talking aloud: the button, the mascot listening, thinking, talking (its voice bouncing it), what is said
        // written into the conversation.
        _panel.TalkRequested += (_, _) => ToggleTalk();

        // Its voice's volume: the slider in the bar while talking, the wheel on the mascot.
        _voiceVolume = new Quick.MixerSlider(Mascot.MascotPalette.Find(settings.MascotColor).Accent);
        VoiceVolumeHost.Child = _voiceVolume;
        AutomationProperties.SetName(_voiceVolume, Loc.T("Volume de sa voix"));
        _voiceVolume.Changed += (_, level) => SetVoiceVolume(level * 100);
        _panel.VolumeWheel += (_, steps) => SetVoiceVolume(settings.AssistantVoiceVolume + steps * VoiceVolumeStep);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.AssistantVoiceVolume))
            {
                ShowVoiceVolume();
            }
            else if (e.PropertyName == nameof(AppSettings.MascotColor))
            {
                _voiceVolume.Tint(Mascot.MascotPalette.Find(settings.MascotColor).Accent);
            }
        };
        ShowVoiceVolume();
        _voice.StateChanged += (_, _) =>
        {
            ShowTalking();
            ShowStoppable();
            ActOut();

            // Talking, the closed notch keeps its pill, the mascot in it as the orb: it still hears. Only the orb in it:
            // a round pill, as tall as the closed notch (the widest asked wins: the player's sung line, if it shows).
            _context.SetPresence(_voice.IsOn ? NotchPresence.Active : NotchPresence.Nothing);
            _context.RequestCompactWidth(_voice.IsOn ? _context.Motion.Layout.Collapsed.Height : null);
        };
        _voice.Levels += (_, levels) =>
        {
            _panel.Voice(levels.Microphone, levels.Voice);
            _context.VoiceMascot(levels.Microphone, levels.Voice);
        };

        // A line more (Shift + Enter, or a long question), or less: the bar follows, and the drop under it.
        LayoutRoot.SizeChanged += (_, e) =>
        {
            double height = Math.Max(AssistantModule.Bar.Height, Math.Ceiling(e.NewSize.Height));
            if (height != _shape.Height)
            {
                _shape = AssistantModule.Bar with { Height = height };
                _context.Reshape();
            }
        };
        QuestionBox.PreviewKeyDown += OnPreviewKeyDown;
        QuestionBox.Paste += OnPaste;
        CaptureButton.Click += (_, _) => CaptureScreen();
        _panel.InterruptRequested += (_, _) => StopAnswer();
        _panel.MuteRequested += (_, _) => _voice.ToggleMute();
        _voice.MutedChanged += (_, _) =>
        {
            ShowMuted();
            if (_voice.IsOn)
            {
                // Said on the closed notch (the open one shows it in the bar).
                _context.Announce(_voice.IsMuted ? MicOffGlyph : MicGlyphOn, _voice.IsMuted ? Loc.T("Micro coupé") : Loc.T("Micro rétabli"));
            }
        };
        _panel.UndoRequested += (_, entry) => _ = _conversation.UndoAsync(entry);
        _conversation.Changed += (_, message) =>
        {
            // An action waits for the user's yes, or, talking, gave something to read (a command's output): the notch may
            // be closed (the pill, while talking), so the bar opens, for them to see it.
            bool waits = message.Action?.State == ActionState.Asking;
            bool gave = _voice.IsOn && message.Action is { State: ActionState.Done or ActionState.Failed, Output.Length: > 0 };
            if ((waits || gave) && !(_open && _context.IsVisible))
            {
                if (_voice.IsOn)
                {
                    // Talking, the user is in another app: the bar opens without taking the keyboard (Windows may not
                    // give it, and the bar would close again, unseen); held open while it waits for their answer.
                    OpenForAttention(hold: waits);
                }
                else
                {
                    Open();
                }
            }

            ShowWaiting();
            AnnounceDone(message.Action);

            if (_conversation.State == AssistantState.Talking)
            {
                Act(MascotAct.Talk); // A bounce for the words coming (it keeps its own pace).
            }

            if (!_refresh.IsRunning)
            {
                _refresh.Start();
            }
        };
        _conversation.StateChanged += (_, _) =>
        {
            ShowStoppable();
            ActOut();
        };
        _panel.NewRequested += (_, _) =>
        {
            _conversation.Clear();
            QuestionBox.Focus(FocusState.Programmatic);
        };

        // The history: shown in the drop, which fits it; a conversation chosen there takes this one's place, to go on.
        _panel.LayoutChanged += (_, _) => ShowConversation();
        _panel.HistoryOpened += (_, saved) =>
        {
            _voice.Stop();
            _conversation.Open(saved);
            QuestionBox.Focus(FocusState.Programmatic);
        };

        // The notch closed (a click elsewhere…), or another tab took over: the bar lets go.
        context.ExpandedChanged += (_, _) =>
        {
            if (!context.IsExpanded)
            {
                _bubble = null; // Drawn back in with the notch.
                _panel.Present(false);
                EndTyping();
                _open = false;
                ActOut();
            }
        };
        context.SelectionChanged += (_, _) =>
        {
            if (!context.IsSelected)
            {
                Leave();
                _open = false;
                ActOut();
            }
        };
    }

    /// <summary>
    /// The mascot clicked, or the shortcut: the bar opens, ready to type, with the conversation so far under it.
    /// </summary>
    public void Open()
    {
        _open = true;
        _conversation.Prepare();
        _context.Show();
        _keyboard ??= _context.TakeKeyboard(OnKeyboardLost);
        if (XamlRoot is not null)
        {
            // The screen captured is the notch's (it is left out of it), whether the user or Gemini asks.
            _capture.Window = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        }
        QuestionBox.Focus(FocusState.Programmatic);
        QuestionBox.SelectAll();
        ShowConversation();
        ActOut();
    }

    /// <summary>The open notch's shape: the bar, as tall as the question.</summary>
    public NotchShape Shape => _shape;

    /// <summary>The shortcut again: open, it closes; else it opens.</summary>
    public void Toggle()
    {
        if (_open && _context.IsVisible)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    /// <summary>Done (Escape, the shortcut): the keyboard goes back to the app that had it, the notch closes at once.</summary>
    public void Close()
    {
        Leave();
        _open = false;
        _context.Close();
        ActOut();
    }

    private void Leave()
    {
        DropConversation();
        EndTyping();
    }

    private void EndTyping()
    {
        var keyboard = _keyboard;
        _keyboard = null;
        keyboard?.Dispose();
    }

    /// <summary>A click elsewhere: the bar is over, at once.</summary>
    private void OnKeyboardLost()
    {
        _keyboard = null;
        DropConversation();
        _open = false;
        _context.Close();
        ActOut();
    }

    /// <summary>
    /// The drop under the bar: as big as the conversation (within its room), or the greeting before it; once the user has
    /// resized it (its bottom corners), that size, kept from then on (the settings), the conversation scrolling in it.
    /// </summary>
    private void ShowConversation()
    {
        var chosen = ChosenSize;
        _panel.PanelWidth = chosen?.Width ?? AssistantPanel.DefaultWidth;
        _panel.Show(_conversation.Messages);
        if (!_context.IsVisible || !_open) // Open with the keyboard, or for the user to see something (OpenForAttention).
        {
            return;
        }

        var size = chosen ?? _panel.Fit();
        if (_bubble is null)
        {
            var bubble = _context.ShowBubble(_panel, size, AssistantPanel.Smallest);
            bubble.Resized += (_, resized) =>
            {
                _settings.AssistantChatWidth = resized.Width;
                _settings.AssistantChatHeight = resized.Height;
                _panel.PanelWidth = resized.Width;
            };
            _bubble = bubble;
            _panel.Present(true);
            ActOut(); // The mascot there acts too.
        }
        else
        {
            _bubble.Resize(size);
        }
    }

    /// <summary>The drop's size the user chose by resizing it, if they did.</summary>
    private Windows.Foundation.Size? ChosenSize =>
        _settings.AssistantChatWidth > 0 && _settings.AssistantChatHeight > 0
            ? new Windows.Foundation.Size(_settings.AssistantChatWidth, _settings.AssistantChatHeight)
            : null;

    private void DropConversation()
    {
        var bubble = _bubble;
        _bubble = null;
        bubble?.Dispose();
        _panel.Present(false);
    }

    /// <summary>
    /// The mascot shows where the conversation is at: the orb while the bar is open or an answer is being written, back
    /// to itself once the bar is closed and nothing is.
    /// </summary>
    private void ActOut() => Act(_voice.IsOn ? _voice.State switch
    {
        VoiceState.Speaking => MascotAct.Talk,
        VoiceState.Thinking => MascotAct.Think,
        _ => MascotAct.Listen,
    } : _conversation.State switch
    {
        AssistantState.Thinking => MascotAct.Think,
        AssistantState.Talking => MascotAct.Talk,
        AssistantState.Done when _open => MascotAct.Done,
        AssistantState.Failed when _open => MascotAct.Fail,
        _ => _open ? MascotAct.Listen : MascotAct.Rest,
    });

    /// <summary>Both mascots act it out: the one in the drop, and the notch's (its glow; the mascot itself, if it shows).</summary>
    private void Act(MascotAct act)
    {
        _context.ActMascot(act);
        _panel.Act(act);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when _voice.IsOn:
                break; // Talking: nothing is written meanwhile (one or the other).

            case VirtualKey.S when IsDown(VirtualKey.Control) && IsDown(VirtualKey.Shift):
                CaptureScreen();
                break;

            // Ctrl + Z with nothing typed: the latest thing it did to the files taken back (with text, the text's own undo).
            case VirtualKey.Z when IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Shift) && QuestionBox.Text.Length == 0 && _conversation.CanUndo:
                _ = _conversation.UndoLastAsync();
                break;

            case VirtualKey.Escape when _panel.IsHistoryShown:
                _panel.ShowHistory(false); // Back to the conversation first.
                break;

            // Talking, the question is off (read only): M cuts the microphone, or brings it back.
            case VirtualKey.M when _voice.IsOn && !IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu):
                _voice.ToggleMute();
                break;

            case VirtualKey.Escape when Stoppable:
                StopAnswer(); // Its answer first; the talk, or the bar, at the next one.
                break;

            case VirtualKey.Escape when _voice.IsOn:
                _voice.Stop(); // The talk first; the bar at the next one.
                break;

            case VirtualKey.Enter when !IsDown(VirtualKey.Shift):
                if ((QuestionBox.Text.Trim().Length > 0 || _images.Count > 0) && !_conversation.IsBusy)
                {
                    _conversation.Ask(QuestionBox.Text, [.. _images]);
                    QuestionBox.Text = "";
                    _images.Clear();
                    ShowImages();
                }

                break;

            case VirtualKey.Escape:
                if (QuestionBox.Text.Length > 0 || _images.Count > 0)
                {
                    QuestionBox.Text = "";
                    _images.Clear();
                    ShowImages();
                }
                else
                {
                    Close();
                }

                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>A notch of the wheel on the mascot moves its voice's volume this much (percent).</summary>
    private const double VoiceVolumeStep = 5;

    /// <summary>Its voice's volume (percent), into the settings: the voice follows at once (see AssistantVoice).</summary>
    private void SetVoiceVolume(double percent) => _settings.AssistantVoiceVolume = Math.Clamp(percent, 0, 100);

    /// <summary>The slider and its glyph, at the voice's volume (a crossed speaker at nothing).</summary>
    private void ShowVoiceVolume()
    {
        double level = _settings.AssistantVoiceVolume / 100;
        _voiceVolume.Show(level, silent: false);
        VoiceVolumeGlyph.Glyph = level <= 0 ? "\uE74F" : level < 0.34 ? "\uE993" : level < 0.67 ? "\uE994" : "\uE767";
        ToolTipService.SetToolTip(VoiceVolume, Loc.F("Volume de sa voix : {0} %", Math.Round(level * 100)));
    }

    /// <summary>At most this many images go with a question.</summary>
    private const int MostImages = 4;

    // The question's room: four lines alone, two with images under it (the bar stays within the open notch).
    private const double TallestQuestion = 104;
    private const double TallestQuestionWithImages = 52;

    /// <summary>
    /// Ctrl + V of an image (a screenshot, image files copied in the Explorer): it joins the question, shown under it,
    /// rather than pasting nothing. Text pastes as text.
    /// </summary>
    private async void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        if (_voice.IsOn)
        {
            e.Handled = true; // Talking: nothing is written meanwhile.
            return;
        }

        var clipboard = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        if (!ChatImage.IsImage(clipboard))
        {
            return;
        }

        e.Handled = true;
        foreach (var image in await ChatImage.FromClipboardAsync(clipboard))
        {
            if (_images.Count < MostImages)
            {
                _images.Add(image);
            }
        }

        ShowImages();
        if (_images.Count > 0 && !_conversation.IsBusy)
        {
            Act(MascotAct.Listen);
            WatchCaret();
        }
    }

    /// <summary>
    /// The button, or Ctrl + Shift + S: the screen joins the question as a pasted image does (the notch left out of it,
    /// see <see cref="ScreenCaptureService"/>); while talking, it is shown to Gemini at once (and in the conversation).
    /// </summary>
    private async void CaptureScreen()
    {
        if (_capturing || (!_voice.IsOn && _images.Count >= MostImages))
        {
            return;
        }

        _capturing = true;
        CaptureButton.IsEnabled = false;
        try
        {
            if (await _capture.CaptureAsync() is not { } image)
            {
                return;
            }

            if (_voice.IsOn)
            {
                await _voice.ShowScreenAsync(image);
            }
            else if (_images.Count < MostImages)
            {
                _images.Add(image);
                ShowImages();
                Act(MascotAct.Listen);
            }
        }
        finally
        {
            _capturing = false;
            CaptureButton.IsEnabled = true;
        }
    }

    /// <summary>The images joined to the question, under it: each a small rounded picture, with a cross to take it off.</summary>
    private void ShowImages()
    {
        Attachments.Children.Clear();
        foreach (var image in _images)
        {
            var picture = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                Background = new ImageBrush { ImageSource = image.Preview, Stretch = Stretch.UniformToFill },
            };
            var remove = new Button
            {
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(0, -6, -6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF0, 0x2A, 0x2A, 0x30)),
                AllowFocusOnInteraction = false, // The question keeps the keyboard.
                Content = new FontIcon { Glyph = "", FontSize = 7 },
            };
            AutomationProperties.SetName(remove, Loc.T("Retirer l'image"));
            remove.Click += (_, _) =>
            {
                _images.Remove(image);
                ShowImages();
            };
            var item = new Grid();
            item.Children.Add(picture);
            item.Children.Add(remove);
            Attachments.Children.Add(item);
        }

        Attachments.Visibility = _images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QuestionBox.MaxHeight = _images.Count > 0 ? TallestQuestionWithImages : TallestQuestion;
        ShowReady();
    }

    /// <summary>
    /// Talking aloud starts, or stops (the mascot in the drop, or the pill under it). It goes on when the notch closes
    /// (the mascot, the orb wherever it is, says it still hears): it stops here, with Escape, or if Google ends it.
    /// </summary>
    private void ToggleTalk()
    {
        if (_voice.IsOn)
        {
            _voice.Stop();
        }
        else
        {
            _voice.Start();
        }
    }

    /// <summary>The closed notch's tick for what it did: the green of a task done.</summary>
    private static readonly Windows.UI.Color DoneTint = Windows.UI.Color.FromArgb(255, 0x5B, 0xD6, 0x8A);

    /// <summary>
    /// What it just did on the PC, said on the closed notch for a moment, with a tick (« Dossier copié », « Fichier
    /// supprimé », « 3 fenêtres fermées »), once; and what was taken back. The open notch shows nothing of it (the
    /// conversation does): the pill only, as when talking aloud with the notch closed.
    /// </summary>
    private void AnnounceDone(AssistantAction? action)
    {
        if (action is null)
        {
            return;
        }

        if (action is { State: ActionState.Done, Announced: false, Said: { Length: > 0 } said })
        {
            action.Announced = true;
            _context.Announce("", said, DoneTint);
        }
        else if (action is { State: ActionState.Failed, Announced: false })
        {
            // Not done (not found, refused…): said too, rather than a silence; the reason is in the conversation.
            action.Announced = true;
            string label = action.Label.Length > 44 ? action.Label[..43] + "…" : action.Label;
            _context.Announce("", Loc.F("Pas fait : {0}", label), FailedTint);
        }
        else if (action.State == ActionState.Undone)
        {
            _context.Announce("", action.Outcome.Length > 0 ? action.Outcome : Loc.T("Revenu en arrière"));
        }
    }

    /// <summary>The notch held open while an action waits for the user's answer (see <see cref="OpenForAttention"/>).</summary>
    private IDisposable? _attention;

    /// <summary>« Autorisation demandée » shows on the closed notch.</summary>
    private bool _waitingShown;

    private static readonly Windows.UI.Color WaitingTint = Windows.UI.Color.FromArgb(255, 0xFF, 0xC1, 0x4D);
    private static readonly Windows.UI.Color FailedTint = Windows.UI.Color.FromArgb(255, 0xFF, 0x6B, 0x6B);

    /// <summary>
    /// The notch open on the conversation for the user to see something (an action to allow, a command's output) while
    /// they talk from another app: without the keyboard (the buttons take clicks as they are), and, with
    /// <paramref name="hold"/>, held open until nothing waits for them any more.
    /// </summary>
    private void OpenForAttention(bool hold)
    {
        _open = true;
        if (hold)
        {
            _attention ??= _context.HoldOpen();
        }

        _conversation.Prepare();
        _context.Show();
        ShowConversation();
        ActOut();
    }

    /// <summary>
    /// While an action waits for the user's yes: « Autorisation demandée » stays on the closed notch (as a live activity),
    /// and the notch is held open; both let go once it is answered (or called off).
    /// </summary>
    private void ShowWaiting()
    {
        bool asking = _conversation.Messages.Any(message => message.Action?.State == ActionState.Asking);
        if (asking != _waitingShown)
        {
            _waitingShown = asking;
            _context.SetActivity(asking ? new NotchActivity("", Loc.T("Autorisation demandée"), null, WaitingTint) : null);
        }

        if (!asking && _attention is { } attention)
        {
            _attention = null;
            attention.Dispose();
        }
    }

    private const string MicGlyphOn = "";
    private const string MicOffGlyph = "";

    /// <summary>The hint as the microphone is (and the buttons under the mascot: see <see cref="ShowControls"/>).</summary>
    private void ShowMuted()
    {
        if (_voice.IsOn)
        {
            Hint.Text = _voice.IsMuted ? Loc.T("Micro coupé : je n'entends plus rien (M pour le rétablir)") : Loc.T("Parlez, je vous écoute…");
        }

        ShowControls();
    }

    /// <summary>
    /// Under the mascot, beside its pill: the microphone (talking: one click cuts it, one brings it back) and
    /// « Interrompre » (while it answers, aloud or in writing). Not up in the bar.
    /// </summary>
    private void ShowControls()
    {
        string tip = _voice.IsMuted
            ? Loc.F("Rétablir le micro (M, ou {0} de n'importe où)", MicrophoneShortcut.Display)
            : Loc.F("Couper le micro sans arrêter la conversation (M, ou {0} de n'importe où)", MicrophoneShortcut.Display);
        _panel.ShowControls(_voice.IsOn, _voice.IsMuted, Stoppable, tip);
    }

    /// <summary>It answers, aloud or in writing (thinking, writing, speaking, its tools running): the user may stop it.</summary>
    private bool Stoppable => _voice.IsOn ? _voice.IsAnswering : _conversation.IsAnswering;

    /// <summary>
    /// « Interrompre », or Escape: its answer stops, the conversation going on (what it said so far stays). Talking, the
    /// microphone stays open: the user may say the next thing at once.
    /// </summary>
    private void StopAnswer()
    {
        if (_voice.IsOn)
        {
            _voice.Interrupt();
        }
        else
        {
            _conversation.Stop();
        }

        ShowStoppable();
        ActOut();
    }

    /// <summary>« Interrompre » shows while it answers, and only then (under the mascot).</summary>
    private void ShowStoppable() => ShowControls();

    /// <summary>
    /// The bar as the talk goes: the hint saying it listens, the question off meanwhile (and the pill under the mascot).
    /// </summary>
    private void ShowTalking()
    {
        bool on = _voice.IsOn;
        VoiceVolume.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Hint.Text = on ? Loc.T("Parlez, je vous écoute…") : _hint;

        // One or the other: talking, the question is off (dimmed, read only: it keeps the keyboard for Escape).
        QuestionBox.IsReadOnly = on;
        QuestionBox.Opacity = on ? 0.4 : 1;
        Attachments.Opacity = on ? 0.4 : 1;
        AskGlyph.Opacity = on ? 0.3 : AskGlyph.Opacity;
        _panel.ShowTalking(on);
        ShowMuted(); // The microphone button with the talk, and its hint if cut.
        ShowReady();
    }

    /// <summary>Something to send (text, or an image): Enter does; else Escape closes.</summary>
    private void ShowReady()
    {
        bool ready = !_voice.IsOn && (QuestionBox.Text.Length > 0 || _images.Count > 0);
        AskGlyph.Opacity = _voice.IsOn ? 0.3 : ready ? 1 : 0.55;
        KeyText.Text = ready ? Loc.T("Entrée") : Loc.T("Échap");
    }

    /// <summary>The mascot's eyes go to the caret: where the question is being written (in the window).</summary>
    private void WatchCaret()
    {
        int index = Math.Clamp(QuestionBox.SelectionStart, 0, QuestionBox.Text.Length);
        var place = new Windows.Foundation.Point(0, QuestionBox.ActualHeight / 2);
        if (QuestionBox.Text.Length > 0)
        {
            // The edge after the character before the caret (there is none before the first).
            var rect = index > 0
                ? QuestionBox.GetRectFromCharacterIndex(index - 1, trailingEdge: true)
                : QuestionBox.GetRectFromCharacterIndex(0, trailingEdge: false);
            place = new Windows.Foundation.Point(rect.X, rect.Y + rect.Height / 2);
        }

        _panel.Watch(QuestionBox.TransformToVisual(null).TransformPoint(place));
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
