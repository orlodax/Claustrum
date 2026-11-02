using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Config;
using Claustrum.Core.Model;

namespace Claustrum.Core.Tracing;

/// <summary>
/// Turns a run's traced tool calls, its role's permission and its report into the receipt's <c>environment_issues</c> (#145). A pure function
/// of its inputs, so a recorded stdout.log is a test. It reads tool error texts and shell output only: never the output of a read, search,
/// edit or fetch (file or page content), and never a shell command that only views files. Every rule, and why each one draws its line where
/// it does: NOTES.md "Environment issues".
/// </summary>
public static partial class EnvironmentClassifier
{
    /// <summary>The <c>first</c> of an issue read off the role's report rather than off stdout.log.</summary>
    public const string ReportSource = "report";

    private const int WhereShown = 5;
    private const int TextChars = 120;
    private const string ExternalDirectory = "external_directory";
    private const string GitHubNotFound = "HTTP 404 from GitHub";

    public static EnvironmentIssue[] Classify(RunTrace? trace, PermissionPolicy permission, ClaustrumReport? report, string cwd, string[]? readDirs)
    {
        IEnumerable<Hit> traced = trace is null ? [] : FromTrace(trace, permission, cwd, readDirs ?? []).OrderBy(hit => hit.Line);
        return [.. Group(traced), .. Group(FromReport(report))];
    }

    private static IEnumerable<EnvironmentIssue> Group(IEnumerable<Hit> hits) =>
        hits.GroupBy(hit => (hit.Kind, hit.What)).Select(group => new EnvironmentIssue(
            group.Key.Kind, group.Key.What, Joined(group.Select(hit => hit.Where)), group.Sum(hit => hit.Count), group.First().First));

    private static string? Joined(IEnumerable<string?> wheres)
    {
        string[] distinct = [.. wheres.OfType<string>().Distinct(StringComparer.Ordinal)];
        if (distinct.Length == 0)
            return null;

        string shown = string.Join(", ", distinct.Take(WhereShown));
        return distinct.Length > WhereShown ? $"{shown} +{distinct.Length - WhereShown} more" : shown;
    }

    private static List<Hit> FromTrace(RunTrace trace, PermissionPolicy permission, string cwd, string[] readDirs)
    {
        List<Hit> hits = [];

        // A readonly role in plan mode is by design; a role meant to run commands or edit is not (#140: `shell` ran in plan mode).
        if (trace.PermissionMode == "plan" && permission.Level > PermissionLevel.ReadOnly)
            hits.Add(new Hit(EnvironmentIssueKind.PlanMode, $"the session started in plan mode, at permission {PermissionLevelParser.ToConfigString(permission.Level)}", null, trace.PermissionModeLine));

        foreach (TracedDenial denial in trace.Denials)
        {
            if (!DeniedOnPurpose(denial.Tool, denial.Command, denial.Path, permission))
                hits.Add(new Hit(EnvironmentIssueKind.PermissionDenied, $"Permission denied: {denial.Tool}", Short(denial.Command ?? denial.Path ?? denial.Url), denial.Line));
        }

        foreach (TracedCall call in trace.Calls)
            hits.AddRange(FromCall(call, permission, cwd, readDirs));

        return hits;
    }

    private static IEnumerable<Hit> FromCall(TracedCall call, PermissionPolicy permission, string cwd, string[] readDirs)
    {
        string? error = call.Failed && call.Error?.Trim() is { Length: > 0 } text ? text : null;

        // The harness's refusals are the whole error of a call that never ran: nothing in them is file content, a viewer's included.
        if (error is not null && RefusalPattern().Match(error) is { Success: true } refusal)
        {
            string name = refusal.Groups["name"].Value;
            if (name == ExternalDirectory)
                return [new Hit(EnvironmentIssueKind.ExternalDirectory, $"Permission denied: {ExternalDirectory}", ExternalTarget(call, cwd), call.Line)];

            return DeniedOnPurpose(name, call.Command, call.Path, permission) ? [] : [new Hit(EnvironmentIssueKind.PermissionDenied, $"Permission denied: {name}", Target(call), call.Line)];
        }

        if (error is not null && MissingToolPattern().Match(error) is { Success: true } missing)
        {
            string tool = missing.Groups["name"].Value;
            return MissingOnPurpose(tool, error, permission.Level) ? [] : [new Hit(EnvironmentIssueKind.MissingTool, $"tool \"{tool}\" is not available", Target(call), call.Line)];
        }

        if (call.Family == ToolFamily.Shell)
            return FromShell(call, error, cwd, readDirs);

        if (error is null)
            return [];

        if (call.Family == ToolFamily.Fetch)
            return FromFetch(call, error) is { } refused ? [refused] : [];

        // A read, search or edit is judged by the path it was given, never by one its error mentions (claude names the cwd in its own).
        return NotFoundPattern().IsMatch(error) && call.Path is { } path && Unreachable(path, cwd, readDirs) is { } gone
            ? [new Hit(EnvironmentIssueKind.MissingDirectory, "path does not exist", gone, call.Line)]
            : [];
    }

