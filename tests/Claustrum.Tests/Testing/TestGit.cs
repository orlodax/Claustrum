using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Claustrum.Tests.Testing;

/// <summary>
/// The real `git` the e2e tests set their repositories up and read them back with — independent of the
/// code under test. Duplicated from Claustrum.Core.Tests' GitRepo because the two test projects share no code.
/// </summary>
public static class TestGit
{
    /// <summary>An identity, no signing, line endings untouched, hooks pinned to the repository's own .git.</summary>
    public static void Init(string directory)
    {
        Run(directory, "init", "-q", "-b", "main");
        Run(directory, "config", "user.email", "test@example.com");
        Run(directory, "config", "user.name", "claustrum-tests");
        Run(directory, "config", "core.autocrlf", "false");
        Run(directory, "config", "commit.gpgsign", "false");
        Run(directory, "config", "core.hooksPath", Path.Combine(directory, ".git", "hooks"));
    }

    public static string CommitAll(string directory, string message)
    {
        Run(directory, "add", "-A");
        Run(directory, "commit", "-q", "-m", message);
        return Head(directory);
    }

    public static string Head(string directory) => Run(directory, "rev-parse", "HEAD");

    public static string RevParse(string directory, string revision) => Run(directory, "rev-parse", revision);

    public static string Status(string directory) => Run(directory, "status", "--porcelain", "--untracked-files=all");

    public static int CommitCount(string directory, string range) => int.Parse(Run(directory, "rev-list", "--count", range), CultureInfo.InvariantCulture);

    public static string[] Branches(string directory) =>
        Run(directory, "branch", "--format=%(refname:short)").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string[] WorktreePaths(string directory) =>
        [.. Run(directory, "worktree", "list", "--porcelain")
            .Split('\n')
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => line["worktree ".Length..].TrimEnd('\r'))];

    public static void WriteHook(string directory, string name, string body)
    {
        string hooks = Path.Combine(directory, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        string path = Path.Combine(hooks, name);
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

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

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("git failed to start");
        process.StandardInput.Close();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
