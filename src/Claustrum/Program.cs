using System.CommandLine;
using Claustrum.Cli;

RootCommand root = new("Claustrum — harness-neutral coding-agent delegation.");

Command splash = new("splash", "Show the terminal splash screen.") { Hidden = true };
splash.SetAction(_ => Splash.Run(animate: Splash.IsWanted));
root.Subcommands.Add(splash);

if (args.Length == 0 && Splash.IsWanted)
    Splash.Run();

return root.Parse(args).Invoke();
