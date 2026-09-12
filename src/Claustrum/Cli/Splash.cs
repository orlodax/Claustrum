using System.Runtime.InteropServices;
using System.Text;

namespace Claustrum.Cli;

// The logo and animation mirror the cover of newsletter issue 008; keep the two in step.
public static partial class Splash
{
    private const int Width = 78;
    private const string Tagline = "one core · two doors · any harness";
    private const string Pool = "░▒▓█╔╗╚╝║═╬╦╩╠╣0101";

    private static readonly string[] logo =
    [
        " ██████╗██╗      █████╗ ██╗   ██╗███████╗████████╗██████╗ ██╗   ██╗███╗   ███╗",
        "██╔════╝██║     ██╔══██╗██║   ██║██╔════╝╚══██╔══╝██╔══██╗██║   ██║████╗ ████║",
        "██║     ██║     ███████║██║   ██║███████╗   ██║   ██████╔╝██║   ██║██╔████╔██║",
        "██║     ██║     ██╔══██║██║   ██║╚════██║   ██║   ██╔══██╗██║   ██║██║╚██╔╝██║",
        "╚██████╗███████╗██║  ██║╚██████╔╝███████║   ██║   ██║  ██║╚██████╔╝██║ ╚═╝ ██║",
        " ╚═════╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝   ╚═╝   ╚═╝  ╚═╝ ╚═════╝ ╚═╝     ╚═╝",
    ];

    public static bool IsWanted =>
        !Console.IsOutputRedirected
        && Environment.GetEnvironmentVariable("NO_COLOR") is null
        && Environment.GetEnvironmentVariable("CLAUSTRUM_NO_SPLASH") is null
        && Console.WindowWidth >= Width + 2;

    public static void Run(bool animate = true)
    {
        Console.OutputEncoding = Encoding.UTF8;
        EnableVirtualTerminal();
        TextWriter o = Console.Out;
        o.Write("\x1b[?25l");
        try
        {
            if (animate)
                Decode(o);
            o.Write("\x1b[92m");
            foreach (string line in logo)
                o.WriteLine(line);
            o.Write("\x1b[0m\n\x1b[32m$\x1b[0m ");
            if (animate)
                Type(o);
            else
                o.Write(Tagline);
            o.WriteLine("\x1b[0m\n");
        }
        finally
        {
            o.Write("\x1b[?25h");
        }
    }

    private static void Decode(TextWriter o)
    {
        Random rng = new();
        StringBuilder frame = new(Width * logo.Length + 64);
        const int frames = 40;
        for (int f = 0; f <= frames; f++)
        {
            int settledUpTo = f * (Width + 12) / frames - 6;
            frame.Clear();
            if (f > 0)
                frame.Append("\x1b[").Append(logo.Length).Append('A');
            foreach (string line in logo)
            {
                for (int c = 0; c < Width; c++)
                {
                    char ch = line[c];
                    bool settled = c < settledUpTo || ch == ' ';
                    frame.Append(settled ? "\x1b[92m" : "\x1b[2;32m")
                         .Append(settled ? ch : Pool[rng.Next(Pool.Length)]);
                }
                frame.Append("\x1b[0m\n");
            }
            o.Write(frame.ToString());
            Thread.Sleep(32);
        }
        o.Write($"\x1b[{logo.Length}A");
    }

    private static void Type(TextWriter o)
    {
        Random rng = new();
        foreach (char ch in Tagline)
        {
            o.Write(ch);
            Thread.Sleep(rng.Next(28, 68));
        }
    }

    // Legacy conhost ignores ANSI unless virtual-terminal processing is switched on.
    private static void EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
            return;
        nint handle = GetStdHandle(-11);
        if (GetConsoleMode(handle, out uint mode))
            _ = SetConsoleMode(handle, mode | 0x0004);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
}
