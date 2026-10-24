using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Ilot.Modules.DropZone.Local;
using Windows.UI;
using Ilot.Localization;

namespace Ilot.Modules.DropZone;

/// <summary>
/// One message bubble in the local Wi-Fi chat: text, link, inline image or file, with quick actions to copy, open,
/// save or decide on an incoming file.
/// </summary>
public sealed partial class LocalMessageCard : UserControl
{
    private const string CopyGlyph = "";
    private const string SaveGlyph = "";
    private const string DoneGlyph = "";
    private static readonly TimeSpan FeedbackTime = TimeSpan.FromSeconds(1.5);

    private static readonly SolidColorBrush PcBackground = new(Color.FromArgb(0x22, 0x22, 0xD3, 0xEE));
    private static readonly SolidColorBrush PcBorder = new(Color.FromArgb(0x48, 0x22, 0xD3, 0xEE));
    private static readonly SolidColorBrush DeviceBackground = new(Color.FromArgb(0xFF, 0x16, 0x16, 0x1A));
    private static readonly SolidColorBrush DeviceBorder = new(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush AmberBrush = new(Color.FromArgb(0xFF, 0xFF, 0xC8, 0x57));
    private static readonly SolidColorBrush GreenBrush = new(Color.FromArgb(0xFF, 0x34, 0xC7, 0x59));
    private static readonly SolidColorBrush RedBrush = new(Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));

    private readonly LocalShareViewModel _viewModel;
    private DispatcherQueueTimer? _feedbackTimer;

    public LocalMessageCard(LocalMessage message, LocalShareViewModel viewModel)
    {
        Message = message;
        _viewModel = viewModel;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        HorizontalAlignment = message.FromPc ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        BubbleBorder.Background = message.FromPc ? PcBackground : DeviceBackground;
        BubbleBorder.BorderBrush = message.FromPc ? PcBorder : DeviceBorder;
        BubbleBorder.CornerRadius = message.FromPc ? new CornerRadius(16, 16, 4, 16) : new CornerRadius(16, 16, 16, 4);

        string sender = Loc.T(message.FromPc ? "Depuis ce PC" : "Depuis l'appareil");
        string sizeExtra = message.Kind == LocalMessageKind.Image && message.FileSize > 0
            ? $" · {UploadItem.FormatSize(message.FileSize)}"
            : "";
        MetaText.Text = $"{sender} · {message.TimeText}{sizeExtra}";

        switch (message.Kind)
        {
            case LocalMessageKind.Text:
                BodyText.Text = message.Text ?? "";
                BodyText.Visibility = Visibility.Visible;
                CopyButton.Visibility = Visibility.Visible;
                break;

            case LocalMessageKind.Link:
                LinkDomainText.Text = message.Domain;
                LinkUrlText.Text = message.Link?.AbsoluteUri ?? message.Text ?? "";
                LinkBox.Visibility = Visibility.Visible;
                OpenLinkButton.Visibility = Visibility.Visible;
                CopyButton.Visibility = Visibility.Visible;
                break;

            case LocalMessageKind.Image:
                ImageBox.Visibility = Visibility.Visible;
                CopyButton.Visibility = Visibility.Visible;
                if (!message.FromPc)
                {
                    SaveImageButton.Visibility = Visibility.Visible;
                }
                else
                {
                    FolderButton.Visibility = Visibility.Visible;
                }

                _ = LoadImageAsync(message.FilePath);
                break;

            case LocalMessageKind.File:
                FileBox.Visibility = Visibility.Visible;
                FileIcon.Glyph = message.IsFolder ? "" : KindGlyph(message.FileName ?? "");
                FileNameText.Text = message.FileName ?? Loc.T("Fichier");
                break;
        }

        Refresh();
    }

    public LocalMessage Message { get; }

    public event EventHandler<LocalMessage>? ImageClicked;

