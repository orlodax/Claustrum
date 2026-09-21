using System.Text.Json;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Mcp;
using Claustrum.Tests.Testing;
using ModelContextProtocol;

namespace Claustrum.Tests.Mcp;

// ClaustrumTools methods are plain static C# methods (the [McpServerTool]/[McpServerToolType]
// attributes only matter to the MCP host, verified separately by publishing+probing the real server —
// see NOTES.md), so they are testable directly without a transport. AppServicesHomeFixture (the
// "AppServices home" collection) keeps every job these tools start out of the real ~/.claustrum/jobs.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class ClaustrumToolsTests(AppServicesHomeFixture fixture) : IDisposable
{
    // Reset on every test regardless of whether it set one — the fixture's HomeRedirectPlatform is
    // one instance shared by every class in this collection (AppServicesHomeFixture's own doc
    // comment), so a tree id left behind here would leak into DelegateEngineTests/JobManagerTests.
    public void Dispose() => fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = null;

    [Fact]
    public void ListRolesReturnsValidJsonNamingEveryLibraryRole()
    {
        string json = ClaustrumTools.ListRoles();

        using JsonDocument document = JsonDocument.Parse(json);
        string[] names = [.. document.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];

        Assert.Contains("builder", names);
        Assert.Contains("code-reviewer", names);
        Assert.Contains("tester", names);
    }

    [Fact]
    public void ListRolesMarksCodeReviewerAsBlind()
    {
        string json = ClaustrumTools.ListRoles();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement codeReviewer = document.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "code-reviewer");

        Assert.True(codeReviewer.GetProperty("blind").GetBoolean());
    }

    [Fact]
    public void ListBackendsIncludesClaude()
    {
        string json = ClaustrumTools.ListBackends();

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Contains(document.RootElement.EnumerateArray(), e => e.GetString() == "claude");
    }

    [Fact]
    public async Task DoctorReportsOneEntryPerRegisteredBackendAsync()
    {
        string json = await ClaustrumTools.DoctorAsync(CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement backends = document.RootElement.GetProperty("backends");
        Assert.Equal(5, backends.GetArrayLength());
        Assert.Equal("claude", backends[0].GetProperty("name").GetString());
        Assert.Equal("api", backends[1].GetProperty("name").GetString());
        Assert.Equal("opencode", backends[2].GetProperty("name").GetString());
        Assert.Equal("copilot", backends[3].GetProperty("name").GetString());
        Assert.Equal("cursor", backends[4].GetProperty("name").GetString());
    }

    [Fact]
    public async Task CastQuestionsIncludesEveryLibraryRoleAsync()
    {
        string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-cast-").FullName;
        try
        {
            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = cwd;
            try
            {
                string json = await ClaustrumTools.CastQuestionsAsync(CancellationToken.None);

                using JsonDocument document = JsonDocument.Parse(json);
                string[] keys = [.. document.RootElement.GetProperty("questions").EnumerateArray().Select(q => q.GetProperty("key").GetString()!)];
                Assert.Contains("builder", keys);
                Assert.Contains("budget", keys);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task DelegateAsyncReturnsAParsedRunResultAsync()
    {
        string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-delegate-").FullName;
        try
        {
            string json = await ClaustrumTools.DelegateAsync(role: "builder", brief: "hi", cwd: cwd, backend: "nonexistent", cancellationToken: CancellationToken.None);

            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal("backend_missing", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("builder", document.RootElement.GetProperty("role").GetString());
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    // Finding #4: delegate's timeoutSeconds used to default to DelegateEngine.DefaultTimeoutSeconds
    // (a non-null 1800), which always populated ConfigOverrides.TimeoutSeconds and short-circuited
    // DelegateEngine's "flag ?? config ?? 1800" fallback before the config layers were ever
    // consulted. With the parameter now nullable, an unspecified timeout must fall through to a
    // repo's claustrum.json defaults.timeout_seconds exactly like the CLI's --timeout does.
    [Fact]
    public async Task DelegateAsyncWithNoTimeoutFallsThroughToRepoConfigAsync()
    {
        string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-timeout-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));
            File.WriteAllText(Path.Combine(cwd, "claustrum.json"), /*lang=json,strict*/ """{"defaults":{"timeout_seconds":5400}}""");

            string json = await ClaustrumTools.DelegateAsync(role: "builder", brief: "hi", cwd: cwd, backend: "nonexistent", cancellationToken: CancellationToken.None);
            using JsonDocument document = JsonDocument.Parse(json);
            string jobId = document.RootElement.GetProperty("job_id").GetString()!;

            string requestPath = Path.Combine(JobDirectory.ResolveRoot(AppServices.Platform), jobId, "request.json");
            RunRequest request = JsonSerializer.Deserialize(File.ReadAllText(requestPath), ClaustrumJsonContext.Default.RunRequest)!;

            Assert.Equal(TimeSpan.FromSeconds(5400), request.Timeout);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    // delegate_async carried the identical non-nullable `int timeoutSeconds = 1800` parameter as
    // delegate, and finding #4's fix changed both — but only delegate's half was pinned.
    [Fact]
    public async Task DelegateStartWithNoTimeoutAlsoFallsThroughToRepoConfigAsync()
    {
        string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-timeout-async-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));
            File.WriteAllText(Path.Combine(cwd, "claustrum.json"), /*lang=json,strict*/ """{"defaults":{"timeout_seconds":5400}}""");

            string startJson = ClaustrumTools.DelegateStart(role: "builder", brief: "hi", cwd: cwd, backend: "nonexistent");
            using JsonDocument started = JsonDocument.Parse(startJson);
            string jobId = started.RootElement.GetProperty("job_id").GetString()!;
            await ClaustrumTools.JobResultAsync(jobId);

            string requestPath = Path.Combine(JobDirectory.ResolveRoot(AppServices.Platform), jobId, "request.json");
            RunRequest request = JsonSerializer.Deserialize(File.ReadAllText(requestPath), ClaustrumJsonContext.Default.RunRequest)!;

            Assert.Equal(TimeSpan.FromSeconds(5400), request.Timeout);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    // The boundary's synchronous half (McpExceptionBoundary.Guard) had no test that made it convert
    // anything: delegate_async deliberately does not throw for a bad role, and cast_create was only
    // ever called with valid answers. An unparsable budget is a CastException raised inside Guard.
    [Fact]
    public void CastCreateWithAnUnparsableBudgetThrowsMcpExceptionWithTheRealMessage()
    {
        McpException exception = Assert.Throws<McpException>(
            () => ClaustrumTools.CastCreate(new Dictionary<string, string> { ["budget"] = "lots" }));

        Assert.Contains("lots", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelegateAsyncStartThenJobStatusAndJobResultRoundTripAsync()
    {
        string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-delegate-async-").FullName;
        try
        {
            string startJson = ClaustrumTools.DelegateStart(role: "builder", brief: "hi", cwd: cwd, backend: "nonexistent");
            using JsonDocument started = JsonDocument.Parse(startJson);
            string jobId = started.RootElement.GetProperty("job_id").GetString()!;
            Assert.NotEmpty(jobId);

            string resultJson = await ClaustrumTools.JobResultAsync(jobId);
            using JsonDocument result = JsonDocument.Parse(resultJson);
            Assert.Equal("backend_missing", result.RootElement.GetProperty("status").GetString());

            string statusJson = ClaustrumTools.JobStatus(jobId);
            using JsonDocument status = JsonDocument.Parse(statusJson);
            Assert.Equal("done", status.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void JobStatusForAnUnknownJobIdReportsUnknown()
    {
        string json = ClaustrumTools.JobStatus("does-not-exist-" + Guid.NewGuid());

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("unknown", document.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task JobResultForAnUnknownJobIdThrowsAsync()
    {
        await Assert.ThrowsAsync<McpException>(() => ClaustrumTools.JobResultAsync("does-not-exist-" + Guid.NewGuid()));
    }

    // McpExceptionBoundary (2026-09-14): before this fix, an unknown role or a blind-gate violation
    // both escaped as the MCP SDK's own generic "An error occurred invoking 'delegate'", with the
    // real message only in the server's stderr log. RoleRenderException/BlindGateException must now
    // surface as an McpException carrying that real message instead.
    [Fact]
    public async Task DelegateAsyncWithUnknownRoleThrowsMcpExceptionWithTheRealMessageAsync()
    {
        string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-badrole-").FullName;
        try
        {
            McpException exception = await Assert.ThrowsAsync<McpException>(
                () => ClaustrumTools.DelegateAsync(role: "no-such-role", brief: "hi", cwd: cwd, cancellationToken: CancellationToken.None));

            Assert.Contains("no-such-role", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task DelegateAsyncWithBlindGateViolationThrowsMcpExceptionAsync()
    {
        string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-blindgate-").FullName;
        try
        {
            McpException exception = await Assert.ThrowsAsync<McpException>(
                () => ClaustrumTools.DelegateAsync(role: "code-reviewer", brief: "## Plan\nstep 1", cwd: cwd, cancellationToken: CancellationToken.None));

            Assert.Contains("blind", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    // DelegateStart itself must NOT throw for a bad role: an async job's `Task<RunResult>` is a
    // faulted task returned by DelegateEngine.RunAsync, not a synchronous throw at Start() — "returns
    // immediately with a job id" (the tool's own description) has to hold even for a job that will
    // fail. The role error surfaces later, via job_result.
    [Fact]
    public async Task DelegateStartWithUnknownRoleThenJobResultThrowsMcpExceptionWithTheRealMessageAsync()
    {
        string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-badrole-async-").FullName;
        try
        {
            string startJson = ClaustrumTools.DelegateStart(role: "no-such-role", brief: "hi", cwd: cwd);
            using JsonDocument started = JsonDocument.Parse(startJson);
            string jobId = started.RootElement.GetProperty("job_id").GetString()!;

            McpException exception = await Assert.ThrowsAsync<McpException>(() => ClaustrumTools.JobResultAsync(jobId));

            Assert.Contains("no-such-role", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void CastCreateThenCastListRoundTrips()
    {
        string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-cast-").FullName;
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = cwd;
        try
        {
            string createJson = ClaustrumTools.CastCreate(new Dictionary<string, string> { ["builder"] = "fast" });
            using JsonDocument created = JsonDocument.Parse(createJson);
            Assert.Equal("default", created.RootElement.GetProperty("name").GetString());

            string listJson = ClaustrumTools.CastList();
            using JsonDocument list = JsonDocument.Parse(listJson);
            Assert.Contains(list.RootElement.EnumerateArray(), e => e.GetString() == "default");
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(cwd, recursive: true);
        }
    }

    // §D4/NOTES.md "Nothing was needed for MCP": RunStatus already serializes snake_case, so delegate
    // returns "status":"budget_exceeded" from the same DelegateEngine the CLI uses — no exception, no
    // McpException, just a normal RunResult document.
    [Fact]
    public async Task DelegateOnAnExhaustedTreeReturnsBudgetExceededJsonWithoutThrowingAsync()
    {
        string treeId = $"tree-{Guid.NewGuid():N}";
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = treeId;
        string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-budget-").FullName;
        try
        {
            SeedExhaustedLedger(treeId);

            string json = await ClaustrumTools.DelegateAsync(role: "builder", brief: "hi", cwd: cwd, backend: "nonexistent", cancellationToken: CancellationToken.None);

            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal("budget_exceeded", document.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task DelegateAsyncStartThenJobResultOnAnExhaustedTreeReturnsBudgetExceededJsonWithoutThrowingAsync()
    {
        string treeId = $"tree-{Guid.NewGuid():N}";
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = treeId;
        string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-budget-async-").FullName;
        try
        {
            SeedExhaustedLedger(treeId);

            string startJson = ClaustrumTools.DelegateStart(role: "builder", brief: "hi", cwd: cwd, backend: "nonexistent");
            using JsonDocument started = JsonDocument.Parse(startJson);
            string jobId = started.RootElement.GetProperty("job_id").GetString()!;

            string resultJson = await ClaustrumTools.JobResultAsync(jobId);
            using JsonDocument result = JsonDocument.Parse(resultJson);
            Assert.Equal("budget_exceeded", result.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    // Written directly, like DelegateEngineTests' own seed — the on-disk shape NOTES.md "Tree budget
    // accounting is a file ledger" documents, already spent down to its own cap.
    private void SeedExhaustedLedger(string treeId)
    {
        string directory = BudgetLedger.DirectoryFor(fixture.Platform, treeId);
        Directory.CreateDirectory(directory);
        BudgetLedgerEntry entry = new("seed", "builder", 5.00m, 5.00m, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(directory, "seed.json"), JsonSerializer.Serialize(entry, ClaustrumJsonContext.Default.BudgetLedgerEntry));
    }
}
