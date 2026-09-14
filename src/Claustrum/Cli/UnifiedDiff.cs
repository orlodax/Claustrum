using System.Text;

namespace Claustrum.Cli;

// `sync --dry-run` (docs/PLAN.md §B4) needs a real unified diff, not just "this file would change".
// Files here are short generated markdown (a couple hundred lines at most), so a single full-file
// hunk from a plain O(N*M) LCS table is simple, correct, and fast enough — no context trimming into
// multiple hunks, and no new package dependency.
public static class UnifiedDiff
{
    public static string Format(string path, string? oldText, string newText)
    {
        string[] oldLines = oldText is null ? [] : SplitLines(oldText);
        string[] newLines = SplitLines(newText);
        List<(char Op, string Line)> edits = Diff(oldLines, newLines);

        StringBuilder builder = new();
        builder.Append("--- ").Append(oldText is null ? "/dev/null" : $"a/{path}").Append('\n');
        builder.Append("+++ b/").Append(path).Append('\n');
        builder.Append("@@ -1,").Append(oldLines.Length).Append(" +1,").Append(newLines.Length).Append(" @@\n");
        foreach ((char op, string line) in edits)
            builder.Append(op).Append(line).Append('\n');

        return builder.ToString();
    }

    private static string[] SplitLines(string text)
    {
        string normalized = text.Replace("\r\n", "\n").TrimEnd('\n');
        return normalized.Length == 0 ? [] : normalized.Split('\n');
    }

    private static List<(char Op, string Line)> Diff(string[] oldLines, string[] newLines)
    {
        int n = oldLines.Length;
        int m = newLines.Length;
        int[,] lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = oldLines[i] == newLines[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        List<(char, string)> edits = [];
        int a = 0;
        int b = 0;
        while (a < n && b < m)
        {
            if (oldLines[a] == newLines[b])
            {
                edits.Add((' ', oldLines[a]));
                a++;
                b++;
            }
            else if (lcs[a + 1, b] >= lcs[a, b + 1])
            {
                edits.Add(('-', oldLines[a]));
                a++;
            }
            else
            {
                edits.Add(('+', newLines[b]));
                b++;
            }
        }

        while (a < n)
        {
            edits.Add(('-', oldLines[a]));
            a++;
        }

        while (b < m)
        {
            edits.Add(('+', newLines[b]));
            b++;
        }

        return edits;
    }
}
