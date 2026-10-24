using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Ilot.Modules.Clipboard;
using Ilot.Modules.DropZone.Local;
using Ilot.Notch;
using Ilot.Settings;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using Ilot.Localization;

namespace Ilot.Modules.DropZone;

/// <summary>
/// The Drop Zone's Wi-Fi sharing and live chat, as shown: the address for the QR code, what is shared, the chat
/// messages, what came in; and what the closed notch says (a ring while a file crosses the network, "Transfert terminé"
/// once there, or a brief acknowledgement when a message, link or image arrives in the chat).
/// </summary>
public sealed class LocalShareViewModel : ObservableObject
{
    private const string WifiGlyph = "";
    private const string ReceiveGlyph = "";
    private const string SendGlyph = "";
    private const string DoneGlyph = "";
    private const string ErrorGlyph = "";
    private const string RefusedGlyph = "";
    private const string ChatGlyph = "";
    private const string LinkGlyph = "";
    private const string ImageGlyph = "";
    private const string PasteGlyph = "";
    private static readonly Color Red = Color.FromArgb(255, 0xFF, 0x6B, 0x6B);

    private readonly LocalTransferService _service;
    private readonly AppSettings _settings;
    private NotchContext? _notch;
    private string? _received;
    private string? _receivedSummary;

    public LocalShareViewModel(LocalTransferService service, AppSettings settings)
    {
        _service = service;
        _settings = settings;
        service.StateChanged += (_, _) =>
        {
            if (!service.IsRunning)
            {
                _received = null;
                _receivedSummary = null;
            }

            OnPropertyChanged(string.Empty); // Everything shown depends on it.
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        };
        service.MessagesChanged += OnMessagesChanged;
        service.Progressed += (_, _) => OnTransfersChanged();
        service.Completed += OnCompleted;
        service.Interrupted += OnInterrupted;
        service.IncomingChanged += (_, _) => OnPropertyChanged(nameof(Incoming));
    }

    /// <summary>The closed notch's activity changed (see <see cref="Activity"/>).</summary>
    public event EventHandler? ActivityChanged;

    /// <summary>A message was added to the local chat (passed as argument) or updated (<c>null</c>).</summary>
    public event EventHandler<LocalMessage?>? MessagesChanged;

    public bool IsActive => _service.IsRunning;

    /// <summary>What the QR code holds.</summary>
    public string? Url => _service.Address?.ToString();

    /// <summary>Over the address to type: which network the phone must be on.</summary>
    public string Where => Loc.F("Scannez, ou tapez ({0}) :", Loc.T(_service.IsWireless ? "même Wi-Fi" : "même réseau"));

    /// <summary>
    /// For a phone without a camera: "mynotch.local/4821" once the name is claimed on the network, else the IP
    /// address, "192.168.1.12/4821".
    /// </summary>
    public string? TypedAddress => _service.NamedAddress ?? _service.TypedAddress;

    /// <summary>Under the name, for phones that do not resolve it: "ou 192.168.1.12/4821".</summary>
    public string? AlternateAddress => _service.NamedAddress is null ? null : Loc.F("ou {0}", _service.TypedAddress);

    public string SharedGlyph => _service.Shared.Count switch
    {
        0 when _service.Messages.Count > 0 => ChatGlyph,
        0 => ReceiveGlyph,
        1 when _service.Shared[0].IsFolder => "",
        _ => "",
    };

    /// <summary>"rapport.pdf · 2,4 Mo", "rapport.pdf et 2 autres · 14 Mo", or ready for chat and files.</summary>
    public string SharedText
    {
        get
        {
            var shared = _service.Shared;
            if (shared.Count == 0)
            {
                int count = _service.Messages.Count;
                return count == 0
                    ? Loc.T("Chat et fichiers prêts (Ctrl+V pour coller)")
                    : count == 1 ? Loc.T("1 élément dans le chat") : Loc.F("{0} éléments dans le chat", count);
            }

            string size = UploadItem.FormatSize(shared.Sum(file => file.Size));
            return shared.Count == 1
                ? $"{shared[0].Name} · {size}"
                : Loc.F(shared.Count > 2 ? "{0} et {1} autres · {2}" : "{0} et {1} autre · {2}", shared[0].Name, shared.Count - 1, size);
        }
    }

