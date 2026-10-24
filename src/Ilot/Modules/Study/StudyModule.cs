using Microsoft.UI.Xaml;
using Ilot.Localization;
using Ilot.Notch;
using Ilot.Settings;

namespace Ilot.Modules.Study;

/// <summary>
/// Révisions: a course (PDF, Word, PowerPoint, a photo of notes) made by Gemini into a summary sheet and precise
/// flashcards, revised at the right time (spaced repetition), more cards on demand; all kept on this computer.
/// </summary>
public sealed class StudyModule(StudyViewModel viewModel, AppSettings settings) : INotchModule
{
    public string Title => Loc.T("Révisions");

    public string Glyph => "\uE7BE";

    private StudyView? _view;

    /// <summary>A row taller than the notch's usual open shape: the sections, above the courses.</summary>
    public NotchShape? OpenShape => _view?.OpenShape;

    public FrameworkElement CreateView(NotchContext context) => _view = new StudyView(viewModel, context, settings);
}
