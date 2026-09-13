using Claustrum.Core.Model;
using Claustrum.Core.Report;

namespace Claustrum.Core.Tests.Report;

public sealed class ReportExtractorTests
{
    [Fact]
    public void NoFenceYieldsMissingStatus()
    {
        ExtractedReport extracted = ReportExtractor.Extract("just some prose, no report block");

        Assert.Null(extracted.Report);
        Assert.Equal(ReportStatus.Missing, extracted.Status);
        Assert.Empty(extracted.Warnings);
    }

    [Fact]
    public void OneFenceParsesTheJsonBody()
    {
        string text = "Done.\n\n```claustrum-report\n{\"status\":\"done\"}\n```\n";

        ExtractedReport extracted = ReportExtractor.Extract(text);

        Assert.Equal(ReportStatus.Ok, extracted.Status);
        Assert.NotNull(extracted.Report!.Data);
        Assert.Equal("done", extracted.Report.Data!.Value.GetProperty("status").GetString());
        Assert.Empty(extracted.Warnings);
    }

    [Fact]
    public void SeveralFencesLastWinsWithAWarning()
    {
        string text = """
            ```claustrum-report
            {"status":"partial"}
            ```

            Actually, final answer:

            ```claustrum-report
            {"status":"done"}
            ```
            """;

        ExtractedReport extracted = ReportExtractor.Extract(text);

        Assert.Equal(ReportStatus.Ok, extracted.Status);
        Assert.Equal("done", extracted.Report!.Data!.Value.GetProperty("status").GetString());
        Assert.Single(extracted.Warnings);
        Assert.Contains("multiple claustrum-report fences found (2)", extracted.Warnings[0]);
    }

    [Fact]
    public void FenceNestedInsideALargerWrappingFenceFindsTheInnerOne()
    {
        string text = """
            Here is my summary, wrapped for display:

            ````
            ```claustrum-report
            {"status":"done","summary":"ok"}
            ```
            ````
            """;

        ExtractedReport extracted = ReportExtractor.Extract(text);

        Assert.Equal(ReportStatus.Ok, extracted.Status);
        Assert.Equal("done", extracted.Report!.Data!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public void IndentedClosingFenceInsideAListItemStillMatches()
    {
        string text = "1. Step one\n   ```claustrum-report\n   {\"status\":\"done\"}\n   ```\n";

        ExtractedReport extracted = ReportExtractor.Extract(text);

        Assert.Equal(ReportStatus.Ok, extracted.Status);
    }

    [Fact]
    public void InvalidJsonBodyIsUnparsedButKeepsRawText()
    {
        string text = "```claustrum-report\nnot json at all\n```\n";

        ExtractedReport extracted = ReportExtractor.Extract(text);

        Assert.Equal(ReportStatus.Unparsed, extracted.Status);
        Assert.Null(extracted.Report!.Data);
        Assert.Equal("not json at all", extracted.Report.RawText);
    }

    [Fact]
    public void CrlfLineEndingsAreTolerated()
    {
        string text = "Done.\r\n\r\n```claustrum-report\r\n{\"status\":\"done\"}\r\n```\r\n";

        ExtractedReport extracted = ReportExtractor.Extract(text);

        Assert.Equal(ReportStatus.Ok, extracted.Status);
        Assert.Equal("done", extracted.Report!.Data!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public void FourBacktickWrapperAroundTheReportItselfStillMatches()
    {
        string text = "````claustrum-report\n{\"status\":\"done\"}\n````\n";

        ExtractedReport extracted = ReportExtractor.Extract(text);

        Assert.Equal(ReportStatus.Ok, extracted.Status);
        Assert.Equal("done", extracted.Report!.Data!.Value.GetProperty("status").GetString());
    }
}
