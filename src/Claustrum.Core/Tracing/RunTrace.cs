namespace Claustrum.Core.Tracing;

/// <summary>
/// What a backend's stream says about a finished run's tool use (#145): every call in the order first seen, the denials the backend listed
/// itself, and the permission mode the session started in (claude's init <c>permissionMode</c>, on <see cref="PermissionModeLine"/>).
/// </summary>
public sealed record RunTrace(TracedCall[] Calls, TracedDenial[] Denials, string? PermissionMode = null, int PermissionModeLine = 0);
