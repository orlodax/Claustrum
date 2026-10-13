using System.Globalization;
using Claustrum.Core.Config;
using Claustrum.Roles.Model;

namespace Claustrum.Roles.Sync;

/// <summary>What `claustrum.json` decided for one agent file: the harness model, the effort, the extra deny list, and why.</summary>
public sealed record TierChoice(string Model, string Effort, string[] Deny, string Reason);

/// <summary>
/// The `claustrum.json` half of `sync` (#53): which model a harness's agent file names for a tier, and
/// the config-layer `roles.&lt;role&gt;.effort|deny` that `run` applies. A null <paramref name="config"/>
/// is the library's own hard-coded defaults. Why it mirrors `run` and where it differs: NOTES.md
/// "sync reads claustrum.json".
/// </summary>
public sealed class SyncSettings(Config? config)
{
    /// <summary>
    /// Chases the model class of <paramref name="tier"/> (named <paramref name="tierName"/>), or
    /// `roles.&lt;role&gt;.model`, through the `models` aliases, as `Config.Resolve` does. The result is the resolved id only when it lands on <paramref name="harness"/>;
    /// any other backend (an `api:` model in a Claude agent file) cannot be expressed there, so
    /// <paramref name="defaultModel"/> of the tier's class stands and the reason says so.
    /// </summary>
    public TierChoice Choose(string harness, string role, string tierName, RoleTier tier, Func<string, string> defaultModel)
    {
        RoleSettings? settings = config?.Merged.Roles?.GetValueOrDefault(role);
        // A tier stub is named for its effort, so `roles.<role>.effort` re-points the base agent only.
        string effort = tierName == "high" ? settings?.Effort ?? tier.Effort : tier.Effort;
        string[] deny = settings?.Deny ?? [];

        if (config is null)
            return new TierChoice(defaultModel(tier.Model), effort, deny, $"default for class '{tier.Model}'");

        string spec = settings?.Model ?? tier.Model;
        bool alias = config.Merged.Models?.ContainsKey(spec) == true;
        // A tier class that is no alias (and no `backend:id`) is not resolvable: `run` would hand it to
        // Claude as a literal id, but a typo in a local role.json must not be written into an agent file.
        if (settings?.Model is null && !alias && !spec.Contains(':', StringComparison.Ordinal))
            return new TierChoice(defaultModel(tier.Model), effort, deny, $"default for class '{tier.Model}' (no alias)");

        (string backend, string id) = config.ResolveModel(spec);
        string key = settings?.Model is null ? $"models.{spec}" : $"roles.{role}.model";
        string layer = config.Origins.TryGetValue(key, out ConfigLayer found) ? found.ToString().ToLower(CultureInfo.InvariantCulture) : "builtin";
        string via = (settings?.Model is null ? "" : $"roles.{role}.model = ") + (alias ? $"alias '{spec}' → " : "") + $"{backend}:{id} ({layer})";

        return backend == harness
            ? new TierChoice(id, effort, deny, via)
            : new TierChoice(defaultModel(tier.Model), effort, deny, $"default for class '{tier.Model}' ({via} cannot be used in {harness} agent files)");
    }

    /// <summary>`--dry-run`'s per-agent line: the model written and why.</summary>
    public static void RecordModel(SyncAccumulator into, string harness, string agent, string model, string reason) =>
        into.Decisions.Add($"{harness} {agent}: model {model} — {reason}");

    /// <summary>
    /// A harness with no per-command deny syntax cannot carry `claustrum.json`'s `deny`; said once per role
    /// so the file's silence is not read as "enforced". Only `claustrum run` enforces it there.
    /// </summary>
    public static void RecordUnrenderedDeny(SyncAccumulator into, string harness, string role, TierChoice choice)
    {
        if (choice.Deny.Length > 0)
            into.Decisions.Add($"{harness} {role}: claustrum.json deny ({string.Join(", ", choice.Deny)}) not rendered — {harness} agent files have no per-command deny; only `claustrum run` enforces it");
    }
}
