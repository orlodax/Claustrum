using System.Text;
using System.Text.Json;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Live;

namespace Claustrum.Core.Report;

// #124 stage 3b, progress notes: a role on any backend may print a line `claustrum-note: <text>`; each one is appended to the job's
// conversation.jsonl as a Message from the role, text and prefix as written, so `jobs follow` can tell it from a live turn's text.
// One-way and best-effort: a job directory that stops taking writes costs the record, never the run. Only a run with no live
// session records here: LiveSession owns the seq of a live job's file, so a live run's notes stay in its stream and log.
public sealed class NoteRecorder(JobPaths job, TimeProvider? timeProvider = null)
{
    public const string Prefix = "claustrum-note:";

    private const int MaxNotes = 200;
    private const int MaxNoteChars = 1000;

    private readonly Lock gate = new();
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private int count;

    /// <summary>How many notes have been recorded.</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return count;
        }
    }

    /// <summary><paramref name="next"/> after this recorder has seen the line: how Runner puts it in front of the stream callback.</summary>
    public Action<string> Tap(Action<string>? next)
    {
        return line =>
        {
            OnStdoutLine(line);
            next?.Invoke(line);
        };
    }

    /// <summary>Records every note in one line of the backend's stdout: plain text, or the assistant text of a JSON event.</summary>
    public void OnStdoutLine(string line)
    {
        // Most lines of a stream are tool traffic: the prefix is the cheap test, before any JSON is parsed.
        if (!line.Contains(Prefix, StringComparison.Ordinal))
            return;

        foreach (string text in TextsIn(line))
            foreach (string note in NotesIn(text))
                Record(note);
    }

    /// <summary>The notes in <paramref name="text"/>: each line that starts with the prefix, with something after it, cut to a sane length.</summary>
    public static IEnumerable<string> NotesIn(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith(Prefix, StringComparison.Ordinal) || line.Length == Prefix.Length || line[Prefix.Length..].Trim().Length == 0)
                continue;

            yield return line.Length > MaxNoteChars ? $"{line[..MaxNoteChars]}…" : line;
        }
    }

    // The text a role said in one stdout line. JSON events are read for the assistant's text only (claude `assistant` and `result`,
    // copilot `assistant.message`, opencode `text`, cursor's one `result`): a tool's output that happens to contain the prefix is no note.
    private static List<string> TextsIn(string line)
    {
        if (!line.TrimStart().StartsWith('{'))
            return [line];

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return AssistantTexts(document.RootElement);
        }
        catch (JsonException)
        {
            return [line];
        }
    }

    private static List<string> AssistantTexts(JsonElement root)
    {
        List<string> texts = [];
        void Add(string? text)
        {
            if (text is not null)
                texts.Add(text);
        }

        switch (StringOf(root, "type"))
        {
            case "assistant" when Child(root, "message") is { } message && Child(message, "content") is { ValueKind: JsonValueKind.Array } content:
                foreach (JsonElement block in content.EnumerateArray().Where(block => StringOf(block, "type") == "text"))
                    Add(StringOf(block, "text"));
                break;
            case "assistant.message" when Child(root, "data") is { } data:
                Add(StringOf(data, "content"));
                break;
            case "result":
                Add(StringOf(root, "result"));
                break;
            case "text" when Child(root, "part") is { } part:
                Add(StringOf(part, "text"));
                break;
            default:
                break;
        }

        return texts;
    }

    private static JsonElement? Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement child) ? child : null;

    private static string? StringOf(JsonElement parent, string name) =>
        Child(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    // A note repeats in claude's `result` event after the assistant event that carried it, and a role that says the same thing twice
    // has said it once: only the first of identical notes is recorded.
    private void Record(string note)
    {
        lock (gate)
        {
            if (count >= MaxNotes || !seen.Add(note))
                return;

            ConversationEntry entry = new(count + 1, time.GetUtcNow(), ConversationParty.Role, ConversationKind.Message, note);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, ClaustrumJsonContext.Default.ConversationEntry) + "\n");
            try
            {
                using FileStream stream = new(job.ConversationJsonl, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
                stream.Write(bytes);
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The record is a courtesy; the run does not depend on it.
            }
        }
    }
}
