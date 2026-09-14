using Claustrum.Casts;

namespace Claustrum.Tests.Casts;

public sealed class CastAnswersTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-answers-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    [Fact]
    public void ReadReturnsTheAnswersDictionary()
    {
        string path = Path.Combine(cwd, "answers.json");
        File.WriteAllText(path, /*lang=json,strict*/ """{"architect":"host","builder":"claude:opus"}""");

        Dictionary<string, string> answers = CastAnswers.Read(path);

        Assert.Equal("host", answers["architect"]);
        Assert.Equal("claude:opus", answers["builder"]);
    }

    [Fact]
    public void ReadOnAMalformedAnswersFileThrowsCastExceptionNamingTheFile()
    {
        string path = Path.Combine(cwd, "answers.json");
        // A number where a question's answer must be a string (review finding #8): used to escape
        // uncaught as a JsonException, reported at exit 1 with no file name.
        File.WriteAllText(path, /*lang=json,strict*/ """{"builder":1}""");

        CastException ex = Assert.Throws<CastException>(() => CastAnswers.Read(path));
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnNonObjectJsonThrowsCastException()
    {
        string path = Path.Combine(cwd, "answers.json");
        File.WriteAllText(path, "[]");

        Assert.Throws<CastException>(() => CastAnswers.Read(path));
    }
}
