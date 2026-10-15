using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #124 stage 3 review F3: in a coordinate tree a child that is not live (`--no-live`, or a backend without a live protocol) cannot wait, so it ends
// needs_input and its `run` exits 7 with its WHOLE RunResult, a different document from the early return of a child that still waits. `jobs reply` has
// nobody alive to answer and says to use `jobs answer`, which resumes it. DetachedRepo supplies the tree (a fake root) and kills any host.
public sealed class TreeNeedsInputEndToEndTests : IDisposable
{
    private const string Asks =
        /*lang=json,strict*/ """{"status":"needs_input","summary":"stopped","questions":[{"key":"db","prompt":"Which database?","options":["sqlite","postgres"],"allow_free_form":true}]}""";

    private readonly DetachedRepo fixture = new();

    public void Dispose() => fixture.Dispose();

    private Dictionary<string, string?> InTree() => DetachedRepo.Tree(fixture.MintRoot());

    [Fact]
    public async Task ANonLiveChildThatNeedsInputExitsSevenWithItsWholeRunResultAndNothingAliveAsync()
    {
        fixture.Repo.Script(new FakeClaudeScript { ReportJson = Asks, SessionId = "child-session" });

        CliResult process = await fixture.RunBuilderAsync(InTree(), "--json", "--no-live");

        Assert.Equal(ExitCodes.NeedsInput, process.ExitCode);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        JsonElement result = document.RootElement;
        Assert.Equal("needs_input", result.GetProperty("status").GetString());
        Assert.Equal("child-session", result.GetProperty("session_id").GetString());
        Assert.Equal("db", Assert.Single(result.GetProperty("questions").EnumerateArray()).GetProperty("key").GetString());

        // Not the early return of a waiting child: no `question`, no host, a receipt on disk.
        Assert.False(result.TryGetProperty("question", out _));
        string id = fixture.OnlyJob();
        Assert.Equal(id, result.GetProperty("job_id").GetString());
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(id), "host.json")));
        Assert.Equal("needs_input", fixture.ResultStatus(id));
    }

    [Fact]
    public async Task JobsReplyOnThatChildIsRefusedPointingToJobsAnswerAndJobsAnswerResumesItAsync()
    {
        fixture.Repo.Script(new FakeClaudeScript { ReportJson = Asks, SessionId = "child-session" });
        CliResult asked = await fixture.RunBuilderAsync(InTree(), "--json", "--no-live");
        string id = fixture.OnlyJob();
        Assert.Equal(ExitCodes.NeedsInput, asked.ExitCode);

        CliResult reply = await fixture.Repo.RunAsync("jobs", "reply", id, "sqlite");

        Assert.Equal(ExitCodes.Usage, reply.ExitCode);
        Assert.Contains($"claustrum jobs answer {id}", reply.Stderr, StringComparison.Ordinal);
        Assert.Empty(Directory.Exists(Path.Combine(fixture.JobDir(id), "inbox")) ? Directory.GetFiles(Path.Combine(fixture.JobDir(id), "inbox")) : []);

        fixture.Repo.Script(new FakeClaudeScript { Summary = "done", SessionId = "child-session", ArgvLog = fixture.Repo.ArgvLog });
        CliResult answer = await fixture.RunWithTreeAsync(InTree(), "jobs", "answer", id, "sqlite", "--json");

        Assert.True(answer.ExitCode == ExitCodes.Ok, answer.Stderr + answer.Stdout);
        string[] argv = File.ReadAllLines(fixture.Repo.ArgvLog);
        Assert.Equal("child-session", argv[Array.IndexOf(argv, "--resume") + 1]);
    }
}
