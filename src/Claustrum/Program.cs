using System.CommandLine;

var root = new RootCommand("Claustrum — harness-neutral coding-agent delegation.");
return root.Parse(args).Invoke();
