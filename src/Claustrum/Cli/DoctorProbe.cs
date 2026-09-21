using System.Globalization;
using Claustrum.Core;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;

namespace Claustrum.Cli;

// docs/PLAN.md §B6's fourth check: one real, minimal, paid round trip per backend ("1-token 'reply
// OK' call, cost shown"). Lives apart from BackendsCommands so every step but the spawn itself is
// unit-testable against a fake IBackend (issue #12); the MCP `doctor` tool deliberately does not
// call it — no MCP tool spends the user's money without a `--probe` typed by hand.
internal static class DoctorProbe
{
    // Cheapest class first: a connectivity check must not land on `frontier-*` when a cheap class
    // resolves to the same backend.
    private static readonly string[] aliasesCheapestFirst = ["fast", "cheap-coding", "standard-coding", "frontier-coding", "frontier-reasoning"];

    // An error/final message mentioning any of these is reported as "no credential" rather than a
    // bare failure: telling "not logged in" apart from "broken" is the probe's whole point.
    private static readonly string[] authMarkers = ["auth", "login", "unauthorized", "401", "403", "api key", "credential", "token"];

    private const decimal ProbeBudgetUsd = 0.05m;
    private const int ProbeTimeoutSeconds = 120;
    private const int ProbeDiffCapBytes = 4 * 1024;

    // The escape hatch that keeps the free auth/mcp/os checks free: `--probe` otherwise spends money
    // on every invocation, including every `dotnet test` on a machine with a backend logged in.
    // Deliberately not a claustrum.json key (no winning layer to show) — a CLI-only env flag.
    public static bool IsSkipped(IPlatform platform)
    {
        string? value = platform.GetEnvironmentVariable("CLAUSTRUM_SKIP_PROBE")?.Trim();
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static string? SelectModelAlias(Config config, string backendName)
    {
        Dictionary<string, string> models = config.Merged.Models ?? [];
        foreach (string alias in aliasesCheapestFirst)
        {
            // Without this check the alias would fall through ResolveModel's colon split and report
            // "claude" for an alias that is not configured at all.
            if (!models.ContainsKey(alias))
                continue;

            try
            {
                if (config.ResolveModelBackend(alias) == backendName)
                    return alias;
            }
            catch (ConfigException)
            {
                // A cyclic or too-deep alias is a claustrum.json problem the merged-config dump
                // already shows; it must not abort a diagnostic command.
            }
        }

        return null;
    }

    public static async Task<ProbeOutcome> RunAsync(IBackend backend, Config config, Runner runner, CancellationToken cancellationToken)
    {
        if (SelectModelAlias(config, backend.Name) is not { } alias)
            return new ProbeOutcome($"skipped (no model alias in claustrum.json resolves to this backend; add e.g. \"fast\": \"{backend.Name}:<model>\")", null);

        // ReportSchema "" is load-bearing: it makes ResolvedRole.HasReport false, so Runner appends
        // no report-format trailer and the probe stays the one-token round trip §B6 asks for.
        RenderedRole rendered = new(
            Name: "doctor-probe",
            SystemBody: "You are a connectivity probe. Reply with exactly the word OK and nothing else.",
            ModelClass: alias,
            Effort: "high",
            Permission: "readonly",
            Deny: [],
            ReportSchema: "",
            Blind: false);

        ResolvedRole resolved = config.Resolve(rendered, new ConfigOverrides());

        // A throwaway cwd, not the repo the user is standing in: Runner snapshots its cwd before and
        // after every call, and hashing a real worktree for a connectivity check is pure waste.
        string probeCwd = Directory.CreateTempSubdirectory("claustrum-probe-").FullName;
        try
        {
            RunRequest request = new(
                Role: "doctor-probe",
                Brief: "Reply with exactly the word OK and nothing else.",
                BriefFile: null,
                Cwd: probeCwd,
                Backend: null,
                Model: null,
                Effort: null,
                Permission: null,
                BudgetUsd: ProbeBudgetUsd,
                Timeout: TimeSpan.FromSeconds(ProbeTimeoutSeconds),
                ResumeSession: null,
                AttachFiles: [],
                Env: [],
                Stream: false);

            RunOptions options = new(
                DiffByteCapBytes: ProbeDiffCapBytes,
                BackendConfig: config.Merged.Backends?.GetValueOrDefault(backend.Name),
                EnvPassthroughAll: config.Merged.Defaults?.EnvPassthrough == "all");

            RunResult result = await runner.RunAsync(request, resolved, options, cancellationToken);
            return new ProbeOutcome(Describe(result), result);
        }
        finally
        {
            try
            {
                if (Directory.Exists(probeCwd))
                    Directory.Delete(probeCwd, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file the backend left locked must not replace the probe's real result with a
                // cleanup throw; the directory is under the OS temp root, which reclaims it.
            }
        }
    }

    public static string Describe(RunResult result) => result.Status switch
    {
        RunStatus.Success => $"OK ({result.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s, cost {DescribeCost(result.CostUsd)}, reply \"{Clamp(result.FinalMessage.Trim(), 40)}\")",
        RunStatus.BackendMissing => $"skipped ({FirstLine(result.Error)})",
        RunStatus.Timeout => $"failed (no reply within {ProbeTimeoutSeconds}s) — log: {result.LogPath}",
        _ when LooksLikeMissingCredential(result) => $"no credential ({FirstLine(result.Error)}) — log: {result.LogPath}",
        _ => $"failed ({FirstLine(result.Error)}) — log: {result.LogPath}",
    };

    private static string DescribeCost(decimal? costUsd) =>
        costUsd is { } cost ? $"${cost.ToString("0.0000", CultureInfo.InvariantCulture)}" : "not reported";

    // A heuristic, and named as one: no backend reports "you are not authenticated" in a machine
    // readable field, so the message text is all there is. A miss degrades to "failed (...)" with
    // the same text and log path, which is what makes matching this loosely safe.
    private static bool LooksLikeMissingCredential(RunResult result)
    {
        string haystack = $"{result.Error} {result.FinalMessage}";
        return authMarkers.Any(marker => haystack.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstLine(string? text)
    {
        string? line = (text ?? "").ReplaceLineEndings("\n").Split('\n')
            .Select(candidate => candidate.Trim())
            .FirstOrDefault(candidate => candidate.Length > 0);

        return line is null ? "no error message" : Clamp(line, 120);
    }

    // The ellipsis counts against maxLength, so the whole field honours its own cap.
    private static string Clamp(string text, int maxLength) =>
        text.Length <= maxLength ? text : $"{text[..(maxLength - 1)]}…";
}
