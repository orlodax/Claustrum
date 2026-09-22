using System.CommandLine;

namespace Claustrum.Cli;

// The whole CLI grammar in one place so Program.cs is only the parse/invoke/exit-code shell — and so
// a test can parse a command line against the *real* grammar (the generated SKILL.md tells agents
// exact commands to run; review finding #2 was that text and this grammar silently disagreeing).
public static class CliRoot
{
    public static RootCommand Build()
    {
        RootCommand root = new("Claustrum — harness-neutral coding-agent delegation.")
        {
            RunCommand.Build(),
            RolesCommands.Build(),
            BackendsCommands.Build(),
            JobsCommands.Build(),
            SyncCommand.Build(),
            CastCommands.Build(),
            CoordinateCommand.Build(),
            McpCommand.Build(),
            InitCommand.Build(),
        };

        Command splash = new("splash", "Show the terminal splash screen.") { Hidden = true };
        splash.SetAction(_ => Splash.Run(animate: Splash.IsWanted));
        root.Subcommands.Add(splash);

        return root;
    }
}
