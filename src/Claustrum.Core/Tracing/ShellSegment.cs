namespace Claustrum.Core.Tracing;

// #145: one command of a shell line: its program (a basename, keywords, wrappers and assignments stripped), whether it only views (its
// output is file or page content), and whether it talks to a remote (gh, curl, wget, or git with a network subcommand).
internal sealed record ShellSegment(string Program, bool Views, bool Network);
