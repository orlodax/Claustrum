using System.Text.Json.Serialization;
using Claustrum.Core.Json;

namespace Claustrum.Core.Model;

// #145: what kept a job from the environment its work needed. Classified from parsed tool calls, a backend's own denial list and the
// role's report, never from a raw grep of the log (which also holds the files the agent read). NOTES.md "Environment issues".
[JsonConverter(typeof(SnakeCaseEnumConverter<EnvironmentIssueKind>))]
public enum EnvironmentIssueKind
{
    PermissionDenied,
    ExternalDirectory,
    MissingTool,
    PlanMode,
    Auth,
    MissingDirectory,
    ContainerPermission,
    TestGateRed,
    ReportedBlocker,
    ReadDirsUnsupported,
    WorktreeHookFailed,
}