    // Monitor: claude's background waiter, which a polling loop replaces; question: opencode's, with no human to ask in a headless run;
    // a `*.env` read: opencode's headless guard; and whatever the level withholds or the role's own deny list names (the policy working).
    private static bool DeniedOnPurpose(string tool, string? command, string? path, PermissionPolicy permission)
    {
        string name = tool.ToLowerInvariant();
        if (name is "monitor" or "question" || (name == "read" && path is not null && IsEnvFile(path)))
            return true;

        return ToolNames.Withheld(ToolNames.FamilyOf(name), permission.Level) || (command is not null && AcpPermission.IsDenied(command, permission.Deny));
    }

    // A miscased name (claude says which tool exists), a name no role is ever given (the model made it up) and a family the level withholds
    // are not the environment's doing; an MCP tool the run should have had is.
    private static bool MissingOnPurpose(string tool, string error, PermissionLevel level)
    {
        if (error.Contains("case-sensitive", StringComparison.OrdinalIgnoreCase))
            return true;

        ToolFamily family = ToolNames.FamilyOf(tool);
        return family == ToolFamily.Other ? !tool.StartsWith("mcp__", StringComparison.Ordinal) : ToolNames.Withheld(family, level);
    }

    private static bool IsEnvFile(string path)
    {
        string name = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
        return name.EndsWith(".env", StringComparison.Ordinal) || (name.Contains(".env.", StringComparison.Ordinal) && !name.EndsWith(".env.example", StringComparison.Ordinal));
    }

    // A shell call names the outside path in its command (`cd /repo && git log`), its cwd being the job's: the first one outside the cwd.
    private static string? ExternalTarget(TracedCall call, string cwd)
    {
        if (call.Family != ToolFamily.Shell)
            return Short(call.Path ?? call.Url ?? call.Command);

        string root = TracePath.Normalize(cwd, cwd);
        foreach (string path in TracePath.AbsolutePaths(call.Command ?? ""))
        {
            string full = TracePath.Normalize(path, cwd);
            if (!full.StartsWith("/dev/", StringComparison.Ordinal) && !TracePath.IsUnder(full, root))
                return full;
        }

        return call.Path is { } workdir && !TracePath.IsUnder(TracePath.Normalize(workdir, cwd), root) ? TracePath.Normalize(workdir, cwd) : Short(call.Command);
    }

    private static IEnumerable<Hit> FromShell(TracedCall call, string? error, string cwd, string[] readDirs)
    {
        ShellSegment[] segments = ShellCommand.Segments(call.Command);
        string?[] said = [error, call.Output];
        string text = string.Join('\n', said.OfType<string>());
        if (ShellCommand.OnlyViews(segments) || text.Length == 0)
            return [];

        bool gh = segments.Any(segment => segment is { Program: "gh", Views: false });
        bool network = gh || segments.Any(segment => segment is { Program: "git" or "curl", Views: false });
        Uri? named = TracePath.Urls(call.Command ?? "").FirstOrDefault(url => !TracePath.IsLoopback(url));

        // One hit per call and per thing it says, however many lines repeat it: a count is of calls, not of log lines.
        return text.Split('\n')
            .SelectMany(line => FromShellLine(line, call, gh, network, named, cwd, readDirs))
            .DistinctBy(hit => (hit.Kind, hit.What, hit.Where));
    }

