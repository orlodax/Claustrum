using System.Globalization;
using Claustrum.Core.Config;

namespace Claustrum.Casts;

// Turns questionnaire answers (docs/PLAN.md §D2 — one string per CastQuestion.Key) into a Cast.
// Pure and AppServices-free: `library` is the caller's AppServices.RoleLibrary.Version, not read
// here, so this stays unit-testable without the embedded role library.
// WithResolved is the one method that needs Config — it is called by the CLI/MCP commands, not by
// tests or library callers, so Config.Describe is not a dependency of FromAnswers itself.
public static class CastBuilder
{
    public const string NotNeeded = "not needed";
    public const string NoCap = "no cap";

    /// <summary>The fallback answer that names none (#126); an unanswered one means the same.</summary>
    public const string NoFallback = "none";

    /// <summary>The architect answer that names a model to spawn on: `spawned on claude:opus`.</summary>
    public const string SpawnedOn = "spawned on ";

    // `roleNames` is the same list CastQuestionnaire asked about (callers pass
    // AppServices.RoleLibrary.ListRoles()), not a hardcoded builder/code-reviewer/tester trio —
    // that hardcoding silently dropped a later milestone's role answer (review finding #9).
    public static Cast FromAnswers(string name, string library, IReadOnlyList<string> roleNames, IReadOnlyDictionary<string, string> answers)
    {
        CastArchitect architect = ParseArchitectAnswer(answers.GetValueOrDefault(Cast.ArchitectRole));
        if (architect.Mode == CastArchitect.Spawned)
            architect = architect with { Fallback = ParseFallbackAnswer(answers.GetValueOrDefault(CastQuestionnaire.FallbackKey(Cast.ArchitectRole))) };

        int? maxParallel = ParseMaxParallelAnswer(answers.GetValueOrDefault(CastQuestionnaire.MaxParallelKey));

        Dictionary<string, CastRoleEntry?> roles = [];
        foreach (string role in roleNames)
        {
            // The architect role's answer is the fixed host/spawned question (CastQuestionnaire asks
            // it once, before the per-role ones) and lands in Cast.Architect, never in Cast.Roles.
            if (role == Cast.ArchitectRole)
                continue;

            CastRoleEntry? entry = ParseRoleAnswer(answers.GetValueOrDefault(role));

            // A fallback answer to a role "not needed" has nothing to stand in for and is dropped, as is the architect's when it is `host`.
            if (entry is not null)
                entry = entry with { Fallback = ParseFallbackAnswer(answers.GetValueOrDefault(CastQuestionnaire.FallbackKey(role))) };

            // Only builder carries max_parallel: it is the one role a cast fans out (docs/PLAN.md
            // §D4). A cast whose builder was answered "not needed" has no entry to hang it on.
            roles[role] = role == "builder" && entry is not null ? entry with { MaxParallel = maxParallel } : entry;
        }

        return new Cast(name, library, architect, roles, ParseBudgetAnswer(answers.GetValueOrDefault("budget")));
    }

    // docs/PLAN.md §D2's "architect (host | spawned on …)". Unanswered means `host`: the mode that
    // needs nothing but the agent already in the room.
    private static CastArchitect ParseArchitectAnswer(string? answer)
    {
        string value = (answer ?? "").Trim();
        if (value.Length == 0 || value.Equals(CastArchitect.Host, StringComparison.OrdinalIgnoreCase))
            return new CastArchitect(CastArchitect.Host);

        if (value.Equals(CastArchitect.Spawned, StringComparison.OrdinalIgnoreCase))
            return new CastArchitect(CastArchitect.Spawned);

        if (value.StartsWith(SpawnedOn, StringComparison.OrdinalIgnoreCase) && value[SpawnedOn.Length..].Trim() is { Length: > 0 } model)
            return new CastArchitect(CastArchitect.Spawned, Model: model);

        throw new CastException(
            $"architect answer '{value}' is not '{CastArchitect.Host}', '{CastArchitect.Spawned}', or '{SpawnedOn}<model>'");
    }

    // A bare model/alias string ("claude:opus", "cheap-coding") becomes CastRoleEntry.Model; the
    // grammar already carries an optional backend prefix (docs/PLAN.md §A7), so there is nothing
    // else to split out here.
    private static CastRoleEntry? ParseRoleAnswer(string? answer) =>
        string.IsNullOrWhiteSpace(answer) || answer.Equals(NotNeeded, StringComparison.OrdinalIgnoreCase)
            ? null
            : new CastRoleEntry(Model: answer, Backend: null, Tier: null);

    private static string? ParseFallbackAnswer(string? answer) =>
        string.IsNullOrWhiteSpace(answer) || answer.Trim().Equals(NoFallback, StringComparison.OrdinalIgnoreCase) ? null : answer.Trim();

    // 1 is kept as 1: it is a cap too, and only a numeric max_parallel is gated (#58). Storing it as
    // null — the rule until 2026-10-08 — left every questionnaire cast with no cap at all. An
    // unanswered question still means null, "no cap", like a hand-written cast without the key.
    private static int? ParseMaxParallelAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Equals(NotNeeded, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 1)
            throw new CastException($"max_parallel answer '{answer}' is not a whole number of 1 or more");

        return value;
    }

    private static decimal? ParseBudgetAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Equals(NoCap, StringComparison.OrdinalIgnoreCase))
            return null;

        return decimal.TryParse(answer, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : throw new CastException($"budget answer '{answer}' is neither a number nor '{NoCap}'");
    }

    // #121: record what each role's alias (or free-form spec) resolved to at creation time, so
    // `run` can warn later when the config has moved it. Calls Config.Describe for every role and
    // the architect that carries a model, and (#126) for each fallback. A free-form 'backend:model-id'
    // resolves to itself, which is why its Resolved field matches the string the user typed.
    public static Cast WithResolved(Cast cast, Config config)
    {
        Dictionary<string, CastRoleEntry?> roles = [];
        foreach ((string role, CastRoleEntry? entry) in cast.Roles)
        {
            roles[role] = entry is null ? null : entry with
            {
                Resolved = entry.Model is { } alias ? Target(config.Describe(alias)) : entry.Resolved,
                ResolvedFallback = entry.Fallback is { } fallback ? Target(config.Describe(fallback)) : entry.ResolvedFallback,
            };
        }

        CastArchitect architect = cast.Architect with
        {
            Resolved = cast.Architect.Model is { } architectAlias ? Target(config.Describe(architectAlias)) : cast.Architect.Resolved,
            ResolvedFallback = cast.Architect.Fallback is { } architectFallback ? Target(config.Describe(architectFallback)) : cast.Architect.ResolvedFallback,
        };

        return cast with { Roles = roles, Architect = architect };
    }

    public static string Target(ModelResolution r) => $"{r.Backend}:{r.ModelId}";
}
