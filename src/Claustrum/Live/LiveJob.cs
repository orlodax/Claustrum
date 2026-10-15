using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Claustrum.Cli;
using Claustrum.Core.IO;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Roles;
using Claustrum.Roles.Sync;

namespace Claustrum.Live;

// #124: the front doors' side of a live job's files (conversation.jsonl, inbox/, live.json, and a detached job's host.json), shared by
// `jobs say|wait|follow`, `run`/`coordinate`'s terminal prompt and the MCP tools. Every read goes through
// PlainFile (a FIFO is a miss, not a hang) behind a fence that refuses a link leaving the jobs store, and a file
// that is missing or half-written reads as null/empty: the caller asks again on its next poll.
internal sealed class LiveJob
{
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly string directory;
    private readonly SyncRoot fence;

    private LiveJob(string root, string id)
    {
        Id = id;
        directory = Path.Combine(root, id);
        fence = SyncRoot.Repo(root);
    }

    public string Id { get; }

    /// <summary>The job, or null when no such job exists (or its directory is a link that leaves the jobs store).</summary>
    public static LiveJob? Find(string id)
    {
        string root = JobDirectory.ResolveRoot(AppServices.Platform);
        if (!JobDirectory.IsJobId(id) || !Directory.Exists(Path.Combine(root, id)))
            return null;

        LiveJob job = new(root, id);
        return job.fence.Escapes(job.directory) ? null : job;
    }

    public static LiveJob At(JobPaths job) => new(JobDirectory.ResolveRoot(AppServices.Platform), job.Id);

    public static string NameOf(LiveStatus status) => status switch
    {
        LiveStatus.WaitingForInput => "waiting_for_input",
        LiveStatus.Done => "done",
        _ => "running",
    };

    /// <summary>The conversation entry live.json names as the open question, when the state is waiting and the entry is already written.</summary>
    public static ConversationEntry? OpenQuestion(LiveState? state, IReadOnlyList<ConversationEntry> entries) =>
        state is { State: LiveStatus.WaitingForInput, QuestionSeq: { } seq } ? entries.FirstOrDefault(entry => entry.Seq == seq) : null;

    /// <summary>
    /// Whether the job's role is blind (role.json `blind: true`), read from request.json and the role library; null while that
    /// cannot be told (no request.json yet, an unreadable or unknown role). Callers treat anything but false as blind.
    /// </summary>
    public bool? IsBlind() => TryReadRole(out string? role, out string? cwd) ? LoadBlind(role, cwd) : null;

    /// <summary>The job's role name from request.json, or null while it cannot be read.</summary>
    public string? Role => TryReadRole(out string? role, out _) ? role : null;

    // Why the job is reported abandoned, shared by every front door (`jobs wait|say|follow`, job_status/job_say).
    public const string AbandonedMessage = "abandoned: the claustrum process running this job is gone";

    /// <summary>
    /// Whether a job that is not finished has lost its owner: no result.json, and the process running it is no longer alive.
    /// That process is the one live.json names; before the session writes it (a detached job still queued for a slot, or one
    /// that is not live at all) it is the host host.json names. A job with neither file is not abandoned.
    /// </summary>
    public bool IsAbandoned(LiveState? state)
    {
        if (File.Exists(Path.Combine(directory, "result.json")))
            return false;

        return state is null ? ReadHost() is { } host && !host.IsAlive() : state.State != LiveStatus.Done && !state.IsOwnerAlive();
    }

    /// <summary>The detached host of this job (`run --detach`, a tree's `run`), or null for a job run by the process that started it.</summary>
    public HostRecord? ReadHost() => Parse(ReadText("host.json"), LiveJsonContext.Default.HostRecord);

    /// <summary>The process that owns this job when it is one `coordinate` runs (owner.json), or null.</summary>
    public OwnerRecord? ReadOwner() => Parse(ReadText("owner.json"), LiveJsonContext.Default.OwnerRecord);

    /// <summary>Whether result.json exists, readable or not: the job has ended.</summary>
    public bool HasResult => File.Exists(Path.Combine(directory, "result.json"));

    /// <summary>Whether the job's receipt is a refusal of its machinery (#104), which `run` ends with exit 2 and not the 1 of a failed status.</summary>
    public bool WasRefused => File.Exists(Path.Combine(directory, "refused"));

    /// <summary>
    /// A RunResult for a job whose own receipt is missing or unreadable, so a front door that must print one document still can
    /// (#124 stage 2 review F4): the job id and the role from request.json, <paramref name="error"/> saying why it is not the real thing.
    /// </summary>
    public RunResult SynthesizedReceipt(RunStatus status, string error) => new(
        SchemaVersion: "1",
        JobId: Id,
        Status: status,
        Backend: "unknown",
        Model: "unknown",
        Role: Role ?? "unknown",
        FinalMessage: "",
        ChangedFiles: [],
        Diff: null,
        DiffTruncated: false,
        SessionId: null,
        CostUsd: null,
        Usage: null,
        ExitCode: -1,
        LogPath: Path.Combine(directory, "stdout.log"),
        DurationSeconds: 0,
        Error: error,
        Raw: null,
        Report: null,
        ReportStatus: ReportStatus.Missing,
        Warnings: []);

