namespace Claustrum.Core.Tracing;

// What a tool call is to EnvironmentClassifier (#145): a shell command, whose output is the command's own; a read, a search, an edit or a
// fetch, whose output is file or page content and is never scanned; or anything else (an MCP tool, a subagent, a todo list).
public enum ToolFamily
{
    Shell,
    Read,
    Search,
    Edit,
    Fetch,
    Other,
}
