using System.Text;

namespace Claustrum.Core.Tracing;

// #145: a shell line read for EnvironmentClassifier: split on unquoted `&&`, `||`, `;`, `|`, newlines and subshell parentheses (a lone `&`
// is kept, so `2>&1` stays in its command); comments and heredoc bodies dropped, `$(…)` and backticks read as commands of their own.
// Not a shell parser: what it cannot read with certainty makes the parse unconfident, and the call is then skipped (NOTES.md
// "Environment issues", the viewer filter).
internal static class ShellCommand
{
    // The brief's viewers, the filters their output is piped through, the two that print the agent's own words, and claustrum, whose
    // `run --json` prints a child's whole receipt, diff included.
    private static readonly HashSet<string> viewers = new(StringComparer.Ordinal)
    {
        "cat", "head", "tail", "less", "more", "grep", "egrep", "fgrep", "rg", "awk", "find", "ls", "jq",
        "sort", "uniq", "wc", "cut", "tr", "nl", "column", "echo", "printf", "claustrum",
    };

    // What prints nothing of its own: a line of only these is judged by what else it runs.
    private static readonly HashSet<string> neutral = new(StringComparer.Ordinal)
    {
        "true", "false", ":", "[", "[[", "test", "cd", "pushd", "popd", "export", "set", "unset", "local", "declare", "readonly",
        "shift", "read", "wait", "trap", "shopt", "umask", "return", "exit", "break", "continue",
    };

    private static readonly HashSet<string> keywords = new(StringComparer.Ordinal) { "if", "then", "else", "elif", "fi", "do", "done", "while", "until", "in", "esac", "}" };
    private static readonly HashSet<string> headers = new(StringComparer.Ordinal) { "for", "select", "function", "case" };
    private static readonly HashSet<string> wrappers = new(StringComparer.Ordinal) { "sudo", "env", "time", "nohup", "exec", "command", "nice" };

    private static readonly HashSet<string> gitViewers = new(StringComparer.Ordinal)
    {
        "show", "diff", "log", "blame", "grep", "status", "rev-parse", "ls-files", "ls-tree", "cat-file", "show-ref", "rev-list",
        "merge-base", "describe", "shortlog", "reflog", "for-each-ref", "name-rev",
    };

    private static readonly HashSet<string> gitNetwork = new(StringComparer.Ordinal) { "fetch", "pull", "push", "clone", "ls-remote" };
    private static readonly HashSet<string> gitOptionsWithValue = new(StringComparer.Ordinal) { "-C", "-c", "--git-dir", "--work-tree", "--namespace" };
    private static readonly HashSet<string> xargsOptionsWithValue = new(StringComparer.Ordinal) { "-n", "-I", "-P", "-d", "-L", "-s", "-E", "-a" };
    private static readonly HashSet<string> ghOptionsWithValue = new(StringComparer.Ordinal) { "-R", "--repo" };
    private static readonly HashSet<string> timeoutOptionsWithValue = new(StringComparer.Ordinal) { "-s", "-k" };
    private const int MaxDepth = 8;

    public static ShellParse Parse(string? command)
    {
        List<ShellSegment> segments = [];
        bool confident = command is null || Collect(command, segments, 0);
        return new ShellParse([.. segments], confident);
    }

    private static bool Collect(string command, List<ShellSegment> segments, int depth)
    {
        if (depth > MaxDepth)
            return false;

        (List<string> parts, List<string> substitutions, bool confident) = Split(command);
        foreach (string part in parts)
        {
            (ShellSegment? segment, bool read) = Segment(part);
            confident &= read;
            if (segment is not null)
                segments.Add(segment);
        }

        foreach (string substitution in substitutions)
            confident &= Collect(substitution, segments, depth + 1);

        return confident;
    }

