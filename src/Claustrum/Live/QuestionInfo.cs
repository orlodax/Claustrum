namespace Claustrum.Live;

// The open question as the front doors report it (`jobs wait --json`, MCP job_status): the conversation entry's Seq and Text.
public sealed record QuestionInfo(int Seq, string Text);
