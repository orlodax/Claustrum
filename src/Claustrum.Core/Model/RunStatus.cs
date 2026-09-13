using System.Text.Json.Serialization;
using Claustrum.Core.Json;

namespace Claustrum.Core.Model;

[JsonConverter(typeof(SnakeCaseEnumConverter<RunStatus>))]
public enum RunStatus
{
    Success,
    Failed,
    Timeout,
    Cancelled,
    BackendMissing,
    BudgetExceeded,
}
