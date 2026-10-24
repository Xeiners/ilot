using System.Text.Json;
using Windows.UI;
using MyNotch.Localization;

namespace MyNotch.Modules.Code;

/// <summary>
/// What a project is made with, recognised from the files at its root (<c>.csproj</c>, <c>package.json</c>,
/// <c>pubspec.yaml</c>…), and the colour that goes with it: the colour its community knows it by.
/// </summary>
public sealed record ProjectStack(string Label, Color Color)
{
    private static readonly ProjectStack CSharp = new("C# / .NET", Rgb(0xA1, 0x79, 0xDC));
    private static readonly ProjectStack ReactNative = new("React Native", Rgb(0x61, 0xDA, 0xFB));
    private static readonly ProjectStack React = new("React", Rgb(0x61, 0xDA, 0xFB));
    private static readonly ProjectStack Next = new("Next.js", Rgb(0xE6, 0xE6, 0xE6));
    private static readonly ProjectStack Vue = new("Vue", Rgb(0x42, 0xB8, 0x83));
    private static readonly ProjectStack Angular = new("Angular", Rgb(0xF0, 0x3E, 0x5C));
    private static readonly ProjectStack Svelte = new("Svelte", Rgb(0xFF, 0x6A, 0x33));
    private static readonly ProjectStack Electron = new("Electron", Rgb(0x9F, 0xEA, 0xF9));
    private static readonly ProjectStack TypeScript = new("TypeScript", Rgb(0x4D, 0x9B, 0xE6));
    private static readonly ProjectStack Node = new("Node.js", Rgb(0x8C, 0xC8, 0x4B));
    private static readonly ProjectStack Flutter = new("Flutter", Rgb(0x4F, 0xC3, 0xF7));
    private static readonly ProjectStack Rust = new("Rust", Rgb(0xE8, 0x8F, 0x5A));
    private static readonly ProjectStack Go = new("Go", Rgb(0x00, 0xAD, 0xD8));
    private static readonly ProjectStack Python = new("Python", Rgb(0xFF, 0xD3, 0x4E));
    private static readonly ProjectStack Kotlin = new("Kotlin", Rgb(0xB1, 0x8C, 0xFF));
    private static readonly ProjectStack Java = new("Java", Rgb(0xF0, 0x9A, 0x3E));
    private static readonly ProjectStack Php = new("PHP", Rgb(0x8F, 0x93, 0xD9));
    private static readonly ProjectStack Ruby = new("Ruby", Rgb(0xE5, 0x4B, 0x4B));
    private static readonly ProjectStack Cpp = new("C / C++", Rgb(0xF3, 0x4B, 0x7D));
    private static readonly ProjectStack Web = new("Web", Rgb(0xF1, 0x65, 0x29));
    private static readonly ProjectStack Workspace = new(Loc.T("Espace de travail"), Rgb(0x9D, 0x8C, 0xFF));

    /// <summary>
    /// The stack of the project in <paramref name="folder"/>, from its root files only (a few file checks, plus
    /// <c>package.json</c> read for a JavaScript project). <c>null</c> when nothing is recognised.
    /// </summary>
    public static ProjectStack? Detect(string folder)
    {
        try
        {
            if (Has("*.sln") || Has("*.slnx") || Has("*.csproj") || Has("*.fsproj"))
            {
                return CSharp;
            }

            if (File.Exists(Path.Combine(folder, "pubspec.yaml")))
            {
                return Flutter;
            }

            if (File.Exists(Path.Combine(folder, "package.json")))
            {
                return FromPackage(Path.Combine(folder, "package.json"))
                    ?? (File.Exists(Path.Combine(folder, "tsconfig.json")) ? TypeScript : Node);
            }

            return Exists("Cargo.toml") ? Rust
                : Exists("go.mod") ? Go
                : Exists("pyproject.toml") || Exists("requirements.txt") || Exists("setup.py") || Has("*.py") ? Python
                : Exists("build.gradle.kts") || Exists("settings.gradle.kts") ? Kotlin
                : Exists("pom.xml") || Exists("build.gradle") ? Java
                : Exists("composer.json") ? Php
                : Exists("Gemfile") ? Ruby
                : Exists("CMakeLists.txt") || Has("*.vcxproj") || Has("*.cpp") || Has("*.c") ? Cpp
                : Exists("index.html") ? Web
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        bool Exists(string name) => File.Exists(Path.Combine(folder, name));

        bool Has(string pattern) => Directory.EnumerateFiles(folder, pattern).Any();
    }

    /// <summary>A workspace gathers several folders: it gets its own colour.</summary>
    public static ProjectStack ForWorkspace => Workspace;

    /// <summary>Nothing recognised: a colour all the same, always the same one for the same project.</summary>
    public static Color ColorFor(string name)
    {
        uint hash = 2166136261;
        foreach (char c in name.ToUpperInvariant())
        {
            hash = (hash ^ c) * 16777619;
        }

        return FromHue(hash % 360, saturation: 0.55, lightness: 0.66);
    }

    /// <summary>The JavaScript framework the project depends on, if a known one.</summary>
    private static ProjectStack? FromPackage(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string section in new[] { "dependencies", "devDependencies" })
            {
                if (json.RootElement.TryGetProperty(section, out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                {
                    foreach (var dependency in dependencies.EnumerateObject())
                    {
                        names.Add(dependency.Name);
                    }
                }
            }

            return names.Contains("react-native") || names.Contains("expo") ? ReactNative
                : names.Contains("next") ? Next
                : names.Contains("electron") ? Electron
                : names.Contains("@angular/core") ? Angular
                : names.Contains("vue") || names.Contains("nuxt") ? Vue
                : names.Contains("svelte") ? Svelte
                : names.Contains("react") ? React
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private static Color FromHue(double hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = lightness - chroma / 2;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return Rgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
