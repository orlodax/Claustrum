using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Jobs;
using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Acp;

/// <summary>One line the client wrote to the agent's stdin, read the way the agent would.</summary>
internal sealed record SentFrame(string Raw)
{
    private JsonElement Root => JsonDocument.Parse(Raw).RootElement;

    public string? Method => Root.TryGetProperty("method", out JsonElement method) ? method.GetString() : null;

    /// <summary>The id as the client wrote it: a string's text, a number's digits.</summary>
    public string Id => Root.GetProperty("id").ToString();

    public JsonElement Params => Root.GetProperty("params").Clone();

    public string ParamString(string name) => Params.GetProperty(name).GetString() ?? "";
}

/// <summary>The agent's stdin as the driver sees it: lines are queued for the test to read, and the close is observable.</summary>
internal sealed class WireStdin : TextWriter
{
    private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
    private readonly StringBuilder partial = new();
    private readonly List<string> everything = [];
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>Every complete line written so far.</summary>
    public IReadOnlyList<string> Everything
    {
        get
        {
            lock (everything)
                return [.. everything];
        }
    }

    public Task Closed => closed.Task;

    public override void Write(char value) => Append(value.ToString());

    public override void Write(string? value) => Append(value ?? "");

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        Append(buffer.ToString());
        return Task.CompletedTask;
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<string> NextAsync(TimeSpan? limit = null)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(10));
        try
        {
            return await lines.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("the client wrote nothing more to the agent's stdin");
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException("stdin was closed; the client wrote nothing more");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lines.Writer.TryComplete();
            closed.TrySetResult();
        }

        base.Dispose(disposing);
    }

    private void Append(string text)
    {
        lock (partial)
        {
            partial.Append(text);
            string all = partial.ToString();
            int start = 0;
            for (int newline = all.IndexOf('\n', start); newline >= 0; newline = all.IndexOf('\n', start))
            {
                string line = all[start..newline];
                lock (everything)
                    everything.Add(line);

                lines.Writer.TryWrite(line);
                start = newline + 1;
            }

            partial.Clear().Append(all[start..]);
        }
    }
}

/// <summary>
/// An ACP driver (AcpSession, reached as ProcessRunner reaches it: through <see cref="IDuplexBackend.CreateDriver"/>) wired to an in-memory stdin, so a test plays
/// the agent: it reads each frame the client sends and answers by handing lines to <c>OnStdoutLine</c>.
/// </summary>
internal sealed class DriverRig : IAsyncDisposable
{
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-acp-rig-").FullName;
    private readonly CancellationTokenSource processEnded = new();

    /// <param name="run">The run the driver is built for.</param>
    /// <param name="harness">The harness row; opencode's by default.</param>
    /// <param name="blockClientLog">Puts a directory where the client's log would be written, so opening it fails.</param>
    public DriverRig(ResolvedRun? run = null, IAcpHarness? harness = null, bool blockClientLog = false)
    {
        FakePlatform platform = new();
        AcpBackend backend = new(platform, harness ?? new OpencodeAcpHarness(platform));
        Job = AcpLines.JobIn(scratch);
        if (blockClientLog)
            Directory.CreateDirectory(Job.AcpClientLog);

        Driver = backend.CreateDriver(run ?? AcpLines.Run(), Job, live: null);
        Running = Driver.RunAsync(Stdin, processEnded.Token);
    }

    public WireStdin Stdin { get; } = new();

    public JobPaths Job { get; }

    public IProcessLive Driver { get; }

    /// <summary>The driver's RunAsync: it completes when the session is over.</summary>
    public Task Running { get; }

    public void Agent(string line) => Driver.OnStdoutLine(line);

    public async Task<SentFrame> NextAsync() => new(await Stdin.NextAsync());

    /// <summary>The process ended (killed, or exited): ProcessRunner cancels the token the driver was given.</summary>
    public void EndProcess() => processEnded.Cancel();

    public async Task CompletesAsync()
    {
        Task finished = await Task.WhenAny(Running, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(ReferenceEquals(finished, Running), "the driver did not finish");
        await Running;
    }

    public async Task StdinClosesAsync()
    {
        Task finished = await Task.WhenAny(Stdin.Closed, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(ReferenceEquals(finished, Stdin.Closed), "stdin was not closed");
    }

    /// <summary>Plays the agent through a successful handshake of <paramref name="configOptions"/> options and returns the frames the client sent, the first prompt last.</summary>
    public async Task<SentFrame[]> OpenAsync(int configOptions = 2, bool load = false)
    {
        List<SentFrame> sent = [];
        SentFrame init = await NextAsync();
        sent.Add(init);
        Agent(AcpLines.Initialized(init.Id));

        SentFrame open = await NextAsync();
        sent.Add(open);
        Agent(load ? AcpLines.Loaded(open.Id) : AcpLines.Opened(open.Id));
        for (int i = 0; i < configOptions; i++)
        {
            SentFrame config = await NextAsync();
            sent.Add(config);
            Agent(AcpLines.Configured(config.Id));
        }

        sent.Add(await NextAsync());
        return [.. sent];
    }

    public async ValueTask DisposeAsync()
    {
        await processEnded.CancelAsync();
        try
        {
            await Running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The assertion that matters was made by the test; a rig that cannot stop is reported by CompletesAsync.
        }

        processEnded.Dispose();
        Stdin.Dispose();
        TempTree.Delete(scratch);
    }
}
