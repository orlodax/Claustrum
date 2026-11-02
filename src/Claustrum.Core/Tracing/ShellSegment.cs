namespace Claustrum.Core.Tracing;

// #145: one segment of a shell command line: its program (a basename, with wrappers and env assignments stripped) and whether it only
// views files, so that its output is their content.
internal sealed record ShellSegment(string Program, bool Views);
