namespace Tk.Filters;

/// <summary>ANSI color helpers for terminal output.</summary>
public static class Ansi
{
    public static readonly bool Enabled = !Console.IsOutputRedirected
        && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public static string Green(string text) => Wrap(text, "32");
    public static string Red(string text) => Wrap(text, "31");
    public static string Yellow(string text) => Wrap(text, "33");
    public static string Dim(string text) => Wrap(text, "2");
    public static string Cyan(string text) => Wrap(text, "36");

    private static string Wrap(string text, string code) =>
        Enabled ? $"\x1b[{code}m{text}\x1b[0m" : text;
}
