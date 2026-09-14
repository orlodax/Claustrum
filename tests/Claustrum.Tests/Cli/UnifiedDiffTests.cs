using Claustrum.Cli;

namespace Claustrum.Tests.Cli;

public sealed class UnifiedDiffTests
{
    [Fact]
    public void IdenticalTextProducesOnlyContextLines()
    {
        string diff = UnifiedDiff.Format("a.md", "line one\nline two\n", "line one\nline two\n");

        Assert.DoesNotContain("\n-", diff);
        Assert.DoesNotContain("\n+l", diff);
        Assert.Contains(" line one", diff);
        Assert.Contains(" line two", diff);
    }

    [Fact]
    public void NewFileHasDevNullAsTheOldPath()
    {
        string diff = UnifiedDiff.Format("a.md", null, "hello\n");

        Assert.StartsWith("--- /dev/null", diff);
        Assert.Contains("+++ b/a.md", diff);
        Assert.Contains("+hello", diff);
    }

    [Fact]
    public void ChangedLineShowsAsRemovedThenAdded()
    {
        string diff = UnifiedDiff.Format("a.md", "old\n", "new\n");

        Assert.Contains("-old", diff);
        Assert.Contains("+new", diff);
    }

    [Fact]
    public void UnchangedSurroundingLinesStayAsContext()
    {
        string diff = UnifiedDiff.Format("a.md", "top\nmiddle\nbottom\n", "top\nchanged\nbottom\n");

        Assert.Contains(" top", diff);
        Assert.Contains("-middle", diff);
        Assert.Contains("+changed", diff);
        Assert.Contains(" bottom", diff);
    }
}
