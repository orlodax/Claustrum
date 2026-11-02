namespace Claustrum.Core.Tracing;

// #145: one command of a shell line: its program (a basename, keywords, wrappers, assignments and redirections stripped), whether it only
// views (its output is file or page content), whether it talks to a remote (gh, curl, wget, or git with a network subcommand), its
// arguments, and the files its own redirections write (`>`, `>>`, `2>`, `&>`; `tee`'s files).
internal sealed record ShellSegment(string Program, bool Views, bool Network, string[] Arguments, string[] Writes);