    /// <summary>Everything exchanged in the local chat during this sharing, oldest first.</summary>
    public IReadOnlyList<LocalMessage> Messages => _service.Messages;

    /// <summary>Files the phone sent, waiting to be accepted or refused, oldest first.</summary>
    public IReadOnlyList<IncomingFile> Incoming => _service.Incoming;

    /// <summary>The last file or chat item the phone sent, if any: "Reçu : IMG_2034.jpg".</summary>
    public string? ReceivedText => _receivedSummary ?? (_received is null ? null : Loc.F("Reçu : {0}", Path.GetFileName(_received)));

    /// <summary>The chat bubble's size, as last set by pulling its corners; <c>null</c> to use the default size.</summary>
    public Windows.Foundation.Size? ChatSize
    {
        get => _settings.LocalChatWidth > 0 && _settings.LocalChatHeight > 0
            ? new(_settings.LocalChatWidth, _settings.LocalChatHeight)
            : null;
        set
        {
            _settings.LocalChatWidth = value?.Width ?? 0;
            _settings.LocalChatHeight = value?.Height ?? 0;
        }
    }

    /// <summary>
    /// A ring filling up while files cross the network (all of them together), on the closed notch; <c>null</c>
    /// otherwise. Just the percentage: the speed is for the Drop Zone itself (<see cref="TransferText"/>).
    /// </summary>
    public NotchActivity? Activity => IsTransferring
        ? new NotchActivity(Receiving ? ReceiveGlyph : WifiGlyph, $"{TransferProgress * 100:0} %", TransferProgress)
        : null;

    public bool IsTransferring => _service.Active.Count > 0;

    /// <summary>All the files crossing the network together, from 0 to 1.</summary>
    public double TransferProgress
    {
        get
        {
            var active = _service.Active;
            long size = active.Sum(transfer => transfer.Size);
            return size > 0 ? Math.Clamp((double)active.Sum(transfer => transfer.Done) / size, 0, 1) : 0;
        }
    }

    /// <summary>Which way: an arrow down into the PC, or up to the phone.</summary>
    public string TransferGlyph => Receiving ? ReceiveGlyph : SendGlyph;

    /// <summary>"42 % • 11,4 Mo/s" (all of them together), for the Drop Zone's sharing panel.</summary>
    public string TransferText
    {
        get
        {
            double speed = _service.Active.Sum(transfer => transfer.BytesPerSecond);
            string percent = $"{TransferProgress * 100:0} %";
            return speed > 0 ? $"{percent} • {UploadItem.FormatSize(speed)}/s" : percent;
        }
    }

    private bool Receiving => _service.Active.All(transfer => transfer.Direction == TransferDirection.FromPhone);

    public void Attach(NotchContext notch) => _notch = notch;

    /// <summary>Shares these files and folders on the Wi-Fi (none: only receives). A refusal is announced.</summary>
    public async Task<bool> StartAsync(IReadOnlyList<string> paths)
    {
        if (await _service.StartAsync(paths) is { } refusal)
        {
            _notch?.Announce(ErrorGlyph, refusal, Red);
            return false;
        }

        return true;
    }

    public void Stop() => _service.Stop();

