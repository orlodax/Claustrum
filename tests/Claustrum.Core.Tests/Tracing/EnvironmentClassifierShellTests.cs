using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Tracing;

// #145: a shell call is the one tool whose output the classifier reads, and only when the command is not just a viewer of files. Each case is
// a command and what it printed, in claude's log shape (the ACP and opencode transports are covered by the cases at the end). The text in
// the outputs is invented; the shapes are those of recorded jobs. NOTES.md "Environment issues", the viewer filter.
public sealed class EnvironmentClassifierShellTests
{
    private const string Traceback =
        "Traceback (most recent call last):\n  File \"/usr/lib/python3/dist-packages/odoo/tools/config.py\", line 777, in _is_addons_path\n    for f in os.listdir(path):\nPermissionError: [Errno 13] Permission denied: '/mnt/extra-addons'";

    private const string GraphQl = "GraphQL: Could not resolve to a Repository with the name 'teksistemi-software/odoo-altus'. (repository)";

    private const string IssuePaste =
        "title:\tDeploy fails on Fedora\nstate:\tOPEN\n--\nThe install stops with:\n\n```\nfatal: Authentication failed for 'https://github.com/teksistemi-software/odoo-altus.git/'\nPermissionError: [Errno 13] Permission denied: '/var/lib/odoo'\n```\n";

    private static EnvironmentIssue[] Run(string command, string output, bool isError = false, PermissionLevel level = PermissionLevel.EditShell) =>
        RunLogs.ClassifyClaude(RunLogs.Claude("default", RunLogs.Bash(command, output, isError)), level);

    private static EnvironmentIssue[] RunAll(params RunLogs.ToolCall[] steps) => RunLogs.ClassifyClaude(RunLogs.Claude("default", steps));

    // ---- the viewer filter: a command that only shows files prints their content, not the environment's answer --------------------------------

    [Theory]
    [InlineData("cat docs/TROUBLESHOOTING.md")]
    [InlineData("grep -E 'a|b' docs/TROUBLESHOOTING.md")]
    [InlineData("git log --oneline | head -20")]
    [InlineData("cd /work/repo/docs && git log")]
    [InlineData("grep -rn Errno NOTES.md || true")]
    [InlineData("for f in *.log; do cat \"$f\"; done")]
    [InlineData("if [ -f x ]; then cat x; fi 2>/dev/null")]
    [InlineData("git ls-files | xargs grep -n PermissionError")]
    [InlineData("X=$(git rev-parse HEAD); grep -n x NOTES.md")]
    [InlineData("(cd /work/repo && git log)")]
    [InlineData("git status --short && cat README.md")]
    [InlineData("cat f.json | python3 -m json.tool")]
    [InlineData("while read -r l; do echo \"$l\"; done < notes.txt")]
    [InlineData("sudo -u odoo cat /etc/odoo.conf")]
    [InlineData("env -i PATH=/usr/bin cat a")]
    [InlineData("nice -n 10 cat a")]
    [InlineData("time cat a")]
    [InlineData("timeout -s KILL 5 cat a")]
    [InlineData("cat a 2>&1")]
    [InlineData("cat a 2>/dev/null; cat b")]
    [InlineData("2>/dev/null cat a")]
    [InlineData("{ cat a; cat b; } | head")]
    [InlineData("cat a > /dev/null; cat b")]
    [InlineData("git diff HEAD~1 -- a.py b.py")]
    [InlineData("for f in *.log; do cat \"$f\"; done > /tmp/all.txt")]
    [InlineData("{ cat a; cat b; } > out.txt")]
    [InlineData("cat <<'EOF' > f.txt\nit's data\nEOF\ncat f.txt")]
    [InlineData("echo $((1 << 3)); cat a")]
    [InlineData("X=$(cat <<'EOF'\nit's\nEOF\n); echo \"$X\"")]
    [InlineData("command -v gh && gh issue view 1")]
    [InlineData("nohup cat a")]
    [InlineData("xargs -n 1 grep x")]
    [InlineData("env FOO=1 BAR=2 cat a")]
    [InlineData("sudo -n cat a")]
    [InlineData("cat < in.txt")]
    [InlineData("grep x <<< \"some text\"")]
    [InlineData("cat a 1>&2")]
    [InlineData("cat a >&2")]
    [InlineData("cat a 2>&1 >/dev/null")]
    [InlineData("cat a &>/dev/null")]
    [InlineData("2>&1 cat a")]
    [InlineData("claustrum jobs show 20261029-095220-0da9d76d")]
    [InlineData("echo \"x $(cat a)\"")]
    public void ACommandThatOnlyViewsFilesIsNotReadForTheEnvironmentsAnswer(string command)
    {
        Assert.Empty(Run(command, Traceback));
        Assert.Empty(Run(command, "fatal: Authentication failed for 'https://github.com/x/y.git/'"));
        Assert.Empty(Run(command, IssuePaste));
    }

