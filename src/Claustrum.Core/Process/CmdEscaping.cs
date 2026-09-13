using System.Text;

namespace Claustrum.Core.Process;

// cmd.exe expands "%...%" even inside quoted arguments, so every literal `%` must be doubled
// before quoting (NOTES.md "npm shims on Windows"); quoting itself follows the standard MSVCRT
// argv escaping rules so `cmd /d /s /c "<this>"` re-splits it the same way a native exe would.
internal static class CmdEscaping
{
    public static string BuildCommandLine(IEnumerable<string> tokens) => string.Join(' ', tokens.Select(Quote));

    private static string Quote(string token)
    {
        string escaped = token.Replace("%", "%%");
        if (escaped.Length > 0 && escaped.IndexOfAny([' ', '\t', '"']) < 0)
            return escaped;

        StringBuilder builder = new();
        builder.Append('"');
        int backslashes = 0;
        foreach (char c in escaped)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }

        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
