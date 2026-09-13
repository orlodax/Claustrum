namespace Claustrum.Core.Model;

public enum RunStatus
{
    Success,
    Failed,
    Timeout,
    Cancelled,
    BackendMissing,
    BudgetExceeded,
}
