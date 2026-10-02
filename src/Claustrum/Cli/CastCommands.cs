using System.CommandLine;
using System.Text.Json;
using Claustrum.Casts;
using Claustrum.Core.Config;

namespace Claustrum.Cli;

// docs/PLAN.md §D5 `claustrum cast new|questions|create --answers <file>|list|show <name>|use
// <name>`.
public static class CastCommands
{
    public static Command Build()
    {
        Option<bool> json = new("--json") { Description = "Emit the questionnaire as one JSON document." };
        Command questions = new("questions", "List the cast questionnaire (docs/PLAN.md §D2).") { json };
        questions.SetAction(async parseResult => await QuestionsAsync(parseResult.GetValue(json)));

        Option<string> answersPath = new("--answers") { Description = "Path to a JSON file mapping question key -> answer.", Required = true };
        Option<string> createName = new("--name") { Description = "Cast name.", DefaultValueFactory = _ => CastStore.DefaultName };
        Command create = new("create", "Create a cast from answered questions.") { answersPath, createName };
        create.SetAction(parseResult => Create(parseResult.GetRequiredValue(answersPath), parseResult.GetValue(createName) ?? CastStore.DefaultName));

        Option<string> newName = new("--name") { Description = "Cast name.", DefaultValueFactory = _ => CastStore.DefaultName };
        Command @new = new("new", "Ask the cast questionnaire on this terminal (docs/PLAN.md §D2 TTY fallback).") { newName };
        @new.SetAction(async parseResult => await NewAsync(parseResult.GetValue(newName) ?? CastStore.DefaultName));

        Command list = new("list", "List saved casts.");
        list.SetAction(_ => List());

        Argument<string> showName = new("name") { Description = "Cast name." };
        Command show = new("show", "Show one cast as JSON.") { showName };
        show.SetAction(parseResult => Show(parseResult.GetRequiredValue(showName)));

        Argument<string> useName = new("name") { Description = "Cast name to make the default." };
        Command use = new("use", "Copy an existing cast over .claustrum/casts/default.json.") { useName };
        use.SetAction(parseResult => Use(parseResult.GetRequiredValue(useName)));

        return new Command("cast", "Manage casts: who plays which role (docs/PLAN.md §D).") { questions, create, @new, list, show, use };
    }

