using MyNotch.Localization;
using MyNotch.Modules.Assistant;
using MyNotch.Modules.Clipboard;
using MyNotch.Modules.Media;
using MyNotch.Modules.Search;
using MyNotch.Modules.Study;
using MyNotch.Notch;
using MyNotch.Settings;
using MyNotch.Windowing;

namespace MyNotch.Welcome;

/// <summary>
/// The tour of the notch (see <see cref="NotchView.StartTour"/>), tried as much as read: what opens it (and keeps it
/// open); another tab to choose; the revisions (sections, cards due, mind maps) and the lyrics (the taskbar), on their
/// tabs; a sample file to drop on it; the volume to turn; the mascot to click; a right click; the search's shortcut (or
/// the clipboard's) to press, as they are set; and last the assistant: tried at once when it has its key, else the way to
/// give it one.
/// </summary>
public static class TourSteps
{
    public static IReadOnlyList<TourStep> Build(
        AppSettings settings, SearchShortcut search, ClipboardShortcut clipboard, AssistantShortcut assistant, ApiKeyStore keys,
        Action openSearch, Action tryAssistant, Action addKey)
    {
        var steps = new List<TourStep>
        {
            new(TourTarget.Notch, "\uE7C4", Loc.T("Voici votre notch"),
                settings.StayOpenAfterClick
                    ? Loc.T("Survolez sa petite poignée en haut de l'écran pour l'ouvrir ; elle se referme quand la souris s'en va. Un clic dedans la garde ouverte jusqu'à un clic ailleurs ; un double-clic l'épingle.")
                    : Loc.T("Survolez sa petite poignée en haut de l'écran pour l'ouvrir ; elle se referme quand la souris s'en va. Un double-clic la garde ouverte.")),
            new(TourTarget.Tabs, "\uE8A9", Loc.T("Les onglets"),
                Loc.T("Chaque onglet est un outil : la musique en cours, vos dernières copies, le partage de fichiers, les notes, vos révisions et le Pomodoro."),
                NeedsTarget: true)
            {
                Wait = TourWait.Tab,
                Try = Loc.T("À vous : cliquez sur un autre onglet."),
                Done = Loc.T("Voilà ! Chaque onglet garde ce que vous y avez laissé."),
            },
            new(TourTarget.Notch, "\uE7BE", Loc.T("Vos révisions"),
                Loc.T("Glissez-y un cours (PDF, Word, PowerPoint, photo) : Notch en fait une fiche, des flashcards et des cartes mentales. Rangez vos cours en sections de couleur avec « + » : chaque section a sa tuile des cartes à revoir. Une fiche, comme une note, se détache en fenêtre."))
            {
                Module = typeof(StudyModule),
            },
            new(TourTarget.Notch, "\uE8D6", Loc.T("Votre musique et ses paroles"),
                settings.LyricsPlace == AppSettings.LyricsInTaskbar
                    ? Loc.T("Ce qui joue s'affiche ici, avec ses paroles. La ligne chantée va dans la barre des tâches, à droite, avec la pochette : tirez son bord gauche pour régler sa largeur. Elle peut y aller ou revenir sur la notch dans Paramètres, Paroles.")
                    : Loc.T("Ce qui joue s'affiche ici, avec ses paroles. La ligne chantée peut aussi aller dans la barre des tâches, à droite, avec la pochette : Paramètres, Paroles."))
            {
                Module = typeof(MediaModule),
            },
            new(TourTarget.Notch, "\uE898", Loc.T("Glissez-y des fichiers"),
                Loc.T("Un fichier déposé sur la notch est envoyé, et son lien copié, prêt à coller. Il peut aussi aller sur votre téléphone, par le Wi-Fi."))
            {
                Wait = TourWait.Drop,
                Try = Loc.T("À vous : glissez ce fichier d'exemple sur la notch."),
                Done = Loc.T("Bien joué ! Un vrai fichier partirait maintenant. Celui-ci n'est envoyé nulle part."),
            },
            new(TourTarget.Quick, "\uE767", Loc.T("Le son et la luminosité"),
                Loc.T("À gauche, la molette ou un glisser les règle, un clic coupe le son. Un clic droit sur le volume règle chaque application."),
                NeedsTarget: true)
            {
                Wait = TourWait.Quick,
                Try = Loc.T("À vous : tournez la molette sur le volume."),
                Done = Loc.T("C'est ça ! Rien à ouvrir, rien à chercher."),
            },
            new(TourTarget.Mascot, "\uE76E", Loc.T("Notch, votre mascotte"),
                Loc.T("Elle danse sur votre musique, réagit à vos copies, lit pendant le Pomodoro et s'endort quand vous partez."),
                NeedsTarget: true)
            {
                Wait = TourWait.Mascot,
                Try = Loc.T("À vous : cliquez sur elle, à droite."),
                Done = Loc.T("Elle vous a vu ! Ce même clic ouvre l'assistant."),
            },
            new(TourTarget.Notch, "\uE712", Loc.T("Le clic droit"),
                Loc.T("Il garde la notch ouverte, la passe en mode minimisé ou ouvre les paramètres."))
            {
                Wait = TourWait.Menu,
                Try = Loc.T("À vous : faites un clic droit sur la notch."),
                Done = Loc.T("Le voici. Échap ou un clic à côté le referme."),
            },
        };

        // The shortcut to try: the search's, else the clipboard's; pressed during the tour, it only says it is the one.
        var shortcut = settings.SearchShortcutOn ? (GlobalShortcut)search : settings.ReplaceWindowsClipboard ? clipboard : null;
        if (shortcut is not null)
        {
            string keysText = shortcut.Gesture.DisplayText;
            string text = shortcut == search
                ? Loc.F("{0} ouvre la recherche depuis n'importe quelle application : une appli, un fichier, un calcul.", keysText)
                : Loc.F("{0} ouvre vos dernières copies depuis n'importe quelle application.", keysText);
            if (shortcut == search && settings.ReplaceWindowsClipboard)
            {
                text += " " + Loc.F("{0} : vos dernières copies.", clipboard.Gesture.DisplayText);
            }

            steps.Add(new(TourTarget.None, "\uE765", Loc.T("Partout, au clavier"),
                text + " " + Loc.T("Ils se mettent en pause quand un jeu ou une vidéo est en plein écran."))
            {
                Wait = TourWait.Custom,
                Try = Loc.F("À vous : appuyez sur {0}.", keysText),
                Done = Loc.T("Parfait ! Ailleurs, c'est ce raccourci qui l'ouvre."),
                // The search opens for real, in the notch: the tour steps aside until it is closed. The clipboard's is
                // only said to be the one (its tab is right there).
                StepsAside = shortcut == search,
                Await = done =>
                {
                    shortcut.Capture = shortcut == search
                        ? () =>
                        {
                            done();
                            openSearch();
                        }
                        : done;
                    return new Release(() => shortcut.Capture = null);
                },
            });
        }

        string ask = (settings.AssistantShortcutOn
            ? Loc.F("Cliquez sur la mascotte (ou {0}) et posez votre question, par écrit ou à voix haute.", assistant.Gesture.DisplayText)
            : Loc.T("Cliquez sur la mascotte et posez votre question, par écrit ou à voix haute."))
            + " " + Loc.T("La flèche dans sa petite notch, à droite, ramène aux onglets.");
        steps.Add(keys.HasKey
            ? new(TourTarget.Mascot, "\uE99A", Loc.T("L'assistant IA"), ask, ActionLabel: Loc.T("L'essayer"), Action: tryAssistant)
            {
                Try = Loc.T("À vous : cliquez sur la mascotte pour lui parler."),
            }
            : new(TourTarget.Mascot, "\uE99A", Loc.T("L'assistant IA"),
                ask + " " + Loc.T("Il lui faut d'abord une clé Google Gemini, gratuite : elle s'ajoute dans les paramètres."),
                ActionLabel: Loc.T("Ajouter une clé"), Action: addKey));
        return steps;
    }

    /// <summary>Undoes what a step's watch set, when the step is left.</summary>
    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose()
        {
            _release?.Invoke();
            _release = null;
        }
    }
}
