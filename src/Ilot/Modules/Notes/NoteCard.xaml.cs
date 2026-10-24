using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Ilot.Localization;

namespace Ilot.Modules.Notes;

/// <summary>A note in the Notes row; a click opens it in the sheet below.</summary>
public sealed partial class NoteCard : UserControl
{
    public NoteCard(Note note)
    {
        Note = note;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        Refresh(DateTimeOffset.Now);
        note.PropertyChanged += OnNoteChanged;
    }

    public Note Note { get; }

    public event EventHandler? Opened;

    /// <summary>Ringed while open in the sheet.</summary>
    public bool IsOpen
    {
        get => Ring.Opacity > 0;
        set => Ring.Opacity = value ? 1 : 0;
    }

    /// <summary>Its texts again; "Il y a 5 min" moves on with <paramref name="now"/>.</summary>
    public void Refresh(DateTimeOffset now)
    {
        TitleText.Text = Note.DisplayTitle;
        PreviewText.Text = Note.Preview;
        WhenText.Text = Note.WhenTextAt(now);
        AutomationProperties.SetName(Body, Note.DisplayTitle);
    }

    /// <summary>Stops following its note (the card is gone).</summary>
    public void Detach() => Note.PropertyChanged -= OnNoteChanged;

    private void OnNoteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Notes.Note.DisplayTitle) or nameof(Notes.Note.Preview) or nameof(Notes.Note.UpdatedAt))
        {
            Refresh(DateTimeOffset.Now);
        }
    }

    private void OnBodyClick(object sender, RoutedEventArgs e) => Opened?.Invoke(this, EventArgs.Empty);
}
