using System.Text;
using System.Text.RegularExpressions;

namespace Claustrum.Core.Tracing;

// #145: a shell line read for EnvironmentClassifier: split on unquoted `&&`, `||`, `;`, `|`, newlines and subshell parentheses (a lone `&`
// is kept, so `2>&1` stays in its command); comments and heredoc bodies dropped, `$(…)` and backticks read as commands of their own, each
// part then read by ShellWords. Not a shell parser: what it cannot read with certainty makes the parse unconfident, and the call is then
// skipped (NOTES.md "Environment issues", the viewer filter).
internal static partial class ShellCommand
{
    private const int MaxDepth = 8;

    public static ShellParse Parse(string? command)
    {
        List<ShellPart> parts = [];
        bool confident = command is null || Collect(command, parts, 0);
        Dictionary<string, string> known = Known(parts.SelectMany(part => part.Assignments));

        ShellSegment[] segments = [.. parts.Select(part => part.Segment).OfType<ShellSegment>()
            .Select(segment => segment with { Arguments = Resolved(segment.Arguments, known), Writes = Resolved(segment.Writes, known) })];
        return new ShellParse(segments, confident, Resolved([.. parts.SelectMany(part => part.Writes)], known), parts.Any(part => part.Moves));
    }

    private static bool Collect(string command, List<ShellPart> parts, int depth)
    {
        if (depth > MaxDepth)
            return false;

        (List<string> pieces, List<string> substitutions, bool confident) = Split(command);
        foreach (string piece in pieces)
        {
            ShellPart part = ShellWords.Read(piece);
            confident &= part.Read;
            parts.Add(part);
        }

        foreach (string substitution in substitutions)
            confident &= Collect(substitution, parts, depth + 1);

        return confident;
    }

    // A variable the line set once, to a plain word (`L=/tmp/x.log; … > $L`); one set twice to two values, or to anything else, is unknown.
    private static Dictionary<string, string> Known(IEnumerable<(string Name, string? Value)> assignments)
    {
        Dictionary<string, string> known = new(StringComparer.Ordinal);
        HashSet<string> unknown = new(StringComparer.Ordinal);
        foreach ((string name, string? value) in assignments)
        {
            if (value is null || (known.TryGetValue(name, out string? earlier) && earlier != value))
                unknown.Add(name);
            else
                known[name] = value;
        }

        foreach (string name in unknown)
            known.Remove(name);
        return known;
    }

    // Words with their quotes off and the line's own variables put in; a word that still names one it did not set is dropped (a doubt).
    private static string[] Resolved(string[] words, Dictionary<string, string> known) =>
        [.. words.Select(word => VariablePattern().Replace(word.Trim('\'', '"'), match => known.TryGetValue(match.Groups["name"].Value, out string? value) ? value : match.Value))
            .Where(word => word.Length > 0 && !word.Contains('$', StringComparison.Ordinal) && !word.Contains("SUBST", StringComparison.Ordinal))];

    [GeneratedRegex(@"\$\{?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}?")]
    private static partial Regex VariablePattern();

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
                // `>|` is a clobbering redirect, not a pipe: it stays in the word for ShellWords to strip.
                case '|' when current.Length > 0 && current[^1] == '>':
                    current.Append(c);
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
}
