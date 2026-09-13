namespace Claustrum.Roles.Model;

/// <summary>One entry of `role.json`'s `tiers` map — a model class (resolved by Core config) and an effort label.</summary>
public sealed record RoleTier(string Model, string Effort);
