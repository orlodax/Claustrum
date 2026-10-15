namespace Claustrum.Live;

// The document a tree's `run --json` (and `jobs reply --json`) prints in place of a RunResult when the child asked its
// architect a question and is still waiting for the answer (#124 stage 2); exit code 7.
public sealed record NeedsInputResult(string Status, string JobId, QuestionInfo Question)
{
    public static NeedsInputResult For(string jobId, QuestionInfo question) => new("needs_input", jobId, question);
}
