using System.CommandLine;

RootCommand root = new("Claustrum — harness-neutral coding-agent delegation.");
return root.Parse(args).Invoke();
