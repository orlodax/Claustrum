using System.Text.Json;
using Claustrum.Mcp;
using ModelContextProtocol;

namespace Claustrum.Tests.Mcp;

// ClaustrumTools methods are plain static C# methods (the [McpServerTool]/[McpServerToolType]
// attributes only matter to the MCP host, verified separately by publishing+probing the real server —
// see NOTES.md), so they are testable directly without a transport.
public sealed class ClaustrumToolsTests
{
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
        Assert.Equal(1, backends.GetArrayLength());
        Assert.Equal("claude", backends[0].GetProperty("name").GetString());
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
}
