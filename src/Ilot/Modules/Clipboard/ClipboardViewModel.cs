using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Ilot.Settings;
using Ilot.Localization;

namespace Ilot.Modules.Clipboard;

/// <summary>Presentation of the clipboard history, and what can be done with an entry.</summary>
public sealed class ClipboardViewModel : ObservableObject
{
    private static readonly string SaveFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Ilot");

    private readonly ClipboardHistory _history;
    private readonly AppSettings _settings;

    public ClipboardViewModel(ClipboardHistory history, AppSettings settings)
    {
        _history = history;
        _settings = settings;
        ((INotifyCollectionChanged)history.Entries).CollectionChanged += OnEntriesChanged;
        settings.PropertyChanged += OnSettingsChanged;
    }

    public ReadOnlyObservableCollection<ClipboardEntry> Entries => _history.Entries;

    public bool IsEmpty => Entries.Count == 0;

    public bool HasEntries => Entries.Count > 0;

    /// <summary>What "Nettoyer" would remove: everything but the pinned entries.</summary>
    public int ClearableCount => Entries.Count(entry => !entry.IsPinned);

    public bool CanClear => ClearableCount > 0;

    public string ClearPrompt => ClearableCount == 1
        ? Loc.T("1 élément sera supprimé. Les épinglés restent.")
        : Loc.F("{0} éléments seront supprimés. Les épinglés restent.", ClearableCount);

    public string EmptyTitle => Loc.T(_settings.RecordClipboard ? "Rien de copié pour l'instant" : "Historique en pause");

    public string EmptyMessage => _settings.RecordClipboard
        ? Loc.T("Copiez un texte, un lien, une couleur ou une image : ils arriveront ici.")
        : Loc.T("Réactivez l'enregistrement du presse-papier dans les paramètres.");

    /// <summary>Whether each copy should be acknowledged by the closed notch.</summary>
    public bool SignalsCopies => _settings.SignalCopies;

    /// <summary>Raised when a copy is recorded.</summary>
    public event EventHandler<ClipboardEntry>? Recorded
    {
        add => _history.Recorded += value;
        remove => _history.Recorded -= value;
    }

    /// <summary>Puts the entry back on the clipboard, ready to paste.</summary>
    public Task<bool> CopyAsync(ClipboardEntry entry) => _history.CopyAsync(entry);

    /// <summary>Copies a colour in RGB: <c>rgb(255, 87, 51)</c>.</summary>
    public Task<bool> CopyRgbAsync(ClipboardEntry entry) => _history.CopyTextAsync(entry.Rgb);

    /// <summary>Copies a text edited in the preview: a new copy, recorded as such (unless it is the same as before).</summary>
    public Task<bool> CopyEditedTextAsync(ClipboardEntry entry, string text) =>
        text == entry.Text ? _history.CopyAsync(entry) : SystemClipboard.WriteTextAsync(text);

    /// <summary>
    /// Copies an image edited in the preview (drawn on, cropped): written as a PNG among the app's temporary files (the
    /// clipboard takes its data at once), then copied, and recorded as a new copy.
    /// </summary>
    public async Task<bool> CopyEditedImageAsync(ImagePixels image)
    {
        string folder = Path.Combine(Path.GetTempPath(), "Ilot", "Edited");
        try
        {
            // The ones copied before are no longer needed: the clipboard holds its own copy.
            if (Directory.Exists(folder))
            {
                foreach (string old in Directory.EnumerateFiles(folder, "*.png"))
                {
                    try
                    {
                        File.Delete(old);
                    }
                    catch (IOException)
                    {
                        // Still held somewhere: next time.
                    }
                }
            }

            string path = Path.Combine(folder, $"{Guid.NewGuid():N}.png");
            await image.SaveAsync(path);
            return await SystemClipboard.WriteImageAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[Clipboard] Edited image not copied: {ex.Message}");
            return false;
        }
    }

    public void TogglePin(ClipboardEntry entry) => _history.SetPinned(entry, !entry.IsPinned);

    public void Remove(ClipboardEntry entry) => _history.Remove(entry);

    /// <summary>Removes every entry that is not pinned.</summary>
    public void Clear() => _history.Clear();

    public async Task OpenLinkAsync(ClipboardEntry entry)
    {
        if (entry.Link is { } link)
        {
            await Windows.System.Launcher.LaunchUriAsync(link);
        }
    }

    /// <summary>
    /// Saves the image in <c>Pictures\Ilot</c> and shows it in the Explorer: a file picker would open behind
    /// the notch's always-on-top window.
    /// </summary>
    public async Task<bool> SaveImageAsync(ClipboardEntry entry)
    {
        if (entry.ImagePath is not { } source)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(SaveFolder);
            string name = $"{Loc.T("Presse-papier")} {entry.CopiedAt.LocalDateTime:yyyy-MM-dd HH.mm.ss}";
            string target = Path.Combine(SaveFolder, name + ".png");
            for (int copy = 2; File.Exists(target); copy++)
            {
                target = Path.Combine(SaveFolder, $"{name} ({copy}).png");
            }

            await Task.Run(() => File.Copy(source, target));
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Debug.WriteLine($"[Clipboard] Image not saved: {ex.Message}");
            return false;
        }
    }

    // Pinning moves the entry too, so the counts are always up to date.
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(ClearableCount));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(ClearPrompt));
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.RecordClipboard):
                OnPropertyChanged(nameof(EmptyTitle));
                OnPropertyChanged(nameof(EmptyMessage));
                break;

            case nameof(AppSettings.SignalCopies):
                OnPropertyChanged(nameof(SignalsCopies));
                break;
        }
    }
}
