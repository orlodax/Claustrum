namespace Claustrum.Core.Model;

/// <summary>Deny patterns are harness-neutral command prefixes ("git push", "rm -rf"); backends translate them.</summary>
public sealed record PermissionPolicy(PermissionLevel Level, string[] Deny);
