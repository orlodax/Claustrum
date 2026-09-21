namespace Claustrum.Core.Backends;

// Exe is the logical command name ("claude"), not a resolved path — BinaryLocator (Process/) owns
// turning that into an actual spawnable target, including the npm-shim unwrapping on Windows.
// StdinText is null for every backend that takes its prompt on argv; only cursor sets it, because
// `cursor-agent -p` has no prompt-file flag and reads stdin when given no positional prompt
// (measured 2026-09-21, issue #14 — NOTES.md "The cursor backend, validated against a real install").
public sealed record ProcessSpec(
    string Exe,
    string[] Args,
    string Cwd,
    Dictionary<string, string> Env,
    string[] TempFiles,
    string? StdinText = null);