    // `2>&1` is a redirect inside one command, not a split on `&`: `docker ps 2>&1` is still docker's own output, read.
    [Fact]
    public void ARedirectOfStderrIsNotASplit()
    {
        EnvironmentIssue issue = Assert.Single(Run("docker compose logs odoo 2>&1", Traceback));

        Assert.Equal(EnvironmentIssueKind.ContainerPermission, issue.Kind);
    }

    [Fact]
    public void SedIsAViewerExceptWhenItEditsInPlace()
    {
        Assert.Empty(Run("sed -n 1,20p notes.txt", Traceback));
        EnvironmentIssue issue = Assert.Single(Run("sed -i s/a/b/ notes.txt", Traceback));
        Assert.Equal(EnvironmentIssueKind.ContainerPermission, issue.Kind);
    }

    [Theory]
    [InlineData("case $x in a) docker ps;; esac")]
    [InlineData("docker ps 'oops")]
    [InlineData("echo $(cat \"unclosed")]
    public void ACommandThatCannotBeReadWithCertaintyIsSkipped(string command)
    {
        Assert.Empty(Run(command, "permission denied while trying to connect to the Docker daemon socket"));
        Assert.Empty(Run(command, Traceback));
    }

    // A program the filter does not know is not a viewer: what it printed is its own, so a script that prints a traceback is read.
    [Theory]
    [InlineData("python3 tools/run.py")]
    [InlineData("git log --oneline | head; python3 tools/run.py")]
    [InlineData("python3 - <<'EOF'\nprint(1)\nEOF")]
    [InlineData("sudo -u odoo python3 tools/run.py")]
    [InlineData("env -i PATH=/usr/bin python3 tools/run.py")]
    [InlineData("nice -n 10 python3 tools/run.py")]
    [InlineData("python3 -c \"print(1)\"")]
    [InlineData("docker compose up odoo")]
    [InlineData("./scripts/run-tests.sh")]
    [InlineData("dotnet test --filter Errno")]
    public void ACommandThatIsNotAViewerIsReadForTheTracebackItPrinted(string command)
    {
        EnvironmentIssue issue = Assert.Single(Run(command, Traceback));

        Assert.Equal(EnvironmentIssueKind.ContainerPermission, issue.Kind);
        Assert.Equal("PermissionError: [Errno 13]", issue.What);
        Assert.Equal("/mnt/extra-addons", issue.Where);
        Assert.Equal(1, issue.Count);
    }

    // ---- gh: a viewer of issues and pull requests, read for gh's own error line only --------------------------------------------------------

    [Theory]
    [InlineData("gh issue view 231 --comments")]
    [InlineData("gh issue view 231 --comments 2>&1 | head -200")]
    [InlineData("gh issue view 231 --comments | jq .title")]
    [InlineData("gh pr view 12 || true")]
    [InlineData("gh issue view 231 > /tmp/i.txt 2>&1; cat /tmp/i.txt")]
    public void AFailedGhIsCaughtByItsFirstLineEvenWhenPipedOrForcedToExitZero(string command)
    {
        // The pipe or `|| true` makes the call exit 0, so claude reports no error: the output is gh's error line, then nothing.
        EnvironmentIssue issue = Assert.Single(Run(command, GraphQl));

        Assert.Equal(EnvironmentIssueKind.Auth, issue.Kind);
        Assert.Equal("HTTP 404 from GitHub", issue.What);
        Assert.Equal(command, issue.Where);
    }

