namespace Claustrum.Cli;

// The terminal a prompt reads its reply from (#124): what QuestionPrompter needs of TerminalInput, so a test can stand in for it
// and assert the type-ahead is discarded before each `reply> ` and never after.
internal interface IPromptTerminal
{
    /// <summary>Throws away what was typed and not yet read, so a reply answers only what is on screen. Only valid while no read is outstanding.</summary>
    void DiscardPending();

    /// <summary>One line, without its line break; null at end of input. At most one read may be outstanding.</summary>
    Task<string?> ReadLineAsync();
}
