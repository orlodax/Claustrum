using System.Text.Json;
using Claustrum.Core.Json;
using Claustrum.Core.Model;

namespace Claustrum.Core.Backends;

// #126: what each backend's parser shares about a provider's refusal. The HTTP status wins when the backend carries one; the error's
// own type name decides otherwise. Narrow on purpose: a message is never searched, so "Agent not found" (opencode `unknown`) cannot
// pass for a missing model, and anything unplaced is Other, which is never retried.
public static class ProviderErrors
{
    public static bool IsProviderSide(ErrorKind kind) => kind is not ErrorKind.Other;

    public static ErrorKind Classify(int? status, string? type)
    {
        ErrorKind? byStatus = status switch
        {
            402 or 429 => ErrorKind.ProviderQuota,
            401 or 403 => ErrorKind.ProviderAuth,
            404 => ErrorKind.ModelNotFound,
            408 or 425 or 502 or 503 or 504 or 529 => ErrorKind.ProviderTransport,
            _ => null,
        };
        if (byStatus is { } known)
            return known;

        string name = type?.ToLowerInvariant() ?? "";
        if (HasAny(name, "quota", "credit", "billing", "rate-limit", "rate_limit", "ratelimit"))
            return ErrorKind.ProviderQuota;
        if (HasAny(name, "auth", "forbidden", "permission"))
            return ErrorKind.ProviderAuth;
        if (HasAny(name, "no-route", "model", "not_found"))
            return ErrorKind.ModelNotFound;
        if (HasAny(name, "transport", "network", "timeout", "overload", "unavailable", "server_error"))
            return ErrorKind.ProviderTransport;

        return ErrorKind.Other;
    }

    /// <summary>
    /// The one place "before any work" is decided: the run changed no file (in a tree that was actually read), it was a single turn (no
    /// human spoke to it after the first), and the backend counted its tool calls and none completed. A backend that cannot count says
    /// null and the run is never "before any work". Steps are not bounded: a tool call that errored did nothing, and the 2026-10-15 402
    /// that started #126 has a second `step_start` after its errored `subagent` call (NOTES.md "A cast can name a fallback").
    /// </summary>
    public static bool BeforeAnyWork(int? toolCallsCompleted, ChangedFile[] changedFiles, bool treeRead, bool firstTurnOnly) =>
        toolCallsCompleted == 0 && changedFiles.Length == 0 && treeRead && firstTurnOnly;

    /// <summary>The kind as the receipt writes it: <c>provider_quota</c>.</summary>
    public static string Name(ErrorKind kind) => JsonSerializer.Serialize(kind, ClaustrumJsonContext.Default.ErrorKind).Trim('"');

    private static bool HasAny(string text, params string[] needles) => needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));
}