    /// <summary>Sends a text message or web link from the PC into the local chat.</summary>
    public bool SendText(string text)
    {
        if (_service.SendText(text) is not null)
        {
            OnPropertyChanged(nameof(SharedGlyph));
            OnPropertyChanged(nameof(SharedText));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Pastes the clipboard's content (copied files or folders, an image/screenshot, a web link or text) straight into
    /// the local Wi-Fi chat, starting the sharing first if it isn't running yet.
    /// </summary>
    public async Task<bool> PasteAsync()
    {
        if (!IsActive && !await StartAsync([]))
        {
            return false;
        }

        var files = await ReadClipboardFilesAsync();
        if (files.Count > 0)
        {
            if (await StartAsync(files))
            {
                _notch?.Announce(PasteGlyph, files.Count == 1 ? Loc.T("Fichier collé") : Loc.F("{0} fichiers collés", files.Count));
                return true;
            }

            return false;
        }

        var capture = await SystemClipboard.ReadAsync();
        switch (capture)
        {
            case CapturedImage image:
                if (await _service.SendImageAsync(image.Pixels, image.Width, image.Height) is not null)
                {
                    _notch?.Announce(ImageGlyph, Loc.T("Image collée dans le chat"));
                    return true;
                }

                break;

            case CapturedText text:
                if (SendText(text.Text))
                {
                    _notch?.Announce(PasteGlyph, Loc.T("Collé dans le chat"));
                    return true;
                }

                break;
        }

        _notch?.Announce(ErrorGlyph, Loc.T("Presse-papier vide"), Red);
        return false;
    }

    /// <summary>Copies a message's text, link or image from the chat onto the PC's clipboard.</summary>
    public Task<bool> CopyMessageAsync(LocalMessage message) => message.Kind switch
    {
        LocalMessageKind.Image when message.FilePath is { } path => SystemClipboard.WriteImageAsync(path),
        LocalMessageKind.Link when message.Link is { } link => SystemClipboard.WriteTextAsync(link.AbsoluteUri),
        _ when !string.IsNullOrEmpty(message.Text) => SystemClipboard.WriteTextAsync(message.Text),
        _ => Task.FromResult(false),
    };

    public async Task OpenLinkAsync(LocalMessage message)
    {
        if (message.Link is { } link)
        {
            await Windows.System.Launcher.LaunchUriAsync(link);
        }
    }

    /// <summary>Saves an image from the chat into Downloads and selects it in Explorer.</summary>
    public async Task<bool> SaveImageAsync(LocalMessage message)
    {
        if (await LocalTransferService.SaveChatImageAsync(message) is not { } saved)
        {
            _notch?.Announce(ErrorGlyph, Loc.T("Image non enregistrée"), Red);
            return false;
        }

        _received = saved;
        _receivedSummary = Loc.F("Enregistré : {0}", Path.GetFileName(saved));
        OnPropertyChanged(nameof(ReceivedText));
        OpenPath(saved);
        return true;
    }

    public void Accept(IncomingFile file)
    {
        if (_service.Accept(file) is { } saved)
        {
            _received = saved;
            _receivedSummary = null;
            OnPropertyChanged(nameof(ReceivedText));
            _notch?.Announce(DoneGlyph, Loc.T("Fichier enregistré"));
        }
        else
        {
            _notch?.Announce(ErrorGlyph, Loc.T("Fichier non enregistré"), Red);
        }
    }

    public void Refuse(IncomingFile file)
    {
        _service.Refuse(file);
        _notch?.Announce(RefusedGlyph, Loc.T("Fichier refusé"));
    }

    /// <summary>Keeps every file waiting (in Downloads), and says so.</summary>
    public void AcceptAll()
    {
        var files = _service.Incoming.ToArray();
        int kept = 0;
        foreach (var file in files)
        {
            if (_service.Accept(file) is { } saved)
            {
                _received = saved;
                _receivedSummary = null;
                kept++;
            }
        }

        OnPropertyChanged(nameof(ReceivedText));
        if (kept < files.Length)
        {
            _notch?.Announce(ErrorGlyph, kept == 0 ? Loc.T("Fichier non enregistré") : Loc.F("{0} non enregistré(s)", files.Length - kept), Red);
        }
        else if (kept > 0)
        {
            _notch?.Announce(DoneGlyph, kept == 1 ? Loc.T("Fichier enregistré") : Loc.F("{0} fichiers enregistrés", kept));
        }
    }

    /// <summary>Deletes every file waiting, and says so.</summary>
    public void RefuseAll()
    {
        var files = _service.Incoming.ToArray();
        foreach (var file in files)
        {
            _service.Refuse(file);
        }

        if (files.Length > 0)
        {
            _notch?.Announce(RefusedGlyph, files.Length == 1 ? Loc.T("Fichier refusé") : Loc.F("{0} fichiers refusés", files.Length));
        }
    }

    /// <summary>Opens the folder files from the phone land in, the last one selected.</summary>
    public void OpenReceived(string? specificPath = null) => OpenPath(specificPath ?? _received);

    private static void OpenPath(string? path)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        if (path is not null && (File.Exists(path) || Directory.Exists(path)))
        {
            start.ArgumentList.Add($"/select,{path}");
        }
        else
        {
            start.ArgumentList.Add(LocalTransferService.ReceiveFolder);
        }

        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"[Local] Explorer not opened: {ex.Message}");
        }
    }

    private void OnMessagesChanged(object? sender, LocalMessage? added)
    {
        OnPropertyChanged(nameof(SharedGlyph));
        OnPropertyChanged(nameof(SharedText));
        if (added is { FromPc: false })
        {
            switch (added.Kind)
            {
                case LocalMessageKind.Link:
                    _receivedSummary = Loc.F("Lien : {0}", added.Domain);
                    OnPropertyChanged(nameof(ReceivedText));
                    _notch?.Announce(LinkGlyph, Loc.T("Lien reçu dans le chat"));
                    break;

                case LocalMessageKind.Image:
                    _receivedSummary = Loc.F("Image : {0}", added.FileName);
                    OnPropertyChanged(nameof(ReceivedText));
                    _notch?.Announce(ImageGlyph, Loc.T("Image reçue dans le chat"));
                    break;

                case LocalMessageKind.Text:
                    string snippet = added.Text!.Length > 28 ? added.Text[..28] + "…" : added.Text;
                    _receivedSummary = Loc.F("Message : {0}", snippet);
                    OnPropertyChanged(nameof(ReceivedText));
                    _notch?.Announce(ChatGlyph, Loc.F("Message : {0}", snippet));
                    break;
            }
        }

        MessagesChanged?.Invoke(this, added);
    }

    private void OnTransfersChanged()
    {
        OnPropertyChanged(nameof(TransferText));
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    // The ring goes first, then the announcement takes its place.
    private void OnCompleted(object? sender, LocalTransfer transfer)
    {
        OnTransfersChanged();
        // A file from the phone is not announced: the notch asks whether to keep it (see Incoming) or shows it in the chat.
        if (transfer.Direction == TransferDirection.ToPhone)
        {
            _notch?.Announce(DoneGlyph, Loc.T("Transfert terminé"));
        }
    }

    /// <summary>
    /// Only a file from the phone cut short is worth saying: a download the phone dropped is often just its browser
    /// handing it over to its download manager, which asks again.
    /// </summary>
    private void OnInterrupted(object? sender, LocalTransfer transfer)
    {
        OnTransfersChanged();
        if (transfer.Direction == TransferDirection.FromPhone && _service.IsRunning)
        {
            _notch?.Announce(ErrorGlyph, Loc.T("Réception interrompue"), Red);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadClipboardFilesAsync()
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var view = WinClipboard.GetContent();
                if (!view.Contains(StandardDataFormats.StorageItems))
                {
                    return [];
                }

                var items = await view.GetStorageItemsAsync();
                return items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)).ToList();
            }
            catch (COMException) when (attempt < 3)
            {
                await Task.Delay(50);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Local] Clipboard files unreadable: {ex.Message}");
                return [];
            }
        }

        return [];
    }
}
