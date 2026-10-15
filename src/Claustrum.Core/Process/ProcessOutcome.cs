namespace Claustrum.Core.Process;

public enum ProcessTermination
{
    Completed,
    TimedOut,
    Cancelled,
}

// LastTurn is set only for a live session that runs one process per turn (#124 stage 3a): Stdout and Stderr
// then hold every turn, and LastTurn the final one alone, which is what the receipt's message comes from.
public sealed record ProcessOutcome(int ExitCode, string Stdout, string Stderr, ProcessTermination Termination, TimeSpan Duration, TurnOutput? LastTurn = null);

public sealed record TurnOutput(string Stdout, string Stderr);
