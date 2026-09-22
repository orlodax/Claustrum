using Claustrum.Cli;
using Claustrum.Core.Platform;
using Claustrum.Roles.Sync;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// issue #24: ClaudeDesktopConfig.Resolve decides, per OS, whether the desktop app's own config file
// is a real --global target — Linux has no such app; Windows needs %APPDATA%; both Windows and macOS
// need the app's own config *directory* to already exist (its file may still be absent — that is a
// fresh install, and ours to create); and the running binary must be resolvable to something real.
// FakePlatform (not HomeRedirectPlatform, whose Os is always this process's real one) is what lets
// every branch run on whichever OS actually executes the test suite.
public sealed class ClaudeDesktopConfigTests
{
    [Fact]
    public void LinuxSkipsWithNoSuchAppReason()
    {
        FakePlatform platform = new() { Os = ClaustrumOs.Linux };

        (ClaudeDesktopTarget? target, string? reason) = ClaudeDesktopConfig.Resolve(platform, binaryPath: "/abs/claustrum");

        Assert.Null(target);
        Assert.Equal("there is no Claude desktop app on Linux", reason);
    }

    [Fact]
    public void WindowsWithoutAppDataSkipsNamingIt()
    {
        FakePlatform platform = new() { Os = ClaustrumOs.Windows };

        (ClaudeDesktopTarget? target, string? reason) = ClaudeDesktopConfig.Resolve(platform, binaryPath: "/abs/claustrum");

        Assert.Null(target);
        Assert.Equal("APPDATA is not set", reason);
    }

    [Fact]
    public void WindowsWithTheAppDirectoryAbsentSkipsNamingTheDirectory()
    {
        string appData = Directory.CreateTempSubdirectory("claustrum-desktop-appdata-").FullName;
        try
        {
            FakePlatform platform = new() { Os = ClaustrumOs.Windows };
            platform.EnvironmentVariables["APPDATA"] = appData;
            // Deliberately never created: %APPDATA%\Claude does not exist on this machine.
            string expectedDirectory = Path.Combine(appData, "Claude");

            (ClaudeDesktopTarget? target, string? reason) = ClaudeDesktopConfig.Resolve(platform, binaryPath: "/abs/claustrum");

            Assert.Null(target);
            Assert.Equal($"Claude desktop app not found at {expectedDirectory}", reason);
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public void MacOsWithTheAppDirectoryAbsentSkipsNamingTheDirectory()
    {
        string home = Directory.CreateTempSubdirectory("claustrum-desktop-machome-").FullName;
        try
        {
            FakePlatform platform = new() { Os = ClaustrumOs.MacOs, HomeDirectory = home };
            // Deliberately never created: ~/Library/Application Support/Claude does not exist here.
            string expectedDirectory = Path.Combine(home, "Library", "Application Support", "Claude");

            (ClaudeDesktopTarget? target, string? reason) = ClaudeDesktopConfig.Resolve(platform, binaryPath: "/abs/claustrum");

            Assert.Null(target);
            Assert.Equal($"Claude desktop app not found at {expectedDirectory}", reason);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void WindowsWithTheAppDirectoryPresentAndABinaryReturnsATarget()
    {
        string appData = Directory.CreateTempSubdirectory("claustrum-desktop-appdata-").FullName;
        try
        {
            string claudeDirectory = Path.Combine(appData, "Claude");
            Directory.CreateDirectory(claudeDirectory);
            FakePlatform platform = new() { Os = ClaustrumOs.Windows };
            platform.EnvironmentVariables["APPDATA"] = appData;

            (ClaudeDesktopTarget? target, string? reason) = ClaudeDesktopConfig.Resolve(platform, binaryPath: "/abs/claustrum");

            Assert.Null(reason);
            Assert.NotNull(target);
            Assert.Equal(Path.Combine(claudeDirectory, "claude_desktop_config.json"), target.ConfigPath);
            Assert.Equal("/abs/claustrum", target.BinaryPath);
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public void MacOsWithTheAppDirectoryPresentAndABinaryReturnsATarget()
    {
        string home = Directory.CreateTempSubdirectory("claustrum-desktop-machome-").FullName;
        try
        {
            string claudeDirectory = Path.Combine(home, "Library", "Application Support", "Claude");
            Directory.CreateDirectory(claudeDirectory);
            FakePlatform platform = new() { Os = ClaustrumOs.MacOs, HomeDirectory = home };

            (ClaudeDesktopTarget? target, string? reason) = ClaudeDesktopConfig.Resolve(platform, binaryPath: "/abs/claustrum");

            Assert.Null(reason);
            Assert.NotNull(target);
            Assert.Equal(Path.Combine(claudeDirectory, "claude_desktop_config.json"), target.ConfigPath);
            Assert.Equal("/abs/claustrum", target.BinaryPath);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // The app directory present but the binary unresolvable (ClaustrumBinaryPath.Resolve's own
    // reason, e.g. "running as 'dotnet', not the claustrum binary") must surface that reason instead
    // of a generic one — the two skip reasons answer different questions (no app vs. no known binary).
    [Fact]
    public void AppPresentButBinaryUnavailableSurfacesTheBinarysOwnReason()
    {
        string home = Directory.CreateTempSubdirectory("claustrum-desktop-machome-").FullName;
        try
        {
            string claudeDirectory = Path.Combine(home, "Library", "Application Support", "Claude");
            Directory.CreateDirectory(claudeDirectory);
            FakePlatform platform = new() { Os = ClaustrumOs.MacOs, HomeDirectory = home };

            (ClaudeDesktopTarget? target, string? reason) = ClaudeDesktopConfig.Resolve(
                platform, binaryPath: null, binaryUnavailableReason: "running as 'dotnet', not the claustrum binary");

            Assert.Null(target);
            Assert.Equal("running as 'dotnet', not the claustrum binary", reason);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
