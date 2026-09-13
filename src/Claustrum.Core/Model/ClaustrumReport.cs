using System.Text.Json;

namespace Claustrum.Core.Model;

/// <summary>The JSON inside the single ```claustrum-report fence of a run's final message (docs/PLAN.md §B3).</summary>
public sealed record ClaustrumReport(string? Status, JsonElement Body);