    /// <summary>The request a `needs_input` job left for `jobs answer` (resume.json), or null when it has none or it cannot be read.</summary>
    public DelegateRequest? ReadResume() => Parse(ReadText("resume.json"), DelegationJsonContext.Default.DelegateRequest);

    /// <summary>The continuation that took this `needs_input` job's answer (answered.json), or null when it has none or it cannot be read.</summary>
    public AnsweredRecord? ReadAnswered() => Parse(ReadText("answered.json"), LiveJsonContext.Default.AnsweredRecord);

    /// <summary>Whether an answer already started a continuation of this job, readable or not.</summary>
    public bool HasAnswered => File.Exists(Path.Combine(directory, "answered.json"));

    /// <summary>The permission level the job ran at (backend.json `permission`), or null for a job that predates it being recorded.</summary>
    public string? ReadPermission()
    {
        if (ReadText("backend.json") is not { } text)
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("permission", out JsonElement level) && level.ValueKind == JsonValueKind.String
                ? level.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The coordinate job that took over this one's tree (`coordinate --resume`), or null.</summary>
    public string? ReadSuccessor() => ReadText("successor")?.Trim() is { } id && JobDirectory.IsJobId(id) ? id : null;

    /// <summary>The receipt, or null while result.json is missing or unreadable (it is written non-atomically).</summary>
    public RunResult? ReadResult() => Parse(ReadText("result.json"), ClaustrumJsonContext.Default.RunResult);

    /// <summary>
    /// Asks the job's detached host to end the job `cancelled`, with its receipt (the host polls for the marker this leaves). Refused
    /// when the job has no host (its own process owns it: Ctrl-C that), is done, or its host is gone.
    /// </summary>
    public void RequestCancel()
    {
        HostRecord host = ReadHost()
            ?? throw new CliUsageException($"job '{Id}' is not a detached job: only the claustrum process that runs it can cancel it (Ctrl-C there)");

        if (File.Exists(Path.Combine(directory, "result.json")) || ReadState()?.State == LiveStatus.Done)
            throw new CliUsageException($"job '{Id}' is done: nothing to cancel");

        if (!host.IsAlive())
            throw new CliUsageException($"job '{Id}' is {AbandonedMessage}: nothing to cancel");

        string marker = Path.Combine(directory, "cancel");
        if (fence.Escapes(marker))
            throw new CliUsageException($"{marker} resolves outside the jobs store ({SyncRoot.Resolve(marker)}): nothing is written through a link that leaves it");

        if (!PlainFile.TryWriteAllText(marker, DateTimeOffset.UtcNow.ToString("o"), FileMode.Create, out string? refusal))
            throw new CliUsageException($"{marker}: {refusal}");
    }

    public LiveState? ReadState() => Parse(ReadText("live.json"), ClaustrumJsonContext.Default.LiveState);

    public IReadOnlyList<ConversationEntry> ReadConversation()
    {
        if (ReadText("conversation.jsonl") is not { } text)
            return [];

        // The last line may be mid-write: it fails to parse and is read whole on the next poll.
        List<ConversationEntry> entries = [];
        foreach (string line in text.Split('\n'))
            if (Parse(line.Trim(), ClaustrumJsonContext.Default.ConversationEntry) is { Text: not null } entry)
                entries.Add(entry);

        return entries;
    }

    /// <summary>
    /// result.json parsed, or null while it is missing or unreadable; <paramref name="exists"/> tells those apart (the
    /// file is written non-atomically, so a present-but-unparsable one is usually a poll away from readable).
    /// </summary>
    public JsonDocument? ReadResultDocument(out bool exists)
    {
        exists = File.Exists(Path.Combine(directory, "result.json"));
        if (!exists || ReadText("result.json") is not { } text)
            return null;

        try
        {
            JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the job stands, for `jobs wait` and MCP job_status. A readable result.json is done; so is a live.json that
    /// says done or an unreadable result.json, with ResultStatus null until the receipt can be read; abandoned is a live.json
    /// whose owner process is gone with no result.json written. An open question is
    /// reported only once its entry is written, and not while an answer for it sits unconsumed in inbox/ (a caller that
    /// says and then waits must not be handed the question it just answered).
    /// </summary>
    public JobWaitResult Observe()
    {
        using JsonDocument? result = ReadResultDocument(out bool resultExists);
        if (result is not null)
            return new JobWaitResult("done", null, StatusOf(result.RootElement));

        LiveState? state = ReadState();
        if (resultExists || state?.State == LiveStatus.Done)
            return new JobWaitResult("done", null, null);

        if (IsAbandoned(state))
            return new JobWaitResult("abandoned", null, null);

        if (OpenQuestion(state, ReadConversation()) is { } question && !HasQueuedMessage())
            return new JobWaitResult("waiting_for_input", new QuestionInfo(question.Seq, question.Text), null);

        return new JobWaitResult("running", null, null);
    }

    public static string StatusOf(JsonElement receipt) =>
        receipt.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String ? status.GetString() ?? "?" : "?";

    /// <summary>Whether a message written by <see cref="Say"/> is still waiting for the session to consume it.</summary>
    public bool HasQueuedMessage()
    {
        string inbox = Path.Combine(directory, "inbox");
        try
        {
            return Directory.Exists(inbox) && !fence.Escapes(inbox) && Directory.EnumerateFiles(inbox, "*.json").Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Queue <paramref name="text"/> for the live session: an answer while a question is open, a message to the running
    /// role otherwise (the session decides which, on consumption). Refused when the job is not live or is done.
    /// </summary>
    public SayResult Say(string text)
    {
        if (text.Trim().Length == 0)
            throw new CliUsageException("nothing to say: the text is empty");

        if (Encoding.UTF8.GetByteCount(text) > MaxMessageBytes)
            throw new CliUsageException($"the text is over {MaxMessageBytes / 1024} KiB");

        LiveState state = ReadState()
            ?? throw new CliUsageException($"job '{Id}' is not live: it has no live.json, so there is no session to talk to{AnswerHint()}");

        if (state.State == LiveStatus.Done || File.Exists(Path.Combine(directory, "result.json")))
            throw new CliUsageException($"job '{Id}' is done: nobody is listening{AnswerHint()}");

        if (IsAbandoned(state))
            throw new CliUsageException($"job '{Id}' is {AbandonedMessage}: nobody is listening");

        // The engine drops such a message too; refusing here tells the sender instead of leaving it to vanish in inbox/.
        if (IsBlind() is not false)
            throw new CliUsageException($"job '{Id}' runs a blind role{(Role is { } role ? $" ({role})" : "")}, or its role cannot be read: blind roles receive no human input, so there is nothing to say");

        WriteInbox(new InboxMessage(DateTimeOffset.UtcNow, text));

        LiveStatus now = ReadState()?.State ?? state.State;
        return new SayResult(state.State == LiveStatus.WaitingForInput ? state.QuestionSeq : null, NameOf(now));
    }

    // #124 stage 3 review F3: a job that finished by asking has no session to talk to, and `say`/`reply` are for one that is alive. Its
    // answer resumes the session as a new job (`jobs answer`), once.
    private string AnswerHint()
    {
        if (ReadResult() is not { Status: RunStatus.NeedsInput })
            return "";

        return ReadAnswered() is { } answered
            ? $" (it stopped with needs_input and job {answered.JobId} already took the answer)"
            : $" (it stopped with needs_input: answer it with `claustrum jobs answer {Id} --answers <file> | \"<text>\"`, or MCP job_answer)";
    }

    /// <summary>
    /// Queues a notice from Claustrum itself for the live session to record in the conversation; the role is sent nothing. Returns the
    /// inbox file, which is gone once the session has recorded it, or null when the job has no session still running (nothing to record into).
    /// </summary>
    public string? Notify(string text) =>
        ReadState() is { State: not LiveStatus.Done } && !HasResult ? WriteInbox(new InboxMessage(DateTimeOffset.UtcNow, text, Notice: true)) : null;

    // Temp file first, rename second: the session must never read a half-written message. The ticks are fixed-width so
    // the names sort in the order they were written; only `*.json` is a message.
    private string WriteInbox(InboxMessage message)
    {
        string inbox = Path.Combine(directory, "inbox");
        if (fence.Escapes(inbox))
            throw new CliUsageException($"{inbox} resolves outside the jobs store ({SyncRoot.Resolve(inbox)}): nothing is written through a link that leaves it");

        Directory.CreateDirectory(inbox);
        string name = $"{DateTime.UtcNow.Ticks:d19}-{Random.Shared.Next():x8}";
        string temp = Path.Combine(inbox, $"{name}.tmp");
        string target = Path.Combine(inbox, $"{name}.json");
        string json = JsonSerializer.Serialize(message, ClaustrumJsonContext.Default.InboxMessage);
        try
        {
            if (!PlainFile.TryWriteAllText(temp, json, FileMode.Create, out string? refusal))
                throw new CliUsageException($"{temp}: {refusal}");

            File.Move(temp, target);
            return target;
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    private bool TryReadRole([NotNullWhen(true)] out string? role, out string? cwd)
    {
        role = cwd = null;
        if (Parse(ReadText("request.json"), ClaustrumJsonContext.Default.RunRequest) is not { Role: { Length: > 0 } name } request)
            return false;

        (role, cwd) = (name, request.Cwd);
        return true;
    }

    private static bool? LoadBlind(string role, string? cwd)
    {
        try
        {
            return AppServices.RoleLibrary.LoadRole(role, cwd).Definition.Blind;
        }
        catch (Exception e) when (e is RoleRenderException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private string? ReadText(string name)
    {
        string path = Path.Combine(directory, name);
        try
        {
            return File.Exists(path) && !fence.Escapes(path) && PlainFile.TryReadAllText(path, out string? text, out _) ? text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static T? Parse<T>(string? text, JsonTypeInfo<T> info)
        where T : class
    {
        if (text is null)
            return null;

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
