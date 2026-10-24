namespace Ilot.Modules.Search;

/// <summary>A page of Windows' settings, and the words people look for it with.</summary>
public sealed record SettingsPage(string Title, string Uri, string Glyph, string Keywords);

/// <summary>Something the system does at once (lock, sleep…).</summary>
public sealed record SystemCommand(string Id, string Title, string Glyph, string Keywords, bool NeedsConfirmation = false);

/// <summary>What the search knows without looking: Windows' settings pages and the system's commands.</summary>
public static class SearchCatalog
{
    public static IReadOnlyList<SettingsPage> Settings { get; } =
    [
        new("Bluetooth et appareils", "ms-settings:bluetooth", "", "bluetooth appareils casque souris clavier airpods"),
        new("Wi-Fi", "ms-settings:network-wifi", "", "wifi wi-fi reseau internet sans fil"),
        new("Réseau et Internet", "ms-settings:network-status", "", "reseau internet connexion ethernet"),
        new("Ethernet", "ms-settings:network-ethernet", "", "ethernet cable reseau"),
        new("VPN", "ms-settings:network-vpn", "", "vpn reseau prive"),
        new("Proxy", "ms-settings:network-proxy", "", "proxy reseau"),
        new("Mode avion", "ms-settings:network-airplanemode", "", "avion mode avion"),
        new("Point d'accès mobile", "ms-settings:network-mobilehotspot", "", "hotspot partage connexion point d'acces"),
        new("Son", "ms-settings:sound", "", "son audio volume haut-parleurs micro sortie entree"),
        new("Volume des applications", "ms-settings:apps-volume", "", "volume mixeur applications son"),
        new("Affichage", "ms-settings:display", "", "ecran affichage resolution luminosite echelle moniteur"),
        new("Éclairage nocturne", "ms-settings:nightlight", "", "nuit eclairage nocturne lumiere bleue"),
        new("Graphiques", "ms-settings:display-advancedgraphics", "", "gpu carte graphique performances"),
        new("Arrière-plan", "ms-settings:personalization-background", "", "fond d'ecran arriere-plan image wallpaper"),
        new("Couleurs", "ms-settings:colors", "", "couleurs mode sombre clair theme accent"),
        new("Thèmes", "ms-settings:themes", "", "themes personnalisation"),
        new("Écran de verrouillage", "ms-settings:lockscreen", "", "verrouillage ecran lock"),
        new("Barre des tâches", "ms-settings:taskbar", "", "barre des taches taskbar"),
        new("Démarrer", "ms-settings:personalization-start", "", "menu demarrer start"),
        new("Applications installées", "ms-settings:appsfeatures", "", "applications installees desinstaller programmes"),
        new("Applications par défaut", "ms-settings:defaultapps", "", "applications par defaut navigateur ouvrir avec"),
        new("Applications au démarrage", "ms-settings:startupapps", "", "demarrage lancement automatique startup"),
        new("Notifications", "ms-settings:notifications", "", "notifications alertes"),
        new("Assistant de concentration", "ms-settings:quiethours", "", "concentration focus ne pas deranger"),
        new("Alimentation et batterie", "ms-settings:powersleep", "", "alimentation batterie veille energie"),
        new("Économiseur de batterie", "ms-settings:batterysaver", "", "economiseur batterie"),
        new("Stockage", "ms-settings:storagesense", "", "stockage disque espace nettoyage"),
        new("Multitâche", "ms-settings:multitasking", "", "multitache fenetres snap bureaux"),
        new("Presse-papiers", "ms-settings:clipboard", "", "presse-papiers clipboard historique"),
        new("Souris", "ms-settings:mousetouchpad", "", "souris curseur pointeur"),
        new("Pavé tactile", "ms-settings:devices-touchpad", "", "pave tactile touchpad gestes"),
        new("Saisie", "ms-settings:typing", "", "saisie clavier correction"),
        new("Langue et région", "ms-settings:regionlanguage", "", "langue region clavier format"),
        new("Date et heure", "ms-settings:dateandtime", "", "date heure fuseau horloge"),
        new("Vos informations", "ms-settings:yourinfo", "", "compte informations profil"),
        new("Options de connexion", "ms-settings:signinoptions", "", "connexion mot de passe code pin windows hello"),
        new("Windows Update", "ms-settings:windowsupdate", "", "mise a jour update windows"),
        new("Confidentialité et sécurité", "ms-settings:privacy", "", "confidentialite securite autorisations"),
        new("Sécurité Windows", "ms-settings:windowsdefender", "", "securite antivirus defender pare-feu"),
        new("Caméra", "ms-settings:privacy-webcam", "", "camera webcam"),
        new("Microphone", "ms-settings:privacy-microphone", "", "microphone micro"),
        new("Localisation", "ms-settings:privacy-location", "", "localisation position gps"),
        new("Imprimantes et scanners", "ms-settings:printers", "", "imprimante scanner impression"),
        new("Projeter sur ce PC", "ms-settings:project", "", "projeter projection miracast"),
        new("Accessibilité", "ms-settings:easeofaccess", "", "accessibilite"),
        new("Loupe", "ms-settings:easeofaccess-magnifier", "", "loupe zoom"),
        new("Narrateur", "ms-settings:easeofaccess-narrator", "", "narrateur lecture voix"),
        new("Game Bar", "ms-settings:gaming-gamebar", "", "jeux game bar xbox capture"),
        new("Mode développeur", "ms-settings:developers", "", "developpeur developer mode"),
        new("Récupération", "ms-settings:recovery", "", "recuperation reinitialiser restauration"),
        new("Activation", "ms-settings:activation", "", "activation licence cle produit"),
        new("Dépannage", "ms-settings:troubleshoot", "", "depannage probleme resoudre"),
        new("Informations système", "ms-settings:about", "", "a propos informations systeme pc nom specifications"),
    ];

    public static IReadOnlyList<SystemCommand> Commands { get; } =
    [
        new("lock", "Verrouiller", "", "verrouiller lock session ecran"),
        new("sleep", "Mettre en veille", "", "veille sleep suspendre"),
        new("restart", "Redémarrer", "", "redemarrer restart reboot", NeedsConfirmation: true),
        new("shutdown", "Arrêter le PC", "", "arreter eteindre shutdown", NeedsConfirmation: true),
        new("logoff", "Se déconnecter", "", "deconnecter deconnexion logoff session", NeedsConfirmation: true),
        new("recycle", "Vider la corbeille", "", "vider corbeille poubelle", NeedsConfirmation: true),
    ];
}