    [Fact]
    public void AGhApiAnswerThatFailsIsCaughtThroughJq()
    {
        EnvironmentIssue issue = Assert.Single(Run("gh api repos/teksistemi-software/odoo-altus/issues/231 | jq .title", "gh: Not Found (HTTP 404)"));

        Assert.Equal("HTTP 404 from GitHub", issue.What);
    }

    [Fact]
    public void AGhFailureOnAnActedOnCallIsReadWholeWhenTheCallFailed()
    {
        EnvironmentIssue issue = Assert.Single(Run("gh api repos/teksistemi-software/odoo-altus/issues/1", "Exit code 1\n{\"message\":\"Not Found\"}\ngh: Not Found (HTTP 404)", isError: true));

        Assert.Equal("HTTP 404 from GitHub", issue.What);
    }

    // A viewer that did not fail prints an issue, a diff or a release: its first line is gh's error or content, never both, so later lines are content.
    [Theory]
    [InlineData("gh issue view 231 --comments | head -50", "title:\tx\n--\nHTTP 404: Not Found (https://api.github.com/x)")]
    [InlineData("gh issue view 1", "title:\tx\n--\nHTTP 404: Not Found (https://api.github.com/x)\nTo get started with GitHub CLI, please run:  gh auth login\nPermissionError: [Errno 13] Permission denied: '/mnt/x'")]
    [InlineData("gh release view v0.4.0", "v0.4.0\n--\nHTTP 404: Not Found (https://api.github.com/x)\nerror connecting to api.github.com")]
    [InlineData("gh issue view 230 --json comments --jq '.comments[].body'", IssuePaste)]
    [InlineData("gh pr diff 5", "diff --git a/x b/x\n+fatal: Authentication failed for 'https://github.com/x/y.git/'")]
    [InlineData("claustrum run builder --json --brief-file b.md", /*lang=json,strict*/ "{\"status\":\"failed\",\"error\":\"HTTP 404: Not Found\"}")]
    public void ContentInAGhViewerOrClaustrumOutputIsSilent(string command, string output)
    {
        Assert.Empty(Run(command, output));
    }

    [Theory]
    [InlineData("You are not logged into any GitHub hosts. To log in, run: gh auth login", "gh is not logged in (gh auth login)")]
    [InlineData("To get started with GitHub CLI, please run:  gh auth login\nAlternatively, populate the GH_TOKEN environment variable.", "gh is not logged in (gh auth login)")]
    [InlineData("error connecting to api.github.com", "gh could not connect to GitHub")]
    [InlineData("HTTP 403: Resource not accessible by integration (https://api.github.com/repos/x/y)", "HTTP 403")]
    [InlineData("HTTP 401: Bad credentials (https://api.github.com/user)", "HTTP 401")]
    public void GhsNotLoggedInAndStatusLinesAreAuthIssues(string output, string what)
    {
        EnvironmentIssue issue = Assert.Single(Run("gh pr create --fill", $"Exit code 4\n{output}", isError: true));

        Assert.Equal(EnvironmentIssueKind.Auth, issue.Kind);
        Assert.Equal(what, issue.What);
    }

    // ---- git and curl: an error line counts only after the verb that talks to a remote ---------------------------------------------------------

    [Theory]
    [InlineData("git push origin HEAD", "fatal: could not read Username for 'https://github.com': No such device or address", "git could not read a username (no credentials)")]
    [InlineData("git fetch origin && git rebase origin/main", "remote: Invalid username or token.\nfatal: Authentication failed for 'https://github.com/x/y.git/'", "Authentication failed")]
    [InlineData("git clone git@github.com:x/y.git", "Cloning into 'y'...\ngit@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", "Permission denied (publickey)")]
    [InlineData("git push", "remote: Permission to x/y.git denied to bot.\nfatal: unable to access 'https://github.com/x/y.git/': The requested URL returned error: 403", "HTTP 403")]
    [InlineData("git -C /work/repo fetch --all 2>&1 | tail -5", "fatal: Authentication failed for 'https://github.com/x/y.git/'", "Authentication failed")]
    [InlineData("git ls-remote origin", "fatal: Authentication failed for 'https://github.com/x/y.git/'", "Authentication failed")]
    [InlineData("git submodule update --init", "fatal: Authentication failed for 'https://github.com/x/y.git/'", "Authentication failed")]
    public void AGitNetworkVerbsAuthErrorLineIsAnAuthIssue(string command, string output, string what)
    {
        EnvironmentIssue issue = Assert.Single(Run(command, output));

        Assert.Equal(EnvironmentIssueKind.Auth, issue.Kind);
        Assert.Equal(what, issue.What);
    }

