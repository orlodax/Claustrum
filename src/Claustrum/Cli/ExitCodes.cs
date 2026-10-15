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

    /// <summary>`jobs wait` on a live job whose claustrum process is gone (#124); its own code, never one a run can end with.</summary>
    public const int Abandoned = 6;
    public const int Cancelled = 130;
}
