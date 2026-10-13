using Claustrum.Mcp;
using Claustrum.Tests.Testing;
using ModelContextProtocol;

namespace Claustrum.Tests.Mcp;

// #103 on the MCP door: `cast_create` writes into the server's working directory, so a cast file or directory that is a link
// leaving it is refused with an McpException carrying the message (the CLI's exit 2), the outside tree stays byte-identical,
// and `cast_list` does not list a linked directory's names. Shares the "AppServices home" collection because the tool reads
// AppServices and the tests move Environment.CurrentDirectory.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class CastCreateLinkToolsTests : IDisposable
{
    private const string Refusal = "resolves outside the working directory";

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-cast-link-").FullName;
    private readonly CastLinkFixture links;

    public CastCreateLinkToolsTests() => links = new CastLinkFixture(cwd);

    public void Dispose()
    {
        TempTree.Delete(cwd);
        links.Dispose();
    }

    public static TheoryData<string> Shapes() => [.. CastLinkFixture.Shapes];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void CastCreateRefusesALinkLeavingTheCwdAndLeavesTheOutsideByteIdentical(string shape)
    {
        links.Arrange(shape);
        string[] before = links.Snapshot();

        InCwd(() =>
        {
            McpException exception = Assert.Throws<McpException>(() => ClaustrumTools.CastCreate(new Dictionary<string, string> { ["builder"] = "claude:opus" }));

            Assert.Contains(Refusal, exception.Message, StringComparison.Ordinal);
            Assert.Contains(links.RealPathFor("default"), exception.Message, CastLinkFixture.PathComparison);
        });

        Assert.Equal(before, links.Snapshot());
        Assert.Equal(CastLinkFixture.Secret, File.ReadAllText(links.RealPathFor("default")));
    }

    [Theory]
    [InlineData("claustrum")]
    [InlineData("casts")]
    public void CastListRefusesALinkedDirectoryLeavingTheCwd(string shape)
    {
        links.Arrange(shape);

        InCwd(() =>
        {
            McpException exception = Assert.Throws<McpException>(ClaustrumTools.CastList);

            Assert.Contains(Refusal, exception.Message, StringComparison.Ordinal);
        });
    }

    private void InCwd(Action body)
    {
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = cwd;
        try
        {
            body();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }
}
