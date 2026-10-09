using System.Diagnostics;
using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// The rows of #74's rounds 3–5 that only a child process can show: the ones that depend on the ENVIRONMENT claustrum runs
// git in — git's language (it translates `add`'s words and the submodule refusal; the `error:`/`fatal:` prefixes stay),
// GIT_LITERAL_PATHSPECS (which turns `:(exclude)…` into a literal path), and a `git` earlier on PATH (a shim that fails
// the stray-gitlink unstage). Each is a `run builder` on a cast with `max_parallel: 2`, so the role (a fake `claude` script
// that leaves a repository in an awkward state) runs in its own worktree and the runner commits what it left. The
// in-process equivalents without environment are JobWorktreeLeftOutTests and JobWorktreeSubmoduleFreeTests.
public sealed class IsolatedRunCommitPathEndToEndTests : IDisposable
{
    private const string English = "C";
    private const string Italian = "it_IT.UTF-8";

    private readonly IsolatedRepo repo = new();

    public void Dispose()
    {
        repo.Dispose();
        foreach (string directory in extraDirectories)
            TempTree.Delete(directory);
    }

    private readonly List<string> extraDirectories = [];

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private static string[] Warnings(JsonElement result) => [.. result.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString() ?? "")];

    private static Dictionary<string, string?> Locale(string locale) => new() { ["LC_ALL"] = locale, ["LANGUAGE"] = null };

    // A language that is not installed makes git answer in English, and the case would pass without proving anything.
    private static void RequireLocale(string locale)
    {
        if (locale == English)
            return;

        ProcessStartInfo startInfo = new("git", ["rev-parse", "--git-dir"])
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.Environment["LC_ALL"] = locale;
        startInfo.Environment["LANGUAGE"] = "";
        startInfo.Environment.Remove("GIT_DIR");
        using Process probe = Process.Start(startInfo) ?? throw new InvalidOperationException("git failed to start");
        string said = probe.StandardError.ReadToEnd();
        probe.WaitForExit();
        if (!said.Contains("non è un repository", StringComparison.Ordinal))
            Assert.Skip($"git here does not speak {locale} (no locale or no git translation installed), so this case would not prove the language independence it exists for.");
    }

    private static void RequirePosix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the role here is a POSIX shell script.");
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string CloneInto(string path) => $"git clone -q '{repo.Repo}' {path}";

    private string Directory(string name)
    {
        string directory = System.IO.Directory.CreateTempSubdirectory(name).FullName;
        extraDirectories.Add(directory);
        return directory;
    }

    // ---- git's language ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(English)]
    [InlineData(Italian)]
    public async Task EmbeddedRepositoriesAreLeftOutInAnyLanguageAndTheRealFileIsStillCommittedAsync(string locale)
    {
        RequirePosix();
        RequireLocale(locale);
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Writes = [("real.txt", "real")], Commands = ["git init -q vendor/x", CloneInto("vendor/y")], Summary = "wrote real.txt" });

        (CliResult process, JsonElement result) = await repo.RunBuilderWithEnvAsync(Locale(locale));

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string branch = Str(result, "branch");
        Assert.Equal("real.txt", TestGit.Run(repo.Repo, "show", "--name-only", "--format=", Str(result, "commit")));
        Assert.Equal(
            [
                $"embedded repository at vendor/x left out of the commit on {branch} — move it out or add it as a submodule",
                $"embedded repository at vendor/y left out of the commit on {branch} — move it out or add it as a submodule",
            ],
            Warnings(result));
        Assert.Equal(["?? vendor/x/", "?? vendor/y/"], TestGit.Status(Str(result, "worktree")).Split('\n'));
    }

    [Theory]
    [InlineData(English)]
    [InlineData(Italian)]
    public async Task AnUnreadableFileAloneQuotesGitsOwnErrorLineWhateverTheLanguageAsync(string locale)
    {
        RequirePosix();
        if (Environment.IsPrivilegedProcess)
            Assert.Skip("an unreadable file needs a user that does not bypass permissions (not root).");

        RequireLocale(locale);
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Commands = ["printf 'secret\\n' > secret.txt", "chmod 000 secret.txt"] });

        (CliResult process, JsonElement result) = await repo.RunBuilderWithEnvAsync(Locale(locale));

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string branch = Str(result, "branch");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("commit").ValueKind);
        string[] warnings = Warnings(result);
        Assert.Equal(2, warnings.Length);
        Assert.Equal($"secret.txt left out of the commit on {branch}: git did not stage it (sparse-checkout, permissions or a filter — see the job's stderr.log)", warnings[0]);
        Assert.StartsWith($"work left uncommitted on {branch}: git add staged nothing: error: open(\"secret.txt\"): ", warnings[1], StringComparison.Ordinal);
        Assert.Contains(locale == Italian ? "Permesso negato" : "Permission denied", warnings[1], StringComparison.Ordinal);
        string log = File.ReadAllText(Path.Combine(repo.JobsRoot, Str(result, "job_id"), "stderr.log"));
        Assert.Contains($"claustrum: git add -A in {Str(result, "worktree")}, for the commit on {branch}:", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("error: open(\"secret.txt\")", log, StringComparison.Ordinal);
    }

    // Sparse-checkout: the case that made `add`'s exit code and words worthless (exit 1, no `error:` line at all).
    [Fact]
    public async Task APathOutsideTheSparseConeIsLeftOutAndGitsSparseAdviceIsInTheJobsStderrLogAsync()
    {
        RequirePosix();
        System.IO.Directory.CreateDirectory(Path.Combine(repo.Repo, "a"));
        System.IO.Directory.CreateDirectory(Path.Combine(repo.Repo, "b"));
        File.WriteAllText(Path.Combine(repo.Repo, "a", "x.txt"), "a\n");
        File.WriteAllText(Path.Combine(repo.Repo, "b", "x.txt"), "b\n");
        repo.CommitAll("a and b");
        repo.Git("sparse-checkout", "set", "--cone", "a");
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Commands = ["mkdir -p a b", "printf 'x\\n' > a/new.txt", "printf 'x\\n' > b/new.txt"] });

        (CliResult process, JsonElement result) = await repo.RunBuilderWithEnvAsync(Locale(English));

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string branch = Str(result, "branch");
        Assert.Equal("a/new.txt", TestGit.Run(repo.Repo, "show", "--name-only", "--format=", Str(result, "commit")));
        Assert.Equal([$"b/new.txt left out of the commit on {branch}: git did not stage it (sparse-checkout, permissions or a filter — see the job's stderr.log)"], Warnings(result));
        string log = File.ReadAllText(Path.Combine(repo.JobsRoot, Str(result, "job_id"), "stderr.log"));
        Assert.Contains("outside of your sparse-checkout definition", log, StringComparison.Ordinal);
        Assert.Equal(["a/new.txt", "b/new.txt"], [.. result.GetProperty("changed_files").EnumerateArray().Select(file => file.GetProperty("path").GetString()).Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task OnlyAPathOutsideTheSparseConeMakesNoCommitAndGitsOwnFirstLineIsTheReasonAsync()
    {
        RequirePosix();
        System.IO.Directory.CreateDirectory(Path.Combine(repo.Repo, "a"));
        System.IO.Directory.CreateDirectory(Path.Combine(repo.Repo, "b"));
        File.WriteAllText(Path.Combine(repo.Repo, "a", "x.txt"), "a\n");
        File.WriteAllText(Path.Combine(repo.Repo, "b", "x.txt"), "b\n");
        repo.CommitAll("a and b");
        repo.Git("sparse-checkout", "set", "--cone", "a");
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Commands = ["mkdir -p b", "printf 'x\\n' > b/new.txt"] });

        (CliResult process, JsonElement result) = await repo.RunBuilderWithEnvAsync(Locale(English));

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string branch = Str(result, "branch");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("commit").ValueKind);
        string[] warnings = Warnings(result);
        Assert.Equal(2, warnings.Length);
        Assert.Contains("b/new.txt left out of the commit", warnings[0], StringComparison.Ordinal);
        Assert.Equal($"work left uncommitted on {branch}: git add staged nothing: The following paths and/or pathspecs matched paths that exist", warnings[1]);
    }

    // ---- the environment git inherits ------------------------------------------------------------------------------

    // With GIT_LITERAL_PATHSPECS=1 in the caller's environment, `:(exclude).claustrum/worktrees` is a literal path: status
    // listed everything and `add` died "pathspec … did not match any files" (exit 128). `git --no-literal-pathspecs` overrides it.
    [Fact]
    public async Task LiteralPathspecsInTheEnvironmentDoNotStopTheRealFileFromBeingCommittedAsync()
    {
        RequirePosix();
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript
        {
            Writes = [("real.txt", "real")],
            Commands =
            [
                "mkdir -p .claustrum/briefs .claustrum/locks",
                "printf 'brief\\n' > .claustrum/briefs/1-builder.md",
                "printf '' > .claustrum/locks/default__builder.0.lock",
                "git init -q vendor/x",
                CloneInto("vendor/y"),
            ],
        });
        Dictionary<string, string?> env = new() { ["GIT_LITERAL_PATHSPECS"] = "1" };

        (CliResult process, JsonElement result) = await repo.RunBuilderWithEnvAsync(env);

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string branch = Str(result, "branch");
        Assert.Equal("real.txt", TestGit.Run(repo.Repo, "show", "--name-only", "--format=", Str(result, "commit")));
        Assert.Equal(
            [
                $"embedded repository at vendor/x left out of the commit on {branch} — move it out or add it as a submodule",
                $"embedded repository at vendor/y left out of the commit on {branch} — move it out or add it as a submodule",
            ],
            Warnings(result));
    }

    // H5: a failure after `add` leaves the index as add made it — here with a stray gitlink staged — and the warning's own
    // remedy ("commit it inside that worktree yourself") would commit it. Every failure up to the commit clears the index.
    [Fact]
    public async Task AFailedUnstageOfAStrayCloneClearsTheIndexAndSaysSoAsync()
    {
        RequirePosix();
        string shims = Directory("claustrum-git-shim-");
        string realGit = Path.Combine(IsolatedRepo.ChildPath().Split(Path.PathSeparator)[0], "git");
        string shim = Path.Combine(shims, "git");
        File.WriteAllText(shim, $"#!/bin/sh\ncase \" $* \" in *\" --literal-pathspecs reset \"*) echo 'fatal: shim says no' >&2; exit 1;; esac\nexec '{realGit}' \"$@\"\n");
        MakeExecutable(shim);
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Writes = [("real.txt", "real")], Commands = [CloneInto("vendor/y")] });
        string path = string.Join(Path.PathSeparator, shims, IsolatedRepo.ChildPath());

        (CliResult process, JsonElement result) = await repo.RunBuilderWithEnvAsync(new Dictionary<string, string?>(), path);

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string branch = Str(result, "branch");
        string worktree = Str(result, "worktree");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("commit").ValueKind);
        Assert.Equal(
            [$"work left uncommitted on {branch}: an embedded repository at vendor/y could not be unstaged: fatal: shim says no (index cleared: nothing is staged)"],
            Warnings(result));
        Assert.Equal("", TestGit.Run(worktree, "diff", "--cached", "--name-only", "--ignore-submodules=none"));
        Assert.Contains("?? real.txt", TestGit.Status(worktree), StringComparison.Ordinal);
        Assert.Equal(TestGit.RevParse(repo.Repo, "main"), TestGit.RevParse(repo.Repo, $"refs/heads/{branch}"));
    }

    // ---- --branch over a finished worktree with a populated submodule, in git's own language ---------------------------

    [Theory]
    [InlineData(English)]
    [InlineData(Italian)]
    public async Task ABranchRunOverAFinishedWorktreeWithAPopulatedSubmoduleGetsTheDedicatedMessageInAnyLanguageAsync(string locale)
    {
        RequirePosix();
        RequireLocale(locale);
        string source = Directory("claustrum-submodule-source-");
        TestGit.Init(source);
        File.WriteAllText(Path.Combine(source, "seed.txt"), "seed\n");
        TestGit.CommitAll(source, "seed");
        repo.Git("-c", "protocol.file.allow=always", "submodule", "add", "-q", source, "sub");
        repo.CommitAll("add sub");
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript
        {
            Writes = [("real.txt", "real")],
            Commands = ["git -c protocol.file.allow=always submodule update --init -q sub"],
        });
        (CliResult first, JsonElement firstResult) = await repo.RunBuilderWithEnvAsync(Locale(locale));
        Assert.True(first.ExitCode == ExitCodes.Ok, first.Stderr);
        string branch = Str(firstResult, "branch");
        string worktree = Str(firstResult, "worktree");

        (CliResult second, JsonElement refused) = await repo.RunBuilderWithEnvAsync(Locale(locale), null, "--branch", branch);

        Assert.Equal(ExitCodes.BackendFailure, second.ExitCode);
        Assert.Equal("failed", Str(refused, "status"));
        Assert.Contains($"--branch {branch}: its previous worktree ", Str(refused, "error"), StringComparison.Ordinal);
        Assert.Contains("holds a populated submodule, which git will not remove — remove it by hand (`git worktree remove --force \"", Str(refused, "error"), StringComparison.Ordinal);
        Assert.Contains("\"`, which also deletes any commit made inside the submodule and pushed nowhere) and retry", Str(refused, "error"), StringComparison.Ordinal);
        Assert.True(System.IO.Directory.Exists(worktree));
    }

    // The same worktree with a dirty file inside the submodule must keep its work: the generic text, never `--force`.
    [Theory]
    [InlineData(English)]
    [InlineData(Italian)]
    public async Task ADirtySubmoduleWorktreeGetsTheGenericLeftInPlaceTextInAnyLanguageAsync(string locale)
    {
        RequirePosix();
        RequireLocale(locale);
        string source = Directory("claustrum-submodule-source-");
        TestGit.Init(source);
        File.WriteAllText(Path.Combine(source, "seed.txt"), "seed\n");
        TestGit.CommitAll(source, "seed");
        repo.Git("-c", "protocol.file.allow=always", "submodule", "add", "-q", source, "sub");
        repo.CommitAll("add sub");
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript
        {
            Writes = [("real.txt", "real")],
            Commands = ["git -c protocol.file.allow=always submodule update --init -q sub", "printf 'dirty\\n' > sub/seed.txt"],
        });
        (CliResult first, JsonElement firstResult) = await repo.RunBuilderWithEnvAsync(Locale(locale));
        Assert.True(first.ExitCode == ExitCodes.Ok, first.Stderr);
        string branch = Str(firstResult, "branch");

        (CliResult second, JsonElement refused) = await repo.RunBuilderWithEnvAsync(Locale(locale), null, "--branch", branch);

        Assert.Equal(ExitCodes.BackendFailure, second.ExitCode);
        string error = Str(refused, "error");
        Assert.Contains("which was left in place (uncommitted changes:  M sub) — commit or discard its changes, then retry", error, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", error, StringComparison.Ordinal);
        Assert.Equal("dirty\n", File.ReadAllText(Path.Combine(Str(firstResult, "worktree"), "sub", "seed.txt")));
    }

    // A main checkout whose `.git/modules` holds the submodule, and a job worktree whose gitlink was never initialised:
    // no false positive — the branch is freed and the second run succeeds, whatever git speaks.
    [Theory]
    [InlineData(English)]
    [InlineData(Italian)]
    public async Task AMainCheckoutWithModulesAndAnUninitialisedGitlinkIsFreedInAnyLanguageAsync(string locale)
    {
        RequirePosix();
        RequireLocale(locale);
        string source = Directory("claustrum-submodule-source-");
        TestGit.Init(source);
        File.WriteAllText(Path.Combine(source, "seed.txt"), "seed\n");
        TestGit.CommitAll(source, "seed");
        repo.Git("-c", "protocol.file.allow=always", "submodule", "add", "-q", source, "sub");
        repo.CommitAll("add sub");
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Writes = [("real.txt", "real")] });
        (CliResult first, JsonElement firstResult) = await repo.RunBuilderWithEnvAsync(Locale(locale));
        Assert.True(first.ExitCode == ExitCodes.Ok, first.Stderr);
        string branch = Str(firstResult, "branch");
        repo.Script(new FakeClaudeScript { Writes = [("second.txt", "second")] });

        (CliResult second, JsonElement result) = await repo.RunBuilderWithEnvAsync(Locale(locale), null, "--branch", branch);

        Assert.True(second.ExitCode == ExitCodes.Ok, second.Stderr + second.Stdout);
        Assert.Equal(branch, Str(result, "branch"));
        Assert.False(System.IO.Directory.Exists(Str(firstResult, "worktree")));
    }
}
