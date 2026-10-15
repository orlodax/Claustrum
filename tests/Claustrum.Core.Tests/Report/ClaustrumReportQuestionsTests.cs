using Claustrum.Core.Model;
using Claustrum.Core.Report;

namespace Claustrum.Core.Tests.Report;

// #124 stage 3b: a report that says `needs_input` carries `questions`, read leniently so a half-formed question can still be answered
// (ClaustrumReport.NeedsInput and .Questions; docs/MANUAL.md "When nobody can answer live").
public sealed class ClaustrumReportQuestionsTests
{
    private static ClaustrumReport Report(string json) => ReportExtractor.Extract($"text\n```claustrum-report\n{json}\n```\n").Report ?? throw new InvalidOperationException("no report");

    [Fact]
    public void OnlyTheStatusNeedsInputIsNeedsInput()
    {
        Assert.True(Report(/*lang=json,strict*/ """{"status":"needs_input"}""").NeedsInput);
        Assert.False(Report(/*lang=json,strict*/ """{"status":"done"}""").NeedsInput);
        Assert.False(Report(/*lang=json,strict*/ """{"status":["needs_input"]}""").NeedsInput);
        Assert.False(Report(/*lang=json,strict*/ """{"summary":"needs_input"}""").NeedsInput);
        Assert.False(Report("""["needs_input"]""").NeedsInput);
    }

    [Fact]
    public void AWellFormedQuestionKeepsEveryField()
    {
        ReportQuestion question = Assert.Single(Report(/*lang=json,strict*/ """{"status":"needs_input","questions":[{"key":"db","prompt":" Which? ","options":["a","b"],"allow_free_form":false}]}""").Questions);

        Assert.Equal(("db", "Which?", false), (question.Key, question.Prompt, question.AllowFreeForm));
        Assert.Equal(["a", "b"], question.Options);
    }

    [Fact]
    public void ALenientReadSkipsAQuestionWithNoPromptNamesAMissingKeyByPositionAndDefaultsFreeFormToTrue()
    {
        ReportQuestion[] questions = Report(/*lang=json,strict*/ """{"status":"needs_input","questions":[{"prompt":""},{"key":"x"},{"prompt":"first real one"},"junk",{"prompt":"second","options":"not a list"}]}""").Questions;

        Assert.Equal(["q1", "q2"], questions.Select(question => question.Key));
        Assert.Equal(["first real one", "second"], questions.Select(question => question.Prompt));
        Assert.All(questions, question => Assert.True(question.AllowFreeForm));
        Assert.All(questions, question => Assert.Empty(question.Options));
    }

    [Fact]
    public void NoQuestionsArrayIsNoQuestions()
    {
        Assert.Empty(Report(/*lang=json,strict*/ """{"status":"needs_input"}""").Questions);
        Assert.Empty(Report(/*lang=json,strict*/ """{"status":"needs_input","questions":"which?"}""").Questions);
    }
}
