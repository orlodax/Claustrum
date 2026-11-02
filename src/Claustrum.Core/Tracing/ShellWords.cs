using System.Text;
using System.Text.RegularExpressions;

namespace Claustrum.Core.Tracing;

// #145: one part of a shell line read into its command: words split on unquoted whitespace, redirections taken out (their targets kept
// as what the part writes), then keywords, assignments, and wrappers with their options. What is left is the program and its arguments,
// judged a viewer, a remote talker, or neither (NOTES.md "Environment issues", the viewer filter).
internal static partial class ShellWords
{
    // The brief's viewers, the filters their output is piped through (`tee` among them: it passes its input on), the two that print
    // the agent's own words, and claustrum, whose `run --json` prints a child's whole receipt, diff included.
    private static readonly HashSet<string> viewers = new(StringComparer.Ordinal)
    {
        "cat", "head", "tail", "less", "more", "grep", "egrep", "fgrep", "rg", "awk", "find", "ls", "jq",
        "sort", "uniq", "wc", "cut", "tr", "nl", "column", "tee", "echo", "printf", "claustrum",
    };

    // What prints nothing of its own: a line of only these is judged by what else it runs.
    private static readonly HashSet<string> neutral = new(StringComparer.Ordinal)
    {
        "true", "false", ":", "[", "[[", "test", "cd", "pushd", "popd", "export", "set", "unset", "local", "declare", "readonly",
        "shift", "read", "wait", "trap", "shopt", "umask", "return", "exit", "break", "continue",
    };

    private static readonly HashSet<string> keywords = new(StringComparer.Ordinal) { "if", "then", "else", "elif", "fi", "do", "done", "while", "until", "in", "esac", "}" };
    private static readonly HashSet<string> headers = new(StringComparer.Ordinal) { "for", "select", "function", "case" };
    private static readonly HashSet<string> declarers = new(StringComparer.Ordinal) { "export", "local", "declare", "readonly" };

    private static readonly HashSet<string> gitViewers = new(StringComparer.Ordinal)
    {
        "show", "diff", "log", "blame", "grep", "status", "rev-parse", "ls-files", "ls-tree", "cat-file", "show-ref", "rev-list",
        "merge-base", "describe", "shortlog", "reflog", "for-each-ref", "name-rev",
    };

    private static readonly HashSet<string> gitNetwork = new(StringComparer.Ordinal) { "fetch", "pull", "push", "clone", "ls-remote" };
    private static readonly HashSet<string> gitOptionsWithValue = new(StringComparer.Ordinal) { "-C", "-c", "--git-dir", "--work-tree", "--namespace" };
    private static readonly HashSet<string> ghOptionsWithValue = new(StringComparer.Ordinal) { "-R", "--repo" };

    // Each wrapper's options that take the next word as their value.
    private static readonly Dictionary<string, HashSet<string>> wrappers = new(StringComparer.Ordinal)
    {
        ["sudo"] = new(StringComparer.Ordinal) { "-u", "-g", "-h", "-p", "-C", "-D", "-r", "-t", "-U" },
        ["env"] = new(StringComparer.Ordinal) { "-u", "-C", "-S", "--unset", "--chdir" },
        ["nice"] = new(StringComparer.Ordinal) { "-n", "--adjustment" },
        ["timeout"] = new(StringComparer.Ordinal) { "-s", "-k", "--signal", "--kill-after" },
        ["xargs"] = new(StringComparer.Ordinal) { "-n", "-I", "-P", "-d", "-L", "-s", "-E", "-a" },
        ["exec"] = new(StringComparer.Ordinal) { "-a" },
        ["time"] = new(StringComparer.Ordinal),
        ["nohup"] = new(StringComparer.Ordinal),
        ["command"] = new(StringComparer.Ordinal),
    };

    public static ShellPart Read(string part)
    {
        List<string> words = [];
        List<string> writes = [];
        TakeRedirections(Words(part), words, writes);

        int at = 0;
        while (at < words.Count && keywords.Contains(words[at]))
            at++;
        if (at < words.Count && headers.Contains(words[at]))
            return new ShellPart(null, words[at] != "case", [.. writes], false, []);

        List<(string Name, string? Value)> assignments = [];
        at = AfterWrappers(words, at, assignments);
        if (at >= words.Count)
            return new ShellPart(null, true, [.. writes], false, [.. assignments]);

        string program = ProgramName(words[at]);
        string[] arguments = [.. words.Skip(at + 1)];
        if (neutral.Contains(program))
        {
            if (declarers.Contains(program))
                assignments.AddRange(arguments.Where(IsAssignment).Select(AssignmentOf));
            return new ShellPart(null, true, [.. writes], program is "cd" or "pushd" or "popd", [.. assignments]);
        }

        // `tee`'s files hold what the command before it printed: whose they are is the whole line's question, as for `} > out`.
        if (program == "tee")
            return new ShellPart(new ShellSegment(program, true, false, arguments, []), true, [.. writes, .. arguments.Where(argument => !argument.StartsWith('-'))], false, [.. assignments]);

        return new ShellPart(new ShellSegment(program, Views(program, arguments), Network(program, arguments), arguments, [.. writes]), true, [], false, [.. assignments]);
    }

