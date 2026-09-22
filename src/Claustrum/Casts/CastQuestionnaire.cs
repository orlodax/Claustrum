using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Roles;

namespace Claustrum.Casts;

// docs/PLAN.md §D2: `claustrum cast questions` / MCP `cast_questions`. Dependencies are passed in
// (not read from AppServices) so this is unit-testable against fake backends instead of a real
// `claude` install.
public static class CastQuestionnaire
{
    public const string MaxParallelKey = "builder_max_parallel";

    public static async Task<CastQuestionnaireResult> BuildAsync(RoleLibrary roleLibrary, BackendRegistry backends, Config config, string cwd, CancellationToken cancellationToken)
    {
        HashSet<string> availableBackends = [];
        foreach (IBackend backend in backends.All)
        {
            BackendConfig? backendConfig = config.Merged.Backends?.GetValueOrDefault(backend.Name);
            Doctor doctor = await backend.DetectAsync(backendConfig, cancellationToken);
            if (doctor.Found)
                availableBackends.Add(backend.Name);
        }

        string[] modelOptions = [.. (config.Merged.Models ?? [])
            .Where(entry => availableBackends.Contains(config.ResolveModelBackend(entry.Key)))
            .Select(entry => entry.Key)
            .OrderBy(name => name, StringComparer.Ordinal)];

        List<CastQuestion> questions = [];
        HashSet<string> keys = [];

        // The fixed architect question and the per-role loop below both mint keys from the same
        // namespace; a role named "budget" would silently steal a fixed question's answer instead of
        // getting its own (review finding #9). Routing every add through this local makes that
        // collision impossible rather than merely unlikely.
        void AddQuestion(CastQuestion question)
        {
            if (!keys.Add(question.Key))
                throw new CastException($"cast questionnaire has two questions with key '{question.Key}'");

            questions.Add(question);
        }

        // This IS the architect role's question (docs/PLAN.md §D2 "architect (host | spawned on …)"),
        // not a second question that happens to share its key — which is why the loop below skips
        // that role instead of asking about it twice.
        AddQuestion(new CastQuestion(
            Key: Cast.ArchitectRole,
            Prompt: $"{Cast.ArchitectRole}: how should it run? '{CastArchitect.Host}' = the agent you're chatting with adopts the role; "
                + $"'{CastBuilder.SpawnedOn}<model>' = `claustrum coordinate` runs it headlessly on that model.",
            Options: [CastArchitect.Host, .. modelOptions.Select(model => CastBuilder.SpawnedOn + model)],
            AllowNotNeeded: false,
            AllowFreeForm: true));

        // Every role.json in the library gets a question, "builder" excepted (§D2: "'not needed' for
        // every role but builder") — so a role added by a later milestone (ui-reviewer, M3) is asked
        // about automatically, with no change needed here.
        foreach (string role in roleLibrary.ListRoles())
        {
            if (role == Cast.ArchitectRole)
                continue;

            AddQuestion(new CastQuestion(
                Key: role,
                Prompt: $"{role}: which model should play this role? A claustrum.json alias, or a free-form 'backend:model-id'.",
                Options: modelOptions,
                AllowNotNeeded: role != "builder",
                AllowFreeForm: true));
        }

        // docs/PLAN.md §D2 asks for "builder model + max_parallel" — without this question the only
        // way to reach worktree isolation was hand-editing the cast JSON, since CastBuilder never
        // set MaxParallel (review finding). Builder-only: it is the one role a cast fans out.
        AddQuestion(new CastQuestion(
            Key: MaxParallelKey,
            Prompt: "builder: how many builders may run at once? More than 1 gives each job its own "
                + "git worktree and branch (docs/PLAN.md §D4). '1' keeps every run in the repo itself.",
            Options: ["1", "2", "3"],
            AllowNotNeeded: false,
            AllowFreeForm: true));

        AddQuestion(new CastQuestion(
            Key: "budget",
            Prompt: $"Budget cap in USD for this cast, or '{CastBuilder.NoCap}' for unlimited.",
            Options: [CastBuilder.NoCap],
            AllowNotNeeded: false,
            AllowFreeForm: true));

        return new CastQuestionnaireResult([.. questions], CastStore.ListNames(cwd));
    }
}
