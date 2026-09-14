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
    CastCommands.Build(),
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

// EnableDefaultExceptionHandler=false: System.CommandLine's own default handler would otherwise
// catch a handler exception itself, print "Unhandled exception: <type>: <message>" plus its own
// stack trace, and return exit 1 — before this file's catch ever sees it (confirmed 2026-09-13 by
// running the built binary; ExceptionBoundary below was silently unreachable without this flag).
InvocationConfiguration invocationConfig = new() { EnableDefaultExceptionHandler = false };

try
{
    return await parseResult.InvokeAsync(invocationConfig);
}
catch (Exception exception)
{
    return ExceptionBoundary.Handle(exception);
}
