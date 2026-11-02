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

    /// <summary>
    /// A tree's `run`, or `jobs reply`, returned early because the child asked its architect a question and is still waiting (#124 stage 2);
    /// and a finished run whose report said `needs_input` (stage 3b: answer it with `jobs answer` or `coordinate --resume`).
    /// </summary>
    public const int NeedsInput = 7;

    /// <summary>
    /// A run that finished while its environment refused it something (#145: status <c>success_with_issues</c>, the receipt's
    /// <c>environment_issues</c>): a path, a tool, a login, a red gate. Not a clean result: tell the user before using it.
    /// </summary>
    public const int EnvironmentIssues = 8;

    public const int Cancelled = 130;
}
