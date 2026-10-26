using System.Text.Json.Serialization;
using Claustrum.Core.Json;

namespace Claustrum.Core.Config;

public sealed record DefaultsSettings(
    int? TimeoutSeconds,
    // A silent layer is left out on write: written as null it would read back as "no default cap" (#137).
    [property: JsonConverter(typeof(DefaultBudgetJsonConverter)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DefaultBudget? BudgetUsd,
    string? EnvPassthrough);
