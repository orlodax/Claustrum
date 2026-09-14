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

        Dictionary<string, CastRoleEntry?> roles = [];
        foreach (string role in roleNames)
            roles[role] = ParseRoleAnswer(answers.GetValueOrDefault(role));

        return new Cast(name, library, new CastArchitect(architectMode), roles, ParseBudgetAnswer(answers.GetValueOrDefault("budget")));
    }

    // A bare model/alias string ("claude:opus", "cheap-coding") becomes CastRoleEntry.Model; the
    // grammar already carries an optional backend prefix (docs/PLAN.md §A7), so there is nothing
    // else to split out here.
    private static CastRoleEntry? ParseRoleAnswer(string? answer) =>
        string.IsNullOrWhiteSpace(answer) || answer.Equals(NotNeeded, StringComparison.OrdinalIgnoreCase)
            ? null
            : new CastRoleEntry(Model: answer, Backend: null, Tier: null);

    private static decimal? ParseBudgetAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Equals(NoCap, StringComparison.OrdinalIgnoreCase))
            return null;

        return decimal.TryParse(answer, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : throw new CastException($"budget answer '{answer}' is neither a number nor '{NoCap}'");
    }
}
