using Claustrum.Core.Config;

namespace Claustrum.Casts;

// #121's shared model-table and warning builders: cast questions and cast create/new print the same
// one-line alias → target → source rows; run/delegate reuse the warning wording so a cast saved now
// and a run later produce the same messages.
public static class CastModelLines
{
    public const string BuiltinNote = "built-in default — no claustrum.json or user config defines it";

    /// <summary>The <c>backend:model-id</c> string from a resolution.</summary>
    public static string Target(ModelResolution r) => $"{r.Backend}:{r.ModelId}";

    /// <summary>
    /// One-line description: <c>cheap-coding → claude:haiku (built-in default …)</c>.
    /// The parenthetical is <see cref="BuiltinNote"/> when the alias's first hop is a built-in
    /// default, otherwise the resolution's <see cref="ModelResolution.Source"/>.
    /// </summary>
    public static string ModelLine(string alias, ModelResolution r) =>
        r.Layer == ConfigLayer.Builtin
            ? $"{alias} → {Target(r)} ({BuiltinNote})"
            : $"{alias} → {Target(r)} ({r.Source ?? "free-form, as typed"})";

    /// <summary>Warning text for item 4's silent-builtin-default case, re-used by item 3.</summary>
    public static string BuiltinDefaultWarning(string castName, string role, string alias, string target, Config config) =>
        config.RepoPath is { } repo
            ? $"cast '{castName}' role '{role}': alias '{alias}' resolved to the built-in default {target}; no claustrum.json or user config defines it (looked in {config.UserConfigPath}, {repo})"
            : $"cast '{castName}' role '{role}': alias '{alias}' resolved to the built-in default {target}; no claustrum.json or user config defines it (looked in {config.UserConfigPath})";

    /// <summary>
    /// Warning text for item 4's "stored resolution differs from current" case: the alias resolved
    /// to one thing when the cast was created and now resolves to something else.
    /// </summary>
    public static string ResolvedChangedWarning(string castName, string role, string alias, string stored, string current, string source) =>
        $"cast '{castName}' role '{role}': alias '{alias}' resolved to {stored} when the cast was created, now to {current} ({source})";

    /// <summary>
    /// Per-role model lines and builtin-fallback warnings for <c>cast create</c>, <c>cast new</c>
    /// and <c>cast_create</c>: one line per role that carries a model, plus a warning on stderr when
    /// any hop of its resolution came from a built-in default.
    /// </summary>
    public static (string[] Lines, string[] Warnings) ForCast(Cast cast, Config config)
    {
        List<string> lines = [];
        List<string> warnings = [];

        void Add(string role, string modelAlias)
        {
            ModelResolution resolution = config.Describe(modelAlias);
            string target = Target(resolution);
            lines.Add(ModelLine(modelAlias, resolution));

            if (resolution.FromBuiltin)
                warnings.Add(BuiltinDefaultWarning(cast.Name, role, modelAlias, target, config));
        }

        foreach ((string role, CastRoleEntry? entry) in cast.Roles)
        {
            if (entry?.Model is { } roleAlias)
                Add(role, roleAlias);
        }

        if (cast.Architect.Model is { } architectAlias)
            Add(Cast.ArchitectRole, architectAlias);

        return ([.. lines], [.. warnings]);
    }
}
