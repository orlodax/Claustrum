using System.Globalization;

namespace Claustrum.Casts;

// Turns questionnaire answers (docs/PLAN.md §D2 — one string per CastQuestion.Key) into a Cast.
// Pure and AppServices-free: `library` is the caller's AppServices.RoleLibrary.Version, not read
// here, so this stays unit-testable without the embedded role library.
public static class CastBuilder
{
    public const string NotNeeded = "not needed";
    public const string NoCap = "no cap";

    /// <summary>The architect answer that names a model to spawn on: `spawned on claude:opus`.</summary>
    public const string SpawnedOn = "spawned on ";

    // `roleNames` is the same list CastQuestionnaire asked about (callers pass
    // AppServices.RoleLibrary.ListRoles()), not a hardcoded builder/code-reviewer/tester trio —
    // that hardcoding silently dropped a later milestone's role answer (review finding #9).
    public static Cast FromAnswers(string name, string library, IReadOnlyList<string> roleNames, IReadOnlyDictionary<string, string> answers)
    {
        CastArchitect architect = ParseArchitectAnswer(answers.GetValueOrDefault(Cast.ArchitectRole));

        int? maxParallel = ParseMaxParallelAnswer(answers.GetValueOrDefault(CastQuestionnaire.MaxParallelKey));

        Dictionary<string, CastRoleEntry?> roles = [];
        foreach (string role in roleNames)
        {
            // The architect role's answer is the fixed host/spawned question (CastQuestionnaire asks
            // it once, before the per-role ones) and lands in Cast.Architect, never in Cast.Roles.
            if (role == Cast.ArchitectRole)
                continue;

            CastRoleEntry? entry = ParseRoleAnswer(answers.GetValueOrDefault(role));

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

    // 1 (or an unanswered question) means "no isolation", which CastRoleEntry spells as null rather
    // than 1 so `cast show` does not imply a setting the user never made.
    private static int? ParseMaxParallelAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Equals(NotNeeded, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 1)
            throw new CastException($"max_parallel answer '{answer}' is not a whole number of 1 or more");

        return value > 1 ? value : null;
    }

    private static decimal? ParseBudgetAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Equals(NoCap, StringComparison.OrdinalIgnoreCase))
            return null;

        return decimal.TryParse(answer, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : throw new CastException($"budget answer '{answer}' is neither a number nor '{NoCap}'");
    }
}
