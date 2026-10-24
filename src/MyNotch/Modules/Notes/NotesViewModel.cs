using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MyNotch.Modules.Clipboard;
using MyNotch.Settings;
using MyNotch.Localization;

namespace MyNotch.Modules.Notes;

/// <summary>
/// The notes, as cards: read when the tab first shows, saved as they are typed (see <see cref="NotesService"/>), each
/// in the sheet that has it open (under the notch, or detached in a window: see NoteSheet). A note left empty is not kept.
/// </summary>
public sealed class NotesViewModel : ObservableObject
{
    private readonly NotesService _service;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<Note> _notes = [];
    private Task? _loading;
    private string _saveText = "";
    private bool _saveFailed;

    public NotesViewModel(NotesService service, AppSettings settings)
    {
        _service = service;
        _settings = settings;
        Place = new Windowing.WindowPlace(
            () => settings.NoteWindowBounds, bounds => settings.NoteWindowBounds = bounds,
            () => settings.NoteWindowOnTop, onTop => settings.NoteWindowOnTop = onTop);
        Notes = new(_notes);
        service.Saved += (_, saved) =>
        {
            // A later change already waits: "Enregistré" would be stale.
            if (!service.HasPendingChanges)
            {
                SaveFailed = !saved;
                SaveText = Loc.T(saved ? "Enregistré" : "Non enregistré");
            }
        };
    }

    /// <summary>Latest changed first.</summary>
    public ReadOnlyObservableCollection<Note> Notes { get; }

    /// <summary>The sheet's size as the user left it (pulling its corners); <c>null</c> until then. Kept in the settings.</summary>
    public Windows.Foundation.Size? SheetSize
    {
        get => _settings.NoteSheetWidth > 0 && _settings.NoteSheetHeight > 0
            ? new Windows.Foundation.Size(_settings.NoteSheetWidth, _settings.NoteSheetHeight)
            : null;
        set
        {
            _settings.NoteSheetWidth = value?.Width ?? 0;
            _settings.NoteSheetHeight = value?.Height ?? 0;
        }
    }

    /// <summary>Where a note detached from the notch was last, and its pin.</summary>
    public Windowing.WindowPlace Place { get; }

    /// <summary>"128 mots · 742 caractères".</summary>
    public static string CountText(string text)
    {
        int words = CountWords(text);
        int characters = text.Count(c => c is not ('\n' or '☐' or '☑'));
        return $"{Loc.Count(words, "mot", "mots")} · {Loc.Count(characters, "caractère", "caractères")}";
    }

    /// <summary>"Enregistré" once the last change is written, "Non enregistré" if it could not be; empty meanwhile.</summary>
    public string SaveText
    {
        get => _saveText;
        private set => SetProperty(ref _saveText, value);
    }

    public bool SaveFailed
    {
        get => _saveFailed;
        private set => SetProperty(ref _saveFailed, value);
    }

    /// <summary>Reads the notes, the first time only.</summary>
    public Task LoadAsync() => _loading ??= Load();

    /// <summary>A blank note, kept only once something is written in it.</summary>
    public Note Create() => _service.Create();

    public void SetTitle(Note note, string title)
    {
        if (note.Title != title)
        {
            note.Title = title;
            Changed(note);
        }
    }

    /// <summary>The text changed: its formatted version and its plain one.</summary>
    public void SetContent(Note note, string rtf, string plain)
    {
        if (note.Content != rtf || note.Plain != plain)
        {
            note.Content = rtf;
            note.Plain = plain;
            Changed(note);
        }
    }

    /// <summary>A sheet leaves <paramref name="note"/>: it is saved now, or forgotten if nothing was written in it.</summary>
    public void Leave(Note note)
    {
        if (note.IsEmpty && _notes.Remove(note))
        {
            _service.Delete(note);
        }

        _service.Flush();
    }

    /// <summary>Deletes <paramref name="note"/> (its sheet closes).</summary>
    public void Delete(Note note)
    {
        if (_notes.Remove(note))
        {
            _service.Delete(note);
        }
    }

    /// <summary>Writes what waits now (leaving the tab, done typing).</summary>
    public void Flush() => _service.Flush();

    /// <summary>The note on the clipboard, formatted (for Word…) and as plain text (for everything else).</summary>
    public Task<bool> CopyAsync(Note note, string rtf, string plain)
    {
        if (note.IsEmpty)
        {
            return Task.FromResult(false);
        }

        string title = note.Title.Trim();
        return SystemClipboard.WriteRichTextAsync(rtf, title.Length == 0 ? plain : $"{title}\n\n{plain}");
    }

    private async Task Load()
    {
        foreach (var note in await _service.LoadAsync())
        {
            _notes.Add(note);
        }
    }

    /// <summary>The note moves to the front of the cards, and is saved a moment later.</summary>
    private void Changed(Note note)
    {
        note.UpdatedAt = DateTimeOffset.Now;
        int index = _notes.IndexOf(note);
        if (index < 0)
        {
            _notes.Insert(0, note);
        }
        else if (index > 0)
        {
            _notes.Move(index, 0);
        }

        SaveText = "";
        _service.Schedule(note);
    }

    private static int CountWords(string text)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            bool letter = char.IsLetterOrDigit(c);
            if (letter && !inWord)
            {
                words++;
            }

            // "aujourd'hui" and "peut-être" are one word.
            inWord = letter || (inWord && c is '\'' or '’' or '-');
        }

        return words;
    }
}
