using System.CommandLine;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;

namespace Claustrum.Cli;

// docs/PLAN.md §A5 `claustrum backends list|doctor [name]`; doctor also prints the merged config
// with the winning layer per key (Config.Origins), per the builder brief item 3.
public static class BackendsCommands
{
    public static Command Build()
    {
        Command list = new("list", "List registered backends.");
        list.SetAction(_ => List());

        Argument<string?> name = new("name") { Description = "Only check this backend.", Arity = ArgumentArity.ZeroOrOne };
        Command doctor = new("doctor", "Check backend availability and print the merged config.") { name };
        doctor.SetAction(async parseResult => await DoctorAsync(parseResult.GetValue(name)));

        return new Command("backends", "Inspect registered backends.") { list, doctor };
    }

    private static int List()
    {
        foreach (IBackend backend in AppServices.Backends.All)
            Console.WriteLine(backend.Name);

        return ExitCodes.Ok;
    }

    private static async Task<int> DoctorAsync(string? name)
    {
        IReadOnlyCollection<IBackend> targets = AppServices.Backends.All;
        if (name is not null)
        {
            if (!AppServices.Backends.TryGet(name, out IBackend? backend))
            {
                Console.Error.WriteLine($"unknown backend '{name}'");
                return ExitCodes.Usage;
            }

            targets = [backend];
        }

        Config config = Config.Load(AppServices.Platform, Environment.CurrentDirectory);

        foreach (IBackend backend in targets)
        {
            BackendConfig? backendConfig = config.Merged.Backends?.GetValueOrDefault(backend.Name);
            Doctor doctor = await backend.DetectAsync(backendConfig, CancellationToken.None);
            Console.WriteLine($"{backend.Name}:");
            Console.WriteLine($"  found:   {doctor.Found}");
            Console.WriteLine($"  path:    {doctor.Path ?? "-"}");
            Console.WriteLine($"  version: {doctor.Version ?? "-"}");
            foreach (string problem in doctor.Problems)
                Console.WriteLine($"  problem: {problem}");
        }

        Console.WriteLine();
        Console.WriteLine("merged config:");
        PrintMergedConfig(config);

        return ExitCodes.Ok;
    }

    private static void PrintMergedConfig(Config config)
    {
        foreach (KeyValuePair<string, string> entry in config.Merged.Models ?? [])
            PrintLine(config, $"models.{entry.Key}", entry.Value);

        foreach ((string roleName, RoleSettings settings) in config.Merged.Roles ?? [])
        {
            if (settings.Model is not null)
                PrintLine(config, $"roles.{roleName}.model", settings.Model);
            if (settings.Effort is not null)
                PrintLine(config, $"roles.{roleName}.effort", settings.Effort);
            if (settings.Permission is not null)
                PrintLine(config, $"roles.{roleName}.permission", settings.Permission);
            if (settings.Deny is { Length: > 0 })
                PrintLine(config, $"roles.{roleName}.deny", string.Join(", ", settings.Deny));
        }

        foreach ((string backendName, BackendConfig backendConfig) in config.Merged.Backends ?? [])
        {
            if (backendConfig.Path is not null)
                PrintLine(config, $"backends.{backendName}.path", backendConfig.Path);
            if (backendConfig.Injection is not null)
                PrintLine(config, $"backends.{backendName}.injection", backendConfig.Injection);
        }

        if (config.Merged.Defaults is { } defaults)
        {
            if (defaults.TimeoutSeconds is { } timeoutSeconds)
                PrintLine(config, "defaults.timeout_seconds", timeoutSeconds.ToString());
            if (defaults.BudgetUsd is { } budgetUsd)
                PrintLine(config, "defaults.budget_usd", budgetUsd.ToString());
            if (defaults.EnvPassthrough is { } envPassthrough)
                PrintLine(config, "defaults.env_passthrough", envPassthrough);
        }

        if (config.Merged.Jobs?.KeepLast is { } keepLast)
            PrintLine(config, "jobs.keep_last", keepLast.ToString());
    }

    private static void PrintLine(Config config, string key, string value)
    {
        string layer = config.Origins.TryGetValue(key, out ConfigLayer found) ? found.ToString() : nameof(ConfigLayer.Builtin);
        Console.WriteLine($"  {key} = {value}  [{layer}]");
    }
}
