namespace Claustrum.Core.Tracing;

// #145: a shell line as ShellCommand read it, neutral commands (`cd`, `true`, an assignment) left out. Confident is false when it could not
// be read with certainty (an unclosed quote or substitution, a `case`): the classifier then skips the call, a missed issue costing less
// than a false stop. GroupWrites are what a command-less part writes (`} > out`); Moves is a `cd` somewhere in the line, after which a
// relative path is not known.
internal sealed record ShellParse(ShellSegment[] Segments, bool Confident, string[] GroupWrites, bool Moves)
{
    // Every command views, so the output and error are file content too. A line of neutral commands only (`cd <dir>`) views nothing:
    // its error is the one that says a directory is missing.
    public bool OnlyViews => Segments.Length > 0 && Segments.All(segment => segment.Views);
}
