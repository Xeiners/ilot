namespace MyNotch.Localization;

/// <summary>
/// The local Wi-Fi page the phone opens (Modules/DropZone/Local/LocalPage.cs), in English: its French phrases, each
/// with what surrounds it in the HTML or the script (quotes, tags, attributes), so that nothing else is touched, not
/// even a chat message that happens to say the same.
/// </summary>
internal static class EnglishPage
{
    /// <summary>Longest first: a phrase that contains another is replaced whole.</summary>
    public static readonly (string French, string English)[] Phrases = new (string, string)[]
    {
        ("lang=\"fr\"", "lang=\"en\""),
        (">MyNotch · Wi-Fi local<", ">MyNotch · Local Wi-Fi<"),
        ("content:\"Déposez pour envoyer au PC\"", "content:\"Drop to send to the PC\""),
        ("aria-label=\"Fermer\"", "aria-label=\"Close\""),
        ("alt=\"Aperçu\"", "alt=\"Preview\""),
        (">Télécharger<", ">Download<"),
        (">Wi-Fi local<", ">Local Wi-Fi<"),
        ("<b>Chat et partage prêts</b>", "<b>Chat and sharing ready</b>"),
        ("Écrivez un message, collez une image ou un lien, ou envoyez des fichiers : tout s'affiche en direct dans la notch.",
            "Write a message, paste an image or a link, or send files: everything shows up live in the notch."),
        ("<span>Coller</span>", "<span>Paste</span>"),
        ("<span>Fichiers</span>", "<span>Files</span>"),
        ("placeholder=\"Message ou lien…\"", "placeholder=\"Message or link…\""),
        ("title=\"Envoyer\"", "title=\"Send\""),
        (")) + ' Ko'", ")) + ' KB'"),
        (".replace('.', ',') + ' Mo'", " + ' MB'"),
        (".replace('.', ',') + ' Go'", " + ' GB'"),
        ("'moins d’1 s'", "'under 1 s'"),
        ("' en '", "' in '"),
        ("'Copié dans le presse-papier'", "'Copied to the clipboard'"),
        ("'Copié'", "'Copied'"),
        ("'Connexion…'", "'Connecting…'"),
        ("'Téléchargé · '", "'Downloaded · '"),
        ("'Échec, touchez pour réessayer'", "'Failed, tap to retry'"),
        (">Copier<", ">Copy<"),
        (">Ouvrir<", ">Open<"),
        (">Aperçu<", ">Preview<"),
        ("'Accepté par le PC · '", "'Accepted by the PC · '"),
        ("'Refusé par le PC'", "'Refused by the PC'"),
        ("'En attente de l’accord du PC · '", "'Waiting for the PC to accept · '"),
        ("'Depuis le PC'", "'From the PC'"),
        ("<span>Cet appareil · Envoi…</span>", "<span>This device · Sending…</span>"),
        ("'Cet appareil'", "'This device'"),
        (">Réessayer<", ">Retry<"),
        ("'Dossier · '", "'Folder · '"),
        ("'Envoi impossible'", "'Could not send'"),
        ("'Image collée.png'", "'Pasted image.png'"),
        ("'Échec de l’envoi'", "'Sending failed'"),
        ("'Envoyé · en attente de l’accord du PC…'", "'Sent · waiting for the PC to accept…'"),
        ("'Texte collé et envoyé'", "'Text pasted and sent'"),
        ("'Collé et envoyé'", "'Pasted and sent'"),
        ("'Presse-papier.'", "'Clipboard.'"),
        ("'Maintenez appuyé dans le champ (ou Ctrl+V) pour coller'", "'Press and hold in the field (or Ctrl+V) to paste'"),
        (">Code de la notch<", ">The notch's code<"),
    }.OrderByDescending(pair => pair.Item1.Length).ToArray();
}