    private static IEnumerable<Hit> FromShellLine(string line, TracedCall call, bool gh, bool network, Uri? named, string cwd, string[] readDirs)
    {
        if (network && AuthWhat(line, gh, named) is { } what)
            yield return new Hit(EnvironmentIssueKind.Auth, what, TracePath.Urls(line).FirstOrDefault()?.OriginalString ?? Short(call.Command), call.Line);

        if (ErrnoPattern().Match(line) is { Success: true } errno)
            yield return new Hit(EnvironmentIssueKind.ContainerPermission, "PermissionError: [Errno 13]", errno.Groups["path"] is { Success: true } path ? path.Value : Short(call.Command), call.Line);
        else if (DockerPattern().IsMatch(line))
            yield return new Hit(EnvironmentIssueKind.ContainerPermission, "permission denied connecting to the Docker daemon", Short(call.Command), call.Line);

        if (!NotFoundPattern().IsMatch(line))
            yield break;

        foreach (string mentioned in TracePath.AbsolutePaths(line))
        {
            if (Unreachable(mentioned, cwd, readDirs) is { } gone)
                yield return new Hit(EnvironmentIssueKind.MissingDirectory, "path does not exist", gone, call.Line);
        }
    }

    // A credential marker, else a 401 or 403 from any host but the app under test (loopback), else a 404 from gh only: gh talks to GitHub,
    // where a private repository answers 404 to a request without credentials. With no host in sight, only gh surely asked GitHub.
    private static string? AuthWhat(string line, bool gh, Uri? named)
    {
        if (CredentialPattern().Match(line) is { Success: true } credential)
            return CredentialWhat(credential.Value);
        if (gh && GhNotFoundPattern().IsMatch(line))
            return GitHubNotFound;
        if (ShellStatusPattern().Match(line) is not { Success: true } status)
            return null;

        string code = status.Groups["code"].Value;
        if (code == "404")
            return gh ? GitHubNotFound : null;

        Uri? host = TracePath.Urls(line).FirstOrDefault() ?? named;
        if (host is null)
            return gh ? $"HTTP {code}" : null;

        return TracePath.IsLoopback(host) ? null : $"HTTP {code}";
    }

    private static Hit? FromFetch(TracedCall call, string error)
    {
        if (FetchStatusPattern().Match(error) is not { Success: true } status)
            return null;

        Uri? url = call.Url is { } written && Uri.TryCreate(written.Trim(), UriKind.Absolute, out Uri? parsed) ? parsed : TracePath.Urls(error).FirstOrDefault();
        if (url is not null && TracePath.IsLoopback(url))
            return null;

        string code = status.Groups["code"].Value;
        string? where = Short(call.Url ?? url?.OriginalString);
        if (code != "404")
            return new Hit(EnvironmentIssueKind.Auth, $"HTTP {code}", where, call.Line);

        // A 404 on a public site is usually a path the agent guessed; on GitHub it is also how a private repository refuses a stranger.
        return url is not null && TracePath.IsGitHub(url) ? new Hit(EnvironmentIssueKind.Auth, GitHubNotFound, where, call.Line) : null;
    }

    private static string CredentialWhat(string marker) => marker.ToLowerInvariant() switch
    {
        "gh auth login" => "gh is not logged in (gh auth login)",
        "could not read username" => "git could not read a username (no credentials)",
        "bad credentials" => "Bad credentials",
        "requires authentication" => "Requires authentication",
        "authentication failed" => "Authentication failed",
        _ => "Permission denied (publickey)",
    };

    // A path that does not exist counts only when it is the job's cwd or inside a read dir: anything else is the agent guessing a path.
    private static string? Unreachable(string path, string cwd, string[] readDirs)
    {
        string full = TracePath.Normalize(path, cwd);
        bool ours = TracePath.Same(full, TracePath.Normalize(cwd, cwd)) || readDirs.Any(dir => TracePath.IsUnder(full, TracePath.Normalize(dir, cwd)));
        return ours ? full : null;
    }

    private static List<Hit> FromReport(ClaustrumReport? report)
    {
        List<Hit> hits = [];
        if (report?.Data is not { ValueKind: JsonValueKind.Object } data)
            return hits;

        // A red gate the role reports itself; test-runner output in the log is never parsed (it is a log of other programs).
        if (data.TryGetProperty("failed", out JsonElement failed))
        {
            if (failed.ValueKind == JsonValueKind.Array)
                hits.AddRange(failed.EnumerateArray().Select(test => new Hit(EnvironmentIssueKind.TestGateRed, "the report lists failed tests", Short(TestName(test)), 0)));
            else if (failed.ValueKind == JsonValueKind.Number && failed.TryGetDecimal(out decimal count) && count > 0)
                hits.Add(new Hit(EnvironmentIssueKind.TestGateRed, "the report lists failed tests", null, 0, (int)Math.Min(count, int.MaxValue)));
        }

        int blockers = hits.Count;
        if (data.TryGetProperty("blocked_by", out JsonElement blockedBy))
            hits.AddRange(BlockersOf(blockedBy));

        // `blocked_by` says what stopped the role; a bare `"status": "blocked"` still says that something did.
        if (hits.Count == blockers && TextOf(data, "status") is { } status && status.Equals("blocked", StringComparison.OrdinalIgnoreCase))
            hits.Add(new Hit(EnvironmentIssueKind.ReportedBlocker, "the report says \"status\": \"blocked\"", null, 0));

        return hits;
    }

