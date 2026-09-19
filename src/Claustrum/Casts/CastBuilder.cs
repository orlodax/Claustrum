using System.Globalization;

namespace Claustrum.Casts;

// Turns questionnaire answers (docs/PLAN.md §D2 — one string per CastQuestion.Key) into a Cast.
// Pure and AppServices-free: `library` is the caller's AppServices.RoleLibrary.Version, not read
// here, so this stays unit-testable without the embedded role library.
public static class CastBuilder
{
    public const string NotNeeded = "not needed";
    public const string NoCap = "no cap";

    // `roleNames` is the same list CastQuestionnaire asked about (callers pass
    // AppServices.RoleLibrary.ListRoles()), not a hardcoded builder/code-reviewer/tester trio —
    // that hardcoding silently dropped a later milestone's role answer (review finding #9).
    public static Cast FromAnswers(string name, string library, IReadOnlyList<string> roleNames, IReadOnlyDictionary<string, string> answers)
    {
        // "architect" is the questionnaire's fixed mode-question key, asked once before the
        // per-role loop; a role literally named "architect" would collide with it, so this is
        // rejected here too even though CastQuestionnaire already refuses to emit that pair.
        if (roleNames.Contains("architect"))
            throw new CastException("a role named 'architect' would collide with the cast architect-mode question key");

        string architectMode = answers.TryGetValue("architect", out string? mode) && mode.Length > 0 ? mode : "host";

        int? maxParallel = ParseMaxParallelAnswer(answers.GetValueOrDefault(CastQuestionnaire.MaxParallelKey));

        Dictionary<string, CastRoleEntry?> roles = [];
        foreach (string role in roleNames)
        {
            CastRoleEntry? entry = ParseRoleAnswer(answers.GetValueOrDefault(role));

            // Only builder carries max_parallel: it is the one role a cast fans out (docs/PLAN.md
            // §D4). A cast whose builder was answered "not needed" has no entry to hang it on.
            roles[role] = role == "builder" && entry is not null ? entry with { MaxParallel = maxParallel } : entry;
        }

        return new Cast(name, library, new CastArchitect(architectMode), roles, ParseBudgetAnswer(answers.GetValueOrDefault("budget")));
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
