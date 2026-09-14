using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Roles;

namespace Claustrum.Casts;

// docs/PLAN.md §D2: `claustrum cast questions` / MCP `cast_questions`. Dependencies are passed in
// (not read from AppServices) so this is unit-testable against fake backends instead of a real
// `claude` install.
public static class CastQuestionnaire
{
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

        // The fixed "architect" mode question and the per-role loop below both mint keys from the
        // same namespace; a role literally named "architect" (or "budget") would silently steal the
        // fixed question's answer instead of getting its own (review finding #9). Routing every add
        // through this local makes that collision impossible rather than merely unlikely.
        void AddQuestion(CastQuestion question)
        {
            if (!keys.Add(question.Key))
                throw new CastException($"cast questionnaire has two questions with key '{question.Key}'");

            questions.Add(question);
        }

        AddQuestion(new CastQuestion(
            Key: "architect",
            Prompt: "Architect: how should it run? Only 'host' (the agent you're chatting with adopts the role) " +
                "works today — a spawned, headless architect lands in a later milestone.",
            Options: ["host"],
            AllowNotNeeded: false,
            AllowFreeForm: false));

        // Every role.json in the library gets a question, "builder" excepted (§D2: "'not needed' for
        // every role but builder") — so a role added by a later milestone (ui-reviewer, M3) is asked
        // about automatically, with no change needed here.
        foreach (string role in roleLibrary.ListRoles())
        {
            AddQuestion(new CastQuestion(
                Key: role,
                Prompt: $"{role}: which model should play this role? A claustrum.json alias, or a free-form 'backend:model-id'.",
                Options: modelOptions,
                AllowNotNeeded: role != "builder",
                AllowFreeForm: true));
        }

        AddQuestion(new CastQuestion(
            Key: "budget",
            Prompt: $"Budget cap in USD for this cast, or '{CastBuilder.NoCap}' for unlimited.",
            Options: [CastBuilder.NoCap],
            AllowNotNeeded: false,
            AllowFreeForm: true));

        return new CastQuestionnaireResult([.. questions], CastStore.ListNames(cwd));
    }
}
