using System.Text.Json;
using System.Text.Json.Nodes;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Mcp;

// The MCP door of #74, over real stdio against a real `claustrum mcp` process, a real repository and a fake
// `claude` (IsolatedRepo.ForCoordinate): `coordinate` answers {job_id, log_path, warnings} at once and the
// architect it started runs in its own worktree on its own branch, exactly as the CLI door's. The server's PATH
// is git's directory plus the system's and the configured script is what the claude backend runs.
public sealed class CoordinateMcpEndToEndTests : IDisposable
{
    private static readonly TimeSpan resultTimeout = TimeSpan.FromSeconds(30);

    private readonly IsolatedRepo repo = IsolatedRepo.ForCoordinate();

    public void Dispose() => repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Arguments(string brief, string cwd) => new() { ["brief"] = brief, ["cwd"] = cwd };

    // The architect runs on after the tool call returned: wait for its receipt rather than for the call.
    private async Task<JsonElement> WaitForResultAsync(string jobId)
    {
        string path = Path.Combine(repo.JobsRoot, jobId, "result.json");
        DateTime deadline = DateTime.UtcNow + resultTimeout;
        while (!File.Exists(path))
        {
            Assert.True(DateTime.UtcNow < deadline, $"no result.json for job {jobId} within {resultTimeout.TotalSeconds}s");
            await Task.Delay(100, Ct);
        }

        // Written, then closed: one more beat so a reader never sees a half-flushed file.
        await Task.Delay(100, Ct);
        return repo.ReadResult(jobId);
    }

    [Fact]
    public async Task CoordinateAnswersJobIdLogPathAndNoWarningsThenTheArchitectRunsInItsOwnWorktreeAsync()
    {
        repo.Script(new FakeClaudeScript { Writes = [("architect-notes.txt", "via mcp")], Summary = "integrated" });
        string headBefore = repo.Git("rev-parse", "HEAD");
        await using McpStdioClient client = repo.StartMcp();
        await client.InitializeAsync(Ct);

        (bool isError, string text) = await client.CallToolAsync("coordinate", Arguments("do the task", repo.Repo), Ct);

        Assert.False(isError, text);
        using JsonDocument started = JsonDocument.Parse(text);
        string jobId = started.RootElement.GetProperty("job_id").GetString() ?? "";
        Assert.NotEmpty(jobId);
        Assert.Contains("stdout.log", started.RootElement.GetProperty("log_path").GetString(), StringComparison.Ordinal);
        JsonElement warnings = started.RootElement.GetProperty("warnings");
        Assert.Equal(JsonValueKind.Array, warnings.ValueKind);
        Assert.Equal(0, warnings.GetArrayLength());

        JsonElement result = await WaitForResultAsync(jobId);
        string branch = $"claustrum/{jobId}";
        Assert.Equal("success", result.GetProperty("status").GetString());
        Assert.Equal(branch, result.GetProperty("branch").GetString());
        Assert.EndsWith(IsolatedRepo.WorktreeTail(jobId), result.GetProperty("worktree").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(repo.Git("rev-parse", $"refs/heads/{branch}"), result.GetProperty("commit").GetString());
        Assert.Equal("architect-notes.txt", repo.Git("show", "--name-only", "--format=", branch));

        // The system prompt the MCP-started architect obeys names the worktree, with no token left.
        string systemMd = File.ReadAllText(Path.Combine(repo.JobsRoot, jobId, "system.md"));
        Assert.Contains($"Work branch: claustrum/{jobId} — you are already on it, in your own worktree {repo.WorktreePath(jobId)}", systemMd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{{job_id}}", systemMd, StringComparison.Ordinal);
        using JsonDocument request = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo.JobsRoot, jobId, "request.json")));
        Assert.Equal(repo.WorktreePath(jobId), request.RootElement.GetProperty("cwd").GetString());

        Assert.Equal("main", repo.Git("branch", "--show-current"));
        Assert.Equal(headBefore, repo.Git("rev-parse", "HEAD"));
        Assert.DoesNotContain("checkout:", repo.Git("reflog", "show", "--format=%gs", "HEAD"), StringComparison.Ordinal);
        Assert.Equal("", TestGit.Status(repo.Repo));
    }

    // G1: a committed .mcp.json with a local edit is not a refusal — the children get HEAD's copy — but the caller
    // is told in the one place that cannot be missed, the tool's own answer.
    [Fact]
    public async Task ATrackedHarnessConfigWithALocalEditRunsAndIsNamedInTheWarningsArrayAsync()
    {
        File.WriteAllText(Path.Combine(repo.Repo, ".mcp.json"), /*lang=json,strict*/ """{"mcpServers":{}}""" + "\n");
        repo.CommitAll("harness config");
        File.WriteAllText(Path.Combine(repo.Repo, ".mcp.json"), /*lang=json,strict*/ """{"mcpServers":{"mine":{}}}""" + "\n");
        repo.Script(new FakeClaudeScript());
        await using McpStdioClient client = repo.StartMcp();
        await client.InitializeAsync(Ct);

        (bool isError, string text) = await client.CallToolAsync("coordinate", Arguments("do the task", repo.Repo), Ct);

        Assert.False(isError, text);
        using JsonDocument started = JsonDocument.Parse(text);
        string warning = Assert.Single(started.RootElement.GetProperty("warnings").EnumerateArray()).GetString() ?? "";
        Assert.Equal(".mcp.json (uncommitted changes): the architect's worktree gets HEAD's copy — your local edit stays out of it", warning);
        string jobId = started.RootElement.GetProperty("job_id").GetString() ?? "";
        JsonElement result = await WaitForResultAsync(jobId);
        Assert.Equal("success", result.GetProperty("status").GetString());
        Assert.Equal(/*lang=json,strict*/ """{"mcpServers":{}}""", File.ReadAllText(Path.Combine(repo.WorktreePath(jobId), ".mcp.json")).Trim());
    }

    [Fact]
    public async Task AnUncommittedClaustrumJsonIsAToolErrorNamingItAndNoJobIsMintedAsync()
    {
        File.AppendAllText(Path.Combine(repo.Repo, "claustrum.json"), "\n");
        await using McpStdioClient client = repo.StartMcp();
        await client.InitializeAsync(Ct);

        (bool isError, string text) = await client.CallToolAsync("coordinate", Arguments("do the task", repo.Repo), Ct);

        Assert.True(isError, text);
        Assert.Contains("coordinate runs the architect in a worktree, which sees only committed files — commit (or un-ignore) claustrum.json (uncommitted changes) first", text, StringComparison.Ordinal);
        Assert.Empty(repo.JobDirectories());
        Assert.False(Directory.Exists(Path.Combine(repo.Repo, ".claustrum", "worktrees")));
    }

    [Fact]
    public async Task ASubdirectoryCwdIsAToolErrorNamingTheRepositoryRootAsync()
    {
        // A subdirectory with a cast of its own: the case the refusal exists for — the architect's worktree is the whole
        // repository, and its children would look for that cast at the root, where it is not.
        string sub = Path.Combine(repo.Repo, "sub");
        Directory.CreateDirectory(Path.Combine(sub, ".claustrum", "casts"));
        File.Copy(Path.Combine(repo.Repo, ".claustrum", "casts", "default.json"), Path.Combine(sub, ".claustrum", "casts", "default.json"));
        await using McpStdioClient client = repo.StartMcp();
        await client.InitializeAsync(Ct);

        (bool isError, string text) = await client.CallToolAsync("coordinate", Arguments("do the task", sub), Ct);

        Assert.True(isError, text);
        Assert.Contains($"run it from {repo.Repo}", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repo.JobDirectories());
    }
}
