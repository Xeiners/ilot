using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ilot.Modules.Study;

/// <summary>A course's parts as kept: the ones that teach something, each once.</summary>
internal static partial class StudyChapters
{
    /// <summary>
    /// The parts that teach something: none for the title page, the table of contents (the « Plan » slides between the
    /// chapters), the objectives, the bibliography, nor the course's practical information (its team, organisation,
    /// planning, assessment, exam rules, prerequisites): Notch sometimes made parts of them, and summarised them. One only
    /// per title (their pages together). An introduction stays: it may teach (« Introduction : rappels et vocabulaire »).
    /// </summary>
    public static List<StudyChapter> Contents(IEnumerable<StudyChapter> chapters)
    {
        var kept = new List<StudyChapter>();
        foreach (var chapter in chapters)
        {
            string title = chapter.Title.Trim();
            string key = Key(Numbering().Replace(title, ""));
            if (key.Length == 0 || FrontMatter().IsMatch(key) || IsPractical(Numbering().Replace(title, "")))
            {
                continue;
            }

            int same = kept.FindIndex(other => Key(Numbering().Replace(other.Title, "")) == key);
            if (same < 0)
            {
                kept.Add(chapter with { Title = title });
            }
            else if (chapter.Pages.Trim().Length > 0)
            {
                var first = kept[same];
                kept[same] = first with { Pages = first.Pages.Length == 0 ? chapter.Pages.Trim() : $"{first.Pages}, {chapter.Pages.Trim()}" };
            }
        }

        return kept;
    }

    /// <summary>
    /// A title made only of the words of a course's practical information (« Organisation de l'UE », « Modalités
    /// d'évaluation », « Équipe enseignante », « Planning des CM », « Prérequis et positionnement »), linking words aside.
    /// </summary>
    private static bool IsPractical(string title)
    {
        var words = Words(title).Where(word => !Linking.Contains(word)).ToList();
        return words.Count > 0 && words.All(Practical.Contains);
    }

    /// <summary>A title's words: lower case, no accents, split at anything but a letter or a digit (« l'UE » : « l », « ue »).</summary>
    private static IEnumerable<string> Words(string title)
    {
        var word = new StringBuilder();
        foreach (char c in title.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
            }
            else if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    private static readonly HashSet<string> Linking =
    [
        "de", "du", "des", "d", "l", "la", "le", "les", "et", "a", "au", "aux", "en", "ou", "sur", "pour", "of", "the", "and",
    ];

    private static readonly HashSet<string> Practical =
    [
        "organisation", "presentation", "planning", "plannings", "calendrier", "horaires", "evaluation", "evaluations",
        "examen", "examens", "modalites", "modalite", "controle", "controles", "continu", "integral", "connaissances",
        "equipe", "enseignante", "pedagogique", "enseignants", "enseignant", "intervenants", "responsable", "contact",
        "contacts", "prerequis", "positionnement", "informations", "pratiques", "bibliographie", "bibliographiques",
        "references", "ressources", "consignes", "qcm", "bareme", "ects", "objectifs", "sommaire", "plan", "ue", "ec",
        "cours", "module", "matiere", "enseignement", "general", "generale", "generales", "syllabus", "course", "overview",
        "schedule", "assessment", "grading", "team", "prerequisites", "reading", "list", "cm", "td", "tp", "cms", "tds", "tps",
        "seances", "seance", "dates", "semaines",
    ];

    /// <summary>A title as compared: letters and digits only, no case, no accents.</summary>
    private static string Key(string title)
    {
        var key = new StringBuilder();
        foreach (char c in title.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                key.Append(char.ToLowerInvariant(c));
            }
        }

        return key.ToString();
    }

    // A part that teaches nothing (its title as Key gives it: letters and digits, no accents, no spaces).
    [GeneratedRegex(@"^(?:sommaire|plan|plandu(?:cours|chapitre)|tabledesmatieres|contenu|objectifs?(?:ducours|pedagogiques)?|pagedetitre|pagedegarde|titre|remerciements?|bibliographie|references(?:bibliographiques)?|sources|annexes?|introductionetsommaire|sommaireetintroduction|introductionetplan|planetintroduction|resume(?:ducours|general)?|recapitulatif|syntheseducours)$")]
    private static partial Regex FrontMatter();

    // A part's numbering, before its title: « I. », « 2) », « 3 – ».
    [GeneratedRegex(@"^\s*(?:[IVXLC]+|\d+)\s*[.)\-–:]\s*")]
    private static partial Regex Numbering();
}