    /// <summary>Updates the status and buttons when an incoming file gets a decision.</summary>
    public void Refresh()
    {
        if (Message.Kind != LocalMessageKind.File)
        {
            return;
        }

        string size = UploadItem.FormatSize(Message.FileSize);
        string kind = Message.IsFolder ? Loc.T("Dossier · ") : "";
        if (Message.FromPc)
        {
            FileDetailText.Text = kind + Loc.F("{0} · Partagé sur le Wi-Fi", size);
            FolderButton.Visibility = Message.FilePath is not null ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        switch (Message.FileState)
        {
            case "pending":
                FileDetailText.Text = Loc.F("{0} · En attente de votre accord", size);
                FileDetailText.Foreground = AmberBrush;
                AcceptButton.Visibility = Visibility.Visible;
                RefuseButton.Visibility = Visibility.Visible;
                FolderButton.Visibility = Visibility.Collapsed;
                break;

            case "accepted":
                FileDetailText.Text = Loc.F("{0} · Enregistré dans Téléchargements", size);
                FileDetailText.Foreground = GreenBrush;
                AcceptButton.Visibility = Visibility.Collapsed;
                RefuseButton.Visibility = Visibility.Collapsed;
                FolderButton.Visibility = Visibility.Visible;
                break;

            case "refused":
                FileDetailText.Text = Loc.F("{0} · Refusé", size);
                FileDetailText.Foreground = RedBrush;
                AcceptButton.Visibility = Visibility.Collapsed;
                RefuseButton.Visibility = Visibility.Collapsed;
                FolderButton.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private async Task LoadImageAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage { DecodePixelWidth = 360 };
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            PreviewImage.Source = bitmap;
        }
        catch (Exception)
        {
            // Image removed or unreadable.
        }
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (await _viewModel.CopyMessageAsync(Message))
        {
            CopyIcon.Glyph = DoneGlyph;
            CopyLabel.Text = Loc.T("Copié");
            ArmReset();
        }
    }

    private async void OnOpenLinkClick(object sender, RoutedEventArgs e) => await _viewModel.OpenLinkAsync(Message);

    private async void OnSaveImageClick(object sender, RoutedEventArgs e)
    {
        if (await _viewModel.SaveImageAsync(Message))
        {
            SaveIcon.Glyph = DoneGlyph;
            SaveLabel.Text = Loc.T("Enregistré");
            ArmReset();
        }
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        if (Message.Incoming is { } incoming)
        {
            _viewModel.Accept(incoming);
            Refresh();
        }
    }

    private void OnRefuseClick(object sender, RoutedEventArgs e)
    {
        if (Message.Incoming is { } incoming)
        {
            _viewModel.Refuse(incoming);
            Refresh();
        }
    }

    private void OnFolderClick(object sender, RoutedEventArgs e) => _viewModel.OpenReceived(Message.FilePath);

    private void OnImagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        ImageClicked?.Invoke(this, Message);
    }

    private void ArmReset()
    {
        if (_feedbackTimer is null)
        {
            _feedbackTimer = DispatcherQueue.CreateTimer();
            _feedbackTimer.Interval = FeedbackTime;
            _feedbackTimer.IsRepeating = false;
            _feedbackTimer.Tick += (_, _) =>
            {
                CopyIcon.Glyph = CopyGlyph;
                CopyLabel.Text = Loc.T("Copier");
                SaveIcon.Glyph = SaveGlyph;
                SaveLabel.Text = Loc.T("Enregistrer");
            };
        }

        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private static string KindGlyph(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".heic" or ".heif" or ".gif" or ".webp" or ".bmp" => "",
        ".mp4" or ".mov" or ".m4v" or ".mkv" or ".avi" or ".webm" or ".3gp" => "",
        ".mp3" or ".m4a" or ".aac" or ".wav" or ".flac" or ".ogg" or ".opus" => "",
        ".zip" or ".rar" or ".7z" => "",
        _ => "",
    };
}

