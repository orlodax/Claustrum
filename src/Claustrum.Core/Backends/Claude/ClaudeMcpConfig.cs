using System.Text.Json;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Backends.Claude;

// #128 (measured 2026-10-15, NOTES.md "A claude role gets the repo's MCP servers, not the operator's"): a headless
// claude run is given `--strict-mcp-config` and, at most, the repo's own `.mcp.json`. The file is judged here (a link that
// leaves the working directory, a FIFO, a device and a file that does not parse are refused, never followed) and the run
// reads a COPY in its job directory, so the bytes judged are the bytes claude loads.
internal static class ClaudeMcpConfig
{
    public const string UserServers = "user";

    private const string FileName = ".mcp.json";
    private const string CopyName = "mcp.json";
    private const int MaxLinkHops = 40;

    private static readonly JsonDocumentOptions lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static McpSelection Select(IPlatform platform, string cwd, string jobDirectory)
    {
        string source = Path.Combine(cwd, FileName);
        FileInfo file = new(source);
        if (!file.Exists && file.LinkTarget is null)
            return new McpSelection(null, []);

        try
        {
            if (LinkRefusal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)), source) is { } linkRefusal)
                return Refused(linkRefusal);

            if (platform.ReadRegularFileText(source, out string? readRefusal) is not { } text)
                return Refused(readRefusal ?? "it could not be read");

            using JsonDocument document = JsonDocument.Parse(text, lenient);
            string[] servers = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("mcpServers", out JsonElement section)
                && section.ValueKind == JsonValueKind.Object
                    ? [.. section.EnumerateObject().Select(server => server.Name)]
                    : [];

            string copy = Path.Combine(jobDirectory, CopyName);
            File.WriteAllText(copy, text);
            return new McpSelection(copy, servers);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return Refused(e is JsonException ? $"it could not be parsed ({e.Message})" : e.Message);
        }
    }

    private static McpSelection Refused(string reason) =>
        new(null, [], $"{FileName} not given to claude: {reason}; the run has no MCP servers");

    // Why the file may not be read, or null. The chain is walked by hand on the raw link text, because a resolved target
    // would have its `..` collapsed before the directory links in front of it were looked at (`sub/../x` with `sub` a link
    // leads elsewhere). So a `..` in a link is refused outright, every hop must stay under the working directory, and
    // no directory on the way below it may be a link. An absolute target spelled through a link to the cwd is refused too.
    private static string? LinkRefusal(string root, string file)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string current = file;
        for (int hop = 0; hop < MaxLinkHops; hop++)
        {
            for (string? directory = Path.GetDirectoryName(current); directory is { Length: > 0 } && directory.Length > root.Length; directory = Path.GetDirectoryName(directory))
                if (new DirectoryInfo(directory).LinkTarget is not null)
                    return $"{directory} on the way to it is a link";

            if (new FileInfo(current).LinkTarget is not { } target)
                return null;

            if (target.Split('/', '\\').Contains(".."))
                return $"it is a link to {target}, which climbs with '..'";

            current = Path.GetFullPath(target, Path.GetDirectoryName(current) ?? root);
            if (!current.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                return $"it is a link to {current}, outside the working directory";
        }

        return "it is a chain of links too long to follow";
    }
}

// ConfigPath is the copy to hand to `--mcp-config`; Servers are the names the run is given ("user" when opted in).
internal sealed record McpSelection(string? ConfigPath, string[] Servers, string? Warning = null);
