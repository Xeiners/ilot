using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Ilot.Modules.DropZone.Hosts;
using Ilot.Notch;
using Ilot.Settings;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using Ilot.Localization;

namespace Ilot.Modules.DropZone;

/// <summary>
/// Presentation of the uploads, and what the closed notch says about them: a live activity with the progress while
/// sending, "Lien copié" once done, the reason when something is refused.
/// </summary>
public sealed class DropZoneViewModel : ObservableObject
{
    private const string UploadGlyph = "";
    private const string ArchiveGlyph = "";
    private const string PausedGlyph = "";
    private const string LinkGlyph = "";
    private const string ErrorGlyph = "";
    private static readonly Color Amber = Color.FromArgb(255, 0xFF, 0xC8, 0x57);
    private static readonly Color Red = Color.FromArgb(255, 0xFF, 0x6B, 0x6B);

    private readonly FileUploadService _uploads;
    private readonly AppSettings _settings;
    private NotchContext? _notch;

    public DropZoneViewModel(FileUploadService uploads, AppSettings settings, LocalShareViewModel local)
    {
        _uploads = uploads;
        _settings = settings;
        Local = local;
        local.ActivityChanged += (_, _) => UpdateActivity();
        ((INotifyCollectionChanged)uploads.Items).CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            UpdateActivity();
        };
        uploads.Completed += OnCompleted;
        uploads.Failed += (_, item) => _notch?.Announce(ErrorGlyph, item.Error ?? Loc.T("Échec de l'envoi"), Red);
        foreach (var item in uploads.Items)
        {
            item.PropertyChanged += OnItemChanged;
        }

        ((INotifyCollectionChanged)uploads.Items).CollectionChanged += (_, e) =>
        {
            foreach (UploadItem item in e.NewItems ?? Array.Empty<UploadItem>())
            {
                item.PropertyChanged += OnItemChanged;
            }

            foreach (UploadItem item in e.OldItems ?? Array.Empty<UploadItem>())
            {
                item.PropertyChanged -= OnItemChanged;
            }
        };
    }

    public ReadOnlyObservableCollection<UploadItem> Items => _uploads.Items;

    /// <summary>The sharing with a phone on the same Wi-Fi, beside the uploads.</summary>
    public LocalShareViewModel Local { get; }

    public bool IsEmpty => Items.Count == 0;

    /// <summary>The hint under the drop area, for the host chosen: "2,5 Go · 24 h · chiffré", "5 Go max via Gofile".</summary>
    public string Limits
    {
        get
        {
            var host = UploadHosts.Get(_settings.UploadHost);
            string expiry = host.HasExpiry ? $" · {_settings.UploadExpiry.Replace("h", " h", StringComparison.Ordinal)}" : "";
            return host.IsEncrypted
                ? Loc.F("{0}{1} · chiffré", host.MaxSizeText, expiry)
                : Loc.F("{0} max via {1}{2}", host.MaxSizeText, host.Name, expiry);
        }
    }

    /// <summary>The same, short enough for the "En ligne" card: "2,5 Go · 24 h", "5 Go · Gofile".</summary>
    public string ShortLimits
    {
        get
        {
            var host = UploadHosts.Get(_settings.UploadHost);
            return host.HasExpiry
                ? $"{host.MaxSizeText} · {_settings.UploadExpiry.Replace("h", " h", StringComparison.Ordinal)}"
                : $"{host.MaxSizeText} · {host.Name}";
        }
    }

    /// <summary>Uploads are encrypted on the PC (Send): a padlock on the card.</summary>
    public bool IsEncrypted => UploadHosts.Get(_settings.UploadHost).IsEncrypted;

    /// <summary>The notch the module lives in: where its activity and announcements show.</summary>
    public void Attach(NotchContext notch)
    {
        _notch = notch;
        Local.Attach(notch);
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.UploadExpiry) or nameof(AppSettings.UploadHost))
            {
                OnPropertyChanged(nameof(Limits));
                OnPropertyChanged(nameof(ShortLimits));
                OnPropertyChanged(nameof(IsEncrypted));
            }
        };
        UpdateActivity();
    }

    /// <summary>Dropped files and folders: sent, zipped first if there are several. A refusal is announced.</summary>
    public async Task DropAsync(IReadOnlyList<IStorageItem> items)
    {
        var paths = items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)).ToList();
        if (paths.Count == 0)
        {
            _notch?.Announce(ErrorGlyph, Loc.T("Seuls les fichiers peuvent être envoyés"), Red);
            return;
        }

        try
        {
            if (await _uploads.EnqueueAsync(paths) is { } refusal)
            {
                _notch?.Announce(ErrorGlyph, refusal, Red);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Uploads] Not taken: {ex.Message}");
            _notch?.Announce(ErrorGlyph, Loc.T("Fichiers illisibles"), Red);
        }
    }

    public Task<bool> CopyLinkAsync(UploadItem item) => item.Url is { } url ? CopyAsync(url) : Task.FromResult(false);

    public async Task OpenLinkAsync(UploadItem item)
    {
        if (item.Url is { } url)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    public void Retry(UploadItem item) => _uploads.Retry(item);

    public void Remove(UploadItem item) => _uploads.Remove(item);

    /// <summary>Sent: the link goes straight to the clipboard, and the notch says so.</summary>
    private async void OnCompleted(object? sender, UploadItem item)
    {
        if (await CopyLinkAsync(item))
        {
            _notch?.Announce(LinkGlyph, Loc.T("Lien copié"));
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UploadItem.State) or nameof(UploadItem.Progress))
        {
            UpdateActivity();
        }
    }

    /// <summary>
    /// The closed notch follows the upload under way: a ring filling up with the percentage, the zipping before it,
    /// or a pause while the network is away; a file crossing the Wi-Fi to or from the phone first (it is quick). Nothing
    /// when there is nothing to send.
    /// </summary>
    private void UpdateActivity()
    {
        if (_notch is null)
        {
            return;
        }

        var item = Items.FirstOrDefault(item => item.State is UploadState.Uploading or UploadState.WaitingForNetwork)
            ?? Items.FirstOrDefault(item => item.State == UploadState.Preparing);
        _notch.SetActivity(Local.Activity ?? item?.State switch
        {
            UploadState.Uploading => new NotchActivity(UploadGlyph, $"{item.Progress * 100:0} %", item.Progress),
            UploadState.Preparing => new NotchActivity(ArchiveGlyph, $"ZIP {item.Progress * 100:0} %", item.Progress),
            UploadState.WaitingForNetwork => new NotchActivity(PausedGlyph, Loc.T("En pause"), Tint: Amber),
            _ => null,
        });
    }

    /// <summary>Puts the link on the clipboard (another app may hold it for a moment: tried a few times).</summary>
    private static async Task<bool> CopyAsync(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                WinClipboard.SetContent(package);
                WinClipboard.Flush();
                return true;
            }
            catch (COMException) when (attempt < 4)
            {
                await Task.Delay(60);
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"[Uploads] Link not copied: {ex.Message}");
                return false;
            }
        }
    }
}
