namespace Claustrum.Core.Model;

/// <summary>Output of the Roles renderer; Config.Resolve turns the model class into a concrete backend+model.</summary>
public sealed record RenderedRole(
    string Name,
    string SystemBody,
    string ModelClass,
    string Effort,
    PermissionPolicy Permission,
    string? ReportSchema,
    bool Blind);