    [Fact]
    public void TheSameAuthTextFromACommandThatIsNotAGitNetworkVerbIsContent()
    {
        Assert.Empty(Run("git status && python3 tools/print_notes.py", "On branch main\nfatal: Authentication failed for 'https://example.com/'\ngh: Not Found (HTTP 404)"));
        Assert.Empty(Run("git commit -m x", "fatal: Authentication failed for 'https://example.com/'"));
    }

    [Fact]
    public void AGitFetchThatPrintedAnAuthErrorOutsideALineStartIsContent()
    {
        Assert.Empty(Run("git fetch origin", "remote: the docs say 'fatal: Authentication failed' when a token expires"));
    }

    [Theory]
    [InlineData("curl -fsS https://api.example.com/v1/x", "Exit code 22\ncurl: (22) The requested URL returned error: 403", "HTTP 403")]
    [InlineData("curl -sf -H 'Authorization: Bearer x' https://api.example.com/v1/x", "Exit code 22\ncurl: (22) The requested URL returned error: 401", "HTTP 401")]
    [InlineData("curl -sSf https://api.github.com/repos/x/y", "Exit code 22\ncurl: (22) The requested URL returned error: 404", "HTTP 404 from GitHub")]
    [InlineData("curl -fsSL https://raw.githubusercontent.com/x/y/main/f.py", "Exit code 22\ncurl: (22) The requested URL returned error: 404", "HTTP 404 from GitHub")]
    public void CurlsErrorLineNamesNoUrlSoTheCommandsFirstRemoteHostIsTheOne(string command, string output, string what)
    {
        EnvironmentIssue issue = Assert.Single(Run(command, output, isError: true));

        Assert.Equal(EnvironmentIssueKind.Auth, issue.Kind);
        Assert.Equal(what, issue.What);
    }

    [Theory]
    [InlineData("curl -fsS http://localhost:8069/web/login")]
    [InlineData("curl -fsS http://127.0.0.1:8069/web/login")]
    [InlineData("curl -fsS http://[::1]:8069/web/login")]
    [InlineData("curl -fsS http://odoo.localhost:8069/web/login")]
    public void ACurl403FromTheAppUnderTestOnLoopbackIsNotTheEnvironment(string command)
    {
        Assert.Empty(Run(command, "Exit code 22\ncurl: (22) The requested URL returned error: 403", isError: true));
    }

    [Fact]
    public void ACurl404OnASiteThatIsNotGitHubIsAGuessedPath()
    {
        Assert.Empty(Run("curl -fsSL https://example.com/missing", "Exit code 22\ncurl: (22) The requested URL returned error: 404", isError: true));
    }

    // ---- container_permission: SELinux and the docker socket ---------------------------------------------------------------------------

    [Theory]
    [InlineData("docker ps", "permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock: Get \"http://%2Fvar%2Frun%2Fdocker.sock/v1.24/containers/json\": dial unix /var/run/docker.sock: connect: permission denied")]
    [InlineData("docker compose up -d", "unable to get image 'postgres:16': permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock")]
    [InlineData("docker ps", "permission denied while trying to connect to the docker API at unix:///var/run/docker.sock")]
    [InlineData("docker compose up -d", "unable to get image 'postgres:16': permission denied while trying to connect to the docker API at unix:///var/run/docker.sock")]
    [InlineData("docker ps", "Got permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock")]
    [InlineData("docker ps", "docker: permission denied while trying to connect to the docker API at unix:///var/run/docker.sock")]
    public void DockersSocketRefusalIsAContainerPermissionIssueInEitherWording(string command, string output)
    {
        EnvironmentIssue issue = Assert.Single(Run(command, output));

        Assert.Equal(EnvironmentIssueKind.ContainerPermission, issue.Kind);
        Assert.Equal("permission denied connecting to the Docker daemon", issue.What);
        Assert.Equal(command, issue.Where);
    }

