using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// #105: IsNetworkSpelled is the pure-string gate that lets the walk refuse a network path before any call that would
// authenticate to its host. It is OS-independent logic, so every spelling runs on every platform; the walk applies it
// on Windows only (SyncRootNetworkLinkTests).
public sealed class SyncRootNetworkSpellingTests
{
    [Theory]
    [InlineData(@"\\server\share\x")]
    [InlineData("//server/share/x")]
    [InlineData(@"\\?\UNC\s\sh")]
    [InlineData(@"\\?\unc\s\sh")]
    [InlineData(@"\??\UNC\s\sh")]
    [InlineData(@"\??\unc\s\sh")]
    [InlineData(@"\\.\UNC\s\sh")]
    [InlineData(@"\\?\GLOBALROOT\Device\Mup\s\sh")]
    [InlineData(@"\??\GLOBALROOT\Device\Mup\s")]
    [InlineData(@"\\?\Global\GLOBALROOT\Device\Mup\s")]
    [InlineData(@"\Device\Mup\s\sh")]
    [InlineData(@"\\127.0.0.1\c$\x")]
    [InlineData(@"\\?")]
    [InlineData(@"\??\global??\unc\h\s")]
    [InlineData(@"\GLOBAL??\UNC\h\s")]
    [InlineData(@"\global??\Device\Mup\h")]
    [InlineData(@"\??\??\UNC\h\s")]
    public void ANetworkSpellingInAnyFormIsRecognised(string path)
    {
        Assert.True(SyncRoot.IsNetworkSpelled(path));
    }

    [Theory]
    [InlineData(@"C:\x")]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\\?\Volume{abc}\x")]
    [InlineData(@"\??\C:\x")]
    [InlineData(@"\Windows\x")]
    [InlineData(@"x\y")]
    [InlineData("")]
    [InlineData(@"..\x")]
    [InlineData(@"\\?\")]
    public void ALocalOrRelativeSpellingIsNotNetwork(string path)
    {
        Assert.False(SyncRoot.IsNetworkSpelled(path));
    }

    // The same names are ordinary file names where there is no UNC: nothing about them is refused on the string off Windows.
    [Fact]
    public void ARelativeNameThatContainsNetworkWordsIsNotNetwork()
    {
        Assert.False(SyncRoot.IsNetworkSpelled(@"docs\UNC\notes.md"));
        Assert.False(SyncRoot.IsNetworkSpelled(@"Device\Mup\x"));
    }
}
