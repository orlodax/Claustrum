using System.Text;

namespace Claustrum.Core.Tracing;

// #145: a shell command line read for EnvironmentClassifier: split on unquoted `&&`, `||`, `;`, `|` and newlines (a lone `&` is kept, so
// `2>&1` stays in its segment), each segment with its program and whether it only views files. Not a shell parser: a heredoc's body becomes
// segments whose programs view nothing, which can only make a command scanned that a parser would have skipped, never the reverse.
internal static class ShellCommand
{
    // The brief's viewers, the filters a viewer's output is piped through, and the two that print the agent's own words.
    private static readonly HashSet<string> viewers = new(StringComparer.Ordinal)
    {
        "cat", "head", "tail", "less", "more", "grep", "egrep", "fgrep", "rg", "awk", "find", "ls", "jq",
        "sort", "uniq", "wc", "cut", "tr", "nl", "column", "echo", "printf",
    };

    private static readonly HashSet<string> gitViewers = new(StringComparer.Ordinal) { "show", "diff", "log", "blame", "grep" };
    private static readonly HashSet<string> wrappers = new(StringComparer.Ordinal) { "sudo", "env", "time", "nohup", "exec", "command", "nice" };
    private static readonly HashSet<string> movers = new(StringComparer.Ordinal) { "cd", "pushd", "popd" };
    private static readonly HashSet<string> gitOptionsWithValue = new(StringComparer.Ordinal) { "-C", "-c", "--git-dir", "--work-tree", "--namespace" };

    public static ShellSegment[] Segments(string? command) =>
        command is null ? [] : [.. Split(command).Select(Segment).OfType<ShellSegment>()];

    // True when the command only views files, so its output and error are file content too: every segment views, a `cd` aside
    // (`cd <dir> && git log` views; a `cd` alone does not, its error being the one that says a directory is missing).
    public static bool OnlyViews(ShellSegment[] segments)
    {
        ShellSegment[] working = [.. segments.Where(segment => !movers.Contains(segment.Program))];
        return working.Length > 0 && working.All(segment => segment.Views);
    }

    private static List<string> Split(string command)
    {
        List<string> parts = [];
        StringBuilder current = new();
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else if (c == '\\' && quote == '"' && i + 1 < command.Length)
                    current.Append(command[i++]);

                current.Append(command[i]);
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    current.Append(c);
                    break;
                case '\\' when i + 1 < command.Length:
                    current.Append(c).Append(command[++i]);
                    break;
                case '|' or ';' or '\n' or '\r':
                    Flush(parts, current);
                    if (c == '|' && i + 1 < command.Length && command[i + 1] == '|')
                        i++;
                    break;
                case '&' when i + 1 < command.Length && command[i + 1] == '&':
                    Flush(parts, current);
                    i++;
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        Flush(parts, current);
        return parts;
    }

    private static void Flush(List<string> parts, StringBuilder current)
    {
        if (current.ToString().Trim() is { Length: > 0 } part)
            parts.Add(part);

        current.Clear();
    }

    private static ShellSegment? Segment(string part)
    {
        string[] words = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int start = 0;
        while (start < words.Length)
        {
            string word = words[start].TrimStart('(', '{', '!', '&');
            if (word.Length == 0 || IsAssignment(word) || wrappers.Contains(word))
                start++;
            else if (word == "timeout")
                start = AfterTimeout(words, start + 1);
            else
                break;
        }

        if (start >= words.Length)
            return null;

        string program = ProgramName(words[start]);
        return new ShellSegment(program, Views(program, words[(start + 1)..]));
    }

    private static bool Views(string program, string[] arguments) => program switch
    {
        // `sed` prints what it reads unless it edits in place.
        "sed" => !arguments.Any(argument => argument.StartsWith("-i", StringComparison.Ordinal) || argument.StartsWith("--in-place", StringComparison.Ordinal)),
        "git" => GitSubcommand(arguments) is { } subcommand && gitViewers.Contains(subcommand),
        _ => viewers.Contains(program),
    };

    private static string? GitSubcommand(string[] arguments)
    {
        for (int i = 0; i < arguments.Length; i++)
        {
            if (gitOptionsWithValue.Contains(arguments[i]))
                i++;
            else if (!arguments[i].StartsWith('-'))
                return arguments[i];
        }

        return null;
    }

    // `timeout [-s SIG|-k DUR|--flag] DURATION command…`: the index of the command.
    private static int AfterTimeout(string[] words, int index)
    {
        while (index < words.Length && words[index].StartsWith('-'))
            index += words[index] is "-s" or "-k" ? 2 : 1;

        return index + 1;
    }

    private static bool IsAssignment(string word)
    {
        int equals = word.IndexOf('=');
        return equals > 0 && !char.IsAsciiDigit(word[0]) && word[..equals].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    private static string ProgramName(string word)
    {
        string name = word.TrimStart('(', '{', '!', '&').Trim('\'', '"');
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
