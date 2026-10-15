using System.CommandLine;
using Claustrum.Core.Jobs;
using Claustrum.Live;

namespace Claustrum.Cli;

// #124 `claustrum jobs say <id> <text>`: the human's way into a live job. One inbox message, queued for the session:
// the answer while a question is open, a message to the running role otherwise. Exit 2 when the job is unknown, not
// live (no live.json) or done.
public static class JobsSayCommand
{
    public static Command Build()
    {
        Argument<string> id = new("id") { Description = "Job id." };
        Argument<string> text = new("text") { Description = "The answer while the job's question is open, otherwise a message to the running role." };
        Command say = new("say", "Answer a live job's open question, or send its running role a message.") { id, text };
        say.SetAction(parseResult => Execute(parseResult.GetRequiredValue(id), parseResult.GetRequiredValue(text)));
        return say;
    }

    private static int Execute(string id, string text)
    {
        LiveJob job = LiveJob.Find(id)
            ?? throw new CliUsageException($"job '{id}' not found under {JobDirectory.ResolveRoot(AppServices.Platform)}");

        SayResult sent = job.Say(text);
        SafeConsole.Out(sent.AnswersQuestion is { } seq ? $"queued: answer to question {seq}" : "queued: message");
        SafeConsole.Out($"state: {sent.State}");
        return ExitCodes.Ok;
    }
}
