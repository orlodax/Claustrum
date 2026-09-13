using Claustrum.Core.Config;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Process;

// FakePlatform.Os drives the Windows npm-shim branch regardless of the OS actually running the
// test (docs brief item 3), so these cover BinaryLocator's Windows-only code path on Linux too.
public sealed class BinaryLocatorTests
{
    private static readonly string pathDir = Path.Combine(Path.GetTempPath(), "claustrum-binloc-path");

    private static FakePlatform WindowsPlatform() => new()
    {
        Os = ClaustrumOs.Windows,
        PathEntries = [pathDir],
        PathExtensions = [".COM", ".EXE", ".BAT", ".CMD"],
    };

    [Fact]
    public void RecognizedCmdShimResolvesToItsWrappedExe()
    {
        FakePlatform platform = WindowsPlatform();
        string shimPath = Path.Combine(pathDir, "claude.cmd");
        platform.Files[shimPath] = "\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\" %*\n";

        ResolvedBinary resolved = BinaryLocator.Locate("claude", ["--version"], config: null, platform);

        // Plain concatenation, not Path.Combine: see NpmShimParserTests for why — the shim's own
        // text is always Windows-authored backslashes, independent of the host OS running this test.
        Assert.Equal(pathDir + "\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe", resolved.Executable);
        Assert.Equal(["--version"], resolved.Args);
    }

    [Fact]
    public void UnrecognizedCmdShimRunsDirectlyWithoutCmdExeComposition()
    {
        FakePlatform platform = WindowsPlatform();
        string shimPath = Path.Combine(pathDir, "mystery.cmd");
        platform.Files[shimPath] = "@ECHO off\necho hi\n";

        ResolvedBinary resolved = BinaryLocator.Locate("mystery", ["--version"], config: null, platform);

        // BinaryLocator builds the candidate as name + extension from PATHEXT (".CMD", upper case,
        // as real PATHEXT spells it) — a real Windows filesystem is case-insensitive, so this is the
        // same file as the "mystery.cmd" the fake stored it under, just not the same string.
        Assert.Equal(Path.Combine(pathDir, "mystery.CMD"), resolved.Executable);
        Assert.Equal(["--version"], resolved.Args);
    }

    [Fact]
    public void BackendConfigPathOverrideWinsOverPathSearch()
    {
        FakePlatform platform = WindowsPlatform();
        string overridePath = Path.Combine(Path.GetTempPath(), "custom-claude.exe");
        platform.Files[overridePath] = "";
        platform.Files[Path.Combine(pathDir, "claude.exe")] = ""; // would also match, override must win

        ResolvedBinary resolved = BinaryLocator.Locate("claude", [], new BackendConfig(overridePath, Injection: null), platform);

        Assert.Equal(overridePath, resolved.Executable);
    }

    [Fact]
    public void BackendConfigPathThatDoesNotExistFallsBackToPathSearch()
    {
        FakePlatform platform = WindowsPlatform();
        platform.Files[Path.Combine(pathDir, "claude.exe")] = "";

        ResolvedBinary resolved = BinaryLocator.Locate("claude", [], new BackendConfig(Path.Combine(Path.GetTempPath(), "missing.exe"), Injection: null), platform);

        Assert.Equal(Path.Combine(pathDir, "claude.EXE"), resolved.Executable);
    }

    [Fact]
    public void PathextSearchTriesExtensionsInOrderAndReturnsFirstMatch()
    {
        FakePlatform platform = WindowsPlatform();
        platform.Files[Path.Combine(pathDir, "claude.BAT")] = "";
        platform.Files[Path.Combine(pathDir, "claude.CMD")] = "\"%dp0%\\node_modules\\x\\claude.exe\" %*\n";

        ResolvedBinary resolved = BinaryLocator.Locate("claude", [], config: null, platform);

        // .BAT precedes .CMD in PATHEXT order, and BinaryLocator never unwraps a .bat shim.
        Assert.Equal(Path.Combine(pathDir, "claude.BAT"), resolved.Executable);
    }

    [Fact]
    public void NonWindowsPlatformSearchesWithoutExtensions()
    {
        FakePlatform platform = new() { Os = ClaustrumOs.Linux, PathEntries = [pathDir], PathExtensions = [] };
        platform.Files[Path.Combine(pathDir, "claude")] = "#!/bin/sh\n";

        ResolvedBinary resolved = BinaryLocator.Locate("claude", [], config: null, platform);

        Assert.Equal(Path.Combine(pathDir, "claude"), resolved.Executable);
    }

    [Fact]
    public void MissingBinaryThrowsBackendNotFound()
    {
        FakePlatform platform = WindowsPlatform();

        Assert.Throws<BackendNotFoundException>(() => BinaryLocator.Locate("claude", [], config: null, platform));
    }
}
