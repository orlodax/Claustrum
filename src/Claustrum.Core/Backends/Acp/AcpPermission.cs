using System.Text.Json;

namespace Claustrum.Core.Backends.Acp;

// The answer to an agent's `session/request_permission`. A backup, never the enforcement point: the deny list is the harness's own
// config (opencode's `deny` rules), because a harness in an auto mode never asks (measured 2026-10-16, NOTES.md "ACP as a transport",
// round 3). What is asked is allowed the way the native backend's `--auto` allows it, unless a deny pattern names the command.
internal static class AcpPermission
{
    private static readonly string[] separators = ["&&", "||", ";", "|", "&", "\n"];

    public static string Answer(JsonElement id, JsonElement? parameters, IReadOnlyList<string> deny)
    {
        JsonElement toolCall = AcpJson.ObjectOf(parameters ?? default, "toolCall") ?? default;
        string? command = AcpJson.StringOf(AcpJson.ObjectOf(toolCall, "rawInput") ?? default, "command") ?? AcpJson.StringOf(toolCall, "title");
        bool denied = command is not null && IsDenied(command, deny);

        List<(string Id, string Kind)> options = OptionsOf(parameters ?? default);
        string[] wanted = denied ? ["reject_once", "reject_always"] : ["allow_once", "allow_always"];
        string? chosen = wanted.SelectMany(kind => options.Where(option => option.Kind == kind)).Select(option => option.Id).FirstOrDefault();

        // A refused call never falls back to "the first option", which could be an allow.
        chosen ??= denied || options.Count == 0 ? null : options[0].Id;
        return chosen is null ? AcpWriter.PermissionCancelled(id) : AcpWriter.PermissionSelected(id, chosen);
    }

    /// <summary>
    /// A pattern names a command when the command, or one of its <c>&amp;&amp;</c> <c>||</c> <c>;</c> <c>|</c> <c>&amp;</c> or newline
    /// segments, is the pattern or starts with it and a space; a trailing <c>" *"</c> is that same match, a trailing <c>*</c> a plain prefix
    /// (docs/MANUAL.md §13's deny semantics). Like the harnesses' own rules, a guard on how a command is written, not a sandbox.
    /// </summary>
    public static bool IsDenied(string command, IReadOnlyList<string> deny)
    {
        if (deny.Count == 0)
            return false;

        string[] segments = [command.Trim(), .. command.Split(separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        return segments.Any(segment => deny.Any(pattern => Names(pattern.Trim(), segment)));
    }

    private static bool Names(string pattern, string segment)
    {
        if (pattern.EndsWith(" *", StringComparison.Ordinal))
            pattern = pattern[..^2];
        else if (pattern.EndsWith('*'))
            return segment.StartsWith(pattern[..^1], StringComparison.Ordinal);

        return pattern.Length > 0 && (segment == pattern || segment.StartsWith($"{pattern} ", StringComparison.Ordinal));
    }

    private static List<(string Id, string Kind)> OptionsOf(JsonElement parameters)
    {
        List<(string Id, string Kind)> options = [];
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("options", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            return options;

        foreach (JsonElement option in list.EnumerateArray())
        {
            if (AcpJson.StringOf(option, "optionId") is { } optionId)
                options.Add((optionId, AcpJson.StringOf(option, "kind") ?? ""));
        }

        return options;
    }
}
