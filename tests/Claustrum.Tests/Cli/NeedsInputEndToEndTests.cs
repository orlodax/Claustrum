using System.Text.Json;
using System.Text.Json.Nodes;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #124 stage 3b, the questions-as-data fallback through the built binary against a fake `claude` (IsolatedRepo): a report that says `needs_input`
// ends the run `needs_input` with exit 7 and its questions, and `jobs answer` resumes the session as a linked job, once, in the original worktree
// and as the original backend, model and permission (stage 3 review F1, F2, F5). docs/MANUAL.md "When nobody can answer live".
public sealed class NeedsInputEndToEndTests : IDisposable
{
    private const string TwoQuestions =
        /*lang=json,strict*/ """{"status":"needs_input","summary":"stopped on two questions","questions":[{"key":"db","prompt":"Which database?","options":["sqlite","postgres"],"allow_free_form":false},{"key":"name","prompt":"What to call it?","options":[],"allow_free_form":true}]}""";

    private const string Session = "sess-1";

    private readonly IsolatedRepo repo = new();

    public NeedsInputEndToEndTests() => FakeClaude.RequirePosixShell();

    public void Dispose() => repo.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static FakeClaudeScript Asks(string reportJson = TwoQuestions, string[]? writes = null) => new()
    {
        ReportJson = reportJson,
        SessionId = Session,
        Writes = writes is null ? [] : [(writes[0], writes[1])],
    };

    private FakeClaudeScript Finishes() => new() { Summary = "finished", SessionId = Session, ArgvLog = repo.ArgvLog, StdinLog = repo.StdinLog, MarkerLog = repo.MarkerLog };

    // A job that stopped to ask: the first run of a cast whose builder runs on the fake claude, not live (the fallback's own case).
    private async Task<(CliResult Process, JsonElement Result)> AskedAsync(int? maxParallel = null, FakeClaudeScript? script = null, params string[] extraArgs)
    {
        repo.WriteCast(maxParallel);
        repo.Script(script ?? Asks());
        return await repo.RunBuilderAsync(["--no-live", .. extraArgs]);
    }

    private string WriteAnswers(string json)
    {
        string path = Path.Combine(repo.Repo, $"answers-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private JsonElement RequestOf(string jobId)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo.JobsRoot, jobId, "request.json")));
        return document.RootElement.Clone();
    }

    private string AnsweredFile(string jobId) => Path.Combine(repo.JobsRoot, jobId, "answered.json");

    // ---- the receipt ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AReportThatSaysNeedsInputEndsTheRunNeedsInputWithExitSevenAndItsQuestionsAsync()
    {
        (CliResult process, JsonElement result) = await AskedAsync();

        Assert.Equal(ExitCodes.NeedsInput, process.ExitCode);
        Assert.Equal("needs_input", Str(result, "status"));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);
        Assert.Equal(Session, Str(result, "session_id"));
        JsonElement[] questions = [.. result.GetProperty("questions").EnumerateArray()];
        Assert.Equal(["db", "name"], questions.Select(question => Str(question, "key")));
        Assert.Equal("Which database?", Str(questions[0], "prompt"));
        Assert.Equal(["sqlite", "postgres"], questions[0].GetProperty("options").EnumerateArray().Select(option => option.GetString()));
        Assert.False(questions[0].GetProperty("allow_free_form").GetBoolean());
        Assert.True(questions[1].GetProperty("allow_free_form").GetBoolean());

        // The receipt on disk says the same, and a resume record is left for `jobs answer`.
        JsonElement onDisk = repo.ReadResult(Str(result, "job_id"));
        Assert.Equal("needs_input", Str(onDisk, "status"));
        Assert.Equal(2, onDisk.GetProperty("questions").GetArrayLength());
        Assert.True(File.Exists(Path.Combine(repo.JobsRoot, Str(result, "job_id"), "resume.json")));
    }

    [Fact]
    public async Task HumanModePrintsTheQuestionsTheirOptionsAndTheCommandThatAnswersThemAsync()
    {
        repo.WriteCast(null);
        repo.Script(Asks());

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "do the task", "--no-live");

