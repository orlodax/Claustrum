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

    // The role stopped to ask its human something and said so in its report (#124 stage 3b): finished, not failed.
    NeedsInput,
}
