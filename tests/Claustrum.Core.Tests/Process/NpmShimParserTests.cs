using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Process;

// Shapes verified against real installs 2026-09-13 (NOTES.md "npm shims on Windows").
public sealed class NpmShimParserTests
{
    private static readonly string shimDir = Path.Combine(Path.GetTempPath(), "claustrum-shim-dir");

    [Fact]
    public void CompiledBinaryShimResolvesTheExeDirectly()
    {
        string shim = "@ECHO off\n\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\" %*\n";

        NpmShimTarget? target = NpmShimParser.TryParse(shim, shimDir, new FakePlatform());

        Assert.NotNull(target);
        // Plain concatenation, not Path.Combine: ResolveDp0 does a literal string.Replace("%dp0%", ...)
        // against the shim's own text, which is always Windows-authored (backslashes) regardless of
        // the host OS running this test — Path.Combine here would use forward slashes on Linux and
        // silently mismatch what the SUT actually returns.
        Assert.Equal(shimDir + "\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe", target.Executable);
        Assert.Empty(target.PrefixArgs);
    }

    [Fact]
    public void ClassicJsShimWithBundledNodeUsesThatNodeExe()
    {
        string shim = "@ECHO off\n"
            + "IF EXIST \"%dp0%\\node.exe\" (SET \"_prog=%dp0%\\node.exe\") ELSE (SET \"_prog=node\")\n"
            + "endLocal & \"%_prog%\"  \"%dp0%\\node_modules\\yo\\lib\\cli.js\" %*\n";
        FakePlatform platform = new();
        platform.Files[Path.Combine(shimDir, "node.exe")] = "";

        NpmShimTarget? target = NpmShimParser.TryParse(shim, shimDir, platform);

        Assert.NotNull(target);
        Assert.Equal(Path.Combine(shimDir, "node.exe"), target.Executable);
        Assert.Equal([shimDir + "\\node_modules\\yo\\lib\\cli.js"], target.PrefixArgs); // see the comment above on ResolveDp0
    }

    [Fact]
    public void ClassicJsShimWithoutBundledNodeFallsBackToBareNode()
    {
        string shim = "@ECHO off\n\"%_prog%\"  \"%dp0%\\node_modules\\yo\\lib\\cli.js\" %*\n";

        NpmShimTarget? target = NpmShimParser.TryParse(shim, shimDir, new FakePlatform());

        Assert.NotNull(target);
        Assert.Equal("node", target.Executable);
    }

    [Fact]
    public void UnrecognizedShimShapeReturnsNull()
    {
        string shim = "@ECHO off\necho hello world\n";

        NpmShimTarget? target = NpmShimParser.TryParse(shim, shimDir, new FakePlatform());

        Assert.Null(target);
    }
}
