using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;
using Claustrum.Core.Report;

namespace Claustrum.Core.Backends.Acp;

// The generic Agent Client Protocol client (#130), opt-in (`--backend acp-<harness>` or an alias `acp-<harness>:<model>`), one per
// IAcpHarness row. Build only launches `<exe> <args>`; AcpSession speaks the protocol over its stdin and stdout for the whole run, and
// Parse reads stdout.log, the agent's half, alone. NOTES.md "The ACP backend as built (2026-10-16)" has the design and what is not done.
public sealed class AcpBackend(IPlatform platform, IAcpHarness harness) : IBackend, IDuplexBackend, IPostRunHook
{
    private const string NoSessionTotal = "the harness has no way to read a session's total";
    private const string NoCost = "it carries no cost";

    public string Name => $"acp-{harness.Name}";

    public string Harness => harness.Name;

    public bool CanResume => harness.LoadsSessions;

    public ILiveProtocol? LiveProtocol => AcpLiveProtocol.Instance;

    public IStreamFormatter? CreateStreamFormatter(string cwd) => new AcpStreamFormatter(cwd);

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) =>
        VersionProbe.RunAsync(harness.Exe, harness.VersionArgs, config, platform, cancellationToken);

    // StdinText stays null: the driver owns stdin from its first byte.
    public ProcessSpec Build(ResolvedRun run)
    {
        AcpHarnessSetup setup = harness.Prepare(run);
        Dictionary<string, string> env = new(run.Env, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in setup.Env)
            env[entry.Key] = entry.Value;

        return new ProcessSpec(harness.Exe, [.. harness.Args], run.Cwd, env, TempFiles: [], StdinText: null, Warnings: setup.Warnings);
    }

    public IProcessLive CreateDriver(ResolvedRun run, JobPaths job, LiveSession? live) => new AcpSession(run, job, harness.Prepare(run), harness.LoadsSessions, live);

    public ProcessSpec CarryTurn(string sessionId, string message) => AcpLiveProtocol.Carry(message);

    public ParsedOutput Parse(string stdout, string stderr, int exitCode)
    {
        AcpTranscript transcript = AcpTranscript.Read(stdout);
        AcpTurn? last = transcript.LastTurn;
        string? failure = transcript.HandshakeError?.Message ?? last?.Failure;
        bool isError = exitCode != 0 || transcript.HandshakeError is not null || last is null || last.Failed;

        return new ParsedOutput(
            FinalMessage(transcript, failure, stdout, stderr),
            transcript.SessionId,
            CostOf(transcript),
            UsageOf(transcript.Turns),
            ReportedEdits: [],
            last?.Raw,
            isError,
            ErrorKind: ErrorKindOf(transcript, last),
            ErrorDetail: failure ?? (!transcript.AnyPromptAnswered && transcript.SawFrame ? "the agent ended before any prompt was answered" : null),
            ToolCallsCompleted: ToolCallsOf(transcript));
    }

    // A turn cut off with text says the last words, else the last answered turn does; a turn that carried the report stays the final
    // message when the chosen text has none (claude's rule). A JSON-RPC refusal is said after the text, as the native opencode backend
    // says its error event; with no text at all, stderr speaks, and stdout itself when the process never spoke JSON-RPC.
    private static string FinalMessage(AcpTranscript transcript, string? failure, string stdout, string stderr)
    {
        string message = transcript.PendingText is { Length: > 0 } cut ? cut : transcript.LastTurn?.Text ?? "";
        if (ReportExtractor.Extract(message).Status == ReportStatus.Missing
            && transcript.Turns.LastOrDefault(turn => ReportExtractor.Extract(turn.Text).Status != ReportStatus.Missing) is { } reported)
        {
            message = reported.Text;
        }

        if (failure is not null)
            message = message.Length == 0 ? failure : $"{message}\n\n{failure}";

        if (message.Length > 0)
            return message;

        return stderr.Trim() is { Length: > 0 } printed ? printed : transcript.SawFrame ? "" : stdout.Trim();
    }

    // A refusal in the handshake prompted nothing, so it cost nothing. The total moves once per turn, just before its answer (measured
    // 2026-10-16, opencode-report.jsonl line 88), so a turn in flight at the end leaves a floor, not a total: unsettled is null, as
    // OpencodeBackend.Parse refuses an incomplete step sum. A loaded session's total is not this run's; a per-turn cost is not trusted.
    private decimal? CostOf(AcpTranscript transcript) =>
        transcript.HandshakeError is not null ? 0m
        : transcript.Settled && harness.CostIsRunningTotal ? transcript.RunningCostUsd
        : null;

    // Each prompt's usage is that turn's own (measured 2026-10-16), so the run's is their sum; thought tokens have no Usage field.
    private static Usage? UsageOf(List<AcpTurn> turns)
    {
        Usage? usage = null;
        foreach (AcpTurn turn in turns)
        {
            if (turn.Usage is not { } tokens)
                continue;

            int? input = AcpJson.IntOf(tokens, "inputTokens");
            int? output = AcpJson.IntOf(tokens, "outputTokens");
            int? cacheRead = AcpJson.IntOf(tokens, "cachedReadTokens");
            int? cacheWrite = AcpJson.IntOf(tokens, "cachedWriteTokens");
            // A usage object with none of the four is not a report of zero, so it must not turn a null run usage into all-null fields.
            if (input is null && output is null && cacheRead is null && cacheWrite is null)
                continue;

            usage = new Usage(
                Add(usage?.InputTokens, input),
                Add(usage?.OutputTokens, output),
                Add(usage?.CacheReadInputTokens, cacheRead),
                Add(usage?.CacheCreationInputTokens, cacheWrite));
        }

        return usage;
    }

    // Null stays null when neither side has a number: "not reported" and "zero" are different answers (OpencodeBackend.Add).
    private static int? Add(int? running, int? turn) =>
        running is null && turn is null ? null : (running ?? 0) + (turn ?? 0);

    // #126: a config option naming a model the agent does not know (`data.modelId`, -32602) is the provider's ModelNotFound; any other
    // handshake refusal is Other; a prompt's error is classed by opencode's `data.errorName`; a stop reason that cut the turn is Other.
    private static ErrorKind? ErrorKindOf(AcpTranscript transcript, AcpTurn? last) => transcript.HandshakeError is { } refused
        ? transcript.HandshakeTag == AcpRequestId.Config && refused.ModelId is not null ? ErrorKind.ModelNotFound : ErrorKind.Other
        : last?.Error is { } error ? ProviderErrors.Classify(null, error.ErrorName)
        : last is { Stopped: true } ? ErrorKind.Other
        : null;

    // Counted only for a run of one turn, as claude's: after a second the human spoke, and the run is never "before any work" again.
    // A `failed` call ran nothing; pending and in_progress count as work, so an unseen shape errs towards no retry (OpencodeBackend's rule).
    private static int? ToolCallsOf(AcpTranscript transcript) =>
        !transcript.AnyPromptAnswered || (transcript.Turns.Count == 1 && !transcript.ActiveAfterLastTurn)
            ? transcript.ToolCallStatus.Values.Count(status => status != "failed")
            : null;

    // A resumed session's total includes what it cost before this run, so its baseline is read first (opencode: `session export`).
    async Task<PreRunBaseline?> IPostRunHook.PreRunAsync(PostRunContext context, CancellationToken cancellationToken)
    {
        if (context.ResumeSession is not { Length: > 0 } sessionId || !harness.LoadsSessions)
            return null;

        AcpSessionTotal? total = await harness.SessionTotalAsync(sessionId, context, cancellationToken);
        // A total with neither a cost nor a failure is a failure, as after the run: never a $0 baseline.
        return total is null
            ? new PreRunBaseline(null, null, NoSessionTotal)
            : new PreRunBaseline(total.CostUsd, null, total.Failure ?? (total.CostUsd is null ? NoCost : null));
    }

    // Every run with a session is charged the session's total read outside the stream, less a resumed run's baseline, as the native
    // opencode backend charges its export; the stream's settled total is only the fallback. Usage is never touched: it is per turn and
    // already the run's own. A handshake refusal prompted nothing, so Parse's $0 stands and nothing is read.
    async Task<PostRunResult?> IPostRunHook.PostRunAsync(ParsedOutput parsed, PostRunContext context, PreRunBaseline? baseline, CancellationToken cancellationToken)
    {
        AcpTranscript stream = AcpTranscript.Read(context.Stdout);
        if (parsed.SessionId is not { Length: > 0 } sessionId || stream.HandshakeError is not null)
            return null;

        // A loaded session's total includes what it cost before: priced as fresh, the other session's whole history would be charged.
        if (stream.Loaded && sessionId != context.ResumeSession)
            return Unknown("the stream names another session than the one resumed");

        PreRunBaseline? before = baseline is not null && sessionId == context.ResumeSession ? baseline : null;
        if (before?.Failure is { } baselineFailure)
            return Unknown($"the pre-run total of the resumed session could not be read ({baselineFailure})");

        AcpSessionTotal? total = await harness.SessionTotalAsync(sessionId, context, cancellationToken);
        if (total is not { CostUsd: { } after, Failure: null })
        {
            string reason = total is null ? NoSessionTotal : total.Failure ?? NoCost;
            return parsed.CostUsd is not null
                ? new PostRunResult(null, null, $"{Name}: session total unavailable ({reason}): cost is the stream's last usage_update")
                : Unknown($"session total unavailable ({reason})");
        }

        decimal cost = after - (before?.CostUsd ?? 0m);
        return cost < 0m ? Unknown("the session's total went down between the two reads") : new PostRunResult(cost, null, null);
    }

    private PostRunResult Unknown(string reason) => new(null, null, $"{Name}: {reason}: cost unknown, charged the cap");
}
