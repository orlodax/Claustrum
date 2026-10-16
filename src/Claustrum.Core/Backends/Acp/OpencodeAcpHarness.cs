using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Backends.Acp;

// `opencode acp` (2.0.30, measured 2026-10-16, NOTES.md "ACP as a transport: measured, not built"): `_meta.systemPrompt` is ignored,
// so the role is the native backend's own agent in OPENCODE_CONFIG_CONTENT (which `opencode acp` honours), deny list included, chosen
// with `set_config_option mode` after `model` — the order measured. A run's total, and a resumed one's baseline, is `session export`.
public sealed class OpencodeAcpHarness(IPlatform platform) : IAcpHarness
{
    public string Name => "opencode";

    public string Exe => "opencode";

    public string[] Args { get; } = ["acp"];

    public string[] VersionArgs { get; } = ["--version"];

    public bool CostIsRunningTotal => true;

    public bool LoadsSessions => true;

    public AcpHarnessSetup Prepare(ResolvedRun run)
    {
        string agentName = $"claustrum-{run.Role.Name}";
        Dictionary<string, string> env = new(StringComparer.Ordinal)
        {
            ["OPENCODE_CONFIG_CONTENT"] = OpencodeBackend.BuildConfigContent(agentName, run.SystemPromptFilePath, run.Role.Permission),

            // A one-shot headless run has no UI to live-update, and the watcher costs an inotify
            // instance: at the host's `fs.inotify.max_user_instances` the server stops dead after
            // its "watcher subscribe" log line and never answers (measured 2026-09-22).
            ["OPENCODE_DISABLE_FILEWATCHER"] = "1",
        };

        return new AcpHarnessSetup(env, [.. ModelOptions(run.Role.Model), ("mode", agentName)]);
    }

    // The ACP `model` option refuses `id#variant` with -32602, which would read as model_not_found and start a cast's fallback, though
    // `opencode run --model id#variant` takes it: ACP carries the variant as the `effort` option (ids seen 2026-10-16: model, effort,
    // mode), whose values follow the model, so it comes second. A bad variant fails loudly there, a -32602 with no `data.modelId`.
    private static (string ConfigId, string Value)[] ModelOptions(string model) =>
        model.Split('#', 2) is [string id, string variant] ? [("model", id), ("effort", variant)] : [("model", model)];

    // `session export <acpSessionId>` reads an ACP session (measured), and BinaryLocator honours `backends.acp-opencode.path` through the context.
    public async Task<AcpSessionTotal?> SessionTotalAsync(string sessionId, PostRunContext context, CancellationToken cancellationToken)
    {
        SessionExport export = await new OpencodeSessionExport(platform).ExportAsync(sessionId, context, cancellationToken);
        return new AcpSessionTotal(export.CostUsd, export.Failure);
    }
}
