namespace Claustrum.Cli;

// Every `--global` MCP target outside a repo (the Claude desktop config, `~/.cursor/mcp.json`)
// registers the *absolute* binary, because the GUI that spawns the server rarely has the install
// directory on PATH (issue #24). ⚠ `Environment.ProcessPath` is only that binary when this process
// *is* it: under `dotnet run`/`dotnet exec` — and this project suppresses its apphost
// (`PackAsTool`) — it is the dotnet host, whose path would register a command that starts the SDK.
internal static class ClaustrumBinaryPath
{
    private const string BinaryName = "claustrum";

    /// <summary>
    /// The running claustrum binary's absolute path, or null with a reason the caller prints and
    /// skips its target with.
    /// </summary>
    public static (string? BinaryPath, string? SkipReason) Resolve()
    {
        if (Environment.ProcessPath is not { Length: > 0 } processPath)
            return (null, "the running binary's own path is unknown");

        string fileName = Path.GetFileNameWithoutExtension(processPath);

        return fileName.Equals(BinaryName, StringComparison.OrdinalIgnoreCase)
            ? (processPath, null)
            : (null, $"running as '{fileName}', not the claustrum binary — install the release binary or the dotnet tool and rerun");
    }
}
