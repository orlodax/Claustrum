using Claustrum.Core.Config;
using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Core.Tracing;

namespace Claustrum.Core.Backends;

public interface IBackend
{
    string Name { get; }

    /// <summary>
    /// The harness family the role is rendered for, and the name role.json <c>harnesses</c> lists (#130): an ACP backend returns its
    /// harness's (<c>acp-opencode</c> → <c>opencode</c>), every other backend its own name.
    /// </summary>
    string Harness => Name;

    Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken);

    ProcessSpec Build(ResolvedRun run);

    ParsedOutput Parse(string stdout, string stderr, int exitCode);

    /// <summary>
    /// Creates a stream formatter for this backend, or null for pass-through (raw lines echoed
    /// unchanged). Called once per run when <c>--stream</c> is active.
    /// </summary>
    IStreamFormatter? CreateStreamFormatter(string cwd) => null;

    /// <summary>
    /// How this backend holds a conversation (#124): messages on one process's stdin (claude) or one process per turn,
    /// the session resumed (opencode). Null when it cannot: a run that asks for a live session on such a backend
    /// simply runs as it always did.
    /// </summary>
    ILiveProtocol? LiveProtocol => null;

    /// <summary>
    /// Whether a run can be continued from the session id it returned (<c>ResolvedRun.ResumeSession</c>): what `jobs answer` needs of
    /// the backend that asked (#124 stage 3b). False for one that keeps no session between runs.
    /// </summary>
    bool CanResume => true;

    /// <summary>
    /// Whether <see cref="Build"/> applies <c>ResolvedRun.ReadDirs</c> (#144). False by default, so a new backend must opt in: until it
    /// does, a run with configured read dirs is told at dispatch that this backend cannot reach them.
    /// </summary>
    bool HonoursReadDirs => false;

    /// <summary>
    /// Whether the read dirs <see cref="Build"/> applies are writable at <paramref name="level"/> (#144): true where the harness can only
    /// grant a directory whole (copilot, cursor at the edit levels), which dispatch says out loud. Read only when <see cref="HonoursReadDirs"/>.
    /// </summary>
    bool ReadDirsWritableAt(PermissionLevel level) => false;

    /// <summary>
    /// Why <see cref="Build"/> leaves <paramref name="dir"/> out at <paramref name="level"/>, or null when it applies it (#144): a path the
    /// harness's permission syntax cannot fence. Dispatch names a configured dir it refuses; the automatic git common dir goes silently.
    /// </summary>
    string? ReadDirRefusal(string dir, PermissionLevel level) => null;

    /// <summary>
    /// What a finished run's stdout says about its tool calls, for <see cref="EnvironmentClassifier"/> (#145). Null when this backend's
    /// stream is not read for them: cursor, copilot and api are unclassified, and their receipts carry dispatch and report issues only.
    /// </summary>
    RunTrace? Trace(string stdout) => null;
}
