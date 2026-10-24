using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;

namespace Ilot.Localization;

/// <summary>
/// The notch's languages: French, as written everywhere in the code and the XAML, and English, from a dictionary keyed
/// by the French text itself (<see cref="English"/>). Chosen once at startup (settings: the system's, French or
/// English); a text without its translation simply stays in French, so nothing can break for want of one.
/// </summary>
/// <remarks>
/// Code goes through <see cref="T"/> (and <see cref="F"/> for texts with values in them). XAML stays as written: each
/// view calls <see cref="Apply"/> after <c>InitializeComponent</c>, which translates its texts in place (text blocks
/// and runs, buttons' contents, placeholders, headers, menus, tooltips, accessible names, combo box items).
/// </remarks>
public static class Loc
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The notch speaks English (else French, as written).</summary>
    public static bool IsEnglish { get; private set; }

    /// <summary>For dates and numbers shown: "21,6" and "3 oct." in French, "21.6" and "Oct 3" in English.</summary>
    public static CultureInfo Culture => IsEnglish ? EnglishCulture : French;

    /// <summary>
    /// The language for this run, from the settings: "fr", "en", or "auto" (French when Windows is in French, else
    /// English). Before any window is made.
    /// </summary>
    public static void Initialize(string language) => IsEnglish = language switch
    {
        "fr" => false,
        "en" => true,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "fr",
    };

    /// <summary>The text in the notch's language (the French one when it has no translation).</summary>
    public static string T(string french) =>
        IsEnglish && french.Length > 0 && English.Strings.TryGetValue(french, out string? english) ? english : french;

    /// <summary>A text with values in it ("Il y a {0} min"), translated, then filled in the notch's culture.</summary>
    public static string F(string french, params object?[] values) => string.Format(Culture, T(french), values);

    /// <summary>"1 mot" / "3 mots": the French singular or plural, translated, with the count in front.</summary>
    public static string Count(long count, string one, string many) =>
        $"{count.ToString("N0", Culture)} {T(count > 1 || (IsEnglish && count == 0) ? many : one)}";

    /// <summary>The local Wi-Fi page for the phone (HTML and its script), in the notch's language.</summary>
    public static string Page(string html)
    {
        if (!IsEnglish)
        {
            return html;
        }

        foreach (var (french, english) in EnglishPage.Phrases)
        {
            html = html.Replace(french, english, StringComparison.Ordinal);
        }

        return html;
    }

    /// <summary>
    /// Translates a view made in XAML, in place, from <paramref name="root"/> down: what it shows and says (to screen
    /// readers too), menus and tooltips included. Nothing to do in French.
    /// </summary>
    public static void Apply(object? root)
    {
        if (IsEnglish && root is not null)
        {
            Translate(root);
        }
    }

    private static void Translate(object item)
    {
        if (item is DependencyObject element)
        {
            TranslateAttached(element);
        }

        switch (item)
        {
            case TextBlock text:
                TranslateInlines(text.Inlines);
                break;

            case RichTextBlock rich:
                foreach (var block in rich.Blocks)
                {
                    if (block is Paragraph paragraph)
                    {
                        TranslateInlines(paragraph.Inlines);
                    }
                }

                break;

            case TextBox box:
                box.PlaceholderText = T(box.PlaceholderText);
                TranslateHeader(box.Header, header => box.Header = header);
                break;

            case ToggleSwitch toggle:
                TranslateHeader(toggle.Header, header => toggle.Header = header);
                TranslateHeader(toggle.OnContent, content => toggle.OnContent = content);
                TranslateHeader(toggle.OffContent, content => toggle.OffContent = content);
                break;

            case ComboBox combo:
                combo.PlaceholderText = T(combo.PlaceholderText);
                TranslateHeader(combo.Header, header => combo.Header = header);
                TranslateItems(combo.Items);
                break;

            case Slider slider:
                TranslateHeader(slider.Header, header => slider.Header = header);
                break;

            case NavigationView navigation:
                foreach (var entry in navigation.MenuItems)
                {
                    Translate(entry);
                }

                if (navigation.Content is not null)
                {
                    Translate(navigation.Content);
                }

                break;

            case Expander expander:
                TranslateHeader(expander.Header, header => expander.Header = header);
                if (expander.Content is not null)
                {
                    Translate(expander.Content);
                }

                break;

            case ContentControl control:
                if (control.Content is string content)
                {
                    control.Content = T(content);
                }
                else if (control.Content is not null)
                {
                    Translate(control.Content);
                }

                if (control is Button { Flyout: { } flyout })
                {
                    Translate(flyout);
                }

                break;

            case UserControl user when user.Content is not null:
                Translate(user.Content);
                break;

            case Panel panel:
                foreach (var child in panel.Children)
                {
                    Translate(child);
                }

                break;

            case Border border when border.Child is not null:
                Translate(border.Child);
                break;

            case Viewbox box when box.Child is not null:
                Translate(box.Child);
                break;

            case ItemsControl items:
                TranslateItems(items.Items);
                break;

            case MenuFlyout menu:
                foreach (var entry in menu.Items)
                {
                    Translate(entry);
                }

                break;

            case MenuFlyoutSubItem sub:
                sub.Text = T(sub.Text);
                foreach (var entry in sub.Items)
                {
                    Translate(entry);
                }

                break;

            case MenuFlyoutItem entry:
                entry.Text = T(entry.Text);
                break;

            case Flyout { Content: { } flyoutContent }:
                Translate(flyoutContent);
                break;

            case Window window when window.Content is not null:
                Translate(window.Content);
                break;
        }
    }

    /// <summary>What any element may carry: its tooltip, its accessible name, its context menu.</summary>
    private static void TranslateAttached(DependencyObject element)
    {
        switch (ToolTipService.GetToolTip(element))
        {
            case string tip:
                ToolTipService.SetToolTip(element, T(tip));
                break;
            case ToolTip { Content: string tip } toolTip:
                toolTip.Content = T(tip);
                break;
        }

        string name = AutomationProperties.GetName(element);
        if (name.Length > 0)
        {
            AutomationProperties.SetName(element, T(name));
        }

        if (element is UIElement { ContextFlyout: { } menu })
        {
            Translate(menu);
        }
    }

    private static void TranslateInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    run.Text = T(run.Text);
                    break;
                case Span span:
                    TranslateInlines(span.Inlines);
                    break;
            }
        }
    }

    private static void TranslateItems(ItemCollection items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is string text)
            {
                items[i] = T(text);
            }
            else if (items[i] is not null)
            {
                Translate(items[i]);
            }
        }
    }

    private static void TranslateHeader(object? header, Action<object> set)
    {
        if (header is string text)
        {
            set(T(text));
        }
        else if (header is not null)
        {
            Translate(header);
        }
    }
}
