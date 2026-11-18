using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ilot.Interop;
using Ilot.Localization;

namespace Ilot.Modules.Assistant;

/// <summary>A command the assistant may run: the program found (its full path), and its arguments.</summary>
public sealed record ShellCommand(string Program, string Path, IReadOnlyList<string> Args)
{
    /// <summary>As the user reads it before allowing it.</summary>
    public string Line => string.Join(' ', new[] { Program }.Concat(Args.Select(arg => arg.Contains(' ', StringComparison.Ordinal) ? $"\"{arg}\"" : arg)));
}

/// <summary>What a command gave: its exit code, its output (cut if long), whether it was stopped for taking too long.</summary>
public sealed record ShellResult(int ExitCode, string Output, bool Truncated, bool TimedOut)
{
    /// <summary>All of its output that was read (far longer than <see cref="Output"/>, which Gemini gets): the user sees it.</summary>
    public string Whole { get; init; } = Output;
}

/// <summary>
/// The commands the assistant may run (its « execute_shell_command » tool), and how they run: a list of programs that
/// only read (network and system diagnostics, git's read commands, versions), each with the arguments it may take; no
/// shell at all (the program itself is started, its arguments passed one by one: nothing is interpreted, no pipe, no
/// redirection, no second command); found where Windows keeps it (System32) or on the PATH, never in the current
/// folder; stopped after a while; its output cut. The user still allows each one (see AssistantTools).
/// </summary>
public static partial class ShellCommands
{
    /// <summary>A command still running after this long is stopped (and what it wrote so far given).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The output given back is cut there (Gemini reads all of it: a long one costs time and quota).</summary>
    public const int MostOutput = 6000;

    /// <summary>The output read at most (the rest is drained, so the program never waits on a full pipe).</summary>
    private const int MostRead = 64 * 1024;

    private const int LongestLine = 400;

    private static readonly string[] Remote = ["/s", "/u", "/p", "-s", "-u", "-p"];

    // Arguments git never gets: they write files or run programs (and none of them is needed to read).
    private static readonly string[] GitBanned = ["--output", "--exec", "--upload-pack", "--receive-pack", "--config-env", "--open-files-in-pager"];

    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        // Network and system diagnostics (Windows' own, in System32).
        ["ipconfig"] = new(true, args => args.All(arg => arg.ToLowerInvariant() is "/all" or "/displaydns") ? null : Loc.T("ipconfig : seulement /all ou /displaydns.")),
        ["ping"] = new(true, Ping),
        ["nslookup"] = new(true, args => args.Count is >= 1 and <= 3 && !args.Contains("-") ? null : Loc.T("nslookup : un nom à chercher (et un serveur, au plus).")),
        ["hostname"] = new(true, args => args.Count == 0 ? null : Loc.T("hostname : sans argument.")),
        ["whoami"] = new(true, args => args.All(arg => arg.ToLowerInvariant() is "/all" or "/user" or "/groups" or "/priv" or "/upn" or "/fqdn" or "/logonid") ? null : Loc.T("whoami : seulement /all, /user, /groups ou /priv.")),
        ["systeminfo"] = new(true, Local),
        ["tasklist"] = new(true, Local),
        ["getmac"] = new(true, Local),
        ["driverquery"] = new(true, Local),
        ["netstat"] = new(true, Netstat),
        ["where"] = new(true, args => args.Count is >= 1 and <= 4 ? null : Loc.T("where : le nom d'un programme à trouver.")),

