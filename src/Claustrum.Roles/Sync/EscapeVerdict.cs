namespace Claustrum.Roles.Sync;

/// <summary>What <see cref="SyncRoot.ProbeEscaping"/> found: where a path lands and whether anything is there (NOTES.md "UNC targets are refused without opening").</summary>
public enum EscapeVerdict
{
    /// <summary>The path stays inside the root (or the root is unfenced): the caller may stat it as usual.</summary>
    Inside,

    /// <summary>It leaves the root and nothing is at its real path: the caller treats it as "not there", and never writes through it.</summary>
    Absent,

    /// <summary>It leaves the root and something is there, or the walk could not tell (unresolved, opaque, network, loop): refuse.</summary>
    Present,
}
