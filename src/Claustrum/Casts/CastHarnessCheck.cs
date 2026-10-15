using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Roles;

namespace Claustrum.Casts;

// The cast-creation half of DelegateEngine.RequireSupportedHarness (#43): a free-form answer that
// lands a role on a harness its role.json does not list is refused here, before it is saved and
// committed, instead of on every later delegation. Same rule — only registered backends are judged —
// but the alias chase uses this machine's claustrum.json, so `run` stays the authoritative check.
public static class CastHarnessCheck
{
    /// <summary>
    /// Null when <paramref name="modelSpec"/> may play <paramref name="role"/>, otherwise the reason it may not. A fallback (#126) lands on the
    /// same harnesses the primary does, since it plays the same role.
    /// </summary>
    public static string? Problem(string role, string? modelSpec, RoleLibrary library, BackendRegistry backends, Config config, string cwd, bool fallback = false)
    {
        if (string.IsNullOrWhiteSpace(modelSpec))
            return null;

        string backend = config.ResolveModelBackend(modelSpec);
        if (!backends.TryGet(backend, out _))
            return null;

        string[] harnesses = library.LoadRole(role, cwd).Definition.Harnesses;
        if (harnesses.Contains(backend, StringComparer.Ordinal))
            return null;

        string otherwise = fallback ? $", or '{CastBuilder.NoFallback}'" : role switch
        {
            Cast.ArchitectRole => $", or '{CastArchitect.Host}'",
            "builder" => "",
            _ => $", or '{CastBuilder.NotNeeded}'",
        };
        return $"'{modelSpec}' puts {(fallback ? $"{role}'s fallback" : role)} on '{backend}', but {role} runs on {string.Join(", ", harnesses)} only "
            + $"(its role.json `harnesses`) — answer an alias that lands on one of those{otherwise}";
    }

    public static void Require(Cast cast, RoleLibrary library, BackendRegistry backends, Config config, string cwd)
    {
        List<string> problems = [];
        if (Problem(Cast.ArchitectRole, cast.Architect.Model, library, backends, config, cwd) is { } architect)
            problems.Add(architect);
        if (Problem(Cast.ArchitectRole, cast.Architect.Fallback, library, backends, config, cwd, fallback: true) is { } architectFallback)
            problems.Add(architectFallback);

        foreach ((string role, CastRoleEntry? entry) in cast.Roles)
        {
            if (Problem(role, entry?.Model, library, backends, config, cwd) is { } problem)
                problems.Add(problem);
            if (Problem(role, entry?.Fallback, library, backends, config, cwd, fallback: true) is { } fallbackProblem)
                problems.Add(fallbackProblem);
        }

        if (problems.Count > 0)
            throw new CastException($"cast '{cast.Name}' not saved: {string.Join("; ", problems)}");
    }
}
