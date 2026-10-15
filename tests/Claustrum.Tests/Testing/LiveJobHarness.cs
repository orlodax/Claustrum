using System.Text.Json;
using Claustrum.Core;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Core.Process;

namespace Claustrum.Tests.Testing;

/// <summary>
/// One live job (#124) run in-process through the real Runner and ClaudeBackend against a conversing <see cref="FakeClaude"/>:
/// the job directory is minted up front so a test can watch live.json and conversation.jsonl and drop messages in inbox/
/// the way `jobs say` does, while the run is in flight. Home, cwd and the fake script live in throwaway directories.
/// </summary>
public sealed class LiveJobHarness : IDisposable
{
    private static readonly TimeSpan limit = TimeSpan.FromSeconds(30);

    private readonly string root = Directory.CreateTempSubdirectory("claustrum-live-").FullName;
    private readonly HomeRedirectPlatform platform;
    private readonly Runner runner;
    private readonly BackendConfig backend;

    /// <param name="script">What the conversing fake claude does.</param>
    /// <param name="sharedPlatform">The platform AppServices is overridden with (AppServicesHomeFixture), for a test that drives a front door which finds the job through AppServices; its home then holds the job.</param>
    public LiveJobHarness(FakeClaudeScript script, HomeRedirectPlatform? sharedPlatform = null)
    {
        FakeClaude.RequirePosixShell();
        Directory.CreateDirectory(Cwd);
        platform = sharedPlatform ?? new HomeRedirectPlatform(Path.Combine(root, "home"));
        string scriptPath = Path.Combine(root, FakeClaude.ScriptName);
        InputLog = script.LiveInputLog ?? Path.Combine(root, "input.log");
        ArgvLog = script.ArgvLog ?? Path.Combine(root, "argv.log");
        FakeClaude.Write(scriptPath, script with { LiveInputLog = InputLog, ArgvLog = ArgvLog });
        backend = new BackendConfig(scriptPath);
        runner = new Runner(platform, new BackendRegistry([new ClaudeBackend(platform)]), new ProcessRunner(platform));
        Job = JobDirectory.Create(platform);
    }

    public JobPaths Job { get; }

    public string Cwd => Path.Combine(root, "cwd");

    public string InputLog { get; }

    public string ArgvLog { get; }

    public string InboxDirectory => Job.InboxDirectory;

    public void Dispose() => TempTree.Delete(root);

    public Task<RunResult> StartAsync(
        bool blind = false,
        bool hasReport = true,
        bool live = true,
        int replyTimeoutSeconds = 60,
        TimeSpan? timeout = null,
        CancellationToken? cancel = null)
    {
        ResolvedRole role = new("builder", "system prompt", "claude", "sonnet", "high", new PermissionPolicy(PermissionLevel.EditShell, []), blind, hasReport);
        RunRequest request = new(
            Role: "builder", Brief: "do it", BriefFile: null, Cwd: Cwd, Backend: null, Model: null, Effort: null,
            Permission: null, BudgetUsd: null, Timeout: timeout, ResumeSession: null, AttachFiles: [], Env: [], Stream: false);
        RunOptions options = new(DiffByteCapBytes: 200_000, BackendConfig: backend, Live: live, ReplyTimeoutSeconds: replyTimeoutSeconds);
        return Task.Run(() => runner.RunAsync(request, role, options, Job, cancel ?? TestContext.Current.CancellationToken), CancellationToken.None);
    }

    public LiveState? State()
    {
        string? text = ReadShared(Job.LiveJson);
        return text is null ? null : TryParse(text, ClaustrumJsonContext.Default.LiveState);
    }

    public ConversationEntry[] Conversation()
    {
        if (ReadShared(Job.ConversationJsonl) is not { } text)
            return [];

        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => TryParse(line, ClaustrumJsonContext.Default.ConversationEntry)).OfType<ConversationEntry>()];
    }

    /// <summary>Every line the fake received on stdin, raw: the brief first, then each message the engine sent.</summary>
    public string[] Input() => File.Exists(InputLog) ? File.ReadAllLines(InputLog) : [];

    /// <summary>Drops a message in inbox/ as `jobs say` writes one (temp file, then rename).</summary>
    public void Say(string text)
    {
        Directory.CreateDirectory(InboxDirectory);
        string name = $"{DateTime.UtcNow.Ticks:d19}-{Random.Shared.Next():x8}";
        string temp = Path.Combine(InboxDirectory, $"{name}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(new InboxMessage(DateTimeOffset.UtcNow, text), ClaustrumJsonContext.Default.InboxMessage));
        File.Move(temp, Path.Combine(InboxDirectory, $"{name}.json"));
    }

    /// <summary>Waits until the job publishes an open question, and returns the state that names it.</summary>
    public async Task<LiveState> WaitForQuestionAsync()
    {
        await WaitUntilAsync(() => State() is { State: LiveStatus.WaitingForInput, QuestionSeq: not null } && Conversation().Length > 0, "the job to wait for input");
        return State() ?? throw new InvalidOperationException("live.json vanished");
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime due = DateTime.UtcNow + limit;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < due, $"timed out waiting for {what}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    public static async Task<RunResult> FinishedAsync(Task<RunResult> run)
    {
        Task finished = await Task.WhenAny(run, Task.Delay(limit, TestContext.Current.CancellationToken));
        Assert.True(ReferenceEquals(finished, run), "the run did not finish in time");
        return await run;
    }

    private static string? ReadShared(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static T? TryParse<T>(string text, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(text, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
