using System.CommandLine;
using System.CommandLine.Parsing;
using Claustrum.Cli;

RootCommand root = new("Claustrum — harness-neutral coding-agent delegation.")
{
    RunCommand.Build(),
    RolesCommands.Build(),
    BackendsCommands.Build(),
    JobsCommands.Build(),
    SyncCommand.Build(),
};

// `claustrum mcp` is M2 — deliberately not registered yet (builder brief for #2).
Command splash = new("splash", "Show the terminal splash screen.") { Hidden = true };
splash.SetAction(_ => Splash.Run(animate: Splash.IsWanted));
root.Subcommands.Add(splash);

if (args.Length == 0 && Splash.IsWanted)
    Splash.Run();

ParseResult parseResult = root.Parse(args);
if (parseResult.Errors.Count > 0)
{
    foreach (ParseError error in parseResult.Errors)
        Console.Error.WriteLine(error.Message);
    return ExitCodes.Usage;
}

return await parseResult.InvokeAsync();
