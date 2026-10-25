# Ilot — passation

Ce document s'adresse au développeur (ou à l'agent IA) qui reprend Ilot. Il complète le
[détail des fonctionnalités](docs/FONCTIONNALITES.md), qui décrit chaque fonctionnalité, la structure des fichiers et
l'animation (le [README](README.md) n'en est que la vitrine). Ici :
comment travailler sur le projet, les règles à respecter, l'état réel des modules, et les pièges déjà rencontrés.

Dernière mise à jour : 21 octobre 2026.

---

## 1. En bref

Ilot est une « Dynamic Island » pour Windows 11 : une pilule noire en haut au centre de l'écran, qui s'efface
sous la souris (elle rentre dans une petite poignée et laisse passer les clics) et s'ouvre sur cinq onglets
quand on survole cette poignée. `Ctrl + Q`, partout, en fait une barre de recherche (module sans onglet).

| Onglet | Rôle |
|---|---|
| **Média** | Lecteur (sessions multimédia Windows), visualiseur audio, paroles synchronisées (LRCLIB), volume |
| **Presse-papier** | Historique des copies (texte, liens, couleurs, images), épingles, recherche, remplace Windows + V, aperçu qui retouche (rogner, crayon, texte) |
| **Drop Zone** | Envoi de fichiers en ligne (Send chiffré ou Gofile, lien copié) et partage Wi-Fi local avec un téléphone |
| **Code** | Plus d'onglet : « code » dans la recherche montre les projets récents de VS Code, reconnus par langage |
| **Notes** | Notes en cartes ; la note ouverte tombe de la notch dans une bulle, avec un éditeur façon Word |
| **Pomodoro** | Focus / pauses, le minuteur en haut de la notch fermée, la mascotte qui lit pendant le focus et joue pendant la pause, sa carte en fin de phase |
| **Recherche** (sans onglet, `Ctrl + Q`) | Lanceur : applis, fichiers (Everything embarqué), projets VS Code, paramètres, commandes, calculs |

**Stack** : C# / .NET 10, WinUI 3 (composants Windows App SDK 2.x, pas le métapaquet), application non
empaquetée et autonome, CommunityToolkit.Mvvm, injection de dépendances Microsoft. Dépendances : NAudio.Wasapi
(capture audio), QRCoder (matrice du QR code). SQLite via `winsqlite3.dll` fourni avec Windows, sans paquet.
Environ 19 000 lignes de C#.

**Interface en français**, code et commentaires en anglais. Pas de dépôt git pour l'instant.

---

## 2. Lancer, compiler, déboguer

```powershell
dotnet run --project src/Ilot      # lance (x64 par défaut)
```

- **Une seule instance** est permise (mutex `Local\Ilot.SingleInstance`) : un second lancement se ferme
  aussitôt, sans message. Pour relancer : clic droit sur la notch → « Quitter Ilot », puis `dotnet run`.
- `dotnet run` n'affiche rien d'autre que « Utilisation des paramètres de lancement… » : c'est normal,
  l'application est une `WinExe`.
- **Compiler pendant que Ilot tourne** : la copie de l'exe échoue (fichier verrouillé). Compiler ailleurs
  pour vérifier le code sans fermer l'appli :
  ```powershell
  dotnet build src/Ilot -p:OutDir=C:/temp/mynotch-out/
  ```
- **Erreurs** : toute exception non gérée est notée dans `%LOCALAPPDATA%\Ilot\errors.log` (256 Ko au plus).
  Si la notch ne peut pas se construire au démarrage, l'application se ferme au lieu de rester en processus
  fantôme sans fenêtre. **Premier réflexe quand « rien ne s'affiche » : lire ce fichier.**
- Vérifier qu'une fenêtre existe : `tasklist /FI "IMAGENAME eq Ilot.exe"`, puis, si besoin, énumérer les
  fenêtres du processus (une fenêtre « Ilot » visible doit exister).
- **Tester qu'une compilation démarre sans fermer la notch du propriétaire** : `ILOT_INSTANCE=test` (variable
  d'environnement) lance une seconde copie à côté (autre mutex). Les deux se superposent à l'écran : quelques
  secondes, lire `errors.log`, puis tuer la copie, et fermer l'Everything qu'elle a lancé
  (`<dossier de sortie>\Everything\Everything.exe -exit`).

**Méthode de travail établie avec le propriétaire du projet** : il est le testeur. On implémente, on vérifie
que ça compile (0 avertissement, 0 erreur), on teste à part ce qui peut l'être sans interface (petit projet
console qui inclut les fichiers concernés), puis on lui donne les étapes de test. Il ne veut pas de captures
d'écran à répétition. Il écrit en français et attend des réponses en français.

---

## 3. Règles de la maison (à respecter)

1. **Animations « divinement belles », sur le compositeur.** Tout mouvement passe par des ressorts
   (`SpringChannel`, `Motion.cs`) et des `ExpressionAnimation` liées à l'état partagé `n` de la notch. Pas
   d'animation XAML Storyboard, pas de layout par image.
2. **0 % de CPU au repos.** Rien ne tourne quand rien ne se passe : pas de scrutation, pas de minuterie
   permanente. Les modules lisent leurs données quand leur onglet devient visible (`NotchContext.IsVisible`) et
   s'arrêtent quand il disparaît. Les écritures disque sont différées et faites hors du thread UI.
3. **Des icônes, pas d'émojis** : Segoe Fluent Icons (`FontIcon`), y compris quand une demande cite des émojis.
4. **`docs/FONCTIONNALITES.md` est tenu à jour** à chaque fonctionnalité ou changement visible (et le README, court,
   quand un réglage ou une grande fonction s'ajoute).
5. **Code lisible et dans le ton de l'existant** : commentaires `///` rédigés comme des phrases qui expliquent
   le *pourquoi*, noms explicites, une classe par fichier, modules rangés dans `Modules/<Nom>/`.
6. **Dépendances légères** : préférer l'API Windows ou quelques centaines de lignes maison (le répondeur mDNS,
   le serveur HTTP local, l'accès SQLite sont faits ainsi).
7. **Ne rien casser de l'existant** : le propriétaire le rappelle souvent. Les nouvelles fonctions s'ajoutent à
   côté (nouvelle carte, nouvel onglet, nouvelle méthode de `NotchContext`).
8. **Textes en français dans le code, traduits à côté** : un texte affiché écrit en C# passe par `Loc.T("…")`,
   `Loc.F("… {0}", x)` ou `Loc.Count(n, "mot", "mots")`, et sa traduction s'ajoute à `Localization/English.cs`
   (clé = le français exact). Les textes du XAML sont traduits seuls (`Loc.Apply(this)` après `InitializeComponent`).

---

## 4. Comprendre l'architecture en 15 minutes

Lire dans cet ordre :

1. `App.xaml.cs` : racine de composition. Chaque module = un `INotchModule` enregistré dans
   `ConfigureServices` ; les onglets suivent l'ordre d'enregistrement.
2. `Notch/NotchLayout.cs` : la géométrie (pilule 200 × 36, ouverte 660 × 158, `ExpandedMax` 1200 × 880 qui
   dimensionne la fenêtre transparente, `BubbleGap` 14).
3. `Notch/NotchMotion.cs` : l'état animé partagé, référencé `n` dans les expressions (`n.Width`, `n.Height`,
   `n.Expansion`, `n.Progress`, `n.Toast`, `n.Card`, `n.Bubble`…).
4. `Notch/NotchSurface.cs` : le dessin de la notch par le compositeur (corps, épaules, liseré, ombre, et la
   bulle « goutte » avec son col et ses congés).
5. `Notch/NotchView.xaml.cs` : l'orchestration (onglets, survol, morphing, région de la fenêtre, cartes
   présentées, questions, bulle et redimensionnement, clavier).
6. `Notch/NotchContext.cs` : **l'API des modules**. Tout ce qu'un module peut demander à la notch :

| Méthode | Usage |
|---|---|
| `Bind(expr)` | Expression liée à `n` (notch) et `t` (sélection de l'onglet) ; formules prêtes `TabOpacity`, `TabOffset` |
| `IsVisible`, `ExpandedChanged`, `SelectionChanged` | Ne travailler que quand l'onglet est à l'écran |
| `TakeKeyboard(lost)` | Prendre le clavier à l'appli en cours (la fenêtre ne s'active jamais d'elle-même) et le rendre |
| `HoldOpen()` | Garder la notch ouverte pendant une interaction |
| `Announce(...)` | Bref message sur la pilule fermée (« Lien copié ») |
| `SetActivity(...)` | Activité en direct sur la pilule fermée (anneau de progression) |
| `SetPresence(NotchPresence)` | Ce que le module a pour la notch fermée : `Active` (la pilule reste), `Idle` (poignée après le délai), `Nothing` (poignée tout de suite) |
| `Present(card, size, durée)` | Carte temporaire sous la pilule fermée (appareil audio connecté) |
| `Ask(card, size)` | Question cliquable qui reste jusqu'à réponse (fichier reçu : Accepter / Refuser) |
| `Show()` | Ouvrir la notch sur le module (un raccourci) ; montrée même si une appli en plein écran l'avait cachée |
| `ShowBubble(content, size, min, max)` | Bulle détachée sous la notch ouverte, redimensionnable (feuille des notes) |
| `RequestExpandedShape(shape)` | Agrandir la notch ouverte elle-même (dans `ExpandedMax`) |
| `RequestCompactWidth(w)` | Élargir la pilule fermée (paroles) |
| `AcceptDrops(target)` | Recevoir ce qui est glissé sur la notch |
| `ActMascot(MascotAct)` | Faire jouer quelque chose à la mascotte (l'assistant : écoute, réflexion, parole, fini, échec) |
| `INotchModule.OpenFromMascot()` | La mascotte cliquée : le module qui y répond s'ouvre (l'assistant) |
| `ShowLevel(glyph, text, level, muted)` | Un niveau qui vient de changer (le volume) sur la pilule fermée : glyphe, piste, et la mascotte comme curseur ; sans mascotte, l'annonce entourée du niveau ; ignoré ouverte |
| `ShareBeat(beat)` | Prêter à la mascotte le rythme de la musique : un jeu de propriétés avec les scalaires `Bass` et `L0`…`L3` (0 à 1) ; `null` à l'arrêt |
| `Close()` | Fermer la notch tout de suite, sans le délai de grâce du survol (recherche abandonnée, lancée…) |
| `HandOffKeyboard(lease)` | Finir une saisie parce que quelque chose vient de s'ouvrir : le clavier lui est laissé, pas rendu à l'appli d'avant |
| `NotchBubble.Resize(size)` | La bulle s'étire ou se resserre sur un ressort (résultats de la recherche) |
| `INotchModule.HasTab` / `OpenShape` | Module sans onglet (atteint par `Show()`), forme ouverte propre (la barre 660 × 60 ; plus haute que l'habituelle, sa vue l'est aussi : `FitModuleHost`) |

**Fenêtre** (`Windowing/NotchWindow.cs`) : transparente, toujours au premier plan, jamais activée par un clic,
absente de la barre des tâches. Seule la zone publiée par `NotchView.InputBoundsChanged` reçoit la souris
(`SetWindowRgn`). **Attention : cette région rogne aussi le rendu** ; elle s'agrandit au début d'un mouvement
et ne rétrécit qu'une fois le ressort stabilisé.

**Traduction** (`Localization/`) : français dans le code, anglais par dictionnaire, choisi une fois au lancement
(`Loc.Initialize(AppSettings.Language)` dans `App.OnLaunched` : `auto` = la langue de Windows, `fr`, `en`).
- `Loc.T` / `Loc.F` / `Loc.Count` pour le C# ; `Loc.Culture` pour les dates et nombres.
- `Loc.Apply(vue)` parcourt l'arbre XAML une fois (Text, Runs, Content, PlaceholderText, Header, ToolTip,
  AutomationProperties.Name, éléments de ComboBox, menus contextuels) ; les textes posés par liaison n'y passent pas,
  ils doivent sortir de `Loc.*` côté C#.
- La page du téléphone (`LocalPage`) passe par `Loc.Page`, qui remplace des phrases entières (`EnglishPage.cs`).
- Un texte absent du dictionnaire reste en français : rien ne casse. Pour vérifier qu'il ne manque rien, un petit
  script Node.js qui liste les textes du XAML et les littéraux de `Loc.*` absents d'`English.cs` suffit.
- Les `{0}` d'une traduction doivent être ceux du français (sinon `string.Format` lève une exception).

**Composants partagés** (`Controls/`) : `MotionButton` (bouton à retour physique), `NotchSearchBox` (loupe qui
se déplie), `RowScroller` (flèches ‹ › et molette pour les rangées horizontales), `ResizeGrip` (coin à tirer),
`Scrubber`, `TextSearch`.

---

## 5. État des modules