    [Theory]
    [InlineData("docker compose logs odoo | tail -20", "odoo-1  | Traceback (most recent call last):\nodoo-1  |   File \"x.py\", line 1\nodoo-1  | PermissionError: [Errno 13] Permission denied: '/mnt/extra-addons'", "/mnt/extra-addons")]
    [InlineData("docker compose up odoo", "odoo-1  | PermissionError: [Errno 13] Permission denied: '/mnt/extra-addons'\nodoo-1 exited with code 1", "/mnt/extra-addons")]
    [InlineData("docker compose exec web pytest tests/ -x", "E   PermissionError: [Errno 13] Permission denied: '/data/media'", "/data/media")]
    [InlineData("docker run --rm -v $PWD/addons:/mnt/extra-addons odoo:19 odoo -i base --stop-after-init", Traceback, "/mnt/extra-addons")]
    [InlineData("docker logs odoo-test 2>&1 | tail -12", "2026-10-29 09:00:00,123 1 ERROR odoo odoo.modules.loading: failed\n" + Traceback, "/mnt/extra-addons")]
    [InlineData("python3 - <<'EOF'\nprint(\"don't panic\")\nimport os; os.listdir('/mnt/extra-addons')\nEOF", Traceback, "/mnt/extra-addons")]
    public void ATracebacksPermissionErrorIsAContainerPermissionIssueWithThePathAsItsWhere(string command, string output, string where)
    {
        EnvironmentIssue issue = Assert.Single(Run(command, output));

        Assert.Equal(EnvironmentIssueKind.ContainerPermission, issue.Kind);
        Assert.Equal("PermissionError: [Errno 13]", issue.What);
        Assert.Equal(where, issue.Where);
    }

    // The traceback's last line is read at the start of its line, with only a Compose service prefix or pytest's `E   ` before it:
    // a grep hit, a log line that quotes it and a source comment are content.
    [Theory]
    [InlineData("config.py:777:    # PermissionError: [Errno 13] Permission denied: '/x'")]
    [InlineData("ERROR odoo: raised PermissionError: [Errno 13] Permission denied: '/x'")]
    [InlineData("See the PermissionError: [Errno 13] note above")]
    [InlineData("odoo-1 exited with code 1 PermissionError: [Errno 13]")]
    public void APermissionErrorThatDoesNotStartItsLineIsNotCounted(string output)
    {
        Assert.Empty(Run("docker compose up odoo", output));
    }

    [Fact]
    public void ATracebackWithNoPathNamesTheCommandInstead()
    {
        EnvironmentIssue issue = Assert.Single(Run("./run.sh", "PermissionError: [Errno 13] Permission denied"));

        Assert.Equal("./run.sh", issue.Where);
    }

    // A count is of calls: the same path in a call's traceback twice is one hit, and two calls make two.
    [Fact]
    public void TheSameIssueFromTwoCallsGroupsWithACountOfTwo()
    {
        EnvironmentIssue[] issues = RunAll(
            RunLogs.Bash("docker compose up odoo", Traceback + "\n" + Traceback),
            RunLogs.Bash("docker compose up odoo", "odoo-1  | PermissionError: [Errno 13] Permission denied: '/mnt/extra-addons'"));

        EnvironmentIssue issue = Assert.Single(issues);
        Assert.Equal(2, issue.Count);
        Assert.Equal("/mnt/extra-addons", issue.Where);
        Assert.Equal("stdout.log:3", issue.First);
    }

    // ---- missing_directory in a shell output: only the job's cwd or a read dir -------------------------------------------------------------------

    [Fact]
    public void ACdIntoTheJobsOwnVanishedDirectoryIsAMissingDirectory()
    {
        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyClaude(
            RunLogs.Claude("default", RunLogs.Bash("cd /work/repo && make", "bash: line 1: cd: /work/repo: No such file or directory", isError: true))));

