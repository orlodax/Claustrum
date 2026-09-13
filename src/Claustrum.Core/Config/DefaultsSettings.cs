namespace Claustrum.Core.Config;

public sealed record DefaultsSettings(int? TimeoutSeconds, decimal? BudgetUsd, string? EnvPassthrough);