### Média — stable
`Modules/Media/`. Sessions SMTC, visualiseur WASAPI loopback (ne tourne que pendant la lecture), paroles
LRCLIB, volume Core Audio, carte « appareil connecté ».
- **Paroles dans la barre des tâches** — nouveau, jamais vu à l'écran. Réglage `AppSettings.LyricsPlace` (`notch` par
  défaut, ou `taskbar`) : Paramètres → Paroles → « Ligne chantée » (liste ; l'aperçu montre la notch fermée ou une barre
  des tâches). Seule la ligne de la notch **fermée** change de place : la notch ouverte garde ses paroles. Dans
  `MediaView`, un canal à part `m.PillLyrics` (`InPill` : paroles montrées et pas pour la barre des tâches) règle la ligne
  de la pilule et sa largeur (`RequestCompactWidth`) ; `m.Lyrics` reste pour la notch ouverte. La pilule n'a plus de ligne
  de progression (retirée : inutile fermée) : entre la pochette et le visualiseur, seulement la ligne chantée s'il y a des paroles ;
  sans paroles, la pilule se resserre sur la pochette et la mascotte (`MediaView.SizePill` / `NarrowPill` : la largeur
  demandée peut maintenant être plus petite que celle de la notch, `NotchLayout.ShapeFor`, jamais moins que ronde).
  `PlaceLyrics` crée ou retire `TaskbarLyrics` selon le réglage ; un clic sur la ligne ouvre la notch sur le lecteur.
  - **Où** (`Windowing/TaskbarSpots`) : UI Automation (`Interop/UIAutomation.cs`, interfaces COM réduites à l'ordre de
    leur vtable) sur `Shell_TrayWnd` : tous les boutons (Démarrer, recherche, applis, icônes de la zone de notification,
    horloge… : la barre de Windows 11 est en XAML, `TrayNotifyWnd` donne une place périmée), leurs étendues fusionnées,
    et le plus large espace libre (`Widest`, testé). Hors du fil de l'interface (`Task.Run`, MTA) ; ~30 à 140 ms. Vu sur
    la machine (lecture seule, `scratchpad/taskbartest`) : barre 1920 × 58, libre de 808 à 1300. Aucune barre visible ou
    barre en masquage automatique (`SHAppBarMessage(ABM_GETSTATE)`) : rien.
  - **La fenêtre** (`TaskbarLyricsWindow`) : comme la notch, une popup transparente sans cadre ni coins
    (`TransparentBackdrop`, `EnablePerPixelTransparency`), fenêtre outil (pas de bouton) jamais activée (`WS_EX_NOACTIVATE`
    + `WM_MOUSEACTIVATE` → `MA_NOACTIVATE`), au premier plan ; un `LyricsPanel` (ligne chantée + suivante grisée,
    `LineForeground` : blanc, ou sombre si la barre est claire, `SystemUsesLightTheme`) ; fondu d'apparition et de
    disparition sur le compositeur, cachée ensuite.
  - **Quand** (`TaskbarLyrics`) : paroles montrées, lecture en cours (une pause de 1,5 s ne l'enlève pas), aucune appli
    en plein écran (`FullscreenWatcher` à elle, écran principal ; jamais au-dessus d'un jeu). Taille du texte : celle de
    la ligne de la notch fermée (`CompactLyricsFontSize`), la paire de lignes centrée. Replacée quand une autre appli passe
    devant (600 ms après : une appli ouverte ajoute son icône) et toutes les 6 s tant qu'elle est là ; remise au-dessus de
    la barre à chaque changement de premier plan (cliquée, la barre repasse devant).
  - **Placement** (demandé sur une maquette : comme Lyricify / les lecteurs de la barre) : **toujours à droite**, contre la
    flèche des icônes cachées : la fin du dernier espace libre assez large (`TaskbarSpots.Gaps`, `TaskbarLyrics.Spot`),
    4 px laissés. Plus de glissement. De gauche à droite : la poignée (12 px, curseur ↔), les lignes calées à droite
    (`FlushRight`), 10 px, la pochette 34 px arrondie (`MediaViewModel.Artwork` ; sans pochette, une note sur un carré
    pâle) avec l'icône de l'appli qui joue sur son coin bas gauche (`MediaViewModel.SourceAppId` → `ShellIcons` de la
    recherche : « shell:AppsFolder\… » pour une appli empaquetée ou du menu Démarrer, sinon le fichier du programme qui
    tourne, lu avec `PROCESS_QUERY_LIMITED_INFORMATION`). **Largeur** des lignes au choix (`AppSettings.LyricsTaskbarWidth`,
    120 à 640, 300 par défaut) : sous le pointeur, un cadre pâle montre jusqu'où elles peuvent aller et sa poignée à
    gauche ; tirée, la fenêtre s'élargit vers la gauche (le bord droit reste), dans la place qu'il y a
    (`TaskbarLyricsWindow.WidthRange`), gardée au relâchement ; aussi Paramètres → Paroles → « Largeur dans la barre des
    tâches ». Un clic ailleurs sur elle ouvre la notch sur le lecteur.
  - **Le lecteur hors de la notch fermée** (option `AppSettings.HidePlayerInNotch`, Paramètres → Paroles, visible en mode
    barre des tâches) : tant que `TaskbarLyrics.IsShown` (événement `ShownChanged`), `MediaView.PillShown` est faux :
    canal `m.Pill` à 0 (pochette, visualiseur et ligne de la pilule effacés fermés ; ouverts,
    rien ne change) et présence `NotchPresence.Elsewhere` (-1) : la notch fermée se repose tout de suite dans son état
    neutre (la mascotte dans son nid, ou la poignée sans elle), même « Se cacher au repos » désactivé
    (`NotchView.RestsElsewhere` dans `UpdateIdle` ; repasser d'ailleurs à rien ne rappelle pas la pilule). Aussi
    pendant que la barre est sur le point de le montrer (lecture, paroles là ou recherchées : `LyricsViewModel.IsLooking`,
    et place trouvée la dernière fois : `TaskbarLyrics.HasRoom`) : ni la reprise ni un nouveau morceau ne font revenir
    la pilule un instant. En pause, « Se cacher au repos » décide (activé : la notch reste au repos sans le lecteur ;
    désactivé : le lecteur revient). Morceau sans paroles, pas de place : le lecteur revient. Jamais vu.
  - **Karaoké** (`LyricsPanel.Karaoke`, seulement ici) : la ligne chantée à 50 %, sa copie claire par-dessus (`FillA`…`D`,
    mêmes expressions de place) découpée à droite (`InsetClip.RightInset` = largeur × (1 − `s.Fill`)), `Fill` animé
    linéairement sur 90 % de la durée de la ligne (paroles synchronisées par ligne, pas par mot) ; la suivante à 72 %
    (`NeighbourOpacity`). Jamais vu à l'écran.

### Révisions — nouveau, jamais vu à l'écran (pas testé avec une vraie clé)
Pour l'utilisateur, l'IA s'appelle **Notch** (le nom de la mascotte) : jamais « Gemini » dans ce qu'elle fait (« Notch
lit le cours… ») ; « Gemini » reste pour la clé et le service (paramètres, quotas). L'assistant se présente aussi ainsi
(`AssistantConversation.Instructions`, `AssistantVoice.Instructions` : « Tu t'appelles Notch… »).
`Modules/Study/`, onglet « Révisions » (après Notes, glyphe E7BE). Six onglets tiennent (calcul : ~592 px sur 660).
- **Import** : « Importer un cours » (`Microsoft.Windows.Storage.Pickers.FileOpenPicker`, fenêtre de la notch via
  `XamlRoot.ContentIslandEnvironment.AppWindowId`, notch tenue ouverte) ou un dépôt sur la notch **ouverte sur cet
  onglet** : `NotchView.FindDropTarget` préfère maintenant la cible du module affiché (sinon la première qui accepte : la
  Drop Zone). `StudyStore.CreateAsync` copie le document dans `%LOCALAPPDATA%\Ilot\Study\<id>.<ext>`.
- **Documents** (`StudyDocument`) : PDF et images envoyés tels quels (inline jusqu'à 14 Mo, sinon File API :
  `GeminiClient.UploadAsync`, upload résumable, gardé 46 h en mémoire), pages du PDF par `Windows.Data.Pdf` ; .docx et
  .pptx lus ici (zip + XML : paragraphes ; diapositives dans l'ordre numérique, « --- Diapositive N --- », notes de
  l'orateur) ; .txt/.md tels quels. .doc/.ppt refusés (« enregistrez en PDF »).
- **Génération** (`StudyGenerator`, `GeminiClient.GenerateJsonAsync` : generateContent non streamé,
  `responseMimeType` JSON + `responseSchema` + `temperature`, `MAX_TOKENS` → message). Modèles : celui de l'assistant
  s'il est choisi, sinon `Candidates` avec les « lite » en dernier ; un modèle sans quota passe au suivant (repos 2 min).
  1) `OutlineAsync` (réflexion minimale : `QuickThinking`) : titre et chapitres (pages), température 0,1. **Pas bloquant** :
  s'il échoue, la fiche trouve ses parties elle-même (« ## Titre (p. 3-12) ») et `ChaptersOf` en fait les chapitres.
  Puis **en parallèle** (`Task.WhenAll`, chacun peut échouer sans l'autre ; les cartes se révisent pendant que la fiche
  s'écrit) : `SummaryAsync` (fiche Markdown, parties = les chapitres du plan : ## par partie, puces, **gras**,
  « À retenir », « À ne pas confondre », page à chaque puce, pas de tableau — MarkdownLite n'en a pas ; température 0,2) et
  2) `CardsAsync` : N cartes (`FirstCount` : 0,75 par page, 20 à 50 ; ensuite 10/20/30) sur tout le cours, un chapitre ou
  les points faibles (cartes ratées, avec leurs réponses), avec la liste des questions existantes à ne pas refaire ;
  dédoublonnage local (lettres et chiffres, sans casse ni accents). Température 0,4 puis 0,8 pour « Nouvelles cartes ».
  Les deux sont **streamés** (`GeminiClient.StreamJsonAsync` : streamGenerateContent + schéma JSON ; `MAX_TOKENS` →
  message) : `PartHeading` compte les « ## » écrits (pas « ### »), `QuestionKey` les cartes commencées ; le cours suit
  `SummaryParts`, `CardsWritten` / `CardsTarget` et ses étapes `OutlineStep`, `SummaryStep`, `CardsStep` (`StudyStep`).
  Tout est streamé, réflexion bornée (`GeminiClient.LightThinking` pour la fiche et les cartes : budget 1024 en 2.5,
  « low » en 3 ; un modèle qui refuse le réglage est redemandé sans) : laissé libre, un modèle réfléchissait des minutes
  sur un cours de 200 pages sans rien écrire (vu : bloqué sur « Le plan du cours »). Abandon après 4 min sans premier
  mot (`FirstWordWithin`) puis 2 min sans nouveau morceau (`PieceWithin`) : pas de limite de durée totale. Un modèle
  n'est remplacé par le suivant que s'il échoue avant d'avoir écrit quoi que ce soit. L'attente montre le temps écoulé
  (« 1:12 », `StudyDeck.Started`, une minuterie d'une seconde seulement pendant le travail).
  Les consignes (`Instructions`) : tout du document seul, termes exacts, page = position dans le PDF, langue du document.
  Les textes envoyés à Gemini restent en français (pas de `Loc`).
- **QCM à 4 choix** : chaque carte a `Choice` (la bonne réponse, courte) et `Decoys` (3 pièges du vocabulaire du cours,
  de même nature ; schéma `decoys` à exactement 3 éléments, `minItems`/`maxItems`), colonnes `choice` et `decoys`
  (JSON) ajoutées aux bases d'avant (`ALTER TABLE`). `StudyGenerator.Tidy` : rognés, pièges distincts et jamais égaux à
  la bonne réponse, sinon aucun choix (la carte se révise alors à l'ancienne). Les cartes sans choix (anciennes, ou
  réponse modifiée : ses choix sont effacés) les reçoivent en arrière-plan (`StudyViewModel.CompleteChoices` →
  `ChoicesAsync`, 40 par requête, à l'ouverture du cours et après chaque génération ; sans `Busy`). En révision : 4
  boutons mélangés (`ShowOptions`), 1 à 4 ou un clic ; juste en vert, faux en rouge (`MarkOptions`), puis la réponse
  complète et la note proposée (`_suggested` : Bien si juste, À revoir si faux ou « Je ne sais pas »), Espace la donne,
  1 à 4 une autre.
- **Lueurs de la feuille** (`StudyPaint.Glows`, `GlowTop`, `GlowBottom`) : deux halos radiaux immobiles à la couleur de
  la mascotte, depuis le coin haut gauche (0x30) et dans le coin bas droit, plus franc (demandé : « trop noir » ; centre
  0,9 / 0,98, rayons 0,62 × 0,58, 0x46 → 0x1C → 0 ; aperçu `scratchpad/glowpng.js`), éteints avant le milieu où est le texte. (Essayées puis retirées : les lignes du bord façon assistant, pas jolies.)
- **Couleur — seulement sur la carte de révision** (demandé : la bulle sobre). La feuille n'a plus de voile ni de
  lueur ; en-tête, mode choisi, pastilles, liste des cartes : neutres (blanc translucide ; la bonne réponse en vert). La
  carte (`Face`) garde sa teinte et son bord lumineux, ses choix aussi ; l'éclat vert ou rouge se fait sur elle seule
  (`FaceFlash`, par-dessus la carte). La fiche (`StudySheet.Build`, sans couleur) : verre neutre, termes en blanc gras,
  14 px / interligne 23 ; « À retenir » neutre, « À ne pas confondre » garde son filet ambre. Ancienne note :
  (`StudyView.Tint`, à chaque ouverture) : la lumière de la mascotte (`MascotColor.Glow`, un indigo si elle
  est grise ou blanche) : voile du haut (0x40 → 0 à 60 %), lueur radiale où est la carte (0x50 → 0x22 → 0), carte teintée
  par le haut (24 % → 8 % → fond) au bord lumineux (dégradé 0x99 → 0x14, 1,5 px), chapitre et mode choisi dans cette
  couleur, choix teintés (0x22, numéro 0x70) ; un choix répondu fait un éclat vert ou rouge (`Flash`, 1,1 s, opacité
  animée par le compositeur ; sinon tout est immobile). Maquette : `scratchpad/revisionpng.js`.
- **La fiche dessinée** (`StudySheet.Build`, plus de RichTextBlock) : chaque « ## » une carte de verre (dégradé de la
  couleur des révisions, bord lumineux, pastille numérotée, titre sans sa numérotation, ses pages en pastille :
  `Heading`), chaque puce une ligne (point, définition « **Terme** : … » avec le terme en couleur, **gras** plus clair,
  pages de fin « (p. 6, 7) » en pastilles : `Sources`), « ### À retenir » encadré coloré (étoile) et « ### À ne pas
  confondre » encadré ambre (avertissement) ; les parties en pastilles au-dessus (un clic y fait défiler).
- **Style « IA »** : chiffres de l'en-tête en pastilles de verre (`ShowStats`, ce qui est dû en couleur) ; tuiles de cours
  teintées (`DeckTile(deck, accent)`) ; cartes de la liste en verre, leurs 4 choix en pastilles (la bonne en vert) ;
  « Réviser » n'est plus dans la ligne des onglets (il chevauchait Pomodoro avec six onglets) : tuile colorée en tête de
  rangée (`ReviewAllTile`, le nombre en grand). La couleur : `StudyView.Accent` (lumière de la mascotte, ou indigo).
- **Sons** (`StudySounds.Render` : timbre propre, sinus + un souffle d'octave qui s'éteint d'abord, attaque douce de
  12 ms et fin adoucie : aucun clic ; joués par `Chime.Play(pcm)`) : juste (mi5 → si5), faux (la4 → fa4, court, feutré),
  session finie (do5 mi5 sol5 do6), cours prêt (sol5 do6 mi6) ; 7,5 % de la pleine échelle à 100 %. Volume :
  `AppSettings.StudySoundVolume` (60 par défaut) ; le haut-parleur à côté de « Nouvelles cartes » (glyphe selon le volume)
  coupe d'un clic (`StudySounds`), et survolé ouvre son curseur à côté (`VolumePill`, replié 0,7 s après, pas tant que
  le curseur est tenu), un son à ce volume quand il se pose. Écoute : `scratchpad/sons/*.wav` (studytest).
- **La couleur** : celle de la mascotte telle que choisie (`Accent` : son `Tone`, sa lumière si elle est trop sombre pour
  luire sur le noir) ; les tuiles, la tuile « Réviser » et une feuille ouverte la suivent quand elle change.
- **La feuille reste ouverte** jusqu'à un clic ailleurs : elle prend le clavier à l'ouverture (`TakeKeyboard`, qui
  tient la notch ouverte) et se ferme quand une autre app le reprend (son `lost`), ou par Échap / la croix.
- **Revenir sur une carte** : `_history` (50 au plus) garde chaque carte notée de la session avec ses choix dans l'ordre
  montré, le choix fait (`_picked`, -1 « Je ne sais pas ») et la note. « ← Précédente » (ou la flèche gauche) l'ouvre en
  lecture seule (`Look` : choix marqués, réponse, « Déjà répondue · Bien ») ; « Suivante → » / « Reprendre » (ou Espace)
  reviennent à la carte en cours telle qu'elle était (`ShowLive` : mêmes choix, même état). Fin de session : la
  précédente reste accessible.
- **Après un QCM** : sur une seule ligne (`AnswerBar` horizontale), « Ajuster : » et les quatre notes en petit (sans
  leur délai), puis « Suivant » (Espace), qui donne la note proposée (`_suggested`) ; les quatre choix restent (demandé),
  les deux ni justes ni pris estompés, tous un peu resserrés (`MarkOptions`) pour laisser la place à la réponse complète, et une réponse encore trop longue fait défiler la
  carte jusqu'à elle. Une carte sans choix garde les quatre grandes notes, empilées.
- **30 cartes** au départ (`FirstCount` : 20 pour 10 pages ou moins).
- **Sections** (matières) — nouveau, jamais vu à l'écran. Demandé : « Maths, Français, SVT, Physique… », des onglets
  comme ceux de la notch, des couleurs, en créer, une « globale » par défaut, filtrer. Une rangée au-dessus des cours
  (`SectionsRow`) : « Global » (tous les cours), les sections (un point de leur couleur ; choisie, allumée de sa couleur),
  « + ». L'onglet est une rangée plus haut (`StudyModule.OpenShape` → `StudyView.OpenShape` : la forme ouverte habituelle
  + 34 px, la notch se déforme en passant sur Révisions ; `NotchView.FitModuleHost` donne à chaque vue la hauteur de sa
  forme, la plus haute fixant celle de `ModuleHost` : sans ça, les tuiles étaient coupées en bas). « + » ouvre une bulle
  sous la notch (`SectionEditor`, 480 × 212 ; « + » à nouveau la rentre) : titre, aperçu de l'onglet tel qu'il sera, nom,
  les 7 couleurs de la palette de la mascotte (sans le blanc ; par défaut la suivante, `NextSectionColor`), Annuler /
  Créer (grisé sans nom) ; Entrée crée, Échap renonce, un clic ailleurs ou changer d'onglet aussi. Elle prend le clavier
  (la feuille, si elle était ouverte, se referme et le lui laisse par `HandOffKeyboard`, et inversement). Clic droit sur
  une section : « Modifier… » (la même bulle, « Enregistrer » et « Supprimer la section »), Supprimer la section (ses
  cours restent, sans section). Clic droit sur un cours : « Ranger dans » (aucune, chaque section, « Nouvelle section… »
  qui ouvre la bulle et l'y range). Les menus (`MenuFlyout` hors de la fenêtre)
  tiennent la notch ouverte. La section choisie (`AppSettings.StudySection`, gardée) ne montre que ses cours ; un cours
  importé ou déposé y va ; la tuile « Réviser » compte et révise ses seules cartes, à sa couleur, et reste là tant que la section a des cours (rien de dû : « À jour », coche, atténuée, non cliquable) (`CourseSheet.Open` avec
  `scope` / `scopeName` : « Révisions du jour · Maths », gardés en détachant, `SheetState.Scope`). Les tuiles des cours
  prennent la couleur de leur section (celle de la mascotte sans section). Données : table `Sections(id, name, color,
  position)` et `Decks.section` (0 : aucune), ajoutées aux bases d'avant ; `StudyViewModel.Sections`, `CreateSection`
  (couleur suivante de `SectionColors`), `Rename`, `Recolor`, `Delete`, `Move`, `In(section)`. Testé (studytest :
  enregistrement, ordre, suppression, base d'avant).
- **Cartes mentales** — nouveau, jamais vu à l'écran. 4ᵉ mode de la feuille, « Cartes mentales » (F22C), à droite de
  « Cartes » (pas pour « Révisions du jour »). Faites à la demande (« Générer les cartes mentales », puis « Redessiner ») :
  `StudyViewModel.DrawMindMaps` → `StudyGenerator.MindMapsAsync`, à partir de **la fiche** (texte, pas le PDF : plus léger,
  plus rapide ; le document seulement si le cours n'a pas de fiche), réflexion minimale, schéma imposé
  `{maps: [{chapter, notion, definition, formulas[], branches: [{label, details[]}]}]}` : une carte par notion importante
  (toutes), dans l'ordre du cours, sa partie écrite comme dans le plan, **sa définition** telle que le cours la donne (une
  phrase, vide sinon), **ses formules** (0 à 4, en texte Unicode, jamais LaTeX), 3 à 6 branches de 1 à 4 mots, 0 à 3 détails
  (mots-clés de 2 à 7 mots, ou « Terme : définition courte »). Demandé ensuite (après un premier essai) : définitions et
  formules dans la carte, l'avancement par partie, une liste plus jolie ; « ça a pris du temps, mais le résultat est bon ».
  `MindMap.Tidy` : rognés, sans vide ni point final, pas de branche en double, 6 branches / 3 détails / 4 formules au plus
  (sans les `$` de LaTeX), 2 branches au moins, une carte par notion ; les cartes gardées avant (sans définition ni
  formules) se lisent toujours. Suivi sur le cours (`MindMapsStep`, `MindMapsWritten` : les « notion » comptées dans le
  flux, `MindMapsPart` : la partie de la dernière carte commencée, lue dans le flux par `StudyGenerator.LastChapter`, les
  cartes venant dans l'ordre du cours, `MindMapsNote`) : « Partie 2 / 6 · Myologie · 9 cartes » et une barre (parties
  passées, celle en cours à moitié) dans la feuille, « Cartes mentales · 2/6 » et un anneau sur la notch fermée tant
  qu'aucun cours ne se prépare ; annonce à la fin ; un échec garde les cartes d'avant et dit pourquoi. Gardées dans `study.db` (`Decks.mindmaps`, JSON ; colonne
  ajoutée aux bases d'avant). **Affichage** : à gauche « Vue d'ensemble » (`MindMap.Overview` : le cours au centre, ses
  parties autour, leurs notions dessous, 6 au plus) puis chaque partie (`PartGroup` : son numéro sur une pastille de sa
  couleur, celle de sa branche dans la vue d'ensemble, son nom sur deux lignes au plus) et ses notions en retrait le long
  d'un fin trait de cette couleur ; à droite la carte choisie, sa partie
  et « 3 / 24 », précédente / suivante. `MindMapDrawing` : la notion au centre (pilule à la couleur de la mascotte, texte
  sombre, **sa définition dessous**, 250 px de large alors), **ses formules** en premier (en haut à droite) : une carte claire,
  chaque formule en Cambria Math sur sa plaquette ; chaque branche une carte teintée d'une couleur de la palette (ciel, menthe, citron, pêche, rose, lavande,
  corail), son nom dans sa couleur, ses détails à puce ; une courbe (Bézier, 2,5 px) du centre à chaque branche.
  `MindMapLayout` (testé) : les premières branches à droite de haut en bas, les autres à gauche de bas en haut
  (lecture dans le sens horaire), autant à droite qu'il faut pour que la carte soit la moins haute (la carte des formules,
  haute, en laisse plus à gauche ; moitié-moitié à égalité), chaque côté centré, 48 px pour les courbes, 14 px entre deux branches. Dessinée à sa
  taille. **Canevas** (`MindMapCanvas`, demandé : le texte devenait minuscule dans une petite feuille) : la carte à sa taille
  dans un `ScrollViewer` zoomable (0,3 à 3 ; **la molette zoome** vers le pointeur — arrêtée sur le plateau pour que le
  `ScrollViewer` ne défile pas, prise sur tout le canevas avec `handledEventsToo`, sauf molette inclinée, pavé tactile et
  barre d'outils ; `_aim` : là où les crans mènent la vue, pas là où elle en est —, pincement, « − » / « + », le
  pourcentage cliqué : toute la carte ; **Ctrl + Z** annule : un clic sur la carte donne le clavier à `_keys`, un
  `ContentControl` invisible, `CourseSheet.FocusContent` aussi ; une note en cours garde son propre Ctrl + Z), 160 px de marge autour pour écrire ; première vue centrée, aussi grande que tient mais jamais sous 85 %
  (une grande carte se déplace plutôt que de rétrécir), gardée si c'est la même carte redessinée (couleur de la mascotte) ;
  coins arrondis par un clip du compositeur. Outils (barre flottante en bas, `ToolStrip` : curseur flèche) : déplacer
  (glisser ; le bouton du milieu aussi, quel que soit l'outil), crayon (2,5 px), surligneur (14 px, 35 %), texte (un clic :
  une note tapée là, `NotchFieldTextBoxStyle`, Échap ou un clic ailleurs la garde, vide elle part ; cliquée, elle se
  modifie), gomme (traits et notes sous elle), 5 couleurs (blanc, couleur de la mascotte, citron, corail, ciel ; en choisir
  une prend le crayon), « Annuler » (pile d'inverses), « Tout effacer sur cette carte » (annulable). Le curseur dit l'outil
  (`MapBoard`). L'encre (`MapInk` : traits, points arrondis au dixième, et notes, dans le repère de la carte) gardée par
  carte (`StudyDeck.MapInks`, clé `MapInk.KeyOf` : la notion, ou « overview »), écrite 0,8 s après le dernier trait et à la
  fermeture (`StudyViewModel.SaveInk`), colonne `Decks.mapink` (JSON, cartes vides omises ; ajoutée aux bases d'avant).
  Dans la notch, écrire une note prend le clavier (`KeyboardWanted`). Une carte redessinée garde l'encre de sa notion
  (elle peut alors ne plus tomber juste). Aperçus : `scratchpad/mindmap-preview.ps1`, `scratchpad/maplist-preview.ps1`. La carte
  regardée suit la feuille détachée (`SheetState.MapIndex`) ; fenêtre détachée des révisions : 700 px au moins.
- **Feuille détachée** — nouveau, jamais vu à l'écran (demandé : « on prend la même fenêtre et on la met de côté », les
  trois modes, pas seulement les cartes). La feuille d'un cours est un contrôle à part, `CourseSheet` (en-tête et
  chiffres, Réviser / Fiche / Cartes, son et volume, « Nouvelles cartes », préparation avec la mascotte qui lit, suppression
  du cours : tout ce qui était la feuille dans `StudyView`). `StudyView` ne garde que la rangée des cours, la bulle, le
  bail clavier et la fenêtre ; sa feuille est une `CourseSheet(detached: false)` posée dans la bulle. Dans la feuille, la
  révision est elle-même un `ReviewView` (la carte, ses choix, sa réponse, les notes, « Précédente », les touches).
  `StudyPaint` : couleurs (juste, faux, fond de carte, `Accent`), dégradés, les deux lueurs (`Glows`), l'arrivée animée
  (`Arrive`) ; `StudySounds.Play(settings, son)` et `StudyScheduler.When` ; styles `StudyPill/Choice/RateStyle` dans
  `Themes/Notch.xaml`. La feuille parle à son hôte par événements : `CloseRequested` (croix, Échap en révision, cours
  supprimé), `MoveRequested` (« Détacher dans une fenêtre », E8A7, ou « Rattacher à la notch », E73F, selon `detached`),
  `KeyboardWanted` (une carte modifiée : la notch prend le clavier) ; `IDetachableSheet` (`AddWindowTools`, `FocusContent`,
  `RoundCorners` : ses deux lueurs arrondies comme la fenêtre), `Close` / `Release` (elle suit la couleur de la mascotte
  elle-même, jusqu'à `Release`). Détacher : `CourseSheet.Export` → `SheetState` (cours, mode, `ReviewState` de la révision :
  session, carte, réponse montrée ou non, choix dans l'ordre, choix fait, note proposée, historique, carte regardée ;
  et la bande « Nouvelles cartes » : ouverte, sur quoi, combien), la place de la bulle à l'écran
  (`DetachedWindow.ScreenBounds`), le bail clavier laissé à la fenêtre (`HandOffKeyboard`), la feuille et la notch se
  referment, et une nouvelle `CourseSheet(detached: true)` reprend exactement là (`Import`) dans une `DetachedWindow`
  (voir « Fenêtres détachées »). Une seule à la fois (détacher à nouveau ferme l'ancienne) ; 600 × 440 au moins ;
  `AppSettings.CourseWindowBounds` / `CourseWindowOnTop` ; Échap (en révision) ou la croix la ferment ; titre dans la barre
  des tâches : « Révisions · cours ». « Rattacher à la notch » : `Export`, la fenêtre se ferme, puis (en file basse, une
  fois fermée) la notch s'ouvre sur l'onglet (`_context.Show()`), la bulle descend et la feuille reprend (`Import`). Les cours sont les mêmes objets que la notch :
  une réponse, une carte modifiée, de nouvelles cartes faites dans la fenêtre sont enregistrées et comptent tout de suite
  partout ; un cours supprimé (ici ou dans la notch) ferme sa fenêtre. (Avant : seule la révision se détachait, dans une
  `ReviewWindow` de 460 × 620, puis la feuille dans une `CourseWindow` à cadre Windows déplacée par son titre ; leurs
  réglages `ReviewWindowBounds` / `ReviewWindowOnTop` n'existent plus.)
- **Gemini 3, quotas, messages** (vu : bloqué 8 min, cartes en échec sans raison, avec gemini-3.6-flash) :
  pas de `temperature` pour Gemini 3 et plus (`GeminiClient.TakesTemperature` : Google la garde à 1, plus basse ils
  bouclent) ; pas de `minItems`/`maxItems` dans le schéma (`Tidy` garde 3 pièges). Un quota épuisé (un cours de 200
  pages ≈ 60 000 jetons par requête, la clé gratuite en a peu par minute) ou un modèle surchargé : **le modèle suivant
  aussitôt**, comme l'assistant (vu : « reprend dans 75 s » sans fin sur gemini-3.8-flash, quota épuisé, pendant que
  l'assistant répondait avec un autre) ; **coupé en pleine réponse** (vu : gemini-3.6-flash « surchargé » après quatre parties
  des cartes mentales, l'échec de toute la génération), le modèle mis de côté aussi et la demande refaite depuis le début
  par le suivant (l'avancement repart de zéro : `progress("")`) ; le modèle mis de côté (`_resting`) le temps que Google dit (`retryDelay`,
  `GeminiException.RetryAfter`), 30 s s'il est surchargé, 1 h si c'est **le quota du jour** (`GeminiException.Daily` :
  `quotaId` « …PerDay… », `GeminiClient.PerDay`, testé), pour la session s'il n'a pas de quota (« limit: 0 »). Seulement
  quand aucun ne peut répondre : le plus tôt revenu attendu (75 s au plus, 4 fois), avec une note sous l'étape (« Google
  demande une pause… reprend dans 37 s ») ; tous au quota du jour : « Le quota du jour de votre clé est atteint… ».
  `CompleteChoices` (le document entier part avec) : une fois par cours toutes les 30 min au plus (`ChoicesAgain`),
  sauf pour enchaîner les lots qui avancent ; chaque étape a sa note (`OutlineNote`, `SummaryNote`,
  `CardsNote`) : l'attente, ou la raison de l'échec en rouge, tout de suite. Les échecs vont dans `errors.log`
  (`ErrorLog`, partagé avec `App`), avec le modèle : « [Study] gemini-… failed: … ».
- **Échecs** : toute exception inattendue de la fiche ou des cartes devient un message (`Unforeseen`) et l'étape échoue
  (vu : une coupure pendant le long flux de la fiche laissait un cours « prêt » sans fiche) ; une coupure réseau
  (`HttpRequestException`, `IOException`) est réessayée une fois depuis le début.
- **Nouvel essai = mêmes nœuds JSON** (vu : « The node already has a parent » sur le plan puis sur la fiche, après
  « Gemini est surchargé… attente 20 s ») : le schéma et le réglage de réflexion d'une demande partent à chaque essai
  (pause de quota, coupure, modèle suivant, repli sans réflexion) ; un `JsonNode` n'a qu'un parent, donc
  `GeminiClient.JsonRequest` (le corps de `StreamJsonAsync`) en met des copies (`DeepClone`). Testé dans studytest.
- **Révision** (`StudyScheduler`, SM-2 façon Anki) : étapes 1 min, 10 min ; diplômée à 1 j (Facile : 4 j) ; facilité
  2,5 (min 1,3) ; Difficile ×1,2 −0,15 ; Bien ×facilité ; Facile ×facilité×1,3 +0,15 ; oubli : réapprentissage 10 min,
  −0,2, intervalle ×0,2 ; 365 j au plus ; le jour commence à 4 h. `ReviewSession` : apprentissage dû, puis révisions
  dues, puis 20 nouvelles au plus par session, puis apprentissage en avance (20 min). Clavier : Espace/Entrée, 1 à 4
  (bail clavier pris au premier clic dans la carte).
- **Vue** (`StudyView`) : rangée de `DeckTile` (dû / préparation / échec, titre, barre de maîtrise) ; feuille dans la
  bulle (880 × 660 par défaut, redimensionnable) : Réviser / Fiche (MarkdownLite, « Copier ») / Cartes (modifier,
  supprimer) ; « Nouvelles cartes » ouvre une bande (sur quoi, combien, Générer). Le travail de Gemini est suivi sur
  le cours (`Busy`, `Progress`, les étapes) ; tant que le mode affiché n'a rien (`IsWaiting` : pas de carte, ou pas de
  fiche), la feuille montre la préparation : la mascotte (`Reader`, une `MascotView` à la couleur choisie) lit son livre,
  et trois étapes (plan, fiche « 4 / 12 parties », flashcards « 18 / 50 ») avec une coche, une icône et une barre qui
  grandit (échelle animée par le compositeur) ; échoué : pourquoi, et « Réessayer » (ce qui est fait est gardé). Sur la notch fermée, une
  **activité en direct** (`StudyViewModel.ActivityChanged` → `SetActivity`) : « Lecture du cours », puis « Fiche 4/12 ·
  18/50 cartes » et un anneau (le plan un sixième, puis fiche et cartes à parts égales ; « Nouvelles cartes » : les
  cartes seules), retirée à la fin (ou celle d'un autre cours en préparation). La fin est annoncée (`Announce` avec progression : pas `SetActivity` ni la
  lecture de la mascotte, qui appartiennent au Pomodoro). Un cours sans fiche au relancement : « Pas terminé : réessayez ».
- **Tests** : `scratchpad/studytest` (répétition, session, schémas et lecture des réponses, .docx/.pptx/PDF, base dans
  un dossier temporaire grâce au constructeur `internal StudyStore(dispatcher, folder)`). Jamais d'appel à Google.

### Fenêtres détachées — nouveau, jamais vu à l'écran
`Windowing/DetachedWindow` : la fenêtre d'une feuille sortie de la notch (cours, note). Demandé : une poignée pour la
déplacer (le titre, c'était pénible), les coins arrondis de la bulle, pouvoir la réduire.
- **Dessin** : fenêtre popup transparente comme la notch (`TransparentBackdrop`, `EnablePerPixelTransparency`, `WS_POPUP`
  sans cadre ni coins DWM, mais `WS_EX_APPWINDOW` + `WS_MINIMIZEBOX` : elle a son bouton dans la barre des tâches) ; dedans,
  `Frame` noir aux coins de 32 (ceux de la bulle) et un filet `#24FFFFFF`. La feuille arrondit ce qu'elle peint d'un bord à
  l'autre (`IDetachableSheet.RoundCorners` : les lueurs du cours).
- **Poignée** (`Controls/MoveHandle` : barre 44 × 5 en haut au centre, zone 168 × 20, curseur de déplacement, plus vive au
  survol) : tenue, la fenêtre suit le pointeur (lu à l'écran, `GetCursorPos`, `AppWindow.Move`). **Coins du bas** : les
  `ResizeGrip` de la bulle ; tirés, la fenêtre se redimensionne, son haut et son autre côté fixes, jamais sous le minimum.
  Tout est fait par l'appli (capture du pointeur), pas par la boucle de Windows (une popup sans `WS_THICKFRAME` ne se
  redimensionne pas par elle).
- **Boutons** de la fenêtre dans l'en-tête de la feuille (`IDetachableSheet.AddWindowTools`, insérés dans sa rangée
  `Actions`) : l'épingle avant « Rattacher » (`SetWindowPos` `HWND_TOPMOST` / `NOTOPMOST`, allumée en blanc translucide,
  réappliquée au premier affichage) et « Réduire » (E921, `SW_MINIMIZE`) avant « Fermer ».
- **Place** (`WindowPlace` : lit et écrit les réglages « x,y,l,h » et l'épingle) : la dernière si elle est encore sur un
  écran, sinon celle de la bulle (`DetachedWindow.ScreenBounds`), sinon au milieu de l'écran principal ; retenue à chaque
  déplacement ou redimensionnement (pas réduite).
- Premier plan → `IDetachableSheet.FocusContent` (la carte, ou le texte de la note si aucun champ n'a déjà le clavier).
- À vérifier : la transparence des coins, la poignée et les coins (fluidité, multi-écrans), réduire puis rouvrir depuis
  la barre des tâches, l'épingle.

### Premier lancement : accueil et visite — nouveau, jamais vu à l'écran
- **Quand** : `App.OnLaunched`, après `ShowNotch`, si `!AppSettings.Welcomed`. `Welcomed` vaut `false` sans
  fichier de réglages (installation neuve) mais `true` par défaut dans `StoredSettings` : un settings.json d'avant
  l'accueil (une notch déjà réglée) ne le voit jamais. Mis à `true` à la fin, en passant ou en fermant la fenêtre.
  Paramètres → Général → Présentation : « Accueil » et « Visite de la notch » (`SettingsWindow.WelcomeRequested` /
  `TourRequested`, branchés dans `App.ShowSettings`).
- **Fenêtre** (`Welcome/WelcomeWindow`) : Mica, barre de titre comme les paramètres, `MascotView` éveillée en haut
  (elle porte la couleur choisie, `Exclaim` à chaque étape). Sept étapes (`Go`, glissement + fondu au compositeur) :
  bienvenue + langue, mascotte (`SettingsWindow.CreateSwatch`, devenu `internal static`), notch (normale / minimisée,
  « Rester ouverte après un clic », « Se cacher au repos », plein écran, `StartupLaunch`), musique (`MusicStep` :
  « Afficher les paroles », « Ligne chantée » notch / barre des tâches via `LyricsPlaceIndex`, « Retirer le lecteur de
  la notch fermée », actif seulement en barre des tâches : `OnTaskbar`), raccourcis (`SettingsWindow.ShowKeys`, `internal static`, interrupteurs liés aux
  réglages), assistant (lien AI Studio, clé vérifiée par `GeminiClient.ListModelsAsync` puis `ApiKeyStore.Save`, comme
  les paramètres), prêt. La langue : `Loc.Initialize` tout de suite ; si la fenêtre ne parle plus la bonne,
  `LanguageChanged` et `App.ShowWelcome(étape)` la refait (la notch, elle, change au prochain lancement).
  `Finished(visite)` est levé une seule fois.
- **Visite** (`Notch/NotchView.Tour.cs`, `TourCard`, `TourStep`, `Welcome/TourSteps.Build`) : `ShowModule` du premier
  onglet + `_controller.Hold()`, la carte dans la bulle (`ShowBubble`, `Resize` d'une étape à l'autre), un anneau en
  composition dans `TourLayer` (rectangle arrondi : trait 2 px + halo 7 px à la couleur `Accent` de la mascotte, cinq
  ressorts X/Y/W/H/R + `Shown`), placé par `TargetBounds` (notch : `BoundsOf(OpenNow)` ; onglets ; réglages rapides ;
  pod de la mascotte ; corps seul, sans les épaules). Collée au bord de l'écran (`IsFlush`), la cible a un anneau qui
  monte au-delà du bord : ses coins du haut hors de vue, il l'épouse en U. Pendant la visite, les zones des petites
  notches (`SideBounds`) grandissent de `TourRoom` : la région de la fenêtre coupe aussi le dessin, l'anneau y tient
  entier. Les étapes sans leur cible (`NeedsTarget` : pas de mascotte, pas de réglages rapides) sautent. Une étape
  peut montrer l'onglet d'un module (`TourStep.Module`, `NotchView.ShowStepModule`) : « Vos révisions »
  (`StudyModule` : sections « + », tuile des cartes à revoir, cartes mentales, fenêtre détachée) et « Votre musique et
  ses paroles » (`MediaModule` : la ligne dans la barre des tâches, sa largeur), après « Les onglets ». La première
  étape dit le clic qui garde la notch ouverte (si `StayOpenAfterClick`), celle de l'assistant la flèche de retour.
- **Interactive** : `TourStep.Wait` (Tab, Drop, Quick, Mascot, Menu, Custom) + `Try` (« À vous : … ») + `Done`.
  `NotchView.TourDid(quoi)` est appelé par `SelectTab`, `OnDrop`, les réglages rapides (molette, clic), le clic sur
  la mascotte (pris par la visite : l'assistant ne s'ouvre pas à cette étape), `OnMenuOpening` ; fait, la carte
  montre une coche et `Done`, la mascotte `Exclaim`, la notch `Kick`, et l'étape suivante vient après `TourDoneFor` (1,6 s).
  Le fichier d'exemple (`TourCard.SampleFile`, `CanDrag`) porte `TourCard.SampleProperty` : `DragTourSample`
  (depuis `OnDragOver`) fait comme pour un vrai fichier au-dessus du corps de la notch (onglet Drop Zone,
  `OnDragHover`, sa légende), `OnDrop` le prend pour la visite, rien ne part. Le raccourci (`Custom`) :
  `TourStep.Await` pose `GlobalShortcut.Capture` (appelé à la place de `Pressed`) le temps de l'étape ; pour la
  recherche, il appelle aussi `SearchRouter.OpenInNotch` : la recherche s'ouvre pour de vrai dans la notch, et
  l'étape (`StepsAside`) met la visite de côté (`StepAside` : carte rentrée, anneau éteint, notch relâchée ;
  `ShowBubble` d'un module ne la termine plus). Quand la notch se referme (`OnPhaseChanged`) ou revient sur un onglet
  (`SelectTab` → `TourBackOnTab`), `ResumeTour` rouvre la notch sur un onglet à l'étape suivante (l'assistant). « Suivant » reste là pour ne pas essayer.
  Un raccourci pendant la visite (Ctrl + Q à une autre étape, celui de l'assistant) : `SelectTab` vers un module sans
  onglet met la visite de côté (`StepAside(advance: false)`, sa surveillance de raccourci relâchée) ; elle revient à
  **la même** étape une fois la recherche ou l'assistant fermés (`Tour.Advance` : seule l'étape essayée passe à la
  suivante). Avant, la bulle de la recherche terminait la visite.
  Fin : dernière étape, « Passer la visite », la notch fermée ou rangée (`OnPhaseChanged`, `Retreat`), une autre bulle
  demandée par un module, ou un clic sur la mascotte. La dernière étape (l'assistant) : « L'essayer »
  (`AssistantModule.OpenFromMascot`) avec une clé, sinon « Ajouter une clé » (`App.ShowSettings("Assistant")`,
  `SettingsWindow.ShowSection`). Rien ne tourne entre deux étapes.
- **Tests** : l'anneau (ses expressions sur le vrai compositeur) dans `scratchpad/mascottest` (`Tour.cs`). La fenêtre et
  la visite n'ont jamais été vues.

### Palette de la mascotte — moins pastel
`MascotPalette.Colors` : lavande B7A0FB, ciel 5CC9F9, menthe 58DBAB, citron FBD53F, pêche FBA65F, rose F68BC3, corail F98B8B
(les teintes « Tailwind 400 » un cinquième vers le blanc : moins pastel qu'au début, pas criard ; blanc inchangé) ; mêmes identifiants, une couleur choisie suit. Avant/après : `scratchpad/palettepng.js`.

### Look de la mascotte — le galet simple, un léger relief (nouveau, jamais vu à l'écran)
Le galet garde son dégradé linéaire (`Tone` → `Shade`), sans sourire au repos, avec un léger relief
(`MascotRig.CreateRelief`) : un calque entre le corps (et ses joues) et les yeux, coupé au corps, deux dégradés radiaux
immobiles, une ombre au bord (`Shade` × 0,7, 0x58, plus forte en bas) et un reflet blanc (0x5C) en haut à gauche ;
éteint en onde et en poignée (`(1 − Flat) × (1 − Wave)`). Les yeux ont donc leur propre calque (8 calques en tout).
Image : `scratchpad/reliefpng.js` (le galet nu, puis avec le relief ; quatre couleurs). Trois essais de « modernisation » ont
été faits puis retirés à la demande (pas aimés) : l'option C (dégradé radial éclairé en haut à gauche, bord bas lumineux,
halo doux, sourire selon l'humeur), sa version brillante (reflet, reflets dans les yeux), et le « style lumineux »
(l'orbe de l'assistant tous les jours, réglable). L'orbe reste réservée au mode IA. Ne pas les reproposer tels quels.

### Activités en direct — une par module
`NotchView.SetActivity(module, activité)` : chaque module a la sienne (`_activities`, dans l'ordre où elles ont
commencé) ; la notch fermée montre la dernière commencée (une mise à jour garde sa place), et quand elle finit, la
précédente revient (un cours en préparation par-dessus le minuteur du Pomodoro, qui revient ensuite). Avant, une seule
place : chaque module effaçait celle des autres.

### Raccourci de la recherche — réglable
`SearchShortcut` est un `GlobalShortcut` comme ceux du presse-papier et de l'assistant (`AppSettings.SearchShortcut`,
défaut `Ctrl+Q`, et `SearchShortcutOn`) ; Paramètres → Recherche : les touches, « Modifier » (enregistre la prochaine
combinaison par le hook), « Rétablir », l'interrupteur.

### Mode minimisé — nouveau, jamais vu à l'écran
`AppSettings.Minimized` (Paramètres → Général, et le clic droit de la notch : `MinimizedMenuItem`). Dans `NotchView` :
`UpdateIdle` force `SetIdle(true)` (la poignée, quoi qu'il se passe) ; `MorphTo` ne fait ni nid (`nested`), ni
activité seule, ni annonce sortie du nid : c'est la poignée (`n.Mini`, la mascotte aplatie en sa ligne). Les annonces,
cartes et questions sortent toujours la pilule le temps qu'il faut (comme au repos).

### Presse-papier — stable
`Modules/Clipboard/`. `WM_CLIPBOARDUPDATE` via une fenêtre message-only, historique dans
`%LOCALAPPDATA%\Ilot\clipboard.json` (+ images dans `clipboard\`), raccourci Windows + V par hook clavier
bas niveau.
- **Aperçu qui retouche** (nouveau, jamais vu à l'écran) : `ClipboardPreview` dans une bulle (`ShowBubble`, taille
  de l'image ajustée dans 900 × 520, redimensionnable, min. 480 × 240 ; texte 660 × 360), ouverte par l'œil d'une tuile
  (`ClipboardCard.PreviewRequested`) ou Espace dans la recherche quand rien n'est tapé (`NotchSearchBox.Peeked`, sur
  `PreviewKeyDown`, seulement si quelqu'un écoute). Elle prend le clavier (`TakeKeyboard`, la notch reste ouverte) et le
  rend en fermant ; un clic dans une autre appli la ferme. Image : `ImagePixels` (BGRA prémultiplié, comme le
  presse-papier) — le crayon est rastérisé ici (couverture max par trait, bord lissé, donc pas plus foncé là où il se
  croise ; 25 ms pour un long trait en 4K), le rognage copie des lignes, pivoter (R) et miroir (M) passent par une vue
  32 bits des pixels (125 ms en 4K, hors fil UI) ; affichage par `WriteableBitmap` dans un
  `Viewbox` (coordonnées du pointeur = pixels de l'image ; le trait en cours est une `Polyline` remplacée une fois
  rastérisé). 12 étapes d'annulation (images entières). Copier : inchangé, c'est la copie elle-même (pas de doublon) ;
  retouché, un PNG dans `%TEMP%\Ilot\Edited` (les anciens effacés) écrit dans le presse-papier, et l'historique
  l'enregistre comme une nouvelle copie. Texte : `CopyEditedTextAsync`. Testé sans écran (`scratchpad/edittest`).

### Drop Zone — stable, partage local récent
`Modules/DropZone/`.
- **Le vrai type des fichiers** (vu : un PDF reçu sur le téléphone en « cours.pdf.txt ») : la page du téléphone télécharge
  elle-même les fichiers de moins de 512 Mo (`fetchFile`) et les enregistrait avec `new Blob(parts)`, sans type : Chrome
  sur Android en fait du texte. Le PC envoie maintenant le type d'après l'extension (`LocalMessage.ContentTypeFor` : PDF,
  Word, Excel, PowerPoint, OpenDocument, texte, ZIP/RAR/7z, APK, audio, vidéo, images ; sinon `application/octet-stream`)
  et la page le donne au `Blob`. Le même type part chez Send (métadonnées chiffrées, que sa page utilise) et Gofile.
- **En ligne** : `FileUploadService` (file, vitesse sur fenêtre de 2 s, détection de blocage à 3 s, nouvel
  essai après 75 s sans progrès, 4 essais), hébergeurs dans `Hosts/` (`IUploadHost`). File d'attente sur disque
  `UploadsQueue\`.
- **Wi-Fi local** (`Local/`) :
  - `LocalTransferService` : serveur HTTP minimal sur `TcpListener` (`HttpListener` exigerait les droits
    administrateur), sur l'interface Wi-Fi seulement, port 80 puis 8080 puis libre.
  - Sécurité : clé aléatoire dans l'adresse, code à 4 chiffres pour la saisie à la main, blocage d'une minute
    après 10 codes faux.
  - Le serveur s'arrête après 10 min sans requête ou si le PC quitte le réseau.
  - `MdnsResponder` : publie `mynotch.local` (RFC 6762, maison). Testé : Windows le résout. **Non testé sur de
    vrais téléphones Android / iPhone.**
  - `LocalPage` : page web autonome servie au téléphone ou autre PC (HTML/CSS/JS en ligne) sous forme de chat en direct et zone de dépôt (boutons Coller, Photo / Image, Fichiers, `Ctrl + V` global, glisser-déposer, long-polling `/chat/events?since=`).
  - **Chat et collage en direct** : bulle détachée redimensionnable sous la notch (`ShowBubble`, dimensions dans `LocalChatWidth` / `LocalChatHeight`), cartes `LocalMessageCard` avec animations implicites sur le compositeur. Les textes, liens et images collées (`POST /chat/text`, `POST /chat/image`, ou `Ctrl + V` / bouton « Coller » côté PC) s'affichent directement dans le fil avec aperçu et actions en 1 clic (Copier, Ouvrir, Enregistrer). Les images temporaires du chat vivent dans `%LOCALAPPDATA%\Ilot\LocalChat\` et sont nettoyées à la fin du partage.
  - Fichiers reçus (`POST /upload`) : écrits cachés (`*.part`) dans Téléchargements, affichés dans le chat et soumis à **Accepter / Refuser** (`IncomingFile`, `IncomingFileCard`, `NotchContext.Ask`, ou directement depuis la carte dans la bulle de chat). Refusé ou jamais décidé à la fermeture : supprimé. La page suit l'état en direct via `/chat/events` et `decision/{id}`.

### Code — stable
`Modules/Code/`. Lit `state.vscdb` de VS Code (SQLite) et `workspaceStorage`, à chaque fois que « code » est tapé dans
la recherche. Plus d'onglet (`CodeModule` / `CodeView` retirés) : `SearchView.CodeWords` reconnaît « code » (seul ou
suivi de mots), et la goutte (`_drop`, une grille) montre `CodeShelf` à la place de `SearchResultsPanel` : une liste
verticale (6 lignes en vue au plus, défilement sans barre, fondus aux bouts), chaque projet une `CodeRow` dans le style
des tuiles (verre teinté de sa couleur, icône sur un carré teinté, badge, boutons emplacement / copier au survol ou
choisi, anneau de sélection), animations implicites ; filtrée par les mots, la première choisie ; ↑ ↓ et Entrée. La
goutte suit la hauteur de la liste (`CodeShelf.Fit`, `Changed`). `CodeCard` n'est plus utilisée (gardée). `CodeViewModel.Launched` (un projet ouvert, d'une tuile ou d'Entrée) ferme la recherche.
Les résultats qui arrivent encore pour la recherche d'avant sont ignorés tant que les projets s'affichent
(`ShowResults`). Le modèle est passé par `SearchViewModel.Projects` (satellite compris).

### Pomodoro — nouveau, jamais vu à l'écran
`Modules/Pomodoro/`. Onglet après Notes (glyphe E916).
- **`PomodoroTimer`** (sans écran, testé avec une horloge simulée) : phases `Focus` / `ShortBreak` / `LongBreak`, états
  `Ready` / `Running` / `Paused`. Il garde l'heure de fin (pas un décompte : il ne dérive pas), `Tick` repère la fin
  (appelé deux fois par seconde, seulement quand il tourne). La grande pause après `SessionsBeforeLongBreak` focus finis
  (`Done`, remis à 0 après elle) ; un focus passé (`Skip`) ne compte pas ; `AutoStartBreaks` / `AutoStartFocus`.
  `Use(plan)` : une phase pas commencée prend la nouvelle durée tout de suite, une en cours garde la sienne.
- **`PomodoroStats`** : `%LOCALAPPDATA%\Ilot\pomodoro.json`, sessions et minutes par jour (60 jours), écrit à chaque
  focus fini (hors fil UI, remplacé d'un coup).
- **`PomodoroView`** : l'anneau (composition : lueur radiale fixe, piste, arc `TrimEnd` = part restante ; en marche, une
  seule animation linéaire jusqu'à la fin, rien image par image ; couleur qui fond d'une phase à l'autre ; rebond `r.Pop`
  à chaque fin). Le temps (chiffres tabulaires), la phase, les points de la série (« Nouvelle série » à côté, une fois
  entamée : `PomodoroTimer.Reset`, les stats du jour restent), le jour. Notch fermée : activité en
  direct (`SetActivity`, icône livre / lune / pause, anneau = temps écoulé, texte = temps restant), rien tant que rien
  n'a commencé. `NotchContext.MascotReads` pendant un focus qui tourne, `MascotPlays` pendant une pause qui tourne.
  Couleurs : celles de la mascotte (`MascotColor.Accent`, `Light(0.8)` / `Light(0.7)` pour les pauses). Fin de phase :
  `Chime.Play` (si réglé), la carte `PomodoroCard` présentée avec la mascotte (`Present(..., withMascot: true)`,
  4,5 s ; `Announce` si la notch est ouverte et refuse la carte), rebond.
- **La mascotte en héros d'une carte** (`NotchContext.Present(card, size, duration, withMascot)`, `HeroRoom` = 84 à
  laisser vide à gauche de la carte) : `n.Hero` (ressort) mêle sa place habituelle et `n.HeroX` / `n.HeroY` /
  `n.HeroScale` (60 px) dans `BindMascot` ; elle revient avec `EndPresentation`. Arrivée (380 ms), `MascotView.Exclaim` :
  contente, regard droit, un saut et `MascotRig.Exclaim` (la bouche, en noir sur son visage, s'ouvre sur 9 syllabes
  jouées par le compositeur, puis disparaît ; elle n'a pas de bouche sinon).
- **`Chime`** : fabriqué (pas de fichier), des notes de cloche (fondamentale, octave et quinte qui s'éteignent plus
  vite, attaque de 6 ms, décroissance) : do-mi-sol-do montant (105 ms d'écart) pour aller en pause, do puis sol pour
  reprendre ; lu par `WasapiPlayer`, relâché 3 s après.
- **La mascotte qui lit** (`MascotRig.CreateBook` / `Book` / `TurnPage`, humeur `Reading`, `MascotView.Read`) : un livre
  ouvert en V tenu au bas du corps (couverture chaude, pages crème, lignes de texte) qui monte dans ses mains ; ses yeux
  suivent une ligne en 4 pas (1,1 s) puis la suivante, une page tourne toutes les 3 lignes (la page de droite se replie
  sur la reliure en se soulevant, `p.Flip`, puis revient d'un coup là où elle est identique). Ni sommeil, ni onde de
  musique, ni regard qui suit le pointeur pendant ce temps ; l'orbe de l'assistant passe devant (livre posé). Vérifié
  par une image (`scratchpad/bookpng.js`, mêmes mesures).
- **La mascotte qui joue** (`MascotRig.CreatePad` / `Pad` / `Wiggle`, humeur `Playing`, `MascotView.Play`) : une manette
  (corps ardoise et deux poignées, croix claire, quatre boutons de couleur) tenue comme le livre ; toutes les 0,7–1,8 s
  (un minuteur non répétitif, seulement pendant la pause), soit elle appuie (la manette s'incline d'un côté puis de
  l'autre, `p.PadTilt`, et plonge un peu, `p.PadBob`), soit elle lève les yeux vers l'écran. Mêmes règles que la
  lecture (pas de sommeil, pas d'onde, l'orbe passe devant puis elle reprend). Image : `scratchpad/padpng.js`.
  Le cercle de minuteur autour de la mascotte a été retiré (demande de l'utilisateur).
- **Bulle de réglages** (`PomodoroSettingsPanel`, la roue dentée de l'onglet) : `ShowBubble`, aussi haute que son
  contenu ; règles graduées (`Controls/RulerPicker` : graduations qui glissent sous un repère fixe de la couleur de la
  mascotte, glisser / molette / clic, ressort pour se poser sur un pas, translation sur le compositeur) avec leur valeur et interrupteurs (enchaînements, carillon), appliqués à chaud et
  suivis si la fenêtre des paramètres les change. Refermée par la roue, la notch qui se ferme, un autre onglet.
- **Paramètres → Pomodoro** : durées (focus 5–90, petite pause 1–30, grande pause 5–60), focus avant la grande pause
  (2–8), curseurs gradués, enchaînements, carillon (`AppSettings.Pomodoro*`).
- À vérifier : le rendu de l'onglet, l'activité en haut (largeur, tenue au fil des secondes), le livre à la taille de la
  pilule, la manette, la carte de fin de phase (la mascotte qui y arrive et parle), le carillon, l'enchaînement des
  phases sur une vraie durée.

### Notes — récent, à éprouver
`Modules/Notes/`.
- **Stockage** : `NotesService`, base `%LOCALAPPDATA%\Ilot\notes.db`, table
  `Notes(id, title, content, plain, created_at, updated_at)`.
  - `content` = **RTF** ; `plain` = texte brut pour les cartes, la recherche et les compteurs.
  - Lecture au premier affichage de l'onglet. Écriture 250 ms (capture) + 300 ms (service) après la dernière
    frappe, en série sur le pool de threads ; dernière écriture à la fermeture.
  - Les notes plus anciennes en Markdown sont converties à l'ouverture (`Editor/MarkdownImport.cs` +
    `MarkdownSyntax.cs`), puis réécrites en RTF à la première modification.
- **Interface** : rangée de cartes (`NoteCard`) + recherche ; un clic ouvre la note dans une bulle
  (`ShowBubble`) qui tombe de la notch comme une goutte. La bulle est redimensionnable par ses coins du bas ; la
  taille est retenue dans `settings.json` (`NoteSheetWidth`, `NoteSheetHeight`).
- **Feuille de note** (`NoteSheet`, nouveau, jamais vu à l'écran) : titre, barre d'outils, texte, compteurs, Copier,
  Supprimer ; chaque feuille a **sa** note (`Open`, `New`, `Close(leave)`, `Export` / `Import` → `NoteState` : la note et la
  sélection), le ViewModel n'a plus de note « courante » : `SetTitle(note, …)`, `SetContent(note, …)`, `Leave(note)`
  (enregistrée, ou oubliée si vide), `Delete(note)`, `CopyAsync(note, …)`, `CountText(texte)`. Événements : `NoteChanged`
  (l'hôte montre ou range la bulle / ferme la fenêtre), `TitleChanged`, `CloseRequested`, `MoveRequested`,
  `KeyboardWanted(champ)` (la notch prend le clavier), `KeyboardDone` (Échap). `NotesView` garde la rangée, la recherche,
  la bulle, le bail clavier et la fenêtre. **Détacher** (E8A7 à côté de la croix) : la note continue dans une
  `DetachedWindow` (une à la fois ; 720 × 320 au moins ; `AppSettings.NoteWindowBounds` / `NoteWindowOnTop` via
  `NotesViewModel.Place`), le curseur où il était ; titre de la fenêtre : « Notes · titre », suivi. Un clic sur la carte
  d'une note détachée ramène sa fenêtre devant au lieu de l'ouvrir aussi dans la notch. « Rattacher » : la notch s'ouvre
  sur la note, le curseur au même endroit. Échap dans la fenêtre : la saisie est prise et écrite.
- **Éditeur** : `Editor/RichNoteEditor.cs` (sous-classe de `RichEditBox`). Barre façon Word :
  - styles Texte / Titre / Titre 2 / Titre 3, taille en points ;
  - G / I / S / barré ;
  - listes à puces, numérotées, de tâches (cercle Segoe Fluent `U+EA3A` / `U+EC61` + espace demi-cadratin en
    tête de paragraphe ; les anciens ☐ / ☑ sont convertis à l'ouverture et `Plain` les réécrit en ☐ / ☑ ;
    l'appui, pas le relâchement, coche : RichEdit garde la souris capturée ; Ctrl + Entrée coche aussi) ;
  - annuler / rétablir ;
  - raccourcis Markdown en début de ligne (« - », « 1. », « [] », « # »).
  - Collage en texte seul ; copie en RTF + texte.

### Mascotte — nouveau, jamais vue à l'écran
`Mascot/` pour elle-même, `NotchView` pour sa place (`Buddy`, région « Mascot »). Pas un module : un élément de la
notch, présent dans tous ses états quand `MascotOn` (notch à onglets, pas le satellite de recherche ;
`AppSettings.ShowMascot` vaut toujours `true` : la mascotte ne se désactive plus, l'interrupteur est retiré des
paramètres et de l'accueil, un ancien réglage « non » n'est plus lu ; reflété dans `n.Mascot`).
- **Pod** (`NotchSurface.CreatePod`, `NotchLayout.PodSize` 40 / `PodGap` 10) : sa petite notch, une goutte noire (liseré,
  congés en style Flush) cachée dans le bout droit de la pilule fermée, qui en sort avec `n.Progress` et se pose à
  droite de la notch ouverte. Même opacité que la mascotte. Il a sa propre part de la région (une des `SideInputBounds`, que
  `NotchWindow.ApplyInputRegion` réunit à celle de la notch par `CombineRgn` ; suivie pendant le retour, cf.
  `OnShrinking`) et du survol (`_podHover`). Surtout pas un rectangle englobant notch + pod : il prenait le coin vide
  à droite de la notch, qui la gardait ouverte et avalait les clics (« reste ouverte quand je clique à côté »).
- **Place** (`BindMascot`) : posée à sa place ouverte (28 px, au centre du pod), portée par la morph
  (`Scale` / `Translation` sur `n.Progress`) au bout droit de la pilule (28 px, marge 4). Opacité :
  `n.Mascot × (1 − n.Mini)` (cachée sous la pilule qui s'efface ; là aussi à côté de la barre de recherche). Cliquable
  seulement ouverte : fermée, son emplacement de mise en page est celui de la poignée.
- **Nid** : au repos (là où la notch serait sa poignée), `NestShape` : une petite pilule ronde 36 × 36 où elle dort ;
  `n.Nest` cache le contenu compact des modules (pochette…). Survolé, il s'efface devant la poignée comme la pilule
  (`resting` vrai, `nest` faux pendant `IsHandle`).
- **Annonces** : avec elle, le message commence après elle (`MascotToastShift` dans la translation et `extra`) et le
  glyphe disparaît (sauf anneau de progression). Le visualiseur compact du lecteur s'efface (`n.Mascot`).
- **Musique** : `NotchContext.ShareBeat(_state)` pendant la lecture (le jeu de propriétés du lecteur, `Bass` mis à jour
  à chaque image, et `L0`…`L3`, les niveaux du petit visualiseur) → `MascotView.Groove` → `p.Beat` et `p.L2`, `p.L3`
  liés sur le compositeur ; `p.Wave` (ressort) la change en onde : le corps devient la barre du milieu (gélule
  centrée qui s'étire sur la basse), une barre épaisse de chaque côté (`CreateBar`, non découpée) suit les niveaux (peu d'éléments : lisible à 28 px), relevés pour bouger avec toute musique : `Pow(niveau, 0.7) × 1,35` et la basse (`Kick` = `Pow(basse, 0.75) × 1,3`), le corps s'étirant de 60 % sur le kick.
  Humeur `Groove`, jamais endormie.
- **Dessin** (`MascotRig`) : un `ShapeVisual` 100 × 100 (corps en rectangle arrondi posé sur le sol de la boîte,
  dégradé blanc → gris bleuté ; yeux en gélules noires, joues en ellipses), le tout découpé au corps. Chaque forme
  est liée par expressions à un jeu de propriétés `p` mené par des `SpringChannel` : rien par image côté UI.
- **Couleur** (`MascotPalette`, réglage `MascotColor` : l'id de la teinte, ou « #RRGGBB » pour une couleur choisie
  librement dans le `ColorPicker` de la pastille « Personnalisée… ») : 8 tons clairs, ou n'importe lequel ; sur un
  corps foncé (luminance < 0,4) les yeux passent en gris clair (`MascotColor.Eyes`) ; `MascotRig.SetColor` anime les deux arrêts du dégradé (le ton, et une ombre un peu plus sombre en bas),
  partagé par le corps et les barres de l'onde. Pastilles rondes dans la carte « Mascotte » des paramètres.
- **Poignée** (`MascotRig.Flatten`) : `p.Flat` suit `n.Mini` ; le corps se change en la ligne de la poignée
  (`Min(n.Width * 0.47, 56)` × 4 px, comme `NotchSurface.CreateGrabber`, dont la ligne grise s'efface avec `n.Mascot`),
  les yeux, les joues et l'onde s'effacent ; `BindMascot` la glisse au milieu de la poignée. Ses calques font trois
  boîtes de large (`CreateLayer`) : aplatie, elle déborde de sa boîte sans être coupée.
- **Volume** (`NotchView.ShowLevel` / `BindGauge`, `n.Gauge` et `n.GaugeLevel`) : `VolumeWatcher` (Media/Audio) écoute
  `AudioEndpointVolume.OnVolumeNotification` de la sortie par défaut (suivie à chaque changement de sortie) ; `MediaView`
  passe chaque changement (et la molette) à `NotchContext.ShowLevel`. Dans la pilule à sa largeur normale (sans
  l'élargir, ni la garder élargie pour les paroles), le contenu des modules et les annonces s'effacent (`n.Gauge`), piste + remplissage à la couleur de la mascotte
  (`_gaugeFill`), mascotte au bout du remplissage (`GaugeHead`, dans `BindMascot`) : `MascotView.ShowLevel` (regard dans
  le sens du changement, yeux fermés si muet, `Happy` à 100 %, l'onde rangée), `EndLevel` 1,6 s après le dernier.
- **Contrôle de volume du lecteur** (le haut-parleur en haut à droite, `VolumeControl`) : retiré, la bulle de volume
  le remplace. Notch ouverte, un changement de volume ne s'affiche que dans la bulle ; fermée, sur la pilule.
- **Bulles rapides** (`Quick/`, `NotchView.BindQuick` / `UpdateQuick`, `NotchSurface.QuickPods`) : deux petites notches
  à gauche de la notch ouverte, en miroir du pod de la mascotte (le même `CreatePod` : suspendue, ou ronde en dessous),
  volume en haut, luminosité dessous, 52 px (`NotchLayout.QuickSize`). `QuickBubble` : anneau (`TrimEnd` sur une ellipse)
  et bouton au bout (`Sin` / `Cos` du niveau), icône ou pourcentage ; l'anneau est un cadran (appui à `RingReach` de
  l'anneau : le niveau suit l'angle, bloqué aux bouts), molette (5 %), au milieu glisser vertical (`DragReach` 140 px
  pour 100 %) et clic (couper le son). Posée exactement sur son pod (centre et taille suivent `QuickLeft` et
  `Lerp(cw, qw)`), visible seulement quand le pod est presque sorti : rien ne déborde à l'ouverture ni à la fermeture ; la notch reste ouverte tant qu'une bulle
  est pressée (`_quickHold`). `BrightnessService` : réglage « luminosité de l'écran » du mode d'alimentation actif
  (`powrprof` : lecture AC/DC selon la source, écriture puis `PowerSetActiveScheme`, hors du fil UI, la dernière valeur
  gagne) ; disponible seulement avec une batterie (le PC du propriétaire est un portable ; son écran ne répond pas en
  DDC/CI et n'a pas de `\\.\LCD`). La région de la fenêtre et le survol prennent chaque petite notch à part
  (`SideInputBounds`, `SideBounds`). Cachées à côté des barres (recherche, assistant), dès le début de l'ouverture (opacité `b.Shown`, sans fondu avec `n.Progress` : sinon elles sortaient à moitié de la pilule), et en poignée.
  Couleur : `MascotColor.Accent` (la couleur de la mascotte, ou sa lumière si elle est trop sombre pour se voir sur le
  noir), `QuickBubble.Tint` (anneau et bouton ; la piste reste blanche pâle), `NotchView.TintQuick`.
- **Mélangeur de volume** (`Quick/VolumeMixer`, `Quick/MixerPanel`, `NotchView.BindMixer` / `OpenMixer` / `CloseMixer`) :
  clic droit sur la bulle du volume (`ContextRequested`, marqué traité : pas le menu de la notch). Les sessions audio de
  la sortie par défaut (NAudio `AudioSessionManager`), lues à l'ouverture et lâchées à la fermeture (rien n'est suivi
  entre-temps) ; regroupées par programme (un navigateur en a plusieurs), expirées et la nôtre exclues ; triées : sons
  système, celles qui jouent, puis par nom. Nom : celui que donne la session (sauf « @… »), sinon la description du
  programme, sinon son nom nettoyé (`VolumeMixer.Tidy` : « VALORANT-Win64-Shipping » → « VALORANT »). Chemin du programme
  par `NativeMethods.ProcessPath` (`PROCESS_QUERY_LIMITED_INFORMATION` + `QueryFullProcessImageName`, comme le
  Gestionnaire des tâches : rien n'est lu du processus, important avec un anti-triche), icône par `ShellIcons` (celui de
  la recherche). Panneau noir, bord pâle, à gauche des bulles, en haut (`MixerTop`), 10 px d'écart ; large de
  272 px si la place le permet, sinon ce qu'il reste jusqu'au bord de la fenêtre (220 au moins) ; suit `n.Width` quand un
  module élargit la notch, sans passer le bord (`MixerEdge`). Grandit depuis son coin haut droit (`m.Open` : opacité,
  échelle 0,9 → 1, sans glissement : rien ne passe sur les bulles). Sa zone (du haut de l'écran jusqu'aux bulles, pour
  que traverser l'écart ne ferme rien) est dans `SideBounds` tant qu'il se voit (`_mixerShowing` : ouvert, ou en train
  de s'effacer ; sinon la région de la fenêtre le coupait net), y compris pendant le début de la fermeture de la notch
  (`progress >= 0.8`). Liste jusqu'à 380 px, sans barre, ses bouts en fondu quand il y a plus. Il ne suit `n.Width`
  que vers la gauche (`Max(n.Width, ew)`) : la notch qui se ferme rétrécit plus vite qu'il ne s'efface, et il glissait
  par-dessus. Fermé avec la notch, ou pour une barre (`UpdateQuick`). `MixerSlider` : piste, niveau et bouton de la
  couleur d'accent, clic ou glisser, molette 5 % ; monter le niveau d'une app coupée rétablit son son (comme Windows).
- **Humeurs** (`MascotMood` / `MascotPose`) : `Neutral`, `Happy`, `Surprised`, `Sleepy`, `Groove` ; gestes :
  `Blink`, `Wink`, `Hop`, `Startle`, `Look`, `Groove(beat)`.
- **Vie** (`MascotView`) : `IsShown` (le réglage : coupée, tout s'arrête) et `IsAttentive` (notch ouverte sur un
  onglet : réveil avec un saut, regard qui suit le pointeur). Sinon endormie après 1 min sans clavier ni souris
  (pas pendant la musique) : `UserActivity.IdleFor` lit l'heure de la dernière entrée (`GetLastInputInfo`), le
  minuteur de sommeil est réglé sur le reste de la minute puis relit ; endormie, `UserActivity.WaitForInput` pose
  des crochets clavier et souris bas niveau, retirés à la première entrée (réveil sans saut). Rien ne sonde. Clignements tous les 2,2 à 5,8 s (parfois doubles), seulement éveillée.
- Réactions : `NotchView.Announce` / `Present` / `Ask` appellent `Buddy.React(NotchEvent)` (pas pour une annonce
  qui se met à jour, comme le volume).
- Expressions validées sans interface (petit projet console qui crée un vrai `Compositor`, monte le rig et lie les
  expressions de la notch et du lecteur).

### Voix — nouvelle, jamais entendue (pas testée avec une vraie clé)
`Modules/Assistant/Voice/`. Lancée par la scène de la mascotte dans la goutte (la mascotte, ou la pastille « Parler » dessous) ; plus de bouton micro dans la barre.
- **`GeminiLive`** : l'API Live par `ClientWebSocket` seul (`wss://…/v1beta.GenerativeService.BidiGenerateContent`). La clé
  dans l'en-tête `x-goog-api-key` (vérifié avec une fausse clé : Google la lit, « API key not valid », alors que la doc
  ne montre que `?key=`). `setup` (modèle, `responseModalities: AUDIO`, voix `Puck`, consignes, transcriptions entrée et
  sortie), attente de `setupComplete` (12 s), puis l'historique écrit en `clientContent` avec `turnComplete: false` (ne
  déclenche pas de réponse). Micro : `realtimeInput.audio` PCM 16 kHz (`mediaChunks` est obsolète). Réponses :
  `serverContent.modelTurn.parts[].inlineData` (PCM 24 kHz), `inputTranscription` / `outputTranscription`,
  `interrupted` (vider la lecture), `turnComplete`, `goAway` (fin, ~10 min de connexion, 15 min de session audio). Une
  seule écriture à la fois (`Channel` + boucle d'envoi). Les erreurs arrivent en raison de fermeture (`Refusal` : clé,
  quota, modèle absent → modèle suivant).
- **Modèles** : `GeminiClient.ListModelsAsync(…, "bidiGenerateContent")` puis `LiveCandidates` (stables avant preview,
  plus récent d'abord, traduction et transcription exclues, « extended-thinking » en dernier), complétés par les connus
  (`gemini-3.8-live`, `gemini-3.1-flash-live-preview`, `gemini-2.5-flash-native-audio-preview-12-2025`) ; celui qui a
  répondu passe en tête pour la session.
- **Audio** (`VoiceAudio`) : `Microphone` = `WasapiRecorderBuilder` (NAudio 3) en mode communications : annulation d'écho de
  Windows quand le micro l'a (`AcousticEchoCancellationControl`, `CancelsEcho`), sinon mode simple ; format demandé
  16 kHz mono 16 bits, converti nous-mêmes sinon (moyenne des canaux, filtre boîte) ; morceaux de 60 ms. `Speaker` =
  `WasapiPlayerBuilder` + `BufferedWaveProvider` 24 kHz, `Loudness` mesure ce qui sort (pour la mascotte). `WasapiCapture`
  / `WasapiOut` sont obsolètes en NAudio 3, et `IWaveProvider.Read` prend un `Span<byte>`.
- **Orchestration** (`AssistantVoice`, singleton) : états `Connecting`, `Listening`, `Thinking` (l'utilisateur a parlé, une voix
  continue d'au moins 350 ms, `Utterance`, ou des mots transcrits par Gemini, `inputTranscription`, puis 700 ms de silence,
  pas encore de voix ; 6 s au plus). Un bruit bref ne compte pas ; la transcription seule ne suffisait pas (elle arrive
  tard, ou pas avant la réponse : la lueur ne s'allumait plus), `Speaking` (la lecture a du son en attente). Sans annulation d'écho,
  pendant que la voix joue (et 300 ms après) le micro envoie du silence sauf au-dessus de `TalkOver` (couper la parole).
  Transcriptions dans la conversation (`AssistantConversation.HearUser` / `HearModel` / `EndVoiceTurn`, `Fail` pour les
  erreurs) : la conversation continue à l'écrit ensuite. Minuterie 50 ms seulement pendant la voix (états, niveaux).
- **Mascotte** : `ActOut` en voix : `Speaking` → `Talk`, `Thinking` → `Think` (la lueur), sinon `Listen` ;
  `MascotView.Voice` (rebond `MascotRig.Speak` proportionnel au volume, 120 ms au plus) et `Hear` (hochement à la voix de
  l'utilisateur, 220 ms au plus).
- **Arrêt** : seulement la scène de la mascotte (`AssistantPanel.TalkRequested` : la mascotte ou la
  pastille « Parler » / « Arrêter » dessous) ou Échap (la voix d'abord, la barre ensuite). La barre qui se ferme, le
  clavier perdu, un autre onglet ne l'arrêtent plus : la conversation continue notch fermée. Pendant ce temps le module
  se dit `NotchPresence.Active` (la pilule reste, pas de poignée ni de nid), `ActOut` garde la mascotte de la notch en
  orbe, `NotchContext.VoiceMascot` lui passe les niveaux (`Buddy.Hear` / `Voice`), et elle ne s'endort pas
  (`MascotView` : pas de sommeil tant qu'elle joue l'assistant, `_orbed`). L'un ou l'autre : pendant la voix, la
  question écrite est coupée (`IsReadOnly`, estompée ; elle garde le clavier pour Échap), Entrée et le collage ne font
  rien. Écrasée dans la poignée (`p.Flat`), l'orbe s'efface : la ligne reste de la couleur de la mascotte.
- **Sourire** : au repos (écoute, réflexion), l'orbe sourit : un arc, le bas d'une ellipse tracé avec `TrimStart` 0,31 /
  `TrimEnd` 0,69 (le chemin d'une ellipse de composition part du haut, dans le sens des aiguilles d'une montre), bouts
  ronds (`MascotRig.CreateSmile`). En parlant, il s'efface quand la bouche s'ouvre (`p.Mouth`) et revient entre les
  syllabes.
- **Volume de sa voix** (`AppSettings.AssistantVoiceVolume`, 0 à 100, retenu) : appliqué aux échantillons dans
  `Speaker.Loudness` (le niveau pour la mascotte est mesuré avant : elle parle autant, voix basse ou non), changé à chaud
  (`AssistantVoice` suit le réglage). Réglé par un `MixerSlider` dans la barre pendant la voix (`AssistantView.VoiceVolume`,
  icône selon le niveau), la molette sur la scène de la mascotte (`AssistantPanel.VolumeWheel`, 5 %), et une carte des
  paramètres (Assistant).
- **Parole expressive** : une bouche dans l'orbe (`MascotRig.CreateMouth` : une capsule centrée sous les yeux, un
  petit trait fermée, qui s'ouvre depuis son milieu avec `p.Mouth`, de forme changeante à chaque syllabe, `p.MouthShape`
  tirée au hasard de « o » rond et haut à « a » large et bas ; visible seulement pendant `Talk`, `p.Talking`, ressort `MouthMove` 0,13 / 0,88 sans tremblement), ouverte par
  `Say(niveau)` à chaque mesure de la voix (`MascotView.Voice`, 20 fois par seconde : racine carrée du niveau, puis
  lissée, montée rapide 0,6, descente douce 0,28), les yeux un peu plissés avec elle ; petits sautillements `Speak` sur les
  syllabes fortes (180 ms au plus) ; `Talk` (réponse écrite) ouvre la bouche un instant (`Kick`). Pas
  d'étirement du corps lié à la bouche : les expressions du corps sont recopiées partout, et Windows refuse une
  expression trop longue (« The expression string is too long » : vu par le test, l'appli aurait planté).
- **Scène de la goutte** : `Stage` 150 × 176, posée par la mise en page à sa place de repos (`PlaceStage` à chaque
  `SizeChanged`, `a.Laid`), le compositeur ne fait que l'écart pendant le ressort de la goutte
  (`AssistantGlass.StageTranslation`) : ses clics tombent juste. Mascotte ×1,22 tant qu'il n'y a pas de conversation
  (`a.Greet`, `FaceScale`, la lueur derrière aussi). Goutte de 190 px au moins (la scène y tient).
- À vérifier : premier essai réel (le modèle choisi, la voix, la latence), l'écho sans casque, le « ducking » de Windows
  (un flux de communication peut baisser les autres sons, réglage Son → Communications).

### Annonces au repos
Une annonce (« Lien copié »…) alors que rien ne se passe (la notch au repos, en nid ou en poignée) sort du nid avec la
mascotte (`announcedFromNest` dans `MorphTo` : forme `NestShape` + l'extension de l'annonce) : juste la mascotte et le
texte, au lieu de toute la pilule et de son vide. Une activité en direct seule (le Pomodoro, un envoi : aucun
module n'a rien en cours, `activityAlone`) sort du nid de la même façon, et y reste. Sans la mascotte, ou quand quelque chose se passe (musique), l'annonce
allonge la pilule comme avant.

### Paramètres en onglets
`SettingsWindow` : un `NavigationView` à gauche (220 px, sans bouton de repli ni « Paramètres » intégré) ; chaque
section est un `StackPanel` (`GeneralSection`, `HandleSection`, `SearchSection`, `LyricsSection`, `ClipboardSection`,
`DropZoneSection`, `AssistantSection`) avec son titre, une seule visible (`OnSectionChanged`, remonte en haut). Les
cartes, liaisons et noms sont restés tels quels (29 cartes, 59 `x:Bind`). Fenêtre 900 × 780. `Loc.Apply` parcourt
maintenant les entrées d'un `NavigationView` et l'en-tête d'un `Expander`. Assistant : `KeyGuide`, un `Expander` en 5
étapes numérotées, ouvert tant qu'il n'y a pas de clé (`ShowKeyState`) ; « Ouvrir » lance aistudio.google.com/apikey,
« Coller et enregistrer » lit le presse-papier seulement à ce clic, refuse un texte avec espaces ou retours, puis fait
comme « Enregistrer » (vérifiée auprès de Google avant d'être gardée).

### « Afficher le bureau » — la notch reste
Windows + D, ou trois doigts vers le bas sur un pavé tactile, réduit ou masque toutes les fenêtres : la notch emportée
aussi (vu). `NotchWindow.OnWindowMessage` : `WM_SYSCOMMAND` `SC_MINIMIZE` refusé ; `WM_WINDOWPOSCHANGING` (`KeepShown`) ôte
`SWP_HIDEWINDOW` et le rangement en (-32000, -32000) d'une fenêtre réduite ; `WM_SIZE` `SIZE_MINIMIZED` → réaffichée aussitôt
(`SW_SHOWNOACTIVATE`, au premier plan, sans focus). Seulement quand elle est censée se voir (`_meantShown`) et pas quand elle
se cache elle-même (`_hiding` : plein écran, satellite rangé). Non vérifié avec le vrai geste (pas de touches injectées).

### Plein écran et raccourcis
`NotchWindow.Summon` (appelée par `NotchView.Summoned` quand un raccourci ouvre un module) remet la fenêtre tout en
haut (`SetWindowPos(HWND_TOPMOST)`), comme `ShowOn` pour la recherche sur un autre écran : une app plein écran est
souvent « toujours au premier plan » elle aussi, et celle montrée en dernier restait devant (la notch s'ouvrait,
invisible derrière). Limites connues : un jeu en plein écran exclusif ne laisse rien s'afficher par-dessus, et une app
lancée en administrateur ne laisse pas un crochet clavier ordinaire voir ses touches (UIPI).
**Les raccourcis s'effacent en plein écran** (nouveau, jamais vu à l'écran) : `GlobalShortcut.Take` laisse passer la
touche (ni prise, ni action) quand `FullscreenWatcher.ForegroundIsFullscreen()` : l'app au premier plan remplit son
propre écran (`MonitorFromWindow` + `GetMonitorInfo`, n'importe lequel, pas seulement celui de la notch ; mêmes règles
que la notch cachée : fenêtre maximisée avec barre de titre, bureau, barre des tâches et Ilot exclus). Lu seulement
quand la combinaison correspond (rien sur les autres touches). L'enregistrement d'un raccourci n'est pas concerné.

### Assistant — nouveau, jamais vu à l'écran
`Modules/Assistant/`. Module **sans onglet** (comme la recherche : `AssistantModule.Bar` 660 × 60), ouvert par un clic
sur la mascotte (`MascotView.Clicked` → `INotchModule.OpenFromMascot`) ou `AssistantShortcut` (Ctrl + Maj + Espace,
`AppSettings.AssistantShortcut` / `AssistantShortcutOn`, enregistrable comme celui du presse-papier : les deux sont des
`Windowing/GlobalShortcut`).
- **Barre** (`AssistantView`) : prend le clavier comme la recherche ; Entrée envoie (`AssistantConversation.Ask`),
  Maj + Entrée va à la ligne (`AcceptsReturn`, défilement au-delà de 4 lignes pour tenir dans la notch ouverte de
  158 px), Échap efface puis ferme. La barre grandit avec ses lignes : `AssistantView.Shape` (lue par
  `AssistantModule.OpenShape`) suit `LayoutRoot.SizeChanged`, puis `NotchContext.Reshape` → `NotchView.Reshape` (la notch
  y ressaute ; `MoveBubble` replace la goutte sans toucher à son ressort de taille). Le contenu de la goutte suit
  `n.BubbleRest` (où son haut repose maintenant) au lieu de la position qu'il avait à son arrivée.
- **Goutte** (`AssistantPanel`) : montrée dès l'ouverture (l'accueil avant la première question), 150 à 470 px,
  redessinée au plus toutes les 60 ms pendant le flux (seul le dernier message est refait). Le verre
  (`AssistantGlass.Create`) : fond bleu profond, trois lueurs radiales qui dérivent avec `n.GlowTurn` (donc à l'arrêt
  sans l'assistant), un reflet en haut, un liseré clair et la lueur tournante de la notch le long du bord (la goutte
  de la notch est recouverte par le contenu, d'où ce double). Tout suit `n.BubbleHeight` (le ressort de la goutte),
  pas la mise en page qui saute d'un coup à la nouvelle taille. La mascotte en grand (`Face`, un second `MascotView`,
  108 px) sur une scène à droite (`AssistantGlass.StageTranslation` : au milieu de la goutte, flottant pendant la
  réflexion, `a.Thinking`) ; `Present` la réveille ou l'arrête entièrement (l'orbe aussi). Pendant ce temps la mascotte
  quitte sa petite notch (`INotchModule.HostsMascot`, `n.Away`), qui reste (même mascotte désactivée) et tient
  « Retour aux onglets » (flèche, `NotchView.BindPodBack` / `OnPodBackClick` : retour au dernier onglet, notch ouverte ;
  l'assistant lâche sa goutte et le clavier sur `SelectionChanged` ; région : `PodShown`). Jamais vu à l'écran. `MascotView.Watch` (regard le long de la question, hochement `MascotRig.Nod`) et `Ponder`
  (pendant `Think`, le regard va d'un côté à l'autre toutes les 1,15 s, minuterie seulement pendant ce temps). Points
  d'attente qui rebondissent : `AssistantGlass.DotTranslation` / `DotOpacity`.
- **Couleur** : tout ce qui brille pour l'assistant suit la couleur de la mascotte, `MascotColor.Glow` / `GlowBright` /
  `GlowDeep` / `Light(l)` (même teinte en HSL, saturation relevée ; une couleur presque sans teinte, blanc, gris, noir,
  donne un argenté bleuté, jamais noir). `NotchSurface.Tint`, `AssistantGlass.Tint` (instance : ses arrêts de dégradé
  sont gardés pour la retéinte) et l'orbe (`MascotRig.SetColor` refait aussi `_orbStops`) fondent vers la nouvelle couleur.
- **Lueur douce** (`Notch/NotchGlow`) : pas de flou en composition sans Win2D, donc 8 traits l'un dans l'autre (retrait
  0,75 → 15,5 px, épaisseur croissante, force 0,6 → 0,01) qui se lisent comme un dégradé ; une seule brosse tournante
  (`Offset` au centre, rotation `n.GlowTurn`) : clair, couleur, presque rien, couleur, clair. Utilisée par la notch
  (`CreateGlow`) et la goutte (`AssistantGlass`, qui recouvre celle de la notch).
- **Goutte sobre** : le noir de la notch (rien dessous), seulement la lueur de la mascotte derrière elle (toujours là,
  plus forte et qui respire pendant la réflexion) et la lueur douce des bords. Tout suit `n.BubbleWidth` /
  `n.BubbleHeight`.
- **Lueur seulement pendant la réflexion** : `NotchView.ActMascot` met `n.Glow` à 1 pour `Think`, 0 sinon ; une fois le
  fondu fini (`_glowTimer`), `Shimmer(false)` arrête `GlowTurn` / `GlowPulse` : rien ne tourne hors réflexion.
- **Redimensionnable** : `ShowBubble(_panel, taille, AssistantPanel.Smallest)` (480 × 150, coins du bas) ; `Resized`
  retient la taille (`AppSettings.AssistantChatWidth` / `Height`, 0 = jamais redimensionnée : la goutte suit alors la
  conversation, 150 à 470 px). Une fois choisie, la taille reste, la conversation défile dedans ; le panneau n'a plus de
  largeur fixe (`PanelWidth` pour mesurer).
- **Conversation** : pas de barre de défilement (`Hidden`, la molette marche), fondus noirs en haut et en bas quand il y
  a plus au-delà (`UpdateFades`), eux-mêmes estompés sur leur dernier quart à droite (`CreateFade` : un `CompositionMaskBrush`,
  dégradé vertical masqué par un horizontal) : la lueur de la mascotte passe sous la conversation, un fondu noir plein
  y faisait une bande à bord net. Lueur resserrée (250 px, presque rien au-delà des trois quarts de son rayon).
- **Orbe** : sans rubans (rien ne tourne dedans) : `MascotRig.Orb(bool)`.
- **Regard** (la mascotte de la goutte) : elle suit le pointeur partout sur l'écran (`AssistantPanel.FollowPointer` :
  `GetCursorPos` toutes les 40 ms, minuterie seulement pendant que la goutte est là, rien si le pointeur n'a pas bougé ;
  `ScreenToClient`, puis l'échelle de la fenêtre) ; une touche tapée ramène ses yeux sur le curseur de texte
  (`AssistantView.WatchCaret` : `GetRectFromCharacterIndex`), jusqu'au prochain mouvement de la souris. `ToFace` corrige
  la place de la scène, que le compositeur déplace (`StageTop`) sans que la mise en page le sache. `MascotView.Follow`
  ne fait rien pendant la réflexion (elle réfléchit, `Ponder`).
- **Images** (`ChatImage`) : `TextBox.Paste` dans la barre ; si le presse-papier a une image (`Bitmap`) ou des fichiers
  images, et pas de texte (Word et Excel joignent une image de leur texte), elles sont jointes, 4 au plus : décodées,
  réduites à 1600 px de côté, en PNG (en JPEG 0,88 au-delà de 2,5 Mo). Vignettes 40 px sous la question (×, sans prendre
  le focus), la question limitée à 2 lignes pendant ce temps pour que la barre tienne. Envoyées en `inlineData` base64
  avec le texte (`GeminiTurn.Images`, `GeminiClient.Parts`), gardées dans l'historique ; montrées au-dessus de la
  question dans la goutte (92 px de haut).
- **Historique** (`AssistantHistory`, `AssistantHistoryView`) : `%LOCALAPPDATA%Ilotassistant-history.json`, les 100
  dernières, la plus récente d'abord ; écrit 800 ms après le dernier changement, hors du fil UI, fichier temporaire puis
  remplacé d'un coup ; un fichier illisible donne un historique vide. `AssistantConversation.Remember` garde la
  conversation (son `Guid`, son titre = la première question, ses messages écrits, sans erreurs, le nombre d'images mais
  pas les images) après chaque réponse, écrite ou vocale (`EndVoiceTurn`). « Nouvelle conversation » en commence une
  autre (nouveau `Guid`) ; `Open` en rouvre une (son `Guid` : la suite la met à jour). Dans la goutte : bouton
  « Historique », la liste à la place de la conversation (`AssistantPanel.ShowHistory`, la goutte s'y ajuste,
  `LayoutChanged`), retour par la flèche ou Échap, corbeille au survol, « Tout effacer » demandant confirmation (3 s).
  Testé sur un fichier de test (jamais celui de l'utilisateur : `AssistantHistory(dispatcher, path)`).
- **Lumière de la goutte** : la lueur derrière la mascotte, 300 px, s'estompe doucement ; une boule de lueur (230 px, sa
  couleur, faible) se promène en bas à gauche (`AssistantGlass.Wandering`, deux rondes mêlées sur `a.Wander`, une boucle
  de 18 s lancée seulement pendant que la goutte est là, `AssistantPanel.Wander`).
- **Conversation** (`AssistantConversation`) : gardée dans l'historique (voir ci-dessus), jusqu'à « Nouvelle conversation » ; un état
  (`Thinking`, `Talking`, `Done`, `Failed`) que la barre traduit en `NotchContext.ActMascot`.
- **Gemini** (`GeminiClient`) : `HttpClient` seul ; `models/{modèle}:streamGenerateContent?alt=sse` (la clé dans
  l'en-tête `x-goog-api-key`, jamais dans l'adresse), lignes `data:` lues une à une (les « pensées » du modèle
  ignorées), erreurs traduites en messages (clé refusée, quota, modèle absent, réseau). Modèle « Automatique » :
  `GeminiClient.Candidates` (les « flash » ordinaires du plus récent au plus ancien, puis les lite, les preview et alias,
  le reste ; les spécialisés exclus), essayés dans l'ordre : un modèle qui répond 429 ou 404 avant le premier mot
  (`GeminiException.ModelUnusable`) cède la place au suivant, et celui qui a répondu passe en tête. Une clé neuve n'a
  pas de quota gratuit pour tous les modèles (les plus récents souvent pas) : un seul modèle choisi d'office donnait
  « quota atteint » dès la première question.
  Vitesse : le modèle qui a répondu est retenu d'un lancement à l'autre (`AppSettings.AssistantAnswered`) et interrogé
  d'emblée, avant même la liste ; la liste se fait à l'ouverture de la barre (`AssistantConversation.Prepare`, qui ouvre
  aussi la connexion) ; un modèle refusé est mis de côté (`_resting` : pour la session si Google dit « limit: 0 » ou
  404, `GeminiException.Lasting` ; 2 min sinon) au lieu d'être réessayé à chaque question. Et la réflexion au minimum
  (`GeminiClient.QuickThinking` : `thinkingBudget` 0 pour les 2.5 Flash, 128 pour Pro, `thinkingLevel` « minimal » /
  « low » pour les 3) : par défaut ces modèles réfléchissent plusieurs secondes avant le premier mot. Un modèle qui
  refuse ce réglage (400, `Malformed`) répond sans lui, et n'est plus réglé ensuite (`_thinksItsWay`).
  Surcharge : 500 / 503 / 504 (« This model is currently experiencing high demand », parfois pendant longtemps, y compris
  en cours de flux) passent aussi au modèle suivant (`GeminiClient.Refusal`), 2 min de côté. Un modèle choisi dans les
  paramètres reste le premier, mais s'il ne peut pas répondre (surcharge, quota), les autres répondent à sa place.
- **Clé** (`ApiKeyStore`) : Gestionnaire d'identification de Windows (`CredWrite` / `CredRead`, cible
  `MyNotch/Gemini`), vérifiée auprès de Google avant d'être gardée (paramètres → Assistant).
- **Mascotte** (`MascotView.Act`, pour les deux : celle de la notch, cachée, et celle de la goutte) : `Listen` (regarde
  la question), `Think` (orbe, yeux levés d'un côté puis de l'autre, `Ponder`), `Talk` (petit rebond,
  150 ms au plus), `Done` (clin d'œil, `Happy`), `Fail` (`Sad` 2,5 s) ; le regard ne suit plus le pointeur pendant ce temps.
- **Orbe** (`MascotRig.CreateOrb` / `Orb`, humeur `Orb`) : calque au-dessus du corps, sa géométrie (il suit les rebonds),
  dégradé radial sombre → bleu → cyan, trois rubans (ellipses rognées, `TrimEnd`) qui tournent avec `p.Spin` (tours
  entiers, donc sans saut ; la boucle repart de `this.StartingValue`, un changement de vitesse ne saute pas), halo,
  yeux clairs. `MascotView.Act` : orbe dès qu'un acte n'est pas `Rest` (écoute 3,4 s par tour, réflexion 1,3 s,
  parole 2,2 s ; plus de points en orbite ni d'onde de musique pendant ce temps).
- **Lueur** (`NotchSurface.CreateGlow` / `Shimmer`, `n.Glow`, `n.GlowTurn`, `n.GlowPulse`) : trois traits à l'intérieur des
  bords de la notch et de la goutte (`GlowLines` : retrait, épaisseur, force), un dégradé cyan / bleu / violet en
  coordonnées absolues tourné autour du centre de la notch ; `NotchView.ActMascot` règle la force (réflexion 1, écoute
  et parole 0,75, échec 0,45, repos 0) et ne fait tourner les boucles que quand elle est là.
- **Mise en forme** (`MarkdownLite`) : titres, listes, blocs de code, gras / italique / `code` dans un `RichTextBlock`.
- Testé sans interface : choix du modèle, lecture d'un évènement du flux, vraie requête avec une fausse clé (message
  « clé refusée »), aller-retour dans le Gestionnaire d'identification (sur une entrée de test), animations du rig.

### Recherche — nouveau, jamais vue à l'écran
`Modules/Search/`. Module **sans onglet** (`HasTab = false`, forme ouverte propre `SearchModule.Bar` 660 × 60),
ouvert par `SearchShortcut` (`Ctrl + Q`, hook clavier bas niveau) via `NotchContext.Show()`. La barre est
`SearchView` ; les résultats tombent dans une bulle (`ShowBubble`, puis `NotchBubble.Resize`) qui contient
`SearchResultsPanel` (lignes recyclées `SearchResultRow`, surlignage à ressort `Motion.Select`, arrivée en cascade).
- **Sources** (`SearchViewModel`) : calcul (`Calculator`), applis (`AppCatalog`, `shell:AppsFolder` en COM sur STA),
  projets VS Code (`CodeService`), paramètres et commandes (`SearchCatalog`), fichiers (`EverythingEngine`), web.
- **Classement** : `FuzzyMatch` (début, mot, initiales, lettres dans l'ordre ; sans casse ni accents) + bonus de type
  + `SearchHistory` (search.json) + pour les fichiers : emplacement, fraîcheur, compteur d'Everything.
- **Everything** (`Everything/`) : copie portable 1.4.1.1032 x64 dans `Engine/` (copiée à côté de l'exe), lancée
  par `EverythingEngine.Start()` au démarrage de l'appli (`-startup -config -db`, ini écrit par la notch : pas
  d'icône, pas de fenêtre). Requêtes par `EverythingClient` (WM_COPYDATA, `EVERYTHING_IPC_QUERY2`, tri par date de
  modification, 150 résultats, exclusions ajoutées en `!"…"`). Un Everything déjà lancé par l'utilisateur est utilisé
  (et jamais fermé). Mode pleine vitesse = service Everything (déjà installé chez le propriétaire).
- **Ouverture** : `ShellExecute` (applis via `shell:AppsFolder\…`, fichiers, `ms-settings:`), `explorer /select,`,
  `runas`, commandes (`LockWorkStation`, `SetSuspendState`, `shutdown.exe`, `SHEmptyRecycleBin`). Après un lancement,
  `HandOffKeyboard` (sinon le retour du clavier à l'appli d'avant passerait par-dessus ce qui s'ouvre).
- **Clic droit** (`SearchResultsPanel.CreateMenu`) : un `MenuFlyout` rempli à l'ouverture selon le résultat ; les
  commandes de l'Explorateur passent par `ShellMenu` (le vrai `IContextMenu` de l'élément : `SHParseDisplayName`,
  `SHBindToParent`, `GetUIObjectOf`) : verbes `copy`, `delete`, `properties` lancés par leur numéro de commande
  (`Run` ne lance qu'un verbe présent dans le menu), « Ouvrir avec » par `SHOpenWithDialog`, et « Afficher plus
  d'options » qui affiche le menu complet (`TrackPopupMenuEx` sur la fenêtre de la notch, dont `OnWindowMessage` passe
  les messages des sous-menus dessinés par le shell à `ShellMenu.HandleMessage`). Testé sans interface : menus d'un
  fichier (26 entrées), d'un dossier (28), d'une appli du Store (5), verbe inconnu refusé, `properties` lancé.
- **Réglages** : `AppSettings.Search*` (sources, nombre de résultats, dossiers ignorés), section « Recherche » des
  paramètres avec l'état d'Everything et « Pleine vitesse ».

---

## 6. À vérifier en conditions réelles

Ces fonctions compilent et ont été raisonnées, mais n'ont pas (ou peu) été vues tourner :

1. **Redimensionnement de la bulle des notes** (dernier ajout) : poignées, curseur, limites, mémorisation de la
   taille, région de la fenêtre pendant le geste.
2. **Animation « goutte »** (col, congés, rebond) : réglages dans `Motion.Drip` / `Motion.Absorb` et
   `NotchSurface` (constantes `BubbleWidth`, `Neck`, `Fillet`…).
3. **Éditeur riche** : détection des styles dans la barre (comparaison exacte des tailles en points), listes
   natives RichEdit (retraits), cases à cocher, conversion des anciennes notes Markdown.
4. **Molette de la souris sur les rangées** (`RowScroller`, écoute avec `handledEventsToo`) : les flèches ‹ ›
   marchent dans tous les cas ; la molette reste à confirmer.
5. **mDNS et Accepter / Refuser** depuis de vrais téléphones (Android récent, iPhone).
6. **Largeur des onglets** : mesurée depuis le texte (`TextBlock.Measure` avant l'arbre visuel).
7. **Chat Wi-Fi local** : flux `/chat/events` (attente longue) sur un vrai téléphone, collage `Ctrl + V`
   (fichiers, image, lien), repli vers l'IP quand le routeur filtre le mDNS, ré-annonce mDNS toutes les 30 s.
8. **Acceptation d'un fichier analysé par Defender** (`.exe`…) : repli par copie quand `File.Move` est bloqué.
9. **Pilule qui s'efface sous la souris** (`NotchLayout.Handle`, réglée par `AppSettings.HandleWidth` / `HandleReach`, `n.Mini`, `NotchSurface.CreateGrabber`) :
   la pilule rentre dans la poignée sous la souris et laisse passer les clics (`NotchView.SetPeeking`, crochet
   souris `Windowing/PointerWatcher.cs` le temps du survol) ; seul l'emplacement de la poignée ouvre la notch ;
   la région suit la notch qui rétrécit (fermeture, pilule qui s'efface) : `NotchView.Track` s'abonne à
   `CompositionTarget.Rendering` le temps du ressort et recalcule la région à chaque image d'après les valeurs
   analytiques des ressorts (`SpringChannel.ValueAgo`, 34 ms en arrière pour couvrir le retard du compositeur ;
   `NotchLayout.InputBoundsOf`), jamais moins que la région finale. Pas pendant une carte présentée ni pendant qu'une
   bulle rentre (l'ancienne règle s'applique : la région rétrécit à la fin). Le double-clic sur la pilule qui s'efface
   ne verrouille plus la notch. (Essai abandonné : un crochet souris qui rétrécit la région au moment du clic ne
   suffisait pas, le premier clic restait à la notch.)
10. **Taille de la bulle du chat** (`LocalChatWidth` / `LocalChatHeight`) : retenue pendant le geste, relue au
   relancement.
11. **Recherche (dernier ajout, jamais vue à l'écran)** : ouverture en barre au `Ctrl + Q` (morph, onglets qui
   s'effacent, loupe), goutte de résultats et `NotchBubble.Resize`, surlignage qui glisse, arrivée des lignes en
   cascade, icônes (`ShellIcons` : alpha prémultiplié ou non), lancement qui garde le premier plan (`HandOffKeyboard`),
   retour au dernier onglet au survol suivant. Testé sans interface : `FuzzyMatch`, `Calculator`, `AppCatalog`
   (192 applis en 0,9 s), `EverythingClient` contre un vrai Everything (70 à 100 ms par requête, 2 disques).
   Le crochet `Ctrl + Q` ignore les touches injectées : il ne se teste pas par `SendInput`. Flow Launcher utilise
   aussi `Ctrl + Q` chez le propriétaire : le hook passe avant, mais mieux vaut le désactiver dans Flow.
12. **Everything sans son service** (non testé) : « Pleine vitesse » (copie dans Program Files + `-install-service`,
   une invite UAC) et le mode « dossier personnel » (`folders=` dans l'ini) ; chez le propriétaire, le service existe.
13. **Plusieurs écrans** (non testé) : la notch reste sur l'écran principal. `SearchRouter` (Ctrl + Q) ouvre la
   recherche dans la notch, ou sur un autre écran dans une fenêtre satellite (`NotchWindow(satellite: true)` + un
   `NotchView` qui n'a que le module de recherche : `ShowOn` / `Withdraw` sur `SearchView.Ended`) ;
   `AppSettings.SearchScreen` ; écrans d'échelles différentes (`WM_DPICHANGED` → `PlaceOn`).
14. **Notch en poignée au repos** (non testé) : `NotchView.UpdateIdle` / `SetIdle` (`_idle` : forme `NotchLayout.Handle`,
   `n.Mini`), `AppSettings.HideWhenIdle` / `IdleSeconds` (5 à 300 s, par 5 ; l'ancien `IdleMinutes` d'un settings.json est converti), présence du lecteur
   (`MediaView.ShowPresence` : en lecture `Active`, en pause `Idle`, sans morceau `Nothing`) ; en poignée dès le démarrage tant que rien ne joue.
15. **Anglais** (non vu à l'écran) : textes plus longs ou plus courts (largeur des onglets, boutons des paramètres),
   page du téléphone en anglais, recherche des paramètres Windows par leur nom anglais (titre traduit + mots français).
16. **Mascotte** (jamais vue à l'écran) : proportions et humeurs (`MascotPose.For`), lisibilité à la taille de la
   pilule, yeux coupés au bord du corps quand elle regarde loin, rebond du saut (`Jump`, `Give`), glissement
   pilule ↔ haut de la notch ouverte, nid au repos, danse sur les basses (amplitude `Beat`), message après elle.
17. **Assistant** (jamais vu) : le flux de Gemini avec une vraie clé, la goutte qui grandit pendant l'écriture, le
   défilement en bas, le raccourci Ctrl + Maj + Espace, le repli sur un autre modèle quand l'un n'a pas de quota.
   L'orbe (rubans lisibles à la taille de la pastille ? yeux bien placés ?) et la lueur des bords (force, vitesse de
   rotation des couleurs, rendu sur la goutte qui grandit) ; le retour à la mascotte normale quand la barre se ferme.
   La goutte en verre (couleurs, lisibilité du texte sur les lueurs), la grande mascotte (taille, place au milieu de
   la goutte qui grandit, flottement), la petite notch qui s'efface, la barre sur plusieurs lignes (alignement de
   l'icône et de « Entrée » sur la première ligne, la goutte qui descend avec elle). Les couleurs dérivées de la
   mascotte (lavande assez vive ?), la lueur douce des bords (seulement pendant la réflexion), le redimensionnement de
   la goutte, les fondus de la conversation ; le collage d'images (capture,
   fichiers, rendu des vignettes, réponse de Gemini sur une image).
18. **Notch engagée après un clic** (jamais vu à l'écran) : un appui dans la notch ouverte (`NotchView.OnPressed`,
   ou le clic qui l'ouvre, `OnTapped`) appelle `NotchController.Engage` : la sortie de la souris ne la ferme plus
   (`CollapseUnlessPinned` / `OnCollapseDelayElapsed` l'ignorent). `NotchWindow` écoute alors le premier clic hors
   d'elle avec `Windowing/ClickWatcher.cs` (crochet souris bas niveau, posé seulement pendant l'engagement, qui ne
   bloque rien) : `WindowFromPoint` sous le clic, la fenêtre de la notch (sa région, vérifiée en lecture seule : un point
   hors région renvoie l'appli de dessous), ses menus (`GA_ROOTOWNER`, ou une fenêtre « Popup » / `#32768` du
   processus) comptent comme dedans. Dehors : `Disengage` la ferme tout de suite (sauf épinglée ; si quelque chose la
   tient, à sa libération). Toute fermeture (`Dismiss` d'un module, plein écran via `Reset`, épinglage) met fin à
   l'engagement et retire le crochet. Pas d'Échap global (aucun crochet clavier) : Échap reste aux champs qui ont le
   clavier. À vérifier : clics dans les menus clic droit et les listes déroulantes (classe de fenêtre des popups
   WinUI non vue), toucher, bulle de la feuille, la fenêtre détachée (dehors : la notch se ferme). Option
   `AppSettings.StayOpenAfterClick` (par défaut oui).

---

## 7. Pièges déjà rencontrés

- **Recherche de Notes et du Presse-papier sur les onglets** : leur case (`NotchSearchBox`, en haut à droite) s'étire
  vers la gauche ; avec six onglets, elle recouvrait « Pomodoro ». Ouverte, elle demande `NotchContext.HideTabs(true)` :
  la barre d'onglets s'efface (`NotchView.TabsShown`, plus cliquable), et revient à sa fermeture (loupe, Échap, onglet
  quitté ou notch fermée : les deux vues la vident alors).

- **Deux bulles sous la poignée au survol** : les petites notches cachées dans les bouts de la pilule fermée (réglages
  rapides, mascotte) gardaient 36 px pendant que la pilule s'effaçait vers sa poignée, et dépassaient dessous en
  s'estompant. Fermées, elles ne sont plus plus hautes que la notch du moment (`NotchSurface.ClosedPod` : `Min(cw, n.Height)`).

- **Booster de son natif** (`Quick/SoundBooster.cs`, `Quick/BoostStream.cs`, jamais vu à l'écran ; Equalizer APO
  abandonné : l'utilisateur ne veut rien à installer). Par appli, jusqu'à 200 % (`SoundBooster.Most`), Windows 11
  (build ≥ 20348 : la capture d'un processus, `WasapiRecorderBuilder.WithProcessLoopback`, NAudio 3.3).
  - **Le principe, mesuré** (sondes du scratchpad `loopbackprobe`) : la capture d'un processus se fait **après** le volume
    de sa session (coupée ou à 0 : plus rien). Mais à 0,1 % (`BoostStream.Floor`), elle est exacte : 148 dB de rapport
    signal/bruit (le moteur mixe en flottants). Donc : la session de l'appli à 0,1 % (−60 dB, ce qui reste ne s'entend
    pas), Ilot capture son arbre de processus et rejoue ×(niveau / 0,001), limiteur (crête < −0,3 dBFS, relâche 200 ms),
    dans sa propre session (`SetDuckingPreference(true)` : pas baissée pendant un appel). Mesuré (`streamtest`) : gain
    exact (×1501 pour ×1500), 0,11 % de CPU, ~65 à 85 ms de décalage (capture ~10 ms, réserve ~10 ms, lecteur
    IAudioClient3 ~22 ms, le reste dans Windows), aucune coupure (`Starved`/`Dropped` à 0).
  - **Sûreté** : silencieux tant que les sessions ne sont pas baissées (`Settle` : 60 ms après, sur l'horloge des
    paquets) ; un paquet plus fort que 2 × 0,001 = une session remontée derrière son dos : silence aussitôt (`Tripped`),
    puis une passe. Les volumes d'origine sont notés sur disque **avant** d'être baissés (`boost-held.json` : Windows
    retient le volume d'une appli, une appli boostée reviendrait muette après un plantage) ; rendus en quittant
    (`Dispose`, passe finale), à l'extinction, ou au lancement suivant (`Start`).
  - **Les passes** (`Pass`, un fil à elles, regroupées) : toutes les sessions de toutes les sorties ; les processus
    joués = ceux des sessions des applis boostées, sauf un dans l'arbre d'un autre (parents : `NativeMethods.ParentProcesses`,
    un instantané Toolhelp, aucun processus ouvert) ; 1. les flux en trop arrêtés d'abord, 2. les voulus démarrés,
    3. les sessions de leurs arbres baissées (notées avant), 4. les sessions notées et plus baissées remontées (le niveau
    demandé ≤ 100 %, sinon 100 % pour une appli boostée, sinon leur volume noté), 5. les sessions baissées suivies
    (`IAudioSessionEvents` : bougées ailleurs → fin du boost à ce volume ; expirées → passe), 6. les nouvelles sessions
    et les sorties surveillées (`OnSessionCreated`, `CreateNotificationClient`) tant que quelque chose est boosté ou noté.
    Sortie par défaut changée : les flux sont refaits (IAudioClient3 n'accepte pas le routage automatique).
    Rien de boosté ni de noté : aucune surveillance, rien ne tourne.
  - Réglages `AppSettings.BoostOn` (l'interrupteur) et `BoostLevels` (programme en minuscules → niveau > 1, gardés
    interrupteur éteint). `MixerApp.Key` = le programme en minuscules. Mélangeur : interrupteur en bas
    (`MixerPanel.ShowBooster`) ; allumé, `MixerSlider.Most` = 2 et un repère à 1 (aimanté à ±4 %), le pourcentage > 100
    dans la couleur de la mascotte ; un curseur passe par `SoundBooster.Set` au-delà de 100 % ou tant que
    `SoundBooster.Holds` (sinon directement le volume de Windows).
  - Testé en direct (scratchpad `boostertest`, 28 vérifications, sur ses propres processus enfants uniquement, bips à
    −120 dBFS, AppData factice) : boost, niveau changé en direct, nouvelle session vue, volume bougé ailleurs,
    interrupteur, sous 100 %, quitter, plantage (éteint et allumé), appli relancée.
- **Mises à jour automatiques** (`Updates/`, jamais vues à l'écran) : `AppUpdater` lit la dernière Release de
  GitHub (`api.github.com/repos/Xeiners/ilot/releases/latest`, sans clé : brouillons et pré-versions ignorés ; 404 = aucune
  Release, « à jour »), 30 s après le démarrage puis toutes les 6 h (une minuterie, rien entre deux), si
  `AppSettings.AutoUpdate` (Paramètres → Général) et seulement pour une copie **installée** (`unins000.exe` à côté :
  jamais `dotnet run`, qui dit seulement qu'une version est sortie). `UpdateRelease.Parse` (testé : scratchpad
  `updatetest`) : le tag `vX.Y.Z` comparé à la version compilée (`Directory.Build.props`, sur trois parties), l'installateur
  `Ilot-Setup-*.exe` téléchargé seulement depuis `https://github.com`. Téléchargé dans `%LOCALAPPDATA%\Ilot\Updates`
  (`.part`, puis vérifié : taille, et SHA-256 quand GitHub donne `digest`), il est proposé sur la notch fermée
  (`UpdateModule`, module sans onglet : `NotchContext.Ask` avec `UpdateCard`, « Plus tard » / « Installer ») et dans la
  carte des Paramètres. Installer : l'installateur en silencieux (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART
  /CLOSEAPPLICATIONS`, plus `/ALLUSERS` si Ilot est installé hors de `%LOCALAPPDATA%`), le mutex lâché (`Installing`),
  puis Ilot quitte ; `Ilot.iss` attend jusqu'à 10 s que le mutex disparaisse (`[Code] InitializeSetup`) et relance Ilot
  en silencieux (`[Run] … Check: WizardSilent`, `runasoriginaluser`). « Plus tard » : au prochain démarrage, une seule fois
  automatiquement (marque `.tried`) ; si ça n'a pas pris, elle est proposée au lieu d'être retentée à chaque lancement.
  **Publier une version** : changer `<Version>` dans `Directory.Build.props`, `installer\build.ps1`, puis une Release au
  tag `vX.Y.Z` avec le fichier `Ilot-Setup-X.Y.Z.exe` (nom exact), ni brouillon ni pré-version.
- **Licence** : MIT, Copyright (c) 2026 Xeiners (`LICENSE`, texte MIT standard en ASCII et CRLF : GitHub la reconnaît,
  et la page de licence de l'installateur, `LicenseFile`, l'affiche bien). Les composants tiers (tous MIT) sont dans
  `THIRD-PARTY-NOTICES.md` ; les deux fichiers sont copiés à côté d'`Ilot.exe` (`Ilot.csproj`). Éditeur « Xeiners » dans
  `Directory.Build.props` (propriétés de l'exécutable) et `installer/Ilot.iss` ; carte « À propos » dans Paramètres →
  Général (`SettingsWindow.AboutTitle`, la version compilée).
- **Logo et icône** : la source est `docs/logo.svg` (tuile noire, lueur blanche floutée, l'îlot noir et Notch dans sa
  pose neutre, aux proportions de `MascotRig`) ; `docs/logo.png` en est le rendu 512 px. `src/Ilot/Assets/Ilot.ico` en est
  tiré : 9 tailles de 16 à 256 px, en PNG (rendu du SVG à chaque taille, découpé, puis assemblé en .ico). Il sert à
  l'exécutable (`ApplicationIcon`), aux fenêtres (`AppWindow.SetIcon`) et à l'installateur (`SetupIconFile`).
- **Renommée MyNotch → Ilot** (25 octobre 2026) : projet `src/Ilot` (`Ilot.csproj`, `Ilot.sln`, `installer/Ilot.iss`,
  `Ilot.exe`), espaces de noms `Ilot.*`, mutex `LocalIlot.SingleInstance`. Les données passent toutes par
  `AppData.Folder` (`%LOCALAPPDATA%Ilot`) : au premier lancement, `%LOCALAPPDATA%MyNotch` y est déplacé d'un bloc
  (`Directory.Move`) ; en cas d'échec (MyNotch encore lancé), l'ancien dossier sert pour ce lancement et le suivant
  réessaie. L'entrée « Lancer avec Windows » `MyNotch` devient `Ilot` (`StartupLaunch.Migrate`). La clé Gemini garde
  son nom `MyNotch/Gemini` dans le coffre de Windows (rien à migrer). L'identifiant de l'installateur (`AppId`) ne
  change pas. Le dossier du dépôt reste `C:devmynotch`. Les tests hors interface ont leur propre `AppData` (dossier
  temporaire) : le vrai déplacerait les données de l'utilisateur.

- **« Quitter » qui laisse un processus** : `Application.Exit()` fermait la notch, mais une autre fenêtre de l'appli
  restée ouverte et cachée (les paroles de la barre des tâches, après `AppWindow.Hide`) gardait le processus en vie,
  invisible : il tenait l'instance unique et verrouillait `Ilot.exe` (`dotnet run` : MSB3027). `App.Quit` : à la
  fermeture de la notch, `Shutdown` (services libérés, donc enregistrés) puis `Environment.Exit(0)`.

- **Ouvert : texte d'une activité invisible sur la notch fermée** (préparation d'un cours, musique en cours). La notch
  s'élargit pour le texte, l'anneau et son icône s'affichent, le texte non. Rien de trouvé à la lecture (calques au-dessus,
  découpe, thème, couleur partagée). Trace temporaire `NotchView.TraceToast` : une ligne `[Toast]` dans `errors.log`
  une seconde après qu'une activité apparaît ou change (au plus toutes les 20 s) : tailles, position, couleur et pixels
  réellement dessinés par le texte (`ink`, via `RenderTargetBitmap`). À lire, puis retirer la trace.

- **Exceptions avalées** : `App.OnUnhandledException` marque tout comme géré (pour qu'un incident de session
  média ne tue pas la notch). Une erreur dans le constructeur d'une vue laissait donc un processus sans fenêtre,
  qui bloquait les lancements suivants. C'est corrigé (journal + fermeture), mais garder ce réflexe : lire
  `errors.log`.
- **RichEdit et les polices d'icônes** : un texte chargé avant que l'éditeur soit affiché (`IsLoaded` faux)
  dessine les glyphes Segoe Fluent en carrés vides ; `NotesView` attend `Editor.Loaded` pour charger la note.
- **RichEdit impose son curseur texte** (`ProtectedCursor` sur le `RichEditBox` est ignoré) : la main sur une case
  vient d'un bouton transparent posé sur un calque au-dessus (`BoxLayer`, `RichNoteEditor.UseBoxLayer`), placé
  d'après `GetRect` ; le repère des coordonnées « client » de RichEdit est déduit d'une case (`ClientSpace`).
- **`FrameworkElement.Parent` vaut `null`** dans le constructeur d'une vue, juste après `InitializeComponent` :
  passer le conteneur explicitement (cf. `new RowScroller(Scroller, LayoutRoot)`).
- **`Path` ambigu** entre `System.IO.Path` et `Microsoft.UI.Xaml.Shapes.Path` : `using Path = Microsoft.UI.Xaml.Shapes.Path;`.
- **`DispatcherQueuePriority`** ambigu (Microsoft.UI.Dispatching / Windows.System) : qualifier.
- **`ReadOnlyObservableCollection<T>.CollectionChanged`** est une implémentation explicite : passer par
  `((INotifyCollectionChanged)collection).CollectionChanged`.
- **RichEdit** :
  - tailles en **points** (pas en pixels XAML : 11 pt = 14,667 px) ;
  - la police variable « Segoe UI Variable » y est rendue trop grasse : utiliser « Segoe UI » ;
  - `GetText` renvoie `\r` comme fin de paragraphe, plus un `\r` final ;
  - `TextChanged` peut arriver après coup (d'où le compteur `_loading` décrémenté en priorité basse).
- **winsqlite3** : `LibraryImport` sans convention d'appel explicite (stdcall sur x86, c'est ce qu'il faut).
- **Région de fenêtre = rognage du rendu** : tout ce qui dépasse de la notch (cartes, bulle, ombre) doit être
  inclus dans les bornes publiées.
- **Scripts d'édition en ligne de commande** : avec perl, ne jamais utiliser `|` comme délimiteur de `s|...|...|`
  si le motif contient `\|` (il devient une alternative vide qui correspond en tête de fichier). Préférer
  `s{...}{...}` ou l'outil d'édition. Attention aussi à `\r` dans les chaînes C# (`@"{\rtf"`) qui finit en
  vrai retour chariot via sed / perl.
- **Python n'est pas installé** sur la machine ; Node.js oui (utile pour `node --check` sur le JavaScript de
  `LocalPage`).
- **Everything et les instances nommées** : `-instance X` change aussi le canal du service
  (`\\.\PIPE\Everything Service (X)`) : une instance nommée n'indexe plus les disques (journal : « Open pipe failed 2 »,
  « access denied »). La copie de la notch tourne donc comme instance par défaut, avec `-config` / `-db` à elle. Pour
  déboguer Everything : `-debug-log` écrit `%TEMP%\Everything Debug Log.txt`.
- **Jamais de `SendMessage` vers Everything depuis le thread UI** : en chargeant son index (110 Mo), Everything met
  jusqu'à une seconde à répondre ; la notch gelait (clics à côté ignorés, recherche qui ne se fermait pas).
  `EverythingClient` envoie depuis le pool de threads (la réponse arrive quand même à sa fenêtre, sur le thread UI).
- **Perte du clavier** : l'événement `Activated` (désactivation) ne vient pas si Windows a refusé le premier plan à
  la notch. `NotchWindow` écoute aussi le changement de fenêtre au premier plan (`FullscreenWatcher.ForegroundChanged`)
  et réessaie de prendre le clavier (4 fois, 70 ms) avant d'abandonner.
- **XamlTypeInfo et `required`** : une propriété publique d'un contrôle XAML dont le type a des membres `required`
  (`SearchResult`) casse la compilation (le générateur veut l'instancier) : la garder `internal`.
- **`TextBox` multiligne** : WinUI sépare les lignes par `\r`, pas `\n` (cf. `SearchExclusions`).
- **Le shell en COM** (`Shell.Application`, `IShellItemImageFactory`) veut un thread STA : `AppCatalog` et
  `ShellIcons` ont le leur.
- **Anneau de progression sur une `CompositionEllipseGeometry`** : son tracé part du **haut** et tourne dans le **sens
  des aiguilles d'une montre** (mesuré à l'écran, sur une capture analysée pixel par pixel) : `TrimEnd = niveau`, **sans
  rotation**. Ne pas se fier à Direct2D (`ComputePointAtLength` d'une ellipse D2D part de la gauche) ni tourner de ±90° :
  trois essais ainsi ont décalé l'arc par rapport à son bouton (cf. `QuickBubble`, et l'anneau des annonces).
- **Tableaux dans une interface `[ComImport]`** : un `nint[]` y part en `SAFEARRAY`, pas en tableau C
  (`IShellFolder.GetUIObjectOf` plantait : violation d'accès). Un seul élément : le passer par `ref`.
- **`IContextMenu.InvokeCommand` avec un verbe inconnu** tue le processus (échec rapide 0xC0000409 dans ntdll) :
  `ShellMenu.Run` cherche d'abord le verbe dans le menu (`GetCommandString`) et lance son numéro de commande.

---

## 8. Données sur disque

| Emplacement | Contenu |
|---|---|
| `%LOCALAPPDATA%\Ilot\settings.json` | Réglages (`AppSettings`, écrits en différé ; `Welcomed` : l'accueil du premier lancement est fait) |
| `%LOCALAPPDATA%\Ilot\clipboard.json`, `clipboard\` | Historique du presse-papier et images |
| `%LOCALAPPDATA%\Ilot\UploadsQueue\` | Envois en attente (reprennent au lancement) |
| `%LOCALAPPDATA%\Ilot\LocalChat\` | Images temporaires du chat Wi-Fi local (supprimées à l'arrêt du partage) |
| `%LOCALAPPDATA%\Ilot\notes.db` | Notes (SQLite, WAL) |
| `%LOCALAPPDATA%\Ilot\study.db`, `Study\` | Révisions : cours, fiches, cartes et leur apprentissage (SQLite, WAL) ; copies des documents |
| `%LOCALAPPDATA%\Ilot\search.json` | Ce qui a été ouvert depuis la recherche (nombre, dernière fois) |
| `%LOCALAPPDATA%\Ilot\Everything\` | Réglages (`Everything.ini`, `mode.txt`) et index (`Everything.db`, ~110 Mo) de la copie d'Everything |
| `%LOCALAPPDATA%\Ilot\errors.log` | Journal des erreurs |
| `%USERPROFILE%\Downloads` | Fichiers reçus du téléphone (cachés `*.part` tant que non acceptés) |
| `Images\Ilot` | Images enregistrées depuis le presse-papier |

---

## 9. Pistes pour la suite

- Faire suivre aux autres bulles le modèle des notes (un module peut appeler `ShowBubble` pour un panneau
  détaché).
- Notes : couleurs de texte / surlignage, export (PDF, .docx), épingler une note, raccourci global pour ouvrir
  une note rapide.
- Option « accepter automatiquement » pour les fichiers du téléphone (paramètres).
- Mettre le projet sous git et ajouter quelques tests unitaires sur les parties sans interface (`MarkdownSyntax`,
  `SqliteDatabase`, `MdnsResponder`, `LocalTransferService` avec un `DispatcherQueue` de substitution).

---

## 10. Prompt de reprise (à donner à un agent IA)

> Tu reprends **Ilot**, une « Dynamic Island » pour Windows 11 en C# / .NET 10 / WinUI 3, dans
> `C:\dev\mynotch`. Avant toute modification, lis `HANDOFF.md` (règles, état, pièges) puis `docs/FONCTIONNALITES.md`
> (fonctionnalités et structure), et parcours `src/Ilot/Notch/NotchContext.cs` (l'API des modules) et
> `src/Ilot/App.xaml.cs` (enregistrement des modules).
>
> Règles impératives :
> - Animations sur le compositeur uniquement (ressorts `SpringChannel` / `Motion.cs`, expressions liées à `n`),
>   fluides et soignées.
> - 0 % de CPU au repos : pas de scrutation ; travail seulement quand l'onglet est visible ; écritures différées
>   hors du thread UI.
> - Icônes Segoe Fluent, jamais d'émojis.
> - Interface en français, code et commentaires en anglais, dans le style existant (commentaires `///` qui
>   expliquent le pourquoi).
> - Ne rien casser de l'existant : ajouter à côté.
> - Tenir `docs/FONCTIONNALITES.md` (et le README, court) à jour.
>
> Méthode : le propriétaire est le testeur et te parle en français. Implémente, compile pour vérifier (0
> avertissement, 0 erreur ; si Ilot tourne, compile avec `-p:OutDir=` vers un dossier temporaire), teste
> sans interface ce qui peut l'être, puis donne-lui des étapes de test courtes : « clic droit sur la notch →
> Quitter, puis `dotnet run --project src/Ilot` ». Ne lui demande pas de captures d'écran à répétition. Si
> « rien ne s'affiche », lis d'abord `%LOCALAPPDATA%\Ilot\errors.log`.
>
> Commence par me résumer en quelques lignes ce que tu as compris du projet et de son état, puis attends ma
> demande.