        Assert.Equal(ExitCodes.NeedsInput, process.ExitCode);
        string id = Assert.Single(repo.JobDirectories());
        string stdout = Lf(process.Stdout);
        Assert.Contains($"needs input: {id}\n", stdout, StringComparison.Ordinal);
        Assert.Contains("? [db] Which database?", stdout, StringComparison.Ordinal);
        Assert.Contains("options: sqlite | postgres\n", stdout, StringComparison.Ordinal);
        Assert.Contains("? [name] What to call it?", stdout, StringComparison.Ordinal);
        Assert.Contains($"claustrum jobs answer {id}", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReportWithNoReadableQuestionStillEndsNeedsInputAndSaysWhyInTheWarningsAsync()
    {
        (CliResult process, JsonElement result) = await AskedAsync(script: Asks(/*lang=json,strict*/ """{"status":"needs_input","summary":"stuck","questions":[{"prompt":""}]}"""));

        Assert.Equal(ExitCodes.NeedsInput, process.ExitCode);
        Assert.Equal("needs_input", Str(result, "status"));
        Assert.Contains(result.GetProperty("warnings").EnumerateArray(), warning => warning.GetString()?.Contains("carries no readable questions", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ARoleThatIsToldNobodyCanAnswerAndStopsWithNeedsInputEndsNeedsInputWhenLiveToAsync()
    {
        repo.WriteCast(null);
        repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("which database?"), new FakeClaudeTurn("cannot decide", ReportJson: TwoQuestions)] });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.NeedsInput, process.ExitCode);
        Assert.Equal("needs_input", Str(result, "status"));
        Assert.Equal(2, result.GetProperty("questions").GetArrayLength());
        string conversation = File.ReadAllText(Path.Combine(repo.JobsRoot, Str(result, "job_id"), "conversation.jsonl"));
        Assert.Contains("No human is available to answer now.", conversation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlindRolesReportCannotProduceNeedsInputAndTheReceiptSaysItWasNotHonouredAsync()
    {
        repo.WriteCast(null, role: "code-reviewer");
        repo.Script(Asks());

        CliResult process = await repo.RunAsync("run", "code-reviewer", "--brief", "Review the diff below.\n\n## Diff\n+x", "--json", "--no-live");

        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        JsonElement result = document.RootElement;
        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("success", Str(result, "status"));
        Assert.True(result.GetProperty("questions").ValueKind is JsonValueKind.Null);
        Assert.Contains(result.GetProperty("warnings").EnumerateArray(), warning => warning.GetString()?.Contains("'needs_input' ignored", StringComparison.Ordinal) == true);
    }

    // ---- jobs answer ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnswerWithAnAnswersFileStartsALinkedJobThatResumesTheSessionAndPrintsAsRunDoesAsync()
    {
        (_, JsonElement asked) = await AskedAsync();
        string id = Str(asked, "job_id");
        repo.Script(Finishes());

        CliResult process = await repo.RunAsync("jobs", "answer", id, "--answers", WriteAnswers(/*lang=json,strict*/ """{"db":"sqlite","name":"widgets"}"""));

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.StartsWith("status: Success (exit 0)", Lf(process.Stdout), StringComparison.Ordinal);
        string next = Assert.Single(repo.JobDirectories(), directory => directory != id);
        Assert.Equal(id, Str(RequestOf(next), "parent_job_id"));
        string[] argv = File.ReadAllLines(repo.ArgvLog);
        Assert.Equal(Session, argv[Array.IndexOf(argv, "--resume") + 1]);
        string brief = File.ReadAllText(repo.StdinLog);
        Assert.Contains("sqlite", brief, StringComparison.Ordinal);
        Assert.Contains("widgets", brief, StringComparison.Ordinal);
        Assert.Contains("Which database?", brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswerWithTextIsTheJsonRunResultOfTheLinkedJobAndTheTextIsTheBriefAsync()
    {
        (_, JsonElement asked) = await AskedAsync();
        string id = Str(asked, "job_id");
        repo.Script(Finishes());

        CliResult process = await repo.RunAsync("jobs", "answer", id, "use sqlite and call it widgets", "--json");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        Assert.Equal("success", Str(document.RootElement, "status"));
        Assert.NotEqual(id, Str(document.RootElement, "job_id"));
        Assert.Equal(id, Str(RequestOf(Str(document.RootElement, "job_id")), "parent_job_id"));
        Assert.Contains("use sqlite and call it widgets", File.ReadAllText(repo.StdinLog), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContinuationThatAsksAgainExitsSevenAgainAndIsTheOneToAnswerNextAsync()
    {
        (_, JsonElement asked) = await AskedAsync();
        string id = Str(asked, "job_id");

        CliResult again = await repo.RunAsync("jobs", "answer", id, "hm", "--json");
        string next = Assert.Single(repo.JobDirectories(), directory => directory != id);
        CliResult second = await repo.RunAsync("jobs", "answer", id, "again");

        Assert.Equal(ExitCodes.NeedsInput, again.ExitCode);
        Assert.Equal("needs_input", Str(JsonDocument.Parse(again.Stdout).RootElement, "status"));
        Assert.Equal(ExitCodes.Usage, second.ExitCode);
        Assert.Contains($"job '{id}' was already answered", second.Stderr, StringComparison.Ordinal);
        Assert.Contains($"by job {next}", second.Stderr, StringComparison.Ordinal);
        Assert.Contains("ended NeedsInput", second.Stderr, StringComparison.Ordinal);
        Assert.Contains($"answer it instead: claustrum jobs answer {next}", second.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"nope":"x"}""", "nope")]
    [InlineData(/*lang=json,strict*/ """{"db":"oracle"}""", "oracle")]
    [InlineData("""{}""", "")]
    public async Task AnAnswersFileWithAnUnknownKeyAnOptionOutsideTheListOrNothingInItIsRefusedBeforeAnythingStartsAsync(string answers, string mention)
    {
        (_, JsonElement asked) = await AskedAsync();
        string id = Str(asked, "job_id");

        CliResult process = await repo.RunAsync("jobs", "answer", id, "--answers", WriteAnswers(answers));

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains(mention, process.Stderr, StringComparison.Ordinal);
        Assert.Equal([id], repo.JobDirectories());
        Assert.False(File.Exists(AnsweredFile(id)));
    }

    [Fact]
    public async Task AnswerNeedsExactlyOneOfAnAnswersFileOrTextAsync()
    {
        (_, JsonElement asked) = await AskedAsync();
        string id = Str(asked, "job_id");

        CliResult none = await repo.RunAsync("jobs", "answer", id);
        CliResult both = await repo.RunAsync("jobs", "answer", id, "text", "--answers", WriteAnswers(/*lang=json,strict*/ """{"db":"sqlite"}"""));

        Assert.Equal(ExitCodes.Usage, none.ExitCode);
        Assert.Equal(ExitCodes.Usage, both.ExitCode);
        Assert.Equal([id], repo.JobDirectories());
    }

    // ---- what it refuses ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnswerRefusesAJobThatEndedAnotherWayAndOneThatIsUnknownAsync()
    {
        repo.WriteCast(null);
        repo.Script(new FakeClaudeScript { Summary = "fine", SessionId = Session });
        (_, JsonElement done) = await repo.RunBuilderAsync("--no-live");

        CliResult notAsking = await repo.RunAsync("jobs", "answer", Str(done, "job_id"), "text");
        CliResult unknown = await repo.RunAsync("jobs", "answer", "20261015-000000-deadbeef", "text");

        Assert.Equal(ExitCodes.Usage, notAsking.ExitCode);
        Assert.Contains("ended Success, not needs_input", notAsking.Stderr, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, unknown.ExitCode);
        Assert.Contains("not found", unknown.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswerRefusesABlindRolesJobAsync()
    {
        repo.WriteCast(null, role: "code-reviewer");
        repo.Script(new FakeClaudeScript { Summary = "fine", SessionId = Session });
        CliResult review = await repo.RunAsync("run", "code-reviewer", "--brief", "Review the diff below.\n\n## Diff\n+x", "--json", "--no-live");
        string id = Str(JsonDocument.Parse(review.Stdout).RootElement, "job_id");

        CliResult process = await repo.RunAsync("jobs", "answer", id, "text");

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains("blind role", process.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("api", "keeps no session")]
    [InlineData("no-session", "recorded no session id")]
    public async Task AnswerRefusesABackendThatCannotResumeAndAReceiptWithNoSessionAsync(string kind, string reason)
    {
        (_, JsonElement asked) = await AskedAsync();
        string id = Str(asked, "job_id");
        string path = Path.Combine(repo.JobsRoot, id, "result.json");
        JsonObject receipt = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidOperationException("result.json");
        if (kind == "api")
            receipt["backend"] = "api";
        else
            receipt["session_id"] = null;

        File.WriteAllText(path, receipt.ToJsonString());

        CliResult process = await repo.RunAsync("jobs", "answer", id, "text");

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains(reason, process.Stderr, StringComparison.Ordinal);
        Assert.Equal([id], repo.JobDirectories());
    }

    // ---- answered once (review F2) --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASecondAnswerIsRefusedNamingTheContinuationAndNothingIsStartedAsync()
    {
        (_, JsonElement asked) = await AskedAsync();
        string id = Str(asked, "job_id");
        repo.Script(Finishes());
        CliResult first = await repo.RunAsync("jobs", "answer", id, "sqlite");
        string next = Assert.Single(repo.JobDirectories(), directory => directory != id);

        CliResult second = await repo.RunAsync("jobs", "answer", id, "postgres");

        Assert.Equal(ExitCodes.Ok, first.ExitCode);
        Assert.Equal(ExitCodes.Usage, second.ExitCode);
        Assert.Contains($"job '{id}' was already answered", second.Stderr, StringComparison.Ordinal);
        Assert.Contains($"by job {next}", second.Stderr, StringComparison.Ordinal);
        Assert.Contains("ended Success", second.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, repo.JobDirectories().Length);
        Assert.Equal(next, JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(AnsweredFile(id))).GetProperty("job_id").GetString());
        Assert.Single(File.ReadAllLines(repo.MarkerLog), "start");
    }

    [Fact]
    public async Task AContinuationThatNeverReachedItsBackendGivesTheAnswerBackAsync()
    {
        (_, JsonElement asked) = await AskedAsync();
        string id = Str(asked, "job_id");
        repo.Script(Finishes());
        // The claude the config names is gone and none is on the child's PATH: the continuation is refused before any backend process exists.
        string config = Path.Combine(repo.Repo, "claustrum.json");
        string working = File.ReadAllText(config);
        File.WriteAllText(config, FakeClaude.ConfigJson(Path.Combine(repo.Repo, "no-such-claude")));

        CliResult lost = await repo.RunAsync("jobs", "answer", id, "sqlite");

        Assert.NotEqual(ExitCodes.Ok, lost.ExitCode);
        Assert.False(File.Exists(AnsweredFile(id)), "the answer was not spent: nothing reached the backend");
        File.WriteAllText(config, working);

        CliResult retried = await repo.RunAsync("jobs", "answer", id, "sqlite");

        Assert.True(retried.ExitCode == ExitCodes.Ok, retried.Stderr);
        Assert.True(File.Exists(AnsweredFile(id)));
    }

    [Fact]
    public async Task TwoAnswersAtOnceStartExactlyOneContinuationAsync()
    {
        (_, JsonElement asked) = await AskedAsync();
        string id = Str(asked, "job_id");
        repo.Script(Finishes() with { SleepSeconds = 2 });

        CliResult[] both = await Task.WhenAll(repo.RunAsync("jobs", "answer", id, "sqlite"), repo.RunAsync("jobs", "answer", id, "postgres"));

        Assert.Equal([ExitCodes.Ok, ExitCodes.Usage], both.Select(result => result.ExitCode).Order());
        Assert.Single(File.ReadAllLines(repo.MarkerLog), "start");

        // The loser either never got a job (refused at once) or lost the claim after minting one: a failed receipt, never a second run.
        string[] others = [.. repo.JobDirectories().Where(directory => directory != id)];
        Assert.Single(others, directory => repo.ReadResult(directory).GetProperty("status").GetString() == "success");
        Assert.All(others.Where(directory => repo.ReadResult(directory).GetProperty("status").GetString() != "success"), directory => Assert.Equal("failed", repo.ReadResult(directory).GetProperty("status").GetString()));
        CliResult loser = Assert.Single(both, result => result.ExitCode == ExitCodes.Usage);
        Assert.Contains("already answered", loser.Stderr + loser.Stdout, StringComparison.Ordinal);
    }

    // ---- the original worktree (review F1) ----------------------------------------------------------------------------------------

    private static FakeClaudeScript AsksAndWrites() => Asks(writes: ["first.txt", "first"]);

    [Fact]
    public async Task AnIsolatedJobsContinuationRunsInTheOriginalWorktreeWhenItIsStillCleanAsync()
    {
        (_, JsonElement asked) = await AskedAsync(maxParallel: 2, script: AsksAndWrites());
        string id = Str(asked, "job_id");
        Assert.EndsWith(IsolatedRepo.WorktreeTail(id), Str(asked, "worktree"), StringComparison.OrdinalIgnoreCase);
        repo.Script(Finishes());

        CliResult process = await repo.RunAsync("jobs", "answer", id, "sqlite", "--json");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        string next = Str(document.RootElement, "job_id");
        Assert.EndsWith(IsolatedRepo.WorktreeTail(id), Str(document.RootElement, "worktree"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"claustrum/{id}", Str(document.RootElement, "branch"));
        Assert.False(Directory.Exists(repo.WorktreePath(next)), "no worktree of its own");
        Assert.True(Directory.Exists(repo.WorktreePath(id)));
        Assert.Equal(repo.WorktreePath(id), Path.GetFullPath(RequestCwd(next)));
    }

    private string RequestCwd(string jobId) => Str(RequestOf(jobId), "cwd");

    [Fact]
    public async Task AnIsolatedJobsContinuationRecreatesTheWorktreeAtTheSamePathAfterJobsCleanAsync()
    {
        (_, JsonElement asked) = await AskedAsync(maxParallel: 2, script: AsksAndWrites());
        string id = Str(asked, "job_id");
        CliResult clean = await repo.RunAsync("jobs", "clean");
        Assert.Equal(ExitCodes.Ok, clean.ExitCode);
        Assert.False(Directory.Exists(repo.WorktreePath(id)));
        repo.Script(Finishes());

        CliResult process = await repo.RunAsync("jobs", "answer", id, "sqlite", "--json");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        Assert.EndsWith(IsolatedRepo.WorktreeTail(id), Str(document.RootElement, "worktree"), StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(repo.WorktreePath(id)));
        Assert.True(File.Exists(Path.Combine(repo.WorktreePath(id), "first.txt")), "the branch's work is checked out again");
        Assert.Contains(repo.WorktreePath(id), repo.Git("worktree", "list", "--porcelain").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("occupied")]
    public async Task AnIsolatedJobsContinuationIsRefusedWhenItsWorktreeHoldsUncommittedWorkOrIsOccupiedAndTheAnswerIsGivenBackAsync(string kind)
    {
        (_, JsonElement asked) = await AskedAsync(maxParallel: 2, script: AsksAndWrites());
        string id = Str(asked, "job_id");
        string worktree = repo.WorktreePath(id);
        if (kind == "dirty")
        {
            File.WriteAllText(Path.Combine(worktree, "uncommitted.txt"), "not saved");
        }
        else
        {
            Assert.Equal(ExitCodes.Ok, (await repo.RunAsync("jobs", "clean")).ExitCode);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, "someone-elses.txt"), "x");
        }

        repo.Script(Finishes());
        CliResult refused = await repo.RunAsync("jobs", "answer", id, "sqlite", "--json");

        // A `failed` receipt, as a `--branch` refusal is (exit 1): the reason is in its error.
        Assert.True(refused.ExitCode == ExitCodes.BackendFailure, refused.Stdout + refused.Stderr);
        using JsonDocument document = JsonDocument.Parse(refused.Stdout);
        Assert.Equal("failed", Str(document.RootElement, "status"));
        Assert.Contains(IsolatedRepo.WorktreeTail(id).Replace('\\', '/'), Str(document.RootElement, "error").Replace('\\', '/'), StringComparison.Ordinal);
        Assert.False(File.Exists(AnsweredFile(id)), "a refusal before the backend gives the answer back");
        Assert.False(File.Exists(repo.MarkerLog), "no backend process ran");

        // Fix what it named, answer again.
        File.Delete(Path.Combine(worktree, kind == "dirty" ? "uncommitted.txt" : "someone-elses.txt"));
        if (kind == "occupied")
            Directory.Delete(worktree);

        CliResult retried = await repo.RunAsync("jobs", "answer", id, "sqlite");

        Assert.True(retried.ExitCode == ExitCodes.Ok, retried.Stderr);
    }

    // ---- pinned to what it continues (review F5) ----------------------------------------------------------------------------------

    [Fact]
    public async Task AContinuationKeepsTheOriginalModelAndWarnsWhenTodaysConfigurationWouldDifferAsync()
    {
        (_, JsonElement asked) = await AskedAsync(script: Asks() with { ArgvLog = repo.ArgvLog });
        string id = Str(asked, "job_id");
        string[] firstArgv = File.ReadAllLines(repo.ArgvLog);
        string original = firstArgv[Array.IndexOf(firstArgv, "--model") + 1];
        // The continuation is resolved against today's claustrum.json: the builder now runs on haiku, read-only.
        File.WriteAllText(
            Path.Combine(repo.Repo, "claustrum.json"),
            "{\"backends\":{\"claude\":{\"path\":\"" + repo.ScriptPath.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"}},\"roles\":{\"builder\":{\"model\":\"claude:haiku\",\"permission\":\"readonly\"}}}");
        repo.Script(Finishes());

        CliResult process = await repo.RunAsync("jobs", "answer", id, "sqlite", "--json");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string[] argv = File.ReadAllLines(repo.ArgvLog);
        Assert.Equal(original, argv[Array.IndexOf(argv, "--model") + 1]);
        Assert.NotEqual("haiku", original);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        string warning = Assert.Single(document.RootElement.GetProperty("warnings").EnumerateArray(), item => item.GetString()?.Contains("today's configuration would run it as", StringComparison.Ordinal) == true).GetString() ?? "";
        Assert.Contains($"job {id} ran as claude:{original} (", warning, StringComparison.Ordinal);
        Assert.Contains("would run it as claude:haiku (readonly)", warning, StringComparison.Ordinal);
        Assert.Contains("the continuation stays on", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContinuationWithNothingChangedCarriesNoPinWarningAsync()
    {
        (_, JsonElement asked) = await AskedAsync();
        repo.Script(Finishes());

        CliResult process = await repo.RunAsync("jobs", "answer", Str(asked, "job_id"), "sqlite", "--json");

        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        Assert.DoesNotContain(document.RootElement.GetProperty("warnings").EnumerateArray(), item => item.GetString()?.Contains("today's configuration", StringComparison.Ordinal) == true);
    }

    // ---- notes ---------------------------------------------------------------------------------------------------------------------

    private const string NoteEvent = /*lang=json,strict*/ """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"claustrum-note: reading the schema"}]}}""";

    [Fact]
    public async Task ANoteInANonLiveRunLandsInConversationJsonlAndFollowShowsItAsADotAsync()
    {
        repo.WriteCast(null);
        repo.Script(new FakeClaudeScript { Summary = "ok", StreamLines = [NoteEvent] });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync("--no-live");

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        string id = Str(result, "job_id");
        string[] lines = File.ReadAllLines(Path.Combine(repo.JobsRoot, id, "conversation.jsonl"));
        JsonElement note = JsonDocument.Parse(Assert.Single(lines)).RootElement;
        Assert.Equal("role", Str(note, "from"));
        Assert.Equal("message", Str(note, "kind"));
        Assert.Equal("claustrum-note: reading the schema", Str(note, "text"));
        Assert.Equal(1, result.GetProperty("conversation").GetProperty("entries").GetInt32());

        CliResult follow = await repo.RunAsync("jobs", "follow", id);

        Assert.Equal(ExitCodes.Ok, follow.ExitCode);
        Assert.Contains("· reading the schema", Lf(follow.Stderr + follow.Stdout), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANoteInALiveRunIsNumberedWithTheTurnsAndShownAsADotByFollowAsync()
    {
        repo.WriteCast(null);
        repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Report("done", "claustrum-note: reading the schema")] });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        string id = Str(result, "job_id");
        JsonElement[] entries = [.. File.ReadAllLines(Path.Combine(repo.JobsRoot, id, "conversation.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone())];
        Assert.Equal([1, 2], entries.Select(entry => entry.GetProperty("seq").GetInt32()));
        Assert.Equal("claustrum-note: reading the schema", Str(entries[0], "text"));

        CliResult follow = await repo.RunAsync("jobs", "follow", id);

        Assert.Contains("· reading the schema", Lf(follow.Stderr + follow.Stdout), StringComparison.Ordinal);
    }
}
