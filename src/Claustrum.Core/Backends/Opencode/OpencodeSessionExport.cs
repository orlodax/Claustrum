using System.Diagnostics;
using System.Text.Json;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends.Opencode;

// `opencode session export`'s running totals of one session: what the IPostRunHook of OpencodeBackend and of acp-opencode (#130)
// charges a run, a resumed session's baseline subtracted. Moved out of OpencodeBackend unchanged; NOTES.md "The opencode and api
// backends, validated against real endpoints" (#125 bullets) has the resume bill, the truncation and the fallback.
internal sealed class OpencodeSessionExport(IPlatform platform)
{
    private static readonly TimeSpan exportTimeout = TimeSpan.FromSeconds(30);

    // One retry, and only for JSON that did not parse: a cut-off export is the one failure a second
    // attempt can cure; a missing binary or a non-zero exit would fail the same way again.
    public async Task<SessionExport> ExportAsync(string sessionId, PostRunContext context, CancellationToken cancellationToken)
    {
        SessionExport export = await ExportOnceAsync(sessionId, context, cancellationToken);
        return export.Unparseable ? await ExportOnceAsync(sessionId, context, cancellationToken) : export;
    }

    private async Task<SessionExport> ExportOnceAsync(string sessionId, PostRunContext context, CancellationToken cancellationToken)
    {
        // On POSIX the export goes to a file in the job directory through a one-line `sh` redirect: opencode
        // exits before a full stdout pipe drains, and a reader that is merely slow gets a cut-off document
        // (measured 2026-10-15: 8 192 bytes of 1.27 MB behind a 3 s reader). A file write completes first.
        string? outputPath = platform.Os == ClaustrumOs.Windows ? null : Path.Combine(context.JobDirectory, $"session-export-{Guid.NewGuid():N}.tmp");
        try
        {
            ResolvedBinary binary = BinaryLocator.Locate("opencode", ["session", "export", "--standalone", sessionId], context.BackendConfig, platform);

            ProcessStartInfo startInfo = new()
            {
                FileName = outputPath is null ? binary.Executable : "/bin/sh",
                WorkingDirectory = context.Spec.Cwd,
                UseShellExecute = false,
                RedirectStandardOutput = outputPath is null,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };

            string[] argv = outputPath is null
                ? binary.Args
                : ["-c", "out=$1; shift; exec \"$@\" > \"$out\"", "sh", outputPath, binary.Executable, .. binary.Args];
            foreach (string arg in argv)
                startInfo.ArgumentList.Add(arg);

            // The run's own environment, `--env` entries and `env_passthrough: all` included.
            startInfo.Environment.Clear();
            foreach (KeyValuePair<string, string> entry in EnvAllowList.Build(platform, context.Spec.Env, context.EnvPassthroughAll))
                startInfo.Environment[entry.Key] = entry.Value;

            using System.Diagnostics.Process process = new() { StartInfo = startInfo };
            process.Start();
            process.StandardInput.Close();

            Task<string>? stdoutTask = outputPath is null ? process.StandardOutput.ReadToEndAsync(CancellationToken.None) : null;
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using CancellationTokenSource timeoutSource = new(exportTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            string stderr;
            string json;
            try
            {
                await process.WaitForExitAsync(linked.Token);

                // The reads share the deadline: a grandchild that inherited the pipe (an MCP/LSP server
                // from the run's config) outlives the export and would hold ReadToEnd open forever.
                stderr = await stderrTask.WaitAsync(linked.Token);
                if (process.ExitCode != 0)
                {
                    string detail = stderr.Trim();
                    return new SessionExport(null, null, detail.Length > 0 ? detail[..Math.Min(detail.Length, 300)] : $"exit code {process.ExitCode}");
                }

                json = stdoutTask is not null ? await stdoutTask.WaitAsync(linked.Token) : await File.ReadAllTextAsync(outputPath!, linked.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync(CancellationToken.None);
                return new SessionExport(null, null, timeoutSource.IsCancellationRequested ? "timed out after 30s" : "cancelled");
            }

            return ReadTotals(json);
        }
        catch (Exception ex)
        {
            return new SessionExport(null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (outputPath is not null)
                try { File.Delete(outputPath); } catch (IOException) { }
        }
    }

    // Only `info.cost` and `info.tokens` matter; the messages (~1 MB for a long session) are ignored.
    private static SessionExport ReadTotals(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object)
                return new SessionExport(null, null, "export JSON has no 'info' object");

            if (!info.TryGetProperty("cost", out JsonElement costProp) || costProp.ValueKind != JsonValueKind.Number || !costProp.TryGetDecimal(out decimal cost))
                return new SessionExport(null, null, "export info has no cost");

            Usage? usage = null;
            if (info.TryGetProperty("tokens", out JsonElement tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                // Same field layout as AddStepFinish; reasoning tokens are dropped (Usage has no field for them).
                JsonElement cache = tokens.TryGetProperty("cache", out JsonElement cacheProp) && cacheProp.ValueKind == JsonValueKind.Object
                    ? cacheProp
                    : default;
                usage = new Usage(OpencodeBackend.TryGetInt(tokens, "input"), OpencodeBackend.TryGetInt(tokens, "output"), OpencodeBackend.TryGetInt(cache, "read"), OpencodeBackend.TryGetInt(cache, "write"));
            }

            return new SessionExport(cost, usage, null);
        }
        catch (JsonException ex)
        {
            return new SessionExport(null, null, $"unparseable export JSON: {ex.Message}", Unparseable: true);
        }
    }
}

internal sealed record SessionExport(decimal? CostUsd, Usage? Usage, string? Failure, bool Unparseable = false);
