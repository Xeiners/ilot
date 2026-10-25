# Ilot

Une « Dynamic Island » pour Windows 11, en C# / WinUI 3 (Windows App SDK 2.5, .NET 10).

Une pilule noire mate collée en haut au centre de l'écran, qui s'efface sous la souris pour ne jamais gêner. Survolée
en haut au centre (sa poignée), elle s'ouvre (660 × 158) avec une barre d'onglets le long de son bord supérieur :

- **Média** : à gauche un mini-lecteur connecté à la session multimédia de Windows (Spotify, YouTube dans le
  navigateur, VLC, Lecteur multimédia…), à droite les paroles synchronisées ou, à défaut, un grand visualiseur.
- **Presse-papier** : l'historique de ce que vous copiez (texte, liens, couleurs, images), avec épingles, et un
  aperçu pour retoucher une copie (rogner une image, l'entourer au crayon, modifier un texte) avant de la recopier.
- **Drop Zone** : glissez des fichiers sur la notch, ils partent sur Send (chiffrés) ou Gofile et le lien est copié.
- **Code** (dans la recherche) : tapez « code » dans Ctrl + Q, les projets récents de VS Code s'affichent en liste,
  ouverts d'un clic ou d'Entrée.
- **Notes** : les notes en cartes ; un clic en ouvre une dans une grande feuille, avec une barre d'outils façon Word.
  Une note se **détache** en fenêtre, comme les révisions.
- **Révisions** : glissez un cours (PDF, Word, PowerPoint, photo de vos notes) sur l'onglet, ou « Importer un cours » :
  Notch, la mascotte (Gemini, avec la clé de l'assistant), en écrit une **fiche de révision** (par partie, termes clés en gras, « À retenir »,
  « À ne pas confondre », la page citée à chaque point) et des **flashcards question-réponse** précises (une notion par
  carte, réponse courte et exacte, page source), tirées du cours seul. Chaque carte se révise en **QCM à 4 choix** (la bonne
  réponse et trois pièges tirés du cours ; 1 à 4 ou un clic, « Je ne sais pas »), puis sa réponse complète et sa page ;
  révision espacée comme Anki (la note proposée selon la réponse, Espace pour la donner, 1 à 4 pour « À revoir / Difficile
  / Bien / Facile » ; chaque carte revient au bon moment), « Réviser · 23 »
  pour tout ce qui est dû, et **« Nouvelles cartes »** à volonté (tout le cours, un chapitre ou vos points faibles ; 10,
  20 ou 30, jamais deux fois la même notion). Cartes modifiables ou supprimables ; tout reste sur l'ordinateur.
  Les cours se rangent en **sections** (Maths, Physique…) : des onglets de couleur au-dessus des cours, « Global »
  pour tous ; une section choisie ne montre et ne fait réviser que ses cours. « + » ouvre une bulle sous la notch pour
  la nommer et choisir sa couleur (clic droit sur une section : la modifier ou la supprimer).
  Des **cartes mentales** à la demande : une par notion importante, rangées par partie, et une vue d'ensemble du cours,
  pour tout comprendre d'un coup d'œil.
  La feuille d'un cours se **détache** en fenêtre (Réviser, Fiche et Cartes, comme dans la notch ; arrondie comme la
  bulle, déplacée par sa poignée, redimensionnée par ses coins, au premier plan ou non, réductible), qui reprend
  exactement là où vous en étiez, et se **rattache** à la notch de la même façon.
  La fiche se lit comme un document soigné : chaque partie dans sa carte (numéro, titre, pages), les définitions en
  couleur, les pages citées en pastilles, « À retenir » et « À ne pas confondre » encadrés, un sommaire cliquable. De
  petits sons doux accompagnent les réponses (on les coupe d'un clic).
  Pendant la préparation, la mascotte lit le cours et trois étapes se remplissent (le plan ; la fiche, partie par
  partie ; les flashcards, une à une) : fiche et cartes s'écrivent en même temps, et les cartes se révisent dès
  qu'elles sont là.
- **Pomodoro** : focus, pause, et une grande pause après une série ; le minuteur reste en haut de la notch fermée,
  la mascotte lit un livre pendant que vous vous concentrez et joue à la manette pendant la pause.
- **Recherche** (`Ctrl + Q`, partout) : la notch devient une barre de recherche et les résultats en tombent comme une
  goutte : applications, fichiers (boostés par [Everything](https://www.voidtools.com)), projets VS Code, paramètres
  Windows, commandes, calculs.

Fonctionnalités :

- **Premier lancement** : après l'installation, une fenêtre d'accueil (style Paramètres, la mascotte qui salue en
  haut) propose quelques étapes, chacune appliquée tout de suite : la langue, la couleur de la mascotte (elle est toujours
  là), la notch (normale ou minimisée, ouverte après un clic, cachée au repos, masquée en plein écran, lancée avec
  Windows), la musique (les paroles, leur ligne dans la notch ou la barre des tâches, le lecteur retiré de la notch
  fermée), les raccourcis (activés ou non) et la clé de l'assistant IA (facultative, vérifiée auprès de Google). Puis une **visite de la notch**, interactive : elle
  s'ouvre et reste ouverte, une carte sous elle présente chaque partie pendant qu'un anneau à la couleur de la
  mascotte l'entoure (le clic qui la garde ouverte ; les Révisions et la musique, sur leur onglet : sections,
  cartes à revoir, cartes mentales, fenêtre détachée, paroles dans la barre des tâches ; la flèche de retour de
  l'assistant), et demande de l'essayer : cliquer un autre onglet, glisser le fichier d'exemple de la carte sur
  la notch (elle passe sur la Drop Zone comme pour un vrai fichier ; rien n'est envoyé), tourner la molette sur le volume, cliquer la mascotte, faire un clic droit, appuyer sur
  le raccourci de la recherche (elle s'ouvre pour de vrai ; la visite revient une fois la recherche fermée, sur
  l'assistant). Fait, la carte le dit (la mascotte s'exclame) et passe seule à la suite. La dernière
  étape présente l'assistant IA, avec « L'essayer » (ou « Ajouter une clé »).
  Passer ou fermer la fenêtre l'accepte telle quelle ; les deux se refont depuis Paramètres → Général → Présentation.
- **Onglets** : des pastilles discrètes en haut de la notch ouverte. La pastille de sélection s'étire vers
  l'onglet choisi puis s'y rassemble, et le contenu glisse sur le côté en fondu, sans aucun recalcul de mise en
  page. Chaque pastille épouse son libellé, pour que cinq tiennent à côté des loupes de recherche. Notch fermée,
  la pilule reste celle du lecteur, quel que soit l'onglet.
  Les rangées qui débordent (presse-papier, envois, projets, notes) ont une flèche ‹ › à chaque bout, sur un fondu,
  tant qu'il reste quelque chose de ce côté (un clic avance de trois quarts de page) ; la molette d'une souris les fait
  aussi défiler, comme le pavé tactile (`Controls/RowScroller.cs`).
- **Survol** : la pilule fermée ne gêne jamais. Survolée, elle ne s'ouvre pas : elle rentre dans une petite poignée
  en haut de l'écran (languette noire 76 × 9 par défaut, ligne claire au milieu) et la souris passe au travers, vers les
  fenêtres de derrière (clics compris, une fois la pilule rentrée). Elle ressort dès que la souris quitte l'endroit
  où elle était ; ce départ est suivi par un crochet souris de Windows posé seulement pendant ce temps
  (`PointerWatcher`). Seul l'emplacement de la poignée (en haut au centre, 6 px de marge de chaque côté par défaut, la
  moitié en dessous, jusqu'au bord de l'écran ; largeur et marge réglables dans les paramètres) ouvre la notch :
  elle « respire » (anticipation), puis s'ouvre après 120 ms
  d'intention, pour qu'un simple passage de la souris en haut de l'écran ne déclenche rien. Une carte passagère ou
  une question (fichier reçu : Accepter / Refuser) se survole et se clique normalement.
- **Visualiseur audio en temps réel** : les barres réagissent aux fréquences du son réellement joué (24 bandes,
  des basses aux aigus) : 6 barres sur la pilule et au bout du titre, 24 dans la colonne droite quand le morceau
  n'a pas de paroles ; la lueur ambiante respire au rythme des basses.
- **Timeline** : temps écoulé, temps restant et un scrubber interactif. Au survol la barre s'épaissit et une
  poignée apparaît ; glisser pour naviguer dans le morceau, avec aperçu du temps en direct.
- **Paroles synchronisées**, affichées quand le morceau en a (bouton bulle au bout de la barre d'onglets pour
  les masquer ou les réafficher). Notch ouverte, colonne droite façon Apple Music : la ligne précédente et la
  suivante, plus petites et grisées, encadrent la ligne chantée ; chaque changement de ligne glisse sur le
  compositeur. Notch fermée, la pilule s'élargit (380 px) et la ligne chantée remplace la barre de progression.
  Une ligne trop longue défile pendant qu'elle est chantée au lieu d'être tronquée. Source : LRCLIB, mise en
  cache en mémoire. Au choix (Paramètres → Paroles), la ligne chantée va **dans la barre des tâches** plutôt que sur la
  notch fermée : à droite, contre les icônes, la pochette à côté (l'icône de l'appli qui joue sur son coin), la ligne
  chantée qui s'éclaire au fil du chant et la suivante dessous ; claire ou sombre selon la barre, cachée quand une
  application est en plein écran. Sa largeur se règle en tirant son bord gauche (ou dans les Paramètres). En option
  (« Retirer le lecteur de la notch fermée »), la notch fermée laisse alors le morceau à la barre des tâches et garde
  son état neutre (la mascotte seule) ; en pause, « Se cacher au repos » décide ; un morceau sans paroles revient sur
  la notch.
- **Presse-papier** : chaque Ctrl + C est enregistré (texte, lien, code couleur hexadécimal, image), sans
  scrutation, et la notch fermée le confirme : elle s'étend un instant vers la droite
  pour annoncer « Texte copié », « Lien copié », « Couleur copiée » ou « Image copiée » (le visualiseur s'efface le
  temps de la notification ; désactivable). Une rangée de tuiles qui
  défile à la molette, chacune avec son aperçu : texte, domaine du lien, pastille de couleur et sa conversion RVB,
  miniature de l'image. Clic sur une tuile : elle repart dans le presse-papier, prête à coller. Au survol :
  aperçu (l'œil), ouvrir le lien, copier la couleur en `rgb(…)`, enregistrer l'image dans `Images\Ilot`,
  supprimer, épingler. **L'aperçu** (l'œil, ou **Espace** dans la recherche : Windows + V puis Espace montre la
  dernière copie) tombe sous la notch, en grand : une image se retouche au **crayon** (cinq couleurs, pour entourer un
  endroit), se **rogne** (tracer le cadre à garder), **pivote** d'un quart de tour (R) et se **retourne** en miroir
  (M), chaque étape s'annule (Ctrl + Z) ; un texte (lien, couleur
  aussi) se modifie comme une page. **Copier** (Ctrl + Entrée ; pour une image, Entrée ou Ctrl + C aussi) met le
  résultat dans le presse-papier, où il arrive comme une nouvelle copie ; Échap (ou Espace pour une image) ferme.
  Le bouton balai, au bout de la barre d'onglets, nettoie tout sauf les épinglés, après confirmation dans la notch.
  La loupe, juste à côté, déplie un champ de recherche qui filtre les tuiles pendant la frappe (sans tenir compte des majuscules ni
  des accents, tous les mots doivent apparaître) et surligne ce qui correspond. Entrée copie le premier résultat et
  rend le clavier à l'application d'avant, prête pour Ctrl + V ; Échap abandonne et replie le champ.
  **Windows + V** (ou le raccourci choisi dans les paramètres) ouvre ce presse-papier à la place de celui de Windows
  (désactivable) : la dernière copie est
  choisie, ← → pour en choisir une autre, la saisie filtre, Entrée ou un clic la colle directement dans
  l'application en cours, Échap (ou Windows + V à nouveau) ferme. Un hook clavier bas niveau prend le raccourci
  avant Windows, comme PowerToys ; il ne voit pas les applications lancées en administrateur. Le raccourci se change
  dans les paramètres : « Modifier », puis la combinaison voulue (avec Ctrl, Alt ou Windows, ou une touche F seule),
  capturée par ce même hook, ce qui permet aussi les combinaisons que Windows se réserve.
  **En plein écran** (un jeu, une vidéo, sur n'importe quel écran), les raccourcis de la notch (presse-papier,
  recherche, assistant) ne font rien : la touche va à l'application, comme si la notch n'était pas là.
  Une tuile se glisse aussi hors de la notch (l'image comme fichier, le texte comme texte). Les éléments épinglés
  restent en tête et ne sont jamais chassés par les nouvelles copies ; les 25 dernières copies sont gardées (de 5 à 100
  dans les paramètres ; au-delà, chaque copie remplace la plus ancienne), une
  copie déjà présente remonte au lieu d'être dupliquée. Les contenus marqués comme privés (gestionnaires de mots
  de passe) ne sont jamais enregistrés. Historique dans `%LOCALAPPDATA%\Ilot\clipboard.json`, images à côté
  dans `clipboard\`.
- **Drop Zone** : glisser des fichiers sur la notch, même fermée, l'ouvre sur l'onglet Drop Zone. Les fichiers
  partent sur l'hébergeur choisi dans les paramètres :
  - [Send](https://send.vis.ee), par défaut : chiffré de bout en bout sur le PC (AES-128-GCM), la clé ne voyage que
    dans le lien, après le `#`, et le navigateur du destinataire déchiffre seul. 2,5 Go, lien valable 1 h, 12 h,
    24 h ou 72 h, 20 téléchargements au plus ;
  - [Gofile](https://gofile.io) : anonyme, sans chiffrement, 5 Go ici ; supprimé quand plus personne ne télécharge.

  Un fichier n'est jamais envoyé ailleurs que sur l'hébergeur choisi. Chaque hébergeur est une classe de
  `Modules/DropZone/Hosts/` (`IUploadHost`) : en ajouter un tient en un fichier. Un fond de couleurs dérive doucement
  derrière la Drop Zone et se rassemble sous le curseur pendant un glisser. Plusieurs fichiers ou un dossier sont
  d'abord zippés. La taille est vérifiée dès le dépôt : au-delà de la limite de l'hébergeur, la notch l'annonce en
  rouge et rien n'est envoyé. Pendant l'envoi, la pilule fermée affiche un anneau de progression et le pourcentage ;
  la tuile, « 45 % • 3,2 Mo/s » sur une fine barre, et un cadenas pour un envoi chiffré. Une fois
  envoyé, le lien est copié et la notch annonce « Lien copié ». Si le réseau tombe, l'envoi se met en pause
  (« attente du réseau… ») et reprend seul dès son retour (ou après 2 s, 4 s… jusqu'à 1 min). La file est gardée
  dans `%LOCALAPPDATA%\Ilot\UploadsQueue\` : un envoi interrompu repart au lancement suivant. Si l'hébergeur
  cesse de prendre le fichier, la tuile le dit (« 73 % • en attente… ») ; au bout de 75 s, l'envoi repart de zéro
  (« Send ne répond plus – nouvel essai… », 4 essais au plus : ni Send ni Gofile ne reprennent en cours). Les liens restent
  dans la liste jusqu'à leur expiration (clic : recopier ; survol : ouvrir, retirer).

  Deux cartes à gauche : **En ligne** (l'hébergeur, ses limites : « 2,5 Go · 24 h », un cadenas si chiffré) et
  **Wi-Fi local** : échange direct avec un téléphone ou un autre PC sur le même Wi-Fi, par le routeur, sans
  Internet. Y déposer des fichiers (ou un dossier, zippé à la volée), coller (`Ctrl + V`) ou simplement cliquer ouvre le partage : un QR
  code remplace la liste des envois, avec à côté une adresse courte à taper sur un téléphone sans caméra :
  `mynotch.local/4821`, et en dessous `ou 192.168.1.12/4821` pour les appareils qui ne résolvent pas les noms
  `.local` (le code à 4 chiffres change à chaque partage ; après 10 codes faux d'affilée, plus aucun n'est accepté
  pendant une minute). Le nom est publié en mDNS pendant le partage par un petit répondeur maison
  (`Local/MdnsResponder.cs`, RFC 6762, sans bibliothèque) : il demande d'abord au réseau si le nom est libre
  (sinon `mynotch-2.local`…), l'annonce, puis le retire à l'arrêt. iPhone, Mac, Windows, Linux et Android récents
  le résolvent ; le QR code, lui, garde l'adresse IP, qui marche partout.
  **Chat et collage en direct** : le bouton **Chat** ouvre sous la notch une bulle redimensionnable (`NotchContext.ShowBubble`) synchronisée en temps réel avec la page `mynotch.local` (attente longue HTTP, 0 % de CPU au repos). Dans les deux sens, on peut envoyer un message ou un lien (détecté automatiquement, cliquable et copiable en un clic), **coller directement (`Ctrl + V` ou bouton « Coller »)** une capture d'écran, une image, du texte, un lien ou des fichiers copiés dans l'Explorateur, ou glisser-déposer des fichiers sur la bulle ou la page. Les textes, liens et images collées arrivent directement dans le fil (aperçu de l'image, copier dans le presse-papier ou enregistrer dans `Images\Ilot`).
  Pour les fichiers envoyés vers le PC (`Téléchargements`), un fichier reçu attend votre accord, caché : la notch fermée descend une carte
  façon appel entrant (« IMG_2034.jpg · 3,2 Mo · depuis le téléphone », **Refuser** en rouge, **Accepter** en vert ;
  plusieurs fichiers en attente se décident ensemble), et la bulle de chat affiche aussi les boutons **Accepter / Refuser** sur le message. Le survol ne l'ouvre pas, pour cliquer tranquille ; si la
  notch s'ouvre, la carte s'efface et revient à la fermeture, et la Drop Zone ouverte pose la même question à côté
  de ses boutons (« Garder IMG_2034.jpg ? »). Accepté, le fichier rejoint Téléchargements (« Fichier enregistré ») ;
  refusé, il est supprimé sans trace (« Fichier refusé ») ; jamais décidé, il disparaît à la fermeture d'Ilot.
  La page du téléphone suit en direct : « en attente de l’accord du PC… », puis « Accepté par le PC » ou « Refusé par le PC ».
  Ces questions passent par `NotchContext.Ask()`, utilisable par tout module. La
  pilule fermée suit le transfert avec un anneau et son pourcentage, la Drop Zone ouverte avec sa vitesse (« 42 % •
  11,4 Mo/s »), puis la notch annonce « Transfert
  terminé » ; la page du téléphone affiche aussi la vitesse, puis « 3,0 Mo en 2 s · 1,5 Mo/s ». Le serveur
  (`Modules/DropZone/Local/LocalTransferService.cs`, un petit serveur HTTP sur `TcpListener`, sur l'interface du Wi-Fi seulement, port 80 si libre, sinon 8080)
  n'existe que pendant le partage : il s'arrête avec « Arrêter », après 10 min sans requête, ou si le PC quitte ce
  réseau. Chaque partage a une clé aléatoire dans son adresse : personne d'autre sur le réseau ne voit les fichiers,
  et un ancien QR code ne marche plus. Au premier partage, Windows demande d'autoriser Ilot dans le pare-feu
  (réseaux privés : TCP pour la page, UDP 5353 pour le nom) ; un Wi-Fi déclaré « public » bloque l'accès.
- **Code**, dans la recherche (plus d'onglet) : « code » tapé dans Ctrl + Q (et des mots après : « code react »)
  remplace les résultats par la liste des projets récents de VS Code (dossiers, espaces de travail `.code-workspace`, dossiers distants WSL /
  SSH / conteneurs), du plus récent au plus ancien, avec leur emplacement abrégé (`~` pour le dossier personnel).
  Clic : ouvrir dans VS Code ; boutons : ouvrir l'emplacement dans l'Explorateur, copier le chemin (annoncé par la
  notch). Chaque projet prend la couleur de ce avec quoi il est fait, reconnu à ses fichiers (`.csproj`,
  `package.json` : React, React Native, Next.js, Vue, Angular, Electron, Node ; `pubspec.yaml`, `Cargo.toml`, `go.mod`,
  Python, Kotlin, Java, PHP…), avec un badge ; à défaut, une couleur qui lui est propre. Les mots après « code »
  filtrent par nom, emplacement ou langage (↑ ↓ pour choisir ; Entrée pour ouvrir, et la
  recherche se ferme). Lu à chaque « code » tapé : la liste « Ouvrir récent » de VS Code
  (`history.recentlyOpenedPathsList` dans `state.vscdb`), complétée par les projets dont VS Code garde l'état
  (`workspaceStorage`) quand cette liste a été vidée. Les projets disparus du disque sont ignorés.
- **Pomodoro** : un anneau de la couleur de la mascotte (plus claire pendant les pauses) qui se vide en continu, le
  temps restant au centre ; le nom de la phase, les sessions de la série en points,
  ce qui a été fait aujourd'hui (« 3 sessions · 1 h 15 de focus ») ; « Nouvelle série » (tout arrêter et repartir de la
  session 1, affiché une fois la série entamée) ; recommencer la phase, démarrer / mettre en pause, passer à
  la suite. Notch fermée, le minuteur reste en haut (son icône entourée du temps écoulé, le temps restant, en pause
  aussi). Pendant un focus, la mascotte tient un grand livre ouvert : ses yeux suivent les lignes, une page tourne de
  temps en temps, et elle ne s'endort pas. Pendant une pause, elle a une manette dans les mains : elle joue (la
  manette sursaute quand elle appuie, ses yeux vont de la manette à l'écran). À la fin de chaque phase : un carillon
  (un petit arpège montant pour aller en pause, deux notes pour reprendre), et la mascotte sort de sa place pour venir,
  en grand, dans une carte sous la notch, et dire « Temps écoulé ! On prend une pause · 5 min » (ou « C'est reparti ! »)
  en bougeant la bouche ; notch ouverte, c'est un mot de la notch. L'anneau rebondit.
  La roue dentée de l'onglet fait tomber sous la notch une bulle avec ses réglages, sur des règles graduées qu'on fait
  glisser (molette, clic sur une graduation), appliqués à chaud ; ils sont
  aussi dans Paramètres → Pomodoro : les durées (25 / 5 / 15 min par défaut), le nombre de focus avant la grande pause (4), les
  enchaînements automatiques, le carillon. Les sessions du jour sont gardées (`%LOCALAPPDATA%\Ilot\pomodoro.json`,
  60 jours). Rien ne tourne quand le minuteur est arrêté.
- **Notes** : les notes en cartes, comme le presse-papier (titre, premières lignes, « Il y a 5 min »), avec
  « Nouvelle note » en tête et une recherche dans les titres et les textes (la loupe). Un clic sur une carte ouvre
  la note dans une grande feuille qui tombe de la notch comme une goutte : une bulle noire s'étire sous la
  notch, son col s'amincit et se détache, puis elle rebondit et se pose 14 px plus bas (900 px de large, aussi haute
  que l'écran le permet) ; fermée, elle remonte et la notch la réabsorbe. Tout est dessiné par le compositeur
  (`NotchContext.ShowBubble()`, utilisable par tout module). Ses deux coins du bas se tirent pour la redimensionner
  (un arc discret, le curseur de redimensionnement) : elle reste centrée sous la notch, s'élargit des deux côtés et
  s'allonge vers le bas, de 720 × 320 à toute la place sous la notch ; la taille choisie est retenue. Les cartes restent dans la notch pour passer d'une
  note à l'autre. La feuille : le titre, « Copier » (avec la mise en forme, pour Word, et en
  texte), « Supprimer » (un second clic confirme), « Fermer » ; une barre d'outils façon Word : Texte / Titre /
  Titre 2 / Titre 3, la taille en points (8 à 72, par crans comme Word), gras, italique, souligné, barré, liste à
  puces, liste numérotée, liste de tâches (un clic sur la case la coche en vert et barre la ligne), annuler /
  rétablir ; elle montre la mise en forme sous le curseur. Les habitudes Markdown marchent aussi en début de ligne :
  « - », « 1. », « [] », « # », « ## », « ### » puis espace. Entrée continue une liste (un élément vide la termine),
  Retour arrière juste après une case l'enlève, Ctrl + Entrée coche, Ctrl + B / I / U. Le collé est du texte seul,
  dans la mise en forme où il arrive. En bas, mots et caractères, et « Enregistré ». Cliquer dans la note prend le
  clavier et garde la notch ouverte ; Échap ou un clic ailleurs le rend. Quitter l'onglet, ou la notch qui se
  referme, range la feuille (la note est enregistrée). Base SQLite `%LOCALAPPDATA%\Ilot\notes.db` (table
  `Notes` : id, title, content en RTF, plain pour les cartes et la recherche, created_at, updated_at) par le
  `winsqlite3.dll` de Windows : lue au premier affichage de l'onglet seulement, écrite peu après la dernière frappe
  hors du thread UI, et une dernière fois à la fermeture. Les notes écrites en Markdown avant sont converties en mise
  en forme à leur ouverture. Une note laissée vide n'est pas gardée.
- **Recherche** (`Ctrl + Q` par défaut, partout, modifiable ou désactivable dans Paramètres → Recherche ; encore une fois pour refermer) : la notch s'ouvre directement en barre de
  recherche (660 × 60, sans onglets ; la loupe pivote et grandit en place) et prend le clavier. Dès la première
  lettre, une bulle de résultats tombe de la barre comme une goutte (le même effet que la feuille des notes), puis
  s'étire ou se resserre sur un ressort quand le nombre de résultats change ; vidée, elle remonte dans la notch.
  Les lignes qui changent glissent et s'éclairent l'une après l'autre ; la ligne choisie est soulignée par un
  surlignage qui glisse de ligne en ligne. Chaque ligne : l'icône de Windows (applis, types de fichiers, dossiers) ou
  un glyphe sur une tuile colorée, le nom avec ce qui correspond en blanc, l'emplacement, le type, et `↵` sur la
  ligne choisie. Dans une seule liste, classée ensemble :
  - **Applications** : toutes celles du menu Démarrer (programmes et applis du Store, lues dans
    `shell:AppsFolder`, en arrière-plan) ;
  - **Fichiers et dossiers**, par **Everything** (voir plus bas) : les disques entiers, à jour à la seconde ;
  - **Projets VS Code** récents ; **Paramètres Windows** (une cinquantaine de pages `ms-settings:`, trouvables aussi
    par mots-clés : « wifi », « fond d'écran »…) ; **Commandes** : verrouiller, veille, redémarrer, arrêter, se
    déconnecter, vider la corbeille (les quatre dernières demandent une seconde Entrée) ;
  - **Calculs** : `18*1,2`, `= 2^10`, `(3+4)²`, `200 * 15%`, `√16`, `pi*2` (Entrée copie le résultat) ;
  - une **recherche sur le web** en dernière ligne.

  La correspondance ignore majuscules et accents (« parametres » trouve « Paramètres ») et lit les initiales (« vsc » :
  Visual Studio Code). Le classement tient compte de la qualité de la correspondance (début du nom, début d'un mot,
  initiales, lettres dans l'ordre), du type, de ce que vous ouvrez souvent et récemment
  (`%LOCALAPPDATA%\Ilot\search.json`, et le compteur d'Everything), et pour les fichiers de leur emplacement
  (Bureau, Documents, Téléchargements… avant les dossiers profonds ou cachés) et de leur fraîcheur. Les sources
  locales répondent tout de suite ; la liste attend Everything 90 ms au plus pour ne pas sauter.
  Clavier : `↑` `↓` pour choisir, `Entrée` pour ouvrir, `Ctrl + Entrée` pour montrer dans son dossier,
  `Maj + Entrée` pour lancer en administrateur, `Ctrl + C` pour copier le chemin, `Échap` pour effacer puis
  fermer. La souris marche aussi (survol, clic). Clic droit sur un résultat : le menu de l'Explorateur, au style de la
  notch (ouvrir, ouvrir avec…, en administrateur, l'emplacement, copier le fichier, copier le chemin, supprimer vers
  la Corbeille, propriétés) et, sous « Afficher plus d'options », le menu complet de Windows (Envoyer vers, Partager,
  les entrées des applis comme 7-Zip ou VS Code…), comme dans Windows 11. Ce qui s'ouvre garde le clavier ; Échap le
  rend à l'appli d'avant.
  Réglages : écran où elle s'ouvre (curseur, fenêtre active ou principal), garder la dernière recherche (oui par
  défaut : fermée sans rien ouvrir, elle revient sélectionnée, avec ses résultats ; après une ouverture, la barre repart vide), sources à cocher, nombre de résultats (4 à 12), dossiers ignorés (par défaut `C:\Windows\`,
  `\AppData\`, `\WindowsApps\`, `\node_modules\`, `\.git\`, `\obj\`…).
- **Boosté par Everything** : les fichiers sont trouvés par [Everything](https://www.voidtools.com) (voidtools,
  licence MIT, crédité en bas de la bulle et dans les paramètres). La notch en emporte une copie portable
  (`Everything\Everything.exe`, 64 bits, non modifiée et signée, avec sa `License.txt`) et la lance hors de vue au
  démarrage : ni fenêtre, ni icône, ses réglages et son index dans `%LOCALAPPDATA%\Ilot\Everything` ; elle la ferme
  en quittant. Un Everything que vous lancez vous-même est utilisé tel quel. À pleine vitesse (les disques entiers,
  lus dans leur table de fichiers), Everything a besoin de son service : s'il est installé, la copie s'en sert ; sinon
  « Pleine vitesse » dans les paramètres l'installe (une autorisation de Windows, une seule fois), et en attendant elle
  indexe votre dossier personnel.
- **Volume** : la molette de la souris sur l'onglet Média règle le volume du système par pas
  de 5 % (notch ouverte, la bulle de volume à gauche le montre et le règle aussi).
  Quand le volume du système change, de n'importe où (les touches du clavier, la barre des tâches, une appli, la
  molette), la pilule fermée le montre un instant, à sa largeur normale (celle d'avant les paroles) : le haut-parleur à gauche, une piste, et la mascotte qui en est
  le curseur, glissant jusqu'au niveau, suivie d'un remplissage à sa couleur et regardant dans le sens du
  changement (yeux fermés quand le son est coupé, ravie à 100 %) ; sans mascotte, le haut-parleur entouré du
  niveau. Windows prévient à chaque changement (notifications Core Audio de la sortie par défaut) : rien n'est
  sondé. Les rangées des autres onglets gardent la molette pour défiler.
- **Parler avec la mascotte, à voix haute** : un clic sur la mascotte de la goutte (ou sur « Parler » juste dessous) lance une vraie conversation vocale (Gemini Live, la même clé) : vous parlez, elle vous
  répond avec sa voix, presque aussitôt, et vous pouvez lui couper la parole. Elle vous écoute en hochant la tête,
  réfléchit (la lueur des bords s'allume), puis parle : un petit « o » s'ouvre doucement avec sa voix, ses yeux
  sourient, elle sautille sur les syllabes fortes. Ce que chacun dit s'écrit dans la conversation, qui continue ensuite à l'écrit (et
  la voix connaît ce qui a été écrit avant). La notch peut se fermer : la conversation continue, la pilule reste, la
  mascotte y garde sa forme d'orbe et réagit aux deux voix, pour montrer qu'elle vous entend. Elle s'arrête par
  « Arrêter » (ou la mascotte) ou Échap ; le micro n'est ouvert que pendant ce temps. L'un ou l'autre : pendant qu'on se
  parle, la question écrite est désactivée. Le volume de sa voix se règle pendant la conversation (un curseur dans la
  barre, ou la molette sur la mascotte) et dans les paramètres ; il est retenu. L'écho des haut-parleurs est retiré
  par Windows quand le micro le permet. Avant la première question, la mascotte de la goutte est plus grande ; elle
  reprend sa taille quand la conversation commence.
- **L'assistant (la mascotte qui parle)** : un clic sur la mascotte (ou Ctrl + Maj + Espace, modifiable) ouvre une
  barre pour lui poser une question (sa petite notch, à droite, garde une flèche « Retour aux onglets » pour revenir
  au mode normal ; Maj + Entrée pour aller à la ligne : la barre grandit avec ; Ctrl + V d'une image,
  capture ou fichiers copiés, la joint à la question, jusqu'à 4), et une goutte tombe dessous, redimensionnable par ses
  coins du bas (la taille est retenue) : le noir de la notch, la mascotte dans sa propre lueur, de sa couleur (argentée
  pour le blanc, le gris ou le noir), l'accueil puis la conversation, sans barre de défilement, ses bords en fondu, à
  gauche, la réponse s'écrivant mot à mot (Gemini, de Google, avec votre clé), et la mascotte en grand à droite (sa
  petite notch s'efface pendant ce temps). Elle suit des yeux ce que vous tapez, un petit hochement à chaque touche ;
  à l'envoi, la goutte s'agrandit, la mascotte flotte dans une lumière plus forte et regarde d'un côté puis de
  l'autre en réfléchissant, pendant que trois points rebondissent. Elle devient une orbe lumineuse : une
  sphère sombre bordée de sa couleur, avec un halo ; elle garde ses yeux, regarde ce que vous tapez, lève les yeux en réfléchissant, rebondit à chaque mot, fait
  un clin d'œil une fois la réponse finie, prend un air triste si ça échoue (pas de clé, pas de réseau…), et redevient
  elle-même quand la barre se ferme. Pendant qu'elle réfléchit, et seulement alors, la barre et la goutte s'illuminent de
  l'intérieur : une lueur douce, vive au bord et qui s'estompe vers le dedans, de la couleur de la mascotte, dont la
  partie la plus claire fait lentement le tour. Ces animations ne tournent que pendant l'assistant. Mise
  en forme simple (gras, listes, `code`, blocs de code), un bouton pour copier chaque réponse, « Nouvelle
  conversation » pour repartir de zéro ; Entrée envoie, Échap ferme. « Historique » montre les conversations passées (écrites ou à voix
  haute, les 100 dernières, gardées sur ce PC dans `%LOCALAPPDATA%Ilotassistant-history.json`, sans les images) :
  un clic en rouvre une pour la continuer, la corbeille en oublie une, « Tout effacer » toutes. Dans la goutte, la
  lueur de la mascotte s'étend doucement, et une petite boule de lumière de sa couleur se promène en bas à gauche. Paramètres → Assistant : la clé API (vérifiée auprès de Google, gardée par le Gestionnaire
  d'identification de Windows, jamais en clair ; un guide pas à pas pour l'obtenir, ouvert tant qu'il n'y en a pas :
  « Ouvrir » Google AI Studio, puis « Coller et enregistrer » une fois la clé copiée), le modèle (« Automatique » : le plus rapide de ceux que la clé
  peut utiliser, ou un autre de la liste lue chez Google), le raccourci.
- **Les paramètres** (clic droit sur la notch → Paramètres) : une barre d'onglets à gauche, une section à la fois :
  Général, Poignée, Recherche, Paroles, Presse-papier, Drop Zone, Assistant.
- **Volume et luminosité, à gauche** : notch ouverte, deux petites bulles sortent de son bout gauche, comme la
  petite notch de la mascotte à droite : le volume en haut (suspendue au bord de l'écran), la luminosité juste
  dessous. Chacune montre son icône entourée d'un anneau de niveau, de la couleur de la mascotte, avec un bouton au
  bout, et le pourcentage au survol. L'anneau est une molette : on l'attrape et on tourne autour, le niveau suit (en haut, rien ; dans le
  sens des aiguilles d'une montre, jusqu'au plein ; il s'arrête aux bouts au lieu de sauter). La molette de la souris
  la règle par pas de 5 % ; au milieu, glisser vers le haut ou le bas la règle comme un curseur, et un clic sur le
  volume coupe ou rétablit le son. Leur contenu suit exactement leur petite notch quand elle sort ou rentre : rien
  ne déborde. La luminosité est celle de l'écran intégré d'un portable (le réglage « luminosité » du mode
  d'alimentation en cours, celui que bougent les touches de Windows) ; sans batterie, sa bulle n'apparaît pas.
  Rien n'est sondé : les niveaux sont relus à l'ouverture, le volume suit Windows en direct.
  Un clic droit sur le volume ouvre dessous un **mélangeur de volume** : chaque application qui fait du son (et les
  sons système), son icône (un clic coupe ou rétablit son son), son nom, un curseur fin de la couleur de la mascotte
  (glisser, cliquer, molette) et son pourcentage. Un nouveau clic droit le ferme, il se ferme aussi avec la notch.
- **Appareil audio connecté / déconnecté** : quand Windows bascule sur une autre sortie (AirPods, casque Bluetooth ou
  USB, écouteurs branchés), la pilule fermée s'agrandit vers le bas, sous sa propre ligne (pochette, paroles,
  visualiseur), pour une carte « façon AirPods » : icône animée (deux ondes à la connexion), nom de l'appareil,
  « Connecté · Bluetooth » et son volume, ou « Déconnecté » et la sortie où le son est passé ; elle se rétracte seule
  après 2,5 s (survolée, la notch s'ouvre normalement). Événements Core Audio uniquement (`IMMNotificationClient`),
  aucune scrutation ; ignorés : le démarrage, les périphériques virtuels, les bascules entre sorties intégrées, et une
  notch déjà ouverte. Ces cartes passent par `NotchContext.Present()`, utilisable par tout module.
- **La mascotte** : un petit galet blanc aux yeux en gélule qui vit dans la notch, dans tous ses états, avec un léger
  relief (une ombre douce sur son bord, un reflet doux en haut à gauche ; rien en onde ni en poignée). Notch fermée, il se tient au bout droit de la pilule (à la place des petites barres du visualiseur, qui restent dans la
  notch ouverte) ; la musique joue, il se change en onde sonore avec des yeux : son corps devient la barre du milieu,
  qui bat sur les basses, et deux barres poussent de chaque côté, chacune sur sa bande du spectre (les médiums près
  du corps, les graves et les aigus dehors, un peu plus courts : une silhouette en montagne). Une
  annonce (« Texte copié »…) : clin d'œil et joues roses, le message s'affiche juste après lui ; une carte ou une
  question (fichier reçu) : un sursaut. Au repos, au lieu de la poignée, la notch garde un petit « nid » rond où il
  dort ; survolé, le nid ouvre la notch (la mascotte est la porte d'entrée), comme la poignée. En mode minimisé, ou
  quand la pilule s'efface sous le curseur, la mascotte s'écrase dans la poignée : elle glisse au milieu, ferme les
  yeux et s'aplatit jusqu'à devenir sa ligne, à sa couleur ; elle se redresse quand le curseur s'en va.
  Notch ouverte, il sort du bout de la pilule dans sa propre petite notch, une goutte noire à droite de la notch
  ouverte (ou de la barre de recherche), s'y réveille d'un petit saut et suit le curseur des yeux ; un clic le fait
  sauter de joie. Il reste éveillé tant que tu utilises le PC : sans musique, il ne s'endort qu'après une minute
  sans toucher ni au clavier ni à la souris, et se réveille à ton premier geste. Endormi, il ne fait plus rien du
  tout, ni minuteur ni animation. Tout est
  dessiné et animé par le compositeur (`Mascot/`), sans image ni paquet. Sa couleur se choisit dans les
  paramètres : 8 teintes douces (blanc, lavande, ciel, menthe, citron, pêche, rose, corail) ou n'importe laquelle
  avec « Personnalisée… » (le sélecteur de couleur de Windows ; il suit en direct, et sur une couleur foncée ses yeux
  passent en clair). Il y passe en fondu. Désactivable (Paramètres → Général →
  Mascotte : la poignée et les barres reviennent). Le principe (une forme qui se transforme, des yeux qui ont l'air
  découpés dedans, la vie par le regard et les clignements) s'inspire de [bloub](https://github.com/jeremy-prt/bloub) ;
  le dessin et le code sont les nôtres.
- **Mode minimisé** : fermée, la notch n'est plus que sa poignée, quoi qu'il se passe (musique, minuteur du
  Pomodoro, nid de la mascotte : la mascotte devient la ligne de la poignée, à sa couleur). La survoler l'ouvre comme
  d'habitude ; les annonces, les cartes et les questions passent quand même le temps qu'il faut. Clic droit sur la notch
  → « Mode minimisé », ou Paramètres → Général (`AppSettings.Minimized`).
- **Double-clic** : verrouille la notch ouverte (petit indicateur en bas). Un second double-clic la referme.
- **Un clic suffit à la garder** : ouverte au survol, elle se referme quand la souris s'en va ; dès qu'on clique dedans
  (un onglet, un bouton, sa bulle, ou le clic qui l'ouvre), elle reste ouverte jusqu'à un clic ailleurs, sans prendre
  le clavier à l'application en cours. Désactivable : Paramètres → Général, « Rester ouverte après un clic ».
- **Clic droit** : menu « Garder ouverte » / « Mode minimisé » / « Paramètres… » / « Quitter Ilot ».
- **Au repos, une poignée** : sans rien à afficher (pas de musique ouverte : la pilule serait vide), la notch n'est que
  sa poignée (la petite languette avec sa ligne claire), tout de suite. Avec un morceau en pause (ou rien qui se passe),
  elle se réduit à sa poignée au bout de 3 minutes (réglable de 5 secondes à 5 minutes, ou désactivable : « Se cacher au repos »), et dès le démarrage. La survoler la fait
  gonfler puis s'ouvrir. La pilule revient d'elle-même dès que la musique
  reprend, et le temps d'une annonce (« Texte copié »…), d'une carte (appareil audio) ou d'une question (fichier
  reçu). Un module le dit par `NotchContext.SetPresence()` (`Active` : la musique joue ; `Idle` : un morceau en pause ;
  `Nothing` : rien à montrer) ; un seul minuteur, armé
  quand plus rien ne se passe : rien ne tourne entre-temps.
- **Plusieurs écrans** : la notch reste sur l'écran principal, toujours. La recherche (`Ctrl + Q`) s'ouvre sur l'écran
  du curseur par défaut (ou celui de la fenêtre active, ou l'écran principal : au choix dans les paramètres). Sur
  l'écran principal, dans la notch elle-même ; sur un autre, dans une notch de recherche à part (une seconde fenêtre,
  créée la première fois puis gardée cachée) qui tombe du haut de cet écran, à son échelle, et rentre dans son bord
  une fois la recherche finie (`Windowing/SearchRouter.cs`). Appelée alors qu'une appli est en plein écran, la notch
  se montre quand même (on l'a demandée), puis redevient discrète au repos.
- **Plein écran** : quand un jeu, une vidéo ou une présentation occupe l'écran de la notch, elle rentre dans
  le bord de l'écran et revient à la sortie (désactivable). Détection par événements Windows, sans scrutation.
- **Paramètres** : fenêtre Windows 11 (Mica, cartes) avec aperçu en direct : masquer en plein écran, largeur de la
  poignée (40 à 200 px) et zone de détection autour d'elle (0 à 40 px), avec un aperçu à taille réelle et un bouton « Rétablir », afficher ou
  non les paroles, taille des paroles dans la notch fermée, enregistrement du presse-papier, annonce de
  chaque copie, taille de l'historique, effacement de l'historique avec confirmation (les épingles
  restent). Appliqués immédiatement, enregistrés dans
  `%LOCALAPPDATA%\Ilot\settings.json`.
- **Français ou anglais** : Paramètres → Général → Langue (« Celle de Windows » par défaut : anglais si Windows n'est
  pas en français). La notch la prend au lancement suivant. Tout est traduit : la notch, ses modules, les paramètres,
  les dates et nombres (« 2.5 GB », « 3 min ago »), et la page du téléphone (`mynotch.local`).
- **Clic à travers** : hors de la notch, les clics atteignent les fenêtres situées dessous. Cliquer la notch ne
  vole jamais le focus de l'application en cours.
- **Erreurs** : ce qui plante est noté dans `%LOCALAPPDATA%\Ilot\errors.log` (256 Ko au plus). Si la notch ne peut pas
  se construire au démarrage, Ilot se ferme au lieu de rester sans rien afficher (ce qui bloquerait le lancement
  suivant, une seule instance étant permise).

## Lancer

Vous reprenez le projet ? Commencez par [HANDOFF.md](../HANDOFF.md) : règles de la maison, état des modules, pièges
connus, et un prompt de reprise pour un agent IA.

Prérequis : Windows 10 2004+ ou Windows 11, et le SDK .NET 10 (`winget install Microsoft.DotNet.SDK.10`).
Le Windows App SDK est embarqué (*self-contained*) : aucun runtime à installer.

```powershell
dotnet run --project src/Ilot            # x64 par défaut
dotnet build Ilot.sln -p:Platform=ARM64  # autre architecture
```

Dans Visual Studio (2026 recommandé, charge de travail « Développement d'applications WinUI ») : ouvrir
`Ilot.sln`, choisir la plateforme puis F5.

## Structure

```
src/Ilot/
├── App.xaml(.cs)              Racine de composition (DI), instance unique
├── Composition/               Moteur de mouvement, sans dépendance à la notch
│   ├── Spring.cs              Oscillateur amorti résolu analytiquement (réponse / amortissement)
│   ├── SpringChannel.cs       Ressort interruptible qui conserve la vitesse, exécuté par le compositeur
│   ├── Motion.cs              Jetons de motion design : tous les ressorts de l'app
│   ├── ImplicitMotion.cs      Animations implicites à ressort (listes : glisser, apparaître, disparaître)
│   └── CompositionExpression.cs
├── Notch/                     La notch, indépendante de la fenêtre
│   ├── NotchLayout.cs         Géométrie (tailles, rayons, style Notch / Island)
│   ├── NotchController.cs     Machine à états : survol, délais, verrouillage
│   ├── NotchMotion.cs         État animé partagé (n.Width, n.Height, n.Expansion…)
│   ├── NotchSurface.cs        Rendu Composition : forme, épaules, liseré, ombre
│   ├── NotchView.xaml(.cs)    Orchestration : modules et onglets, pointeur → contrôleur → morphing → région
│   ├── NotchContext.cs        Ce que reçoit un module : mouvement, onglet, état ouvert, « garder ouverte »
│   └── INotchModule.cs        Contrat d'un module : titre et icône d'onglet, vue
├── Quick/                     Volume et luminosité à gauche : QuickBubble (anneau, molette, glisser, clic),
│                              BrightnessService (luminosité par le mode d'alimentation, powrprof), QuickServices
├── Mascot/                    La mascotte : MascotRig (formes du compositeur, ressorts), MascotMood (humeurs et poses),
│                              MascotView (clignements, sommeil, regard, réactions), MascotPalette (couleurs),
│                              UserActivity (dernière entrée clavier / souris, et la suivante)
├── Modules/Media/             Le lecteur multimédia
│   ├── MediaSessionService.cs Session SMTC (GlobalSystemMediaTransportControlsSessionManager)
│   ├── MediaSnapshot.cs       Instantané immuable de la session
│   ├── ArtworkPalette.cs      Couleur d'accent extraite de la pochette
│   ├── MediaViewModel.cs      État de présentation (MVVM, CommunityToolkit)
│   ├── MediaView.xaml(.cs)    Vue et chorégraphie
│   ├── Audio/                 Visualiseur : capture loopback (NAudio), FFT, bandes de fréquences ; volume système (AudioService),
│   │                          appareils audio (AudioDeviceMonitor), changements de volume (VolumeWatcher)
│   ├── AudioPopupView         La carte « Connecté » d'un nouvel appareil audio
│   └── Lyrics/                Paroles : service LRCLIB + cache, parseur LRC, synchro, panneau animé
├── Modules/Clipboard/         Le gestionnaire de presse-papier ; ClipboardPreview (l'aperçu qui retouche) et
│                              ImagePixels (les pixels d'une image retouchée : crayon, rognage, PNG)
│   ├── ClipboardMonitor.cs    Notifications de Windows (AddClipboardFormatListener), sans scrutation
│   ├── ClipboardShortcut.cs   Raccourci (Windows + V par défaut) : hook clavier, enregistrement d'un nouveau
│   ├── KeyGesture.cs          Un raccourci : modificateurs + touche, texte « Win+V », noms des touches
│   ├── SystemClipboard.cs     Lecture / écriture asynchrone, contenus privés ignorés
│   ├── ClipboardEntry.cs      Un élément copié : texte, lien, couleur (HexColor.cs) ou image
│   ├── ClipboardHistory.cs    L'historique : doublons, épingles, limite, copies de la notch elle-même
│   ├── ClipboardStore.cs      clipboard.json + images PNG, écrits en différé hors du thread UI
│   ├── ClipboardViewModel.cs  Actions : recopier, RVB, ouvrir, enregistrer, épingler, supprimer
│   └── ClipboardView / Card   La rangée de tuiles et chaque tuile
├── Modules/DropZone/          Le dépôt de fichiers et le chat Wi-Fi local
│   ├── FileUploadService.cs   Envoi : file, progression, vitesse, pause réseau, reprise
│   ├── Hosts/                 Un fichier par hébergeur (Send chiffré, Gofile) derrière IUploadHost
│   ├── Local/                 Partage et chat Wi-Fi local (LocalTransferService, MdnsResponder, LocalPage, LocalMessage)
│   ├── ProgressStreamContent  Corps de requête qui compte les octets envoyés
│   ├── UploadQueueStore.cs    File d'attente sur disque (UploadsQueue\), archives ZIP comprises
│   ├── UploadItem.cs          Un envoi : état, progression, lien, expiration
│   ├── DropZoneViewModel.cs   Activité de la notch fermée, annonces, copie du lien
│   ├── LocalShareViewModel.cs État du partage Wi-Fi local, chat en direct, collage (fichiers, images, liens, texte)
│   └── DropZoneView / UploadCard / LocalMessageCard  La zone de dépôt, la bulle de chat et les cartes
├── Modules/Code/              Les projets VS Code récents
│   ├── CodeService.cs         Lecture de l'historique (SQLite, workspaceStorage), lancement de VS Code
│   ├── ProjectStack.cs        Le langage / framework d'un projet et sa couleur
│   ├── CodeViewModel.cs       Liste, messages, actions (ouvrir, emplacement, copier le chemin)
│   └── CodeShelf / CodeRow    La liste des projets (dans la goutte de la recherche) et chaque ligne
├── Modules/Pomodoro/          Le Pomodoro : PomodoroTimer (l'horloge, sans écran), PomodoroStats (les sessions du jour),
│                              Chime (le carillon, fabriqué), PomodoroView (l'onglet, l'activité en haut, la lecture,
│                              le jeu), PomodoroCard (la fin de phase), PomodoroSettingsPanel (la bulle de réglages)
├── Modules/Notes/             Les notes
│   ├── NotesService.cs        Base SQLite notes.db : lecture à la demande, écriture différée (300 ms) en série
│   ├── Note.cs                Une note : titre, RTF et texte brut, dates, titre affiché, aperçu
│   ├── NotesViewModel.cs      Les cartes, l'écriture d'une note, compteurs, copie (RTF + texte), suppression
│   ├── NotesView.xaml(.cs)    Les cartes, la recherche, la bulle, la note détachée
│   ├── NoteSheet.xaml(.cs)    La feuille d'une note et sa barre d'outils (dans la bulle ou détachée)
│   ├── NoteCard               Une carte de note
│   └── Editor/                RichNoteEditor (RichEditBox façon Word : styles, tailles, listes, cases à cocher),
│                              MarkdownImport + MarkdownSyntax (anciennes notes en Markdown)
├── Modules/Assistant/         L'assistant (Gemini) : AssistantView (la barre), AssistantPanel (la conversation dans la
│                              goutte), AssistantConversation, GeminiClient (flux SSE, modèles), ApiKeyStore
│                              (Gestionnaire d'identification), MarkdownLite, AssistantShortcut
├── Modules/Search/            La recherche (Ctrl + Q), sans onglet
│   ├── SearchShortcut.cs      Ctrl + Q : hook clavier, comme le raccourci du presse-papier
│   ├── SearchViewModel.cs     Les sources interrogées ensemble, le classement, l'ouverture (dossier, admin, chemin)
│   ├── FuzzyMatch.cs          Correspondance sans casse ni accents : début, mots, initiales, lettres dans l'ordre
│   ├── Calculator.cs          Les calculs (descente récursive), formatés à la française
│   ├── AppCatalog.cs          Les applis du menu Démarrer (shell:AppsFolder)
│   ├── SearchCatalog.cs       Les pages de paramètres de Windows et les commandes
│   ├── SearchHistory.cs       Ce qui a été ouvert, combien et quand (search.json)
│   ├── ShellIcons.cs          Les icônes de Windows, dessinées sur un thread à elles, gardées
│   ├── SearchView, SearchResultsPanel, SearchResultRow   La barre, la bulle de résultats, une ligne
│   ├── ShellMenu.cs           Le menu de l'Explorateur d'un résultat (IContextMenu) : ses verbes, ou tout le menu
│   └── Everything/            EverythingClient (requêtes par WM_COPYDATA, sans DLL), EverythingEngine (la copie
│                              lancée hors de vue, le service, la pleine vitesse), Engine/ (Everything.exe, licence)
├── Controls/                  MotionButton (retour physique), Scrubber (barre de progression interactive),
│                              NotchSearchBox (recherche dépliable partagée), TextSearch (correspondances, surlignage),
│                              RulerPicker (une valeur choisie sur une règle graduée)
├── Localization/              Loc (T, F, Count, Apply sur l'arbre XAML, Page), English (dictionnaire), EnglishPage
├── Settings/                  Réglages (modèle observable, enregistrement JSON) et fenêtre de paramètres (onglets
│                              à gauche : Général, Poignée, Recherche, Paroles, Presse-papier, Drop Zone, Pomodoro, Assistant)
├── Modules/Study/             Révisions : StudyStore (SQLite study.db + copies des documents), StudyDocument (PDF,
│                              images, .docx, .pptx, texte), StudyGenerator (Gemini, sortie JSON imposée),
│                              StudyScheduler (répétition espacée SM-2), ReviewSession, StudyView, DeckTile,
│                              CourseSheet (la feuille d'un cours), ReviewView (la révision), StudyPaint,
│                              MindMap (cartes mentales, leur disposition), MindMapDrawing (leur dessin),
│                              MindMapCanvas (le canevas annoté), MapInk, StudySection (les sections), SectionEditor (sa bulle)
├── Welcome/                   Premier lancement : WelcomeWindow (les étapes), TourSteps (la visite de la notch, que
│                              NotchView.Tour joue avec TourCard sous la notch et son anneau)
├── Windowing/                 Fenêtre hôte : transparence, région, z-order ; DetachedWindow (une feuille sortie
│                              de la notch : arrondie comme la bulle, poignée, coins, épingle, réduire)
├── Interop/NativeMethods.cs   P/Invoke Win32 / DWM
├── Interop/WinSqlite.cs       Lecture SQLite par winsqlite3.dll (fourni avec Windows, sans paquet)
├── Interop/SqliteDatabase.cs  Base SQLite en lecture / écriture (requêtes paramétrées), même DLL
└── Themes/Notch.xaml          Couleurs, typographie, styles
```

## Comment c'est animé

Le mouvement tourne sur le **thread du compositeur** : zéro layout et zéro travail par image sur le thread UI.
Seul le visualiseur, et seulement pendant la lecture, pousse les niveaux audio à chaque image. Sans musique, le
CPU reste à ~0 %.

1. **Un état partagé.** `NotchMotion` porte un `CompositionPropertySet` (`n`) : `Width`, `Height`, `Radius`
   (la forme) et `Expansion` (la révélation du contenu). Chaque visuel y est lié par une `ExpressionAnimation` :
   le corps, son masque, les épaules, l'ombre, et chaque élément du lecteur bougent en parfaite synchronie.
2. **Des ressorts interruptibles.** `SpringChannel` résout un vrai ressort amorti et le « cuit » en images clés
   relatives à `this.StartingValue`. Une nouvelle cible part donc exactement de la position courante et
   **conserve la vitesse** (comme SwiftUI) : entrer, sortir et rentrer en plein vol reste parfaitement fluide.
3. **Une chorégraphie dérivée de la forme.** Le lecteur n'a qu'une mise en page, l'étendue. En mode réduit, la
   pochette et l'égaliseur sont ramenés sur les bords de la pilule par des expressions. À l'ouverture ils
   glissent vers leur place : une *connected animation* pilotée par le morphing lui-même. Titre, artiste et
   boutons apparaissent dans la seconde moitié et disparaissent en premier à la fermeture.
4. **Des onglets sans layout.** Toutes les vues de modules sont superposées en permanence. Chaque module a son
   état de sélection animé (`t.Selected`, `t.Direction`) : changer d'onglet ne fait que lancer deux ressorts,
   et le contenu sortant glisse d'un côté pendant que l'entrant arrive de l'autre. La pastille de sélection a un
   ressort par bord : celui qui mène part plus vite, elle s'étire puis se rassemble sur le nouvel onglet.
5. **Des listes vivantes.** Les tuiles du presse-papier portent des animations implicites : quand une copie
   arrive, remonte ou s'en va, la mise en page XAML déplace les tuiles et le compositeur les fait glisser sur un
   ressort jusqu'à leur nouvelle place.

Le ressenti se règle dans `Composition/Motion.cs` (réponse, amortissement) et la géométrie dans
`Notch/NotchLayout.cs`. Par exemple, `NotchStyle.Island` donne une capsule flottante comme sur l'iPhone.

## Ajouter un module

Un module est un service qui implémente `INotchModule` : un titre et une icône (son onglet), et sa vue. Il
reçoit un `NotchContext` :

- `ContentTop` : où commence son contenu, sous la barre d'onglets ;
- `Bind(…)` : une expression où `n` est l'état de la notch et `t` la sélection de son onglet, avec les
  formules prêtes `NotchContext.TabOpacity` et `NotchContext.TabOffset` pour la transition d'onglet ;
- `IsExpanded`, `IsSelected`, `IsVisible` et leurs événements, pour ne travailler que quand on le voit ;
- `TakeKeyboard()` pour saisir du texte : la notch ne prend jamais le clavier d'elle-même ; elle le prend à
  l'application en cours et le lui rend ensuite ;
- `SetActivity()` pour une activité en direct sur la pilule fermée (texte, anneau de progression), et
  `AcceptDrops()` pour recevoir ce qui est glissé sur la notch (elle s'ouvre alors sur l'onglet du module) ;
- `HoldOpen()` pour garder la notch ouverte pendant une interaction, `RequestCompactWidth()` pour élargir la
  pilule fermée (comme une activité en direct) et `Announce()` pour y afficher un bref message (la
  pilule s'étend vers la droite, `n.Extra` ; le contenu compact du bord droit s'efface devant `n.Toast`).

```csharp
public sealed class TimerModule : INotchModule
{
    public string Title => "Minuteur";

    public string Glyph => "";

    public FrameworkElement CreateView(NotchContext context)
    {
        var view = new TimerView { Margin = new Thickness(0, context.ContentTop, 0, 0) };
        var visual = ElementCompositionPreview.GetElementVisual(view);
        ElementCompositionPreview.SetIsTranslationEnabled(view, true);

        // Apparaître avec l'ouverture de la notch, glisser avec l'onglet :
        context.Bind($"Clamp((n.Expansion - 0.45) * 2.4, 0, 1) * {NotchContext.TabOpacity}").Start(visual, "Opacity");
        context.Bind($"Vector3({NotchContext.TabOffset}, 0, 0)").Start(visual, "Translation");
        return view;
    }
}
```

Puis l'enregistrer dans `App.ConfigureServices` : `services.AddSingleton<INotchModule, TimerModule>();`. Les
onglets suivent l'ordre d'enregistrement.

## Choix techniques

- **Transparence** : `DwmEnableBlurBehindWindow` avec une région de flou vide (canal alpha honoré, sans flou),
  plus un `SystemBackdrop` totalement transparent côté XAML.
- **Clic à travers** : une région de fenêtre (`SetWindowRgn`) épouse la notch. Elle s'agrandit au début d'une
  ouverture et ne rétrécit qu'une fois le ressort stabilisé, pour ne jamais rogner l'animation.
- **Matériau** : un backdrop système Mica / Acrylic remplit toujours le rectangle entier de la fenêtre. Ici, la
  fenêtre est donc entièrement transparente, et la notch est dessinée par le compositeur : noir mat
  anticrénelé, liseré lumineux, et une lueur aux couleurs de la pochette. Cette lueur est ancrée au bord
  gauche *vivant* de la forme et s'éteint avant chaque bord : la silhouette reste d'un noir pur, raccord avec
  les épaules et le bord de l'écran, à chaque instant du morphing.
- **Ombre** : dégradé radial lié au morphing. Un `DropShadow` de Composition se comporte mal sur une fenêtre
  à transparence par pixel : il ignore son opacité et transparaît à travers le contenu.
- **Visualiseur** : capture WASAPI en *loopback* du périphérique de sortie par défaut (ce que jouent réellement
  les haut-parleurs), FFT de 4096 points sur le thread de capture, 24 bandes logarithmiques avec compensation
  +3 dB/octave et gain automatique. La capture ne tourne que pendant la lecture et suit les changements de
  périphérique ; sans périphérique, les barres gardent un mouvement organique de repli.
- **Paroles** : `/api/get` exact (titre, artiste, album, durée) puis `/api/search` en repli, avec quelques
  nettoyages pour les titres de vidéos (« (Official Video) », chaînes « VEVO » / « - Topic », « Artiste - Titre »).
  Les requêtes simultanées pour un même morceau sont partagées ; les absences de paroles sont mises en cache,
  pas les erreurs réseau. La synchro n'interroge rien : un minuteur unique est armé pour l'instant exact de la
  ligne suivante, recalé à chaque mise à jour du lecteur, et ne tourne que notch ouverte. Chacune des trois
  lignes affichées a une « position » animée par ressort, dont l'opacité, la taille et la place découlent.
- **Scrubber** : la barre avance seule sur le compositeur (une animation linéaire par mise à jour du lecteur,
  rien par image) ; la recherche passe par `TryChangePlaybackPositionAsync` et l'interface bouge de façon
  optimiste, en ignorant les positions périmées que certains lecteurs renvoient juste après un saut.
- **Presse-papier** : une fenêtre invisible (*message-only*) reçoit `WM_CLIPBOARDUPDATE`. Rien ne tourne entre
  deux copies, et les notifications d'une même copie (une par format) sont regroupées en une seule lecture.
  Les formats `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore` et
  `CanIncludeInClipboardHistory = 0`, posés par les gestionnaires de mots de passe, sont respectés. Une image est
  reconnue par l'empreinte SHA-256 de ses pixels (hachée hors du thread UI). Ce que la notch remet dans le
  presse-papier n'est pas réenregistré. L'image enregistrée va dans `Images\Ilot` et s'ouvre dans
  l'Explorateur : un sélecteur de fichier s'ouvrirait derrière la fenêtre de la notch, toujours au premier plan.
- **Recherche de fichiers** : rien n'est réinventé, Everything fait ce qu'il fait mieux que tout. La notch lui parle
  comme son SDK, mais sans sa DLL : une requête `EVERYTHING_IPC_QUERY2` envoyée en `WM_COPYDATA` à sa fenêtre cachée
  (nom, dossier, taille, date, nombre d'ouvertures, parties surlignées), la réponse reçue de même par une fenêtre
  invisible de la notch, en quelques dizaines de millisecondes. Les 150 fichiers les plus récents qui correspondent
  sont reclassés par la notch. La copie tourne comme l'instance par défaut d'Everything : le service ne sert que
  celle-là (une instance nommée chercherait un service à son nom).
