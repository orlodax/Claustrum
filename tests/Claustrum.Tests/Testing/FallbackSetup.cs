using System.Text.Json;

namespace Claustrum.Tests.Testing;

/// <summary>
/// Turns an <see cref="IsolatedRepo"/> into the repository of the owner's 2026-10-15 case (#126): the builder and the code-reviewer
/// (and, spawned, the architect) play on a fake opencode that is refused by its provider, with <c>claude:opus</c> — the repo's fake
/// claude — named as their fallback. Nothing paid can start: the child's PATH holds no backend and both paths are scripts.
/// </summary>
public static class FallbackSetup
{
    public const string Primary = "opencode:fake/pro";

    public const string Fallback = "claude:opus";

    /// <summary>The fake opencode's directory, one per repository.</summary>
    public static string FakeDirectory(IsolatedRepo repo) => Directory.CreateDirectory(Path.Combine(repo.Home, "fake-opencode")).FullName;

    /// <summary>
    /// Writes the fake opencode that replays <paramref name="events"/>, the claustrum.json that points both backends at scripts and the
    /// default cast. <paramref name="fallback"/> null leaves the roles without one; <paramref name="budgetUsd"/> is the cast's cap, <paramref name="costUsd"/> what the refused run billed.
    /// </summary>
    public static FakeRefusals.Opencode Apply(IsolatedRepo repo, string events, string? fallback = Fallback, decimal? budgetUsd = null, bool spawnedArchitect = false, decimal costUsd = 0m)
    {
        FakeRefusals.RequirePosixShell();
        FakeRefusals.Opencode opencode = new(FakeDirectory(repo), events, costUsd: costUsd);
        string claude = repo.ScriptPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(repo.Repo, "claustrum.json"), "{\"backends\":{\"claude\":{\"path\":\"" + claude + "\"}," + opencode.BackendJson + "}}");
        WriteCast(repo, fallback, budgetUsd, spawnedArchitect);
        return opencode;
    }

    public static void WriteCast(IsolatedRepo repo, string? fallback = Fallback, decimal? budgetUsd = null, bool spawnedArchitect = false)
    {
        string castsDirectory = Directory.CreateDirectory(Path.Combine(repo.Repo, ".claustrum", "casts")).FullName;
        string fallbackText = fallback is null ? "null" : JsonSerializer.Serialize(fallback);
        string entry = "{\"model\":\"" + Primary + "\",\"backend\":null,\"tier\":null,\"fallback\":" + fallbackText + "}";
        string architect = spawnedArchitect
            ? "{\"mode\":\"spawned\",\"model\":\"" + Primary + "\",\"tier\":null,\"fallback\":" + fallbackText + "}"
            : /*lang=json,strict*/ """{"mode":"host"}""";
        string budget = budgetUsd is { } usd ? usd.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
        File.WriteAllText(
            Path.Combine(castsDirectory, "default.json"),
            "{\"name\":\"default\",\"library\":\"1.0.0\",\"architect\":" + architect + ",\"roles\":{\"builder\":" + entry + ",\"code-reviewer\":" + entry + "},\"budget_usd\":" + budget + "}");
    }
}
