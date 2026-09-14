using System.CommandLine;
using Claustrum.Roles;
using Claustrum.Roles.Model;

namespace Claustrum.Cli;

// docs/PLAN.md §A5 `claustrum roles list|show <name>`.
public static class RolesCommands
{
    public static Command Build()
    {
        Command list = new("list", "List every role in the library.");
        list.SetAction(_ => List());

        Argument<string> name = new("name") { Description = "Role name." };
        Command show = new("show", "Show one role's metadata.") { name };
        show.SetAction(parseResult => Show(parseResult.GetRequiredValue(name)));

        return new Command("roles", "Inspect the embedded role library.") { list, show };
    }

    private static int List()
    {
        string cwd = Environment.CurrentDirectory;
        foreach (string role in AppServices.RoleLibrary.ListRoles())
        {
            RoleDefinition definition = AppServices.RoleLibrary.LoadRole(role, cwd).Definition;
            Console.WriteLine($"{role,-16} {(definition.Blind ? "[blind] " : "")}{definition.Description}");
        }

        return ExitCodes.Ok;
    }

    private static int Show(string role)
    {
        try
        {
            RoleDefinition definition = AppServices.RoleLibrary.LoadRole(role, Environment.CurrentDirectory).Definition;
            Console.WriteLine($"name:        {definition.Name}");
            Console.WriteLine($"description: {definition.Description}");
            Console.WriteLine($"blind:       {definition.Blind}");
            Console.WriteLine($"permission:  {definition.Permission}");
            Console.WriteLine($"deny:        {string.Join(", ", definition.Deny)}");
            Console.WriteLine($"mayDelegate: {string.Join(", ", definition.MayDelegate)}");
            Console.WriteLine($"report:      {definition.Report}");
            Console.WriteLine($"harnesses:   {string.Join(", ", definition.Harnesses)}");
            Console.WriteLine("tiers:");
            foreach ((string tierName, RoleTier tier) in definition.Tiers)
                Console.WriteLine($"  {tierName,-8} model={tier.Model} effort={tier.Effort}");

            return ExitCodes.Ok;
        }
        catch (RoleRenderException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Usage;
        }
    }
}