        // Developer tools (on the PATH): what they are, what a repository holds.
        ["git"] = new(false, Git, Utf8: true),
        ["dotnet"] = new(false, args => Only(args, "--info", "--version", "--list-sdks", "--list-runtimes")),
        ["node"] = new(false, args => Only(args, "--version", "-v")),
        ["python"] = new(false, args => Only(args, "--version", "-V")),
        ["py"] = new(false, args => Only(args, "--version", "-V", "--list", "-0")),
        ["java"] = new(false, args => Only(args, "-version", "--version")),
        ["winget"] = new(false, Winget, Utf8: true),
    };

    /// <summary>The programs allowed, as said to Gemini and to the user.</summary>
    public static IEnumerable<string> Allowed => Rules.Keys;

    /// <summary>
    /// <paramref name="line"/> as a command that may run (its program found), or why not: the reason, for the user and
    /// for Gemini. Nothing is run here.
    /// </summary>
    public static (ShellCommand? Command, string? Refusal) Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
        {
            return (null, Loc.T("Commande vide."));
        }

        if (line.Length > LongestLine)
        {
            return (null, Loc.T("Commande trop longue."));
        }

        if (line.IndexOfAny(['&', '|', '<', '>', '^', '`', '\r', '\n', ';']) >= 0 || line.Contains("$(", StringComparison.Ordinal))
        {
            return (null, Loc.T("Une seule commande, sans tube, redirection ni enchaînement."));
        }

        if (Split(line) is not { Count: > 0 } words)
        {
            return (null, Loc.T("Guillemets mal fermés."));
        }

        string program = words[0];
        if (program.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            return (null, Loc.T("Le nom du programme seul, sans chemin."));
        }

        if (program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            program = program[..^4];
        }

        if (!Rules.TryGetValue(program, out var rule))
        {
            return (null, Loc.F("« {0} » n'est pas dans la liste des commandes autorisées : {1}.", program, string.Join(", ", Allowed)));
        }

        var args = words.Skip(1).ToList();
        if (rule.Check(args) is { } why)
        {
            return (null, why);
        }

        program = program.ToLowerInvariant();
        string? path = rule.System ? InSystem(program) : OnPath(program);
        if (path is null)
        {
            return (null, Loc.F("« {0} » n'est pas installé sur ce PC.", program));
        }

        if (program == "winget" && args.Count > 0 && args[0].ToLowerInvariant() is "list" or "search" or "show")
        {
            args.Add("--disable-interactivity"); // A question it would ask would never be answered.
        }

        return (new ShellCommand(program, path, args), null);
    }

    /// <summary>Runs <paramref name="command"/> in <paramref name="folder"/> (the user's folder by default), its output read as it comes.</summary>
    public static async Task<ShellResult> RunAsync(ShellCommand command, string? folder, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        var encoding = Rules[command.Program].Utf8 ? Encoding.UTF8 : Console();
        var start = new ProcessStartInfo(command.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            WorkingDirectory = folder is { Length: > 0 } && Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (string arg in command.Args)
        {
            start.ArgumentList.Add(arg);
        }

        // git: never a password asked for, nothing written to the repository by reading it.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Not started.");
        process.StandardInput.Close(); // A program that asks for something gets nothing, at once.
        var output = new StringBuilder();
        var reading = Task.WhenAll(Drain(process.StandardOutput, output), Drain(process.StandardError, output));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout ?? Timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended meanwhile.
            }

            cancellation.ThrowIfCancellationRequested();
            timedOut = true;
        }

        await reading;
        string text;
        lock (output)
        {
            text = output.ToString().Trim();
        }

        bool truncated = text.Length > MostOutput;
        return new ShellResult(timedOut ? -1 : process.ExitCode, truncated ? text[..MostOutput] : text, truncated, timedOut) { Whole = text };
    }

    /// <summary>A stream read to its end, the first <see cref="MostRead"/> characters kept.</summary>
    private static async Task Drain(StreamReader reader, StringBuilder into)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            lock (into)
            {
                if (into.Length < MostRead)
                {
                    into.Append(buffer, 0, Math.Min(read, MostRead - into.Length));
                }
            }
        }
    }

    /// <summary>The words of <paramref name="line"/>, a quoted one kept whole (« --grep="fix bug" »); <c>null</c> if a quote is left open.</summary>
    internal static List<string>? Split(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    any = false;
                }
            }
            else
            {
                word.Append(c);
                any = true;
            }
        }

        if (quoted)
        {
            return null;
        }

        if (any)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    // ── The rules ───────────────────────────────────────────────────────────────────────────────────────────

    /// <param name="System">Windows' own (found in System32), else found on the PATH.</param>
    /// <param name="Check">Why these arguments are not allowed; <c>null</c> if they are.</param>
    /// <param name="Utf8">Writes UTF-8 (else the console's code page).</param>
    private sealed record Rule(bool System, Func<List<string>, string?> Check, bool Utf8 = false);

    private static string? Only(List<string> args, params string[] allowed) =>
        args.Count == 1 && allowed.Contains(args[0]) ? null : Loc.F("Seulement : {0}.", string.Join(", ", allowed));

    /// <summary>This PC only: no other computer asked (nor a password given for it).</summary>
    private static string? Local(List<string> args) =>
        args.Any(arg => Remote.Contains(arg.ToLowerInvariant())) ? Loc.T("Seulement sur ce PC (ni /s, ni /u, ni /p).") : null;

    /// <summary>A few pings, never endless (-t), ten at most.</summary>
    private static string? Ping(List<string> args)
    {
        if (args.Count == 0)
        {
            return Loc.T("ping : une adresse à joindre.");
        }

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i].ToLowerInvariant();
            if (arg is "-t" or "/t")
            {
                return Loc.T("ping : pas de ping sans fin (-t).");
            }

            if (arg is "-n" or "/n" && (i + 1 >= args.Count || !int.TryParse(args[i + 1], out int count) || count is < 1 or > 10))
            {
                return Loc.T("ping : -n entre 1 et 10.");
            }
        }

        return null;
    }

    /// <summary>A snapshot of the connections, never one repeated every few seconds (an interval would run forever).</summary>
    private static string? Netstat(List<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i].ToLowerInvariant();
            if (arg is "-p" or "/p" && i + 1 < args.Count && args[i + 1].ToLowerInvariant() is "tcp" or "udp" or "tcpv6" or "udpv6" or "ip" or "ipv6" or "icmp" or "icmpv6")
            {
                i++;
                continue;
            }

            if (!NetstatFlags().IsMatch(arg))
            {
                return Loc.T("netstat : seulement ses options (-a, -n, -o…), sans intervalle.");
            }
        }

        return null;
    }

    [GeneratedRegex("^[-/][abefnoqrstxy]+$")]
    private static partial Regex NetstatFlags();

    /// <summary>git's commands that read: what a repository holds, never one that changes it.</summary>
    private static string? Git(List<string> args)
    {
        if (args.Count == 0)
        {
            return Loc.T("git : une commande qui lit (status, log, diff, show, branch…).");
        }

        if (args.Any(arg => arg == "-c" || GitBanned.Any(banned => arg.StartsWith(banned, StringComparison.OrdinalIgnoreCase))))
        {
            return Loc.T("git : options non autorisées.");
        }

        var rest = args.Skip(1).ToList();
        return args[0] switch
        {
            "status" or "log" or "diff" or "show" or "rev-parse" or "describe" or "shortlog" or "ls-files" or "blame" => null,
            "branch" => rest.All(arg => arg is "-a" or "-r" or "-v" or "-vv" or "--list" or "--all" or "--remotes" or "--show-current" or "--merged" or "--no-merged" or "--verbose")
                ? null : Loc.T("git branch : seulement pour lister les branches."),
            "remote" => rest.Count == 0 || (rest.Count == 1 && rest[0] is "-v" or "--verbose") || (rest.Count == 2 && rest[0] is "show" or "get-url")
                ? null : Loc.T("git remote : seulement pour lister les dépôts distants."),
            "tag" => rest.Count == 0 || rest[0] is "-l" or "--list" or "-n"
                ? null : Loc.T("git tag : seulement pour lister les étiquettes."),
            _ => Loc.T("git : seulement status, log, diff, show, branch, remote, tag, rev-parse, describe, shortlog, ls-files ou blame."),
        };
    }

    /// <summary>winget, to see what is installed or could be: never to install, update or remove.</summary>
    private static string? Winget(List<string> args) =>
        args.Count > 0 && args[0].ToLowerInvariant() is "list" or "search" or "show" or "--version" or "--info"
            ? null : Loc.T("winget : seulement list, search, show ou --version.");

    // ── Where the programs are ──────────────────────────────────────────────────────────────────────────────

    private static string? InSystem(string program)
    {
        string path = System.IO.Path.Combine(Environment.SystemDirectory, program + ".exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The program on the PATH (its folders as written there, never the current one): an .exe only (a .cmd would go through the shell).</summary>
    private static string? OnPath(string program)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string expanded = Environment.ExpandEnvironmentVariables(folder.Trim('"'));
            if (!System.IO.Path.IsPathFullyQualified(expanded))
            {
                continue;
            }

            string path = System.IO.Path.Combine(expanded, program + ".exe");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static Encoding? _console;

    /// <summary>What Windows' console programs write in, redirected: the OEM code page (850 for French).</summary>
    private static Encoding Console()
    {
        if (_console is null)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try
            {
                _console = Encoding.GetEncoding((int)NativeMethods.GetOEMCP());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                _console = Encoding.UTF8;
            }
        }

        return _console;
    }
}