        Assert.Equal(EnvironmentIssueKind.MissingDirectory, issue.Kind);
        Assert.Equal("path does not exist", issue.What);
        Assert.Equal("/work/repo", issue.Where);
    }

    [Fact]
    public void AMissingDirectoryInsideAReadDirCountsAndAnyOtherPathIsAGuess()
    {
        string log = RunLogs.Claude("default", RunLogs.Bash("make -C /srv/shared/lib", "make: *** /srv/shared/lib: No such file or directory.  Stop.", isError: true));
        string guess = RunLogs.Claude("default", RunLogs.Bash("make -C /tmp/elsewhere", "make: *** /tmp/elsewhere: No such file or directory.  Stop.", isError: true));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyClaude(log, readDirs: ["/srv/shared"]));
        Assert.Equal("/srv/shared/lib", issue.Where);
        Assert.Empty(RunLogs.ClassifyClaude(log));
        Assert.Empty(RunLogs.ClassifyClaude(guess, readDirs: ["/srv/shared"]));
    }

    [Fact]
    public void ACdThatFailsInALineOfViewersOnlyIsNotRead()
    {
        Assert.Empty(RunLogs.ClassifyClaude(RunLogs.Claude("default", RunLogs.Bash("cd /work/gone && git log", "bash: line 1: cd: /work/gone: No such file or directory", isError: true)), cwd: "/work/gone"));
    }

    // ---- the run's own files: a viewer that reads back what the run's commands wrote shows their output ---------------------------------------

    [Fact]
    public void AViewerOverAFileARedirectWroteIsReadLikeTheCommandThatWroteIt()
    {
        EnvironmentIssue issue = Assert.Single(RunAll(
            RunLogs.Bash("./scripts/run-tests.sh > /tmp/i231-run.log 2>&1 &"),
            RunLogs.Bash("tail -30 /tmp/i231-run.log", Traceback)));

        Assert.Equal(EnvironmentIssueKind.ContainerPermission, issue.Kind);
        Assert.Equal("stdout.log:5", issue.First);
    }

    [Theory]
    [InlineData("docker compose up odoo > /tmp/x.log 2>&1 &", "until grep -q '^EXIT' /tmp/x.log; do :; done; tail -30 /tmp/x.log")]
    [InlineData("L=/tmp/w/base.log; docker exec db createdb x > $L 2>&1", "tail -20 /tmp/w/base.log")]
    [InlineData("docker compose up 2>&1 | tee /tmp/w/up.log", "grep -n Errno /tmp/w/up.log; tail /tmp/w/up.log")]
    [InlineData("/tmp/w/setup.sh > /tmp/w/setup.log 2>&1; echo exit=$?", "tail -20 /tmp/w/setup.log")]
    [InlineData("./run.sh &> /tmp/w/all.log", "cat /tmp/w/all.log")]
    [InlineData("./run.sh >> /tmp/w/all.log", "cat /tmp/w/all.log")]
    [InlineData("./run.sh 2> /tmp/w/err.log", "cat /tmp/w/err.log")]
    [InlineData("./run.sh >/tmp/w/all.log", "cat /tmp/w/./sub/../all.log")]
    public void AViewerOverTheRunsOwnLogIsReadForTheTracebackInIt(string writer, string viewer)
    {
        EnvironmentIssue issue = Assert.Single(RunAll(RunLogs.Bash(writer), RunLogs.Bash(viewer, Traceback)));

        Assert.Equal(EnvironmentIssueKind.ContainerPermission, issue.Kind);
        Assert.Equal("/mnt/extra-addons", issue.Where);
    }

    // claude says where a background command's output goes; a viewer of that file shows the command's own output.
    [Fact]
    public void AClaudeBackgroundTaskOutputFileIsTheRunsOwn()
    {
        EnvironmentIssue issue = Assert.Single(RunAll(
            RunLogs.Bash("docker run --rm odoo:19 odoo -i base", "Command running in background with ID: b1abc. Output is being written to: /tmp/claude-1000/p/s/tasks/b1abc.output. You will be notified when it completes."),
            RunLogs.Bash("cat /tmp/claude-1000/p/s/tasks/b1abc.output", Traceback)));

        Assert.Equal(EnvironmentIssueKind.ContainerPermission, issue.Kind);
    }

    // The comparison is exact on the normalised path, in the order the calls came, and by what produced the file.
    [Fact]
    public void AViewerOverAnyOtherFileOrBeforeTheWriteOrAfterACopyIsContent()
    {
        // n1: a viewer's own redirect copies a document, it produces nothing.
        Assert.Empty(RunAll(RunLogs.Bash("cat docs/TROUBLESHOOTING.md > /tmp/copy.txt"), RunLogs.Bash("cat /tmp/copy.txt", Traceback)));

        // n4: another file than the one written.
        Assert.Empty(RunAll(RunLogs.Bash("./run.sh > /tmp/a.log 2>&1"), RunLogs.Bash("tail /tmp/b.log", Traceback)));

        // n5: the viewer came before the write.
        Assert.Empty(RunAll(RunLogs.Bash("tail /tmp/c.log", Traceback), RunLogs.Bash("./run.sh > /tmp/c.log 2>&1")));
    }

    // n2: a path through a variable the line never set is a doubt, and a doubt is not read.
    [Fact]
    public void AViewerOverAPathThroughAnUnknownVariableIsNotRead()
    {
        Assert.Empty(RunAll(
            RunLogs.Bash("SCR=$(cat /tmp/p); bash $SCR/install.sh > $SCR/install.log 2>&1"),
            RunLogs.Bash("SCR=$(cat /tmp/p); tail -15 $SCR/install.log", Traceback)));
    }

    // n3: claude reports no working directory for a shell call, so a relative path cannot be matched to the writer's.
    [Fact]
    public void ARelativeLogPathIsNotMatchedInClaudesLog()
    {
        Assert.Empty(RunAll(RunLogs.Bash("./run.sh > out.log 2>&1"), RunLogs.Bash("tail out.log", Traceback)));
    }

    // ---- ACP and opencode report the call's directory, so a relative path can be matched there --------------------------------------------

    [Fact]
    public void AnAcpViewerOverARelativeLogIsReadWhenTheCallsDirectoryIsReportedAndNoCdMovedIt()
    {
        string log = RunLogs.Acp(RunLogs.Execute("./run.sh > out.log 2>&1"), RunLogs.Execute("tail -20 out.log", Traceback));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyAcp(log));
        Assert.Equal(EnvironmentIssueKind.ContainerPermission, issue.Kind);

        string moved = RunLogs.Acp(RunLogs.Execute("./run.sh > out.log 2>&1"), RunLogs.Execute("cd sub && tail -20 out.log", Traceback));
        Assert.Empty(RunLogs.ClassifyAcp(moved));
    }

    [Fact]
    public void AGhFailureFirstLineIsCaughtOnAcpWithExitZeroAndItsBodyIsNot()
    {
        string failed = RunLogs.Acp(RunLogs.Execute("gh issue view 231 --comments 2>&1 | head -200", GraphQl, exit: 0));
        string body = RunLogs.Acp(RunLogs.Execute("gh issue view 231 --comments | head -50", "title:\tx\n--\nHTTP 404: Not Found (https://api.github.com/x)", exit: 0));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyAcp(failed, PermissionLevel.ReadOnly));
        Assert.Equal("HTTP 404 from GitHub", issue.What);
        Assert.Equal("stdout.log:5", issue.First);
        Assert.Empty(RunLogs.ClassifyAcp(body, PermissionLevel.ReadOnly));
    }

    [Fact]
    public void AnAcpGhViewerThatExitedNonZeroIsReadWhole()
    {
        string log = RunLogs.Acp(RunLogs.Execute("gh issue view 231", "title:\tx\nHTTP 404: Not Found (https://api.github.com/x)", exit: 1));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyAcp(log, PermissionLevel.ReadOnly));
        Assert.Equal("HTTP 404 from GitHub", issue.What);
    }

    [Fact]
    public void AnOpencodeShellCallIsReadLikeAnAcpOne()
    {
        string log = RunLogs.Opencode(new RunLogs.ToolCall("shell", new { command = "docker compose up odoo", workdir = "/work/repo" }, Traceback));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyOpencode(log));
        Assert.Equal(EnvironmentIssueKind.ContainerPermission, issue.Kind);
        Assert.Equal("stdout.log:1", issue.First);
    }
}
