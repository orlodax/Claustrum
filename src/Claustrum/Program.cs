using System.CommandLine;
using System.CommandLine.Parsing;
using Claustrum.Cli;

// Before anything writes a byte: on Windows the default is the console code page (ConsoleEncoding).
ConsoleEncoding.ForceUtf8();

RootCommand root = CliRoot.Build();

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
// ProcessTerminationTimeout=null: Claustrum owns termination through Runner. The 2 s default ended
// `run`/`coordinate` before their CancelKeyPress handler let Runner kill the backend's tree and write
// the receipt — exit 130 after 2006-2009 ms, no result.json, the child still spending. Null: exit 130
// after 352-404 ms with a `cancelled` receipt (measured 2026-10-09, #88; NOTES.md "Ctrl-C reaches the runner").
InvocationConfiguration invocationConfig = new() { EnableDefaultExceptionHandler = false, ProcessTerminationTimeout = null };

try
{
    return await parseResult.InvokeAsync(invocationConfig);
}
catch (Exception exception)
{
    return ExceptionBoundary.Handle(exception);
}
