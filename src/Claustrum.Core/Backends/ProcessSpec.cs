namespace Claustrum.Core.Backends;

// Exe is the logical command name ("claude"), not a resolved path — BinaryLocator (Process/) owns
// turning that into an actual spawnable target, including the npm-shim unwrapping on Windows.
// StdinText is null for every backend that takes its prompt on argv (opencode, copilot). cursor sets
// it because `cursor-agent -p` has no prompt-file flag and reads stdin when given no positional prompt
// (measured 2026-09-21, issue #14 — NOTES.md "The cursor backend, validated against a real install");
// claude does the same since #68 (measured 2026-10-08), so a brief never shows in a process listing.
public sealed record ProcessSpec(
    string Exe,
    string[] Args,
    string Cwd,
    Dictionary<string, string> Env,
    string[] TempFiles,
    string? StdinText = null);
