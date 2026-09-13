namespace Claustrum.Core.Process;

public enum ProcessTermination
{
    Completed,
    TimedOut,
    Cancelled,
}

public sealed record ProcessOutcome(int ExitCode, string Stdout, string Stderr, ProcessTermination Termination, TimeSpan Duration);
