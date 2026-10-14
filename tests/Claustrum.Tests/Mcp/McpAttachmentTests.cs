using System.Text.Json;
using Claustrum.Mcp;
using Claustrum.Tests.Testing;
using ModelContextProtocol;

namespace Claustrum.Tests.Mcp;

// 2026-10-14 (found by review): the MCP tools' `files` are read as regular files only; a FIFO or a device among them would block
// or never end a server call. The refusal is a prompt error naming the file, delegate_async returns its job id without
// waiting on the file, and an ordinary file is still attached. (The CLI's `run --file` stays a plain read: a pipe works there.)
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class McpAttachmentTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-attach-").FullName;

    public void Dispose() => TempTree.Delete(cwd);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Fifo()
    {
        string path = Path.Combine(cwd, "pipe.txt");
        PipeFiles.MakeFifo(path);
        return path;
    }

    private string DevZero()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("/dev/zero is a Unix device.");

        string path = Path.Combine(cwd, "zero.txt");
        TestLinks.Create(path, "/dev/zero");
        return path;
    }

    [Fact]
    public async Task DelegateWithAFifoInFilesIsAPromptErrorAsync()
    {
        string fifo = Fifo();

        McpException ex = await PipeFiles.WithinAsync(() => Assert.ThrowsAsync<McpException>(
            () => ClaustrumTools.DelegateAsync(role: "builder", brief: "hi", cwd: cwd, backend: "nonexistent", files: [fifo], cancellationToken: Ct)));

        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
        Assert.Contains(fifo, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelegateWithALinkToDevZeroInFilesIsAPromptErrorAsync()
    {
        string zero = DevZero();

        McpException ex = await PipeFiles.WithinAsync(() => Assert.ThrowsAsync<McpException>(
            () => ClaustrumTools.DelegateAsync(role: "builder", brief: "hi", cwd: cwd, backend: "nonexistent", files: [zero], cancellationToken: Ct)));

        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelegateAsyncReturnsItsJobIdWithoutWaitingOnAFifoAndTheJobFailsAsync()
    {
        string fifo = Fifo();

        string started = PipeFiles.Within(() => ClaustrumTools.DelegateStart(role: "builder", brief: "hi", cwd: cwd, backend: "nonexistent", files: [fifo]));
        using JsonDocument document = JsonDocument.Parse(started);
        string jobId = document.RootElement.GetProperty("job_id").GetString() ?? "";

        McpException ex = await PipeFiles.WithinAsync(() => Assert.ThrowsAsync<McpException>(() => ClaustrumTools.JobResultAsync(jobId)));
        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryFileInFilesIsStillAttachedToTheBriefAsync()
    {
        FakeClaude.RequirePosixShell();
        using IsolatedRepo repo = new();
        repo.Script(new FakeClaudeScript { StdinLog = repo.StdinLog });
        string notes = Path.Combine(repo.Repo, "notes.txt");
        File.WriteAllText(notes, "ATTACHED-CONTENT-MARKER\n");

        string json = await ClaustrumTools.DelegateAsync(role: "builder", brief: "hi", cwd: repo.Repo, backend: "claude", files: [notes], cancellationToken: Ct);

        using JsonDocument result = JsonDocument.Parse(json);
        Assert.Equal("success", result.RootElement.GetProperty("status").GetString());
        string brief = File.ReadAllText(repo.StdinLog);
        Assert.Contains("## Attached:", brief, StringComparison.Ordinal);
        Assert.Contains("ATTACHED-CONTENT-MARKER", brief, StringComparison.Ordinal);
    }
}
