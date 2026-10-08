using System.Diagnostics;
using System.Text;
// Not `using System.Diagnostics.Process`: Claustrum.Core.Process is an enclosing-namespace member and wins
// over a using (the trap WorktreeSnapshotTests documents), so the type is renamed here.
using SystemProcess = System.Diagnostics.Process;

namespace Claustrum.Core.Tests.Testing;

/// <summary>
/// The real `git` the tests drive their scratch repositories with — the fixture side of what
/// JobWorktree and WorktreeSnapshot do through GitProcess, kept independent so a defect in the code under
/// test cannot hide itself in its own setup.
/// </summary>
public static class GitRepo
{
    /// <summary>
    /// An identity, no signing, line endings untouched, and the hooks directory pinned to the repository's
    /// own: a developer's global `commit.gpgsign` or `core.hooksPath` must not change what a test commits.
    /// </summary>
    public static void Init(string directory)
    {
        Run(directory, "init", "-q", "-b", "main");
        Run(directory, "config", "user.email", "test@example.com");
        Run(directory, "config", "user.name", "claustrum-tests");
        Run(directory, "config", "core.autocrlf", "false");
        Run(directory, "config", "commit.gpgsign", "false");
        Run(directory, "config", "core.hooksPath", Path.Combine(directory, ".git", "hooks"));
    }

    public static string Commit(string directory, string fileName, string content)
    {
        File.WriteAllText(Path.Combine(directory, fileName), content);
        Run(directory, "add", "-A");
        Run(directory, "commit", "-q", "-m", $"add {fileName}");
        return Head(directory);
    }

    public static string Head(string directory) => Run(directory, "rev-parse", "HEAD");

    public static string RevParse(string directory, string revision) => Run(directory, "rev-parse", revision);

    /// <summary>`git status --porcelain --untracked-files=all`, trimmed: empty when the tree is clean.</summary>
    public static string Status(string directory) => Run(directory, "status", "--porcelain", "--untracked-files=all");

    public static int CommitCount(string directory, string range) => int.Parse(Run(directory, "rev-list", "--count", range), System.Globalization.CultureInfo.InvariantCulture);

    public static string[] Branches(string directory) =>
        Run(directory, "branch", "--format=%(refname:short)").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Every path `git worktree list --porcelain` registers, the main checkout first.</summary>
    public static string[] WorktreePaths(string directory) =>
        [.. Run(directory, "worktree", "list", "--porcelain")
            .Split('\n')
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => line["worktree ".Length..].TrimEnd('\r'))];

    /// <summary>A hook script (run by git through sh on every OS) in the repository's shared hooks directory.</summary>
    public static void WriteHook(string directory, string name, string body)
    {
        string hooks = Path.Combine(directory, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        string path = Path.Combine(hooks, name);
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Stdout with the trailing newline trimmed; throws with git's own words when it exits non-zero.</summary>
    public static string Run(string directory, params string[] args)
    {
        (int exitCode, string stdout, string stderr) = Try(directory, args);
        return exitCode == 0
            ? stdout.TrimEnd('\r', '\n')
            : throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({exitCode}) in {directory}: {stderr}");
    }

    public static (int ExitCode, string Stdout, string Stderr) Try(string directory, params string[] args)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        using SystemProcess process = SystemProcess.Start(startInfo) ?? throw new InvalidOperationException("git failed to start");
        process.StandardInput.Close();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
