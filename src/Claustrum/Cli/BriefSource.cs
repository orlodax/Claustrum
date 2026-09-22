namespace Claustrum.Cli;

// docs/PLAN.md §B3 lists three ways a brief comes in: `--brief`, `--brief-file <path|->`, or bare
// stdin. Shared by `run` (where a brief is required) and `coordinate` (where `--issues` is the other
// way to say the same thing), so the two verbs cannot drift on what `-` or an empty file means.
public static class BriefSource
{
    /// <summary>
    /// The brief text, or null when no source was given at all — a file or a stdin that reads back
    /// empty counts as none, so an empty brief never reaches a spawned process.
    /// <paramref name="allowBareStdin"/> is false for a verb that has another way to be given its
    /// task: reading a stdin nobody promised to close would hang it (NOTES.md "MCP child stdin
    /// inheritance hung git" is the same hazard from the other side), and `-` still opts in.
    /// </summary>
    public static string? TryResolve(string? briefText, string? briefFilePath, bool allowBareStdin)
    {
        if (briefText is { Length: > 0 } && briefFilePath is { Length: > 0 })
            throw new CliUsageException("use either --brief or --brief-file, not both");

        if (briefText is { Length: > 0 } text)
            return text;

        string brief = briefFilePath is { Length: > 0 } path
            ? path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(path)
            : allowBareStdin && Console.IsInputRedirected ? Console.In.ReadToEnd() : "";

        return brief is { Length: > 0 } ? brief : null;
    }
}