    private static (List<string> Parts, List<string> Substitutions, bool Confident) Split(string command)
    {
        List<string> parts = [];
        List<string> substitutions = [];
        List<string> heredocs = [];
        StringBuilder current = new();
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            char next = i + 1 < command.Length ? command[i + 1] : '\0';
            if (quote == '\'')
            {
                current.Append(c);
                if (c == '\'')
                    quote = '\0';
                continue;
            }

            if ((c == '$' && next == '(') || c == '`')
            {
                if (Substitution(command, i, substitutions) is not { } end)
                    return (parts, substitutions, false);

                current.Append("SUBST");
                i = end;
                continue;
            }

            if (quote == '"')
            {
                if (c == '\\' && next != '\0')
                    current.Append(command[i++]);
                else if (c == '"')
                    quote = '\0';

                current.Append(command[i]);
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    current.Append(c);
                    break;
                case '\\' when next != '\0':
                    current.Append(c).Append(command[++i]);
                    break;
                case '#' when current.Length == 0 || char.IsWhiteSpace(current[^1]):
                    while (i + 1 < command.Length && command[i + 1] != '\n')
                        i++;
                    break;
                case '<' when next == '<':
                    i = Heredoc(command, i, current, heredocs);
                    break;
                case '(' when next == ')':
                    // `name() {`: a function's name runs nothing.
                    current.Clear();
                    i++;
                    break;
                case '(' or ')' or ';' or '\r':
                    Flush(parts, current);
                    break;
                case '|':
                    Flush(parts, current);
                    if (next == '|')
                        i++;
                    break;
                case '&' when next == '&':
                    Flush(parts, current);
                    i++;
                    break;
                case '\n':
                    Flush(parts, current);
                    i = SkipHeredocBodies(command, i, heredocs);
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        Flush(parts, current);
        return (parts, substitutions, quote == '\0');
    }

    // `$(…)` or a backtick pair, its content kept as a command of its own; `$((…))` is arithmetic and runs nothing. The index of the
    // closing character, or null when there is none (the line is then not read with certainty).
    private static int? Substitution(string command, int start, List<string> substitutions)
    {
        if (command[start] == '`')
        {
            int closing = command.IndexOf('`', start + 1);
            if (closing < 0)
                return null;

            substitutions.Add(command[(start + 1)..closing]);
            return closing;
        }

        int depth = 0;
        char quote = '\0';
        for (int i = start + 1; i < command.Length; i++)
        {
            char c = command[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                if (command[start + 2] != '(')
                    substitutions.Add(command[(start + 2)..i]);
                return i;
            }
        }

        return null;
    }

    // `<<WORD`, `<<-WORD`, `<<'WORD'`: the delimiter is remembered, and the body after the line's end is skipped (data, not commands).
    // `<<<` is a here-string, read on.
    private static int Heredoc(string command, int start, StringBuilder current, List<string> heredocs)
    {
        int i = start + 2;
        if (i < command.Length && command[i] == '<')
        {
            current.Append("<<<");
            return i;
        }

        if (i < command.Length && command[i] == '-')
            i++;
        while (i < command.Length && command[i] is ' ' or '\t')
            i++;

        int begin = i;
        while (i < command.Length && !char.IsWhiteSpace(command[i]) && command[i] is not (';' or '|' or '&' or '<' or '>' or '(' or ')'))
            i++;

        string delimiter = command[begin..i].Trim('\'', '"').TrimStart('\\');
        if (delimiter.Length > 0)
            heredocs.Add(delimiter);

        current.Append("<<");
        return i - 1;
    }

    private static int SkipHeredocBodies(string command, int newline, List<string> heredocs)
    {
        int i = newline;
        foreach (string delimiter in heredocs)
        {
            while (i < command.Length)
            {
                int end = command.IndexOf('\n', i + 1);
                string line = (end < 0 ? command[(i + 1)..] : command[(i + 1)..end]).Trim();
                i = end < 0 ? command.Length : end;
                if (line == delimiter)
                    break;
            }
        }

        heredocs.Clear();
        return i;
    }

    private static void Flush(List<string> parts, StringBuilder current)
    {
        if (current.ToString().Trim() is { Length: > 0 } part)
            parts.Add(part);

        current.Clear();
    }

    // One command: keywords, assignments and wrappers stripped. Null for a neutral one; Read is false for a `case`, whose patterns
    // (`a)`) cannot be told from commands here.
    private static (ShellSegment? Segment, bool Read) Segment(string part)
    {
        string[] words = [.. part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.TrimStart('{', '!', '&').TrimEnd(')'))
            .Where(word => word.Length > 0)];

        int at = 0;
        while (at < words.Length && keywords.Contains(words[at]))
            at++;
        if (at < words.Length && headers.Contains(words[at]))
            return (null, words[at] != "case");

        while (at < words.Length)
        {
            if (IsAssignment(words[at]) || wrappers.Contains(words[at]))
                at++;
            else if (words[at] == "timeout")
                at = AfterTimeout(words, at + 1);
            else if (words[at] == "xargs")
                at = AfterOptions(words, at + 1, xargsOptionsWithValue);
            else
                break;
        }

        if (at >= words.Length)
            return (null, true);

        string program = ProgramName(words[at]);
        string[] arguments = words[(at + 1)..];
        return neutral.Contains(program) ? (null, true) : (new ShellSegment(program, Views(program, arguments), Network(program, arguments)), true);
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

    // gh's content commands: what they print is an issue, a pull request, a diff or an API answer.
    private static bool GhViews(string[] subcommands) =>
        subcommands is ["issue", "view" or "list", ..] or ["pr", "view" or "diff" or "list" or "checks", ..] or ["run", "view", ..]
            or ["repo", "view", ..] or ["api" or "search", ..];

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

    // `timeout [-s SIG|-k DUR|--flag] DURATION command…`: the index of the command.
    private static int AfterTimeout(string[] words, int index) => AfterOptions(words, index, timeoutOptionsWithValue) + 1;

    private static int AfterOptions(string[] words, int index, HashSet<string> optionsWithValue)
    {
        while (index < words.Length && words[index].StartsWith('-'))
            index += optionsWithValue.Contains(words[index]) ? 2 : 1;

        return index;
    }

    private static bool IsAssignment(string word)
    {
        int equals = word.IndexOf('=');
        return equals > 0 && !char.IsAsciiDigit(word[0]) && word[..equals].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    private static string ProgramName(string word)
    {
        string name = word.Trim('\'', '"');
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
