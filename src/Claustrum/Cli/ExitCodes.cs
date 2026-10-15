namespace Claustrum.Cli;

/// <summary>The `claustrum` process exit codes (docs/PLAN.md §A5) — fixed, not System.CommandLine's own.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int BackendFailure = 1;
    public const int Usage = 2;
    public const int BackendMissing = 3;
    public const int Timeout = 4;
    public const int Budget = 5;

    /// <summary>A job whose claustrum process is gone with no receipt (#124): what `jobs wait`, `jobs reply` and a tree's `run` report; never a status a run ends with.</summary>
    public const int Abandoned = 6;

    /// <summary>A tree's `run`, or `jobs reply`, returned early because the child asked its architect a question and is still waiting (#124 stage 2).</summary>
    public const int NeedsInput = 7;
    public const int Cancelled = 130;
}