    // Unquoted whitespace splits; quotes stay in the word (`"a > b"` is one word, not a redirection).
    private static List<string> Words(string part)
    {
        List<string> words = [];
        StringBuilder current = new();
        char quote = '\0';
        foreach (char c in part)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                    words.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            words.Add(current.ToString());
        return words;
    }

    // `> f`, `>>f`, `2> f`, `&>f`, `>&f` write f; `2>&1` and `<` read nothing new; `<<` is a heredoc whose delimiter Split already took.
    private static void TakeRedirections(List<string> raw, List<string> words, List<string> writes)
    {
        for (int i = 0; i < raw.Count; i++)
        {
            if (RedirectionPattern().Match(raw[i]) is not { Success: true } redirection)
            {
                string word = raw[i].TrimStart('{', '!', '&').TrimEnd(')');
                if (word.Length > 0)
                    words.Add(word);
                continue;
            }

            string op = redirection.Groups["op"].Value;
            if (op == "<<")
                continue;

            string target = redirection.Groups["target"].Value;
            if (target.Length == 0 && i + 1 < raw.Count)
                target = raw[++i];
            if (op is ">&" or "<&" && (target == "-" || target.All(char.IsAsciiDigit)))
                continue;
            if (op is ">" or ">>" or ">|" or ">&" && target.Length > 0)
                writes.Add(target);
        }
    }

    // Wrappers, their options (`sudo -u x`, `nice -n 10`, `timeout -s KILL 5`), `env`'s assignments: the index of the program, or past
    // the end for a line with none (`command -v x` asks whether x exists, and runs nothing).
    private static int AfterWrappers(List<string> words, int at, List<(string Name, string? Value)> assignments)
    {
        while (at < words.Count)
        {
            if (IsAssignment(words[at]))
            {
                assignments.Add(AssignmentOf(words[at++]));
                continue;
            }

            if (!wrappers.TryGetValue(words[at], out HashSet<string>? withValue))
                return at;
            if (words[at] == "command" && at + 1 < words.Count && words[at + 1] is "-v" or "-V")
                return words.Count;

            bool timeout = words[at] == "timeout";
            at++;
            while (at < words.Count && words[at].StartsWith('-'))
            {
                bool last = words[at] == "--";
                at += withValue.Contains(words[at]) ? 2 : 1;
                if (last)
                    break;
            }

            // `timeout`'s duration comes before its command.
            if (timeout)
                at++;
        }

        return at;
    }

    private static bool Views(string program, string[] arguments) => program switch
    {
        // `sed` prints what it reads unless it edits in place.
        "sed" => !arguments.Any(argument => argument.StartsWith("-i", StringComparison.Ordinal) || argument.StartsWith("--in-place", StringComparison.Ordinal)),
        "git" => Subcommands(arguments, gitOptionsWithValue) is [var subcommand, ..] && gitViewers.Contains(subcommand),
        "gh" => GhViews(Subcommands(arguments, ghOptionsWithValue)),
        "python" or "python3" => arguments is ["-m", "json.tool", ..],
        _ => viewers.Contains(program),
    };

    // gh's content commands: what they print is an issue, a pull request, a release, a run, a diff or an API answer.
    private static bool GhViews(string[] subcommands) =>
        subcommands is ["issue" or "pr", "view" or "list" or "status", ..] or ["pr", "diff" or "checks", ..] or ["run" or "release" or "workflow", "view" or "list", ..]
            or ["repo" or "gist", "view", ..] or ["label" or "cache", "list", ..] or ["api" or "search", ..];

    private static bool Network(string program, string[] arguments) => program switch
    {
        "gh" or "curl" or "wget" => true,
        "git" => Subcommands(arguments, gitOptionsWithValue) is [var subcommand, .. var rest]
            && (gitNetwork.Contains(subcommand) || (subcommand is "remote" or "submodule" && rest.Contains("update"))),
        _ => false,
    };

    // The words that are not options, in order, an option's value skipped when it is one that takes the next word.
    private static string[] Subcommands(string[] arguments, HashSet<string> optionsWithValue)
    {
        List<string> subcommands = [];
        for (int i = 0; i < arguments.Length; i++)
        {
            if (optionsWithValue.Contains(arguments[i]))
                i++;
            else if (!arguments[i].StartsWith('-'))
                subcommands.Add(arguments[i]);
        }

        return [.. subcommands];
    }

    private static bool IsAssignment(string word)
    {
        int equals = word.IndexOf('=');
        return equals > 0 && !char.IsAsciiDigit(word[0]) && word[..equals].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    // A value that is not a plain word (a substitution, another variable, a glob) is unknown.
    private static (string Name, string? Value) AssignmentOf(string word)
    {
        int equals = word.IndexOf('=');
        string value = word[(equals + 1)..].Trim('\'', '"');
        return (word[..equals], value.Length == 0 || value.IndexOfAny(['$', '`', '*', '?']) >= 0 || value.Contains("SUBST", StringComparison.Ordinal) ? null : value);
    }

    private static string ProgramName(string word)
    {
        string name = word.Trim('\'', '"');
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    [GeneratedRegex(@"^(?:\d+|&)?(?<op>>>|>\||>&|>|<<<|<<|<&|<>|<)(?<target>.*)$")]
    private static partial Regex RedirectionPattern();
}