    private static IEnumerable<Hit> BlockersOf(JsonElement blockedBy)
    {
        IEnumerable<JsonElement> entries = blockedBy.ValueKind == JsonValueKind.Array ? blockedBy.EnumerateArray() : [blockedBy];
        foreach (JsonElement entry in entries)
        {
            string? what = entry.ValueKind == JsonValueKind.Object ? TextOf(entry, "what") : entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
            string? where = entry.ValueKind == JsonValueKind.Object ? TextOf(entry, "where") : null;
            if (Short(what) is { } said)
                yield return new Hit(EnvironmentIssueKind.ReportedBlocker, said, Short(where), 0);
        }
    }

    private static string? TestName(JsonElement test) => test.ValueKind switch
    {
        JsonValueKind.String => test.GetString(),
        JsonValueKind.Object => TextOf(test, "test") ?? TextOf(test, "name"),
        _ => null,
    };

    private static string? TextOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static string? Target(TracedCall call) => Short(call.Command ?? call.Path ?? call.Url);

    // One line of at most TextChars: a command can run to a heredoc, and the receipt is read by a human.
    private static string? Short(string? text)
    {
        string? line = text?.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0);
        return line is null ? null : line.Length <= TextChars ? line : $"{line[..(TextChars - 1)]}…";
    }

    // opencode's own refusal, the whole error: `Permission denied: external_directory`, `Permission denied: shell` (measured 2026-10-29).
    [GeneratedRegex(@"^Permission denied: (?<name>[A-Za-z_]+)$")]
    private static partial Regex RefusalPattern();

    // opencode: `No tool named "execute" is currently available…`; claude: `<tool_use_error>Error: No such tool available: bash. …`.
    [GeneratedRegex("""No tool named "(?<name>[^"]+)"|No such tool available: (?<name>[A-Za-z0-9_\-]+)""")]
    private static partial Regex MissingToolPattern();

    [GeneratedRegex("No such file or directory|does not exist|File not found|cannot find the path", RegexOptions.IgnoreCase)]
    private static partial Regex NotFoundPattern();

    // gh `HTTP 404: Not Found (…)` and `gh: Not Found (HTTP 404)`, curl -i `HTTP/2 403`, git and curl -f `returned error: 403`.
    [GeneratedRegex(@"^\s*HTTP(?:/[\d.]+)?\s+(?<code>40[134])\b|\(HTTP (?<code>40[134])\)|returned error:\s*(?<code>40[134])\b")]
    private static partial Regex ShellStatusPattern();

    [GeneratedRegex(@"^\s*GraphQL: Could not resolve to an? ")]
    private static partial Regex GhNotFoundPattern();

    // opencode's webfetch: `StatusCode: non 2xx status code (404 GET https://…)`.
    [GeneratedRegex(@"status code\s*\(?\s*(?<code>40[134])\b|\bHTTP(?:/[\d.]+)?\s+(?<code>40[134])\b|\b(?<code>40[134])\s+(?:Unauthorized|Forbidden|Not Found)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FetchStatusPattern();

    [GeneratedRegex(@"gh auth login|Bad credentials|Requires authentication|could not read Username|Authentication failed|Permission denied \(publickey\)", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"PermissionError: \[Errno 13\][^\n']*(?:'(?<path>[^']+)')?")]
    private static partial Regex ErrnoPattern();

    [GeneratedRegex("permission denied while trying to connect to the Docker daemon", RegexOptions.IgnoreCase)]
    private static partial Regex DockerPattern();

    // Line 0 marks what the report said: its `first` is `report`.
    private sealed record Hit(EnvironmentIssueKind Kind, string What, string? Where, int Line, int Count = 1)
    {
        public string First => Line > 0 ? $"stdout.log:{Line.ToString(CultureInfo.InvariantCulture)}" : ReportSource;
    }
}
