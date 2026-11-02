using System.ComponentModel;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Git;

/// <summary>
/// Runs the user config's <c>projects.&lt;glob&gt;.worktree_hook</c> commands on a job worktree right after it is cut, checked out or
/// reopened (#146): an argv, never a shell string, with <see cref="ProjectAccess.WorktreeToken"/> replaced by the worktree's path, in the
/// worktree, under the backends' env allow-list. A hook that fails is a warning and a <see cref="EnvironmentIssueKind.WorktreeHookFailed"/>
/// issue, and the job still runs; a cancel propagates. On success nothing reaches the receipt.
/// </summary>
public static class WorktreeHook
{
    private static readonly TimeSpan timeout = TimeSpan.FromSeconds(120);

    public static async Task<(string[] Warnings, EnvironmentIssue[] Issues)> RunAsync(
        IReadOnlyList<string[]> hooks, string worktreePath, IPlatform platform, bool envPassthroughAll, CancellationToken cancellationToken)
    {
        List<string> warnings = [];
        List<EnvironmentIssue> issues = [];
        foreach (string[] template in hooks)
        {
            // A JSON null in the array arrives as a null element whatever the annotation says.
            string[] argv = [.. template.Select(arg => (arg ?? "").Replace(ProjectAccess.WorktreeToken, worktreePath, StringComparison.Ordinal))];
            string command = string.Join(' ', argv);
            string? failure = await FailureAsync(argv, worktreePath, platform, envPassthroughAll, cancellationToken);
            if (failure is null)
                continue;

            warnings.Add($"worktree_hook failed: {command}: {failure}; the job runs anyway");
            issues.Add(new EnvironmentIssue(EnvironmentIssueKind.WorktreeHookFailed, failure, command, 1, "dispatch"));
        }

        return ([.. warnings], [.. issues]);
    }

    // Null when the hook exited 0; otherwise the exit code and the first line it wrote to stderr, or why it never ran to the end.
    private static async Task<string?> FailureAsync(string[] argv, string worktreePath, IPlatform platform, bool envPassthroughAll, CancellationToken cancellationToken)
    {
        if (Program(argv, platform) is not { } program)
            return $"{argv[0]} not found on PATH";

        try
        {
            Dictionary<string, string> environment = EnvAllowList.Build(platform, new Dictionary<string, string>(), envPassthroughAll);
            (int exitCode, _, string stderr) = await CommandProcess.RunAsync(program.Executable, worktreePath, program.Args, timeout, cancellationToken, environment);
            if (exitCode == 0)
                return null;

            string? firstLine = stderr.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
            return firstLine is null ? $"exited {exitCode}" : $"exited {exitCode}: {firstLine}";
        }
        catch (TimeoutException)
        {
            return $"timed out after {timeout.TotalSeconds:0} s";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return $"could not be started: {ex.Message}";
        }
    }

    // #146 review: .NET looks for a bare name in Claustrum's own cwd before PATH on Unix, and a cloned repo matching the glob could
    // commit an executable `chcon` there. So: an absolute path as is, a bare name on PATH only, from an absolute PATH entry.
    private static ResolvedBinary? Program(string[] argv, IPlatform platform)
    {
        string program = argv[0].Trim();
        if (Path.IsPathFullyQualified(program))
            return new ResolvedBinary(program, argv[1..]);

        try
        {
            ResolvedBinary found = BinaryLocator.Locate(program, argv[1..], config: null, platform);
            return Path.IsPathFullyQualified(found.Executable) ? found : null;
        }
        catch (BackendNotFoundException)
        {
            return null;
        }
    }
}
