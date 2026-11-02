namespace Claustrum.Core.Tracing;

// #145: what ShellWords read of one part of a shell line: its command (null for a neutral one), whether it was read with certainty, the
// files a command-less part (`} > out`) or a `tee` writes, whether it changes directory, and its assignments (`L=/tmp/x.log`), the
// value null when it is not a plain word (`X=$(…)`).
internal sealed record ShellPart(ShellSegment? Segment, bool Read, string[] Writes, bool Moves, (string Name, string? Value)[] Assignments);
