namespace Claustrum.Core.Live;

// RunResult.Conversation (#124): where a live job's conversation.jsonl is and how many entries it holds,
// the way LogPath points at stdout.log. A path and a count, not the entries: the last one repeats
// final_message, and most live jobs never ask anything.
public sealed record ConversationSummary(string Path, int Entries);