    private static async Task<int> QuestionsAsync(bool jsonMode)
    {
        string cwd = Environment.CurrentDirectory;
        Config config = Config.Load(AppServices.Platform, cwd);
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(AppServices.RoleLibrary, AppServices.Backends, config, cwd, CancellationToken.None);

        if (jsonMode)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, CastJsonContext.Default.CastQuestionnaireResult));
            return ExitCodes.Ok;
        }

        if (result.ExistingCasts.Length > 0)
            Console.WriteLine($"existing casts: {string.Join(", ", result.ExistingCasts)} (reuse one instead?)");

        foreach (CastQuestion question in result.Questions)
        {
            Console.WriteLine($"{question.Key}: {question.Prompt}");
            Console.WriteLine(OptionsLine(question));
        }

        return ExitCodes.Ok;
    }

    // Same questions as `cast questions`/MCP `cast_questions`, asked directly on this terminal for
    // people working outside any chat (docs/PLAN.md §D2 "Interactive TTY fallback").
    private static async Task<int> NewAsync(string name)
    {
        string cwd = Environment.CurrentDirectory;
        Config config = Config.Load(AppServices.Platform, cwd);
        CastQuestionnaireResult questionnaire = await CastQuestionnaire.BuildAsync(AppServices.RoleLibrary, AppServices.Backends, config, cwd, CancellationToken.None);

        if (questionnaire.ExistingCasts.Length > 0)
            Console.WriteLine($"existing casts: {string.Join(", ", questionnaire.ExistingCasts)}");

        Dictionary<string, string> answers = [];
        foreach (CastQuestion question in questionnaire.Questions)
        {
            Console.WriteLine(question.Prompt);
            Console.WriteLine(OptionsLine(question));
            answers[question.Key] = AskUntilSupported(question.Key, config, cwd);
        }

        Cast cast = CastBuilder.FromAnswers(name, AppServices.RoleLibrary.Version, AppServices.RoleLibrary.ListRoles(), answers);
        CastHarnessCheck.Require(cast, AppServices.RoleLibrary, AppServices.Backends, config, cwd);
        CastStore.Save(cwd, cast);

        Console.WriteLine(CastStore.PathFor(cwd, name));
        return ExitCodes.Ok;
    }

    private static int Create(string answersPath, string name)
    {
        string cwd = Environment.CurrentDirectory;
        Dictionary<string, string> answers = CastAnswers.Read(answersPath);
        Cast cast = CastBuilder.FromAnswers(name, AppServices.RoleLibrary.Version, AppServices.RoleLibrary.ListRoles(), answers);
        CastHarnessCheck.Require(cast, AppServices.RoleLibrary, AppServices.Backends, Config.Load(AppServices.Platform, cwd), cwd);
        CastStore.Save(cwd, cast);

        Console.WriteLine(CastStore.PathFor(cwd, name));
        return ExitCodes.Ok;
    }

    // An empty list is an answer too — no claustrum.json alias lands on an installed backend the role
    // runs on — and it must not hide 'not needed', which for the demo-author is the "no demo" switch.
    private static string OptionsLine(CastQuestion question)
    {
        string options = question.Options.Length > 0
            ? string.Join(", ", question.Options)
            : "none here (no claustrum.json alias lands on an installed backend this role runs on); a free-form 'backend:model-id' still works";
        return question.AllowNotNeeded ? $"  options: {options}, or '{CastBuilder.NotNeeded}'" : $"  options: {options}";
    }

    // A role answer is checked as it is typed, so a free-form one on the wrong harness is asked again
    // rather than failing the whole questionnaire at the end. Non-role keys (budget, max_parallel)
    // and the architect's host/spawned grammar are left to CastBuilder and the final Require.
    // Piped input is never re-asked: the next line was written for the next question, and asking
    // again shifted every later answer one slot (review, 2026-10-02: ui-reviewer saved as "2").
    private static string AskUntilSupported(string key, Config config, string cwd)
    {
        while (true)
        {
            Console.Write($"{key}> ");
            string answer = Console.ReadLine() ?? "";
            bool isRole = key != Cast.ArchitectRole && AppServices.RoleLibrary.ListRoles().Contains(key, StringComparer.Ordinal);
            bool notNeeded = answer.Trim().Equals(CastBuilder.NotNeeded, StringComparison.OrdinalIgnoreCase);
            if (!isRole || notNeeded || CastHarnessCheck.Problem(key, answer.Trim(), AppServices.RoleLibrary, AppServices.Backends, config, cwd) is not { } problem)
                return answer;

            if (Console.IsInputRedirected)
                throw new CastException($"cast not saved: {problem}");

            Console.WriteLine($"  {problem}");
        }
    }

    private static int List()
    {
        foreach (string name in CastStore.ListNames(Environment.CurrentDirectory))
            Console.WriteLine(name);

        return ExitCodes.Ok;
    }

    private static int Show(string name)
    {
        Cast cast = CastStore.Load(Environment.CurrentDirectory, name);
        Console.WriteLine(JsonSerializer.Serialize(cast, CastJsonContext.Default.Cast));
        return ExitCodes.Ok;
    }

    private static int Use(string name)
    {
        string cwd = Environment.CurrentDirectory;
        Cast cast = CastStore.Load(cwd, name) with { Name = CastStore.DefaultName };
        CastStore.Save(cwd, cast);

        Console.WriteLine(CastStore.PathFor(cwd, CastStore.DefaultName));
        return ExitCodes.Ok;
    }
}
