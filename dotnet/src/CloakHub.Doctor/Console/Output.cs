namespace CloakHub.Doctor.Console;

using Sys = System.Console;

/// <summary>
/// Console formatting for the diagnostics report.
/// <para>
/// Colour is applied through a helper that always restores the previous value,
/// because this tool is meant to be run in a Windows terminal the user then keeps
/// using — leaving the console in a coloured state would be a visible defect in a
/// diagnostic tool, which is the last place it belongs.
/// </para>
/// </summary>
public static class Output
{
    /// <summary>
    /// Whether to emit ANSI/colour at all.
    /// <para>
    /// Honours <c>NO_COLOR</c> (the de-facto convention) and switches off when
    /// output is redirected, so piping the report to a file produces clean text
    /// rather than escape sequences.
    /// </para>
    /// </summary>
    public static bool UseColour { get; set; } =
        Environment.GetEnvironmentVariable("NO_COLOR") is null && !Sys.IsOutputRedirected;

    public static void Title(string text)
    {
        Sys.WriteLine();
        In(ConsoleColor.White, () => Sys.WriteLine(text));
        In(ConsoleColor.DarkGray, () => Sys.WriteLine(new string('=', Math.Min(text.Length, 78))));
    }

    public static void Section(string text)
    {
        Sys.WriteLine();
        In(ConsoleColor.Cyan, () => Sys.WriteLine(text));
        In(ConsoleColor.DarkGray, () => Sys.WriteLine(new string('-', Math.Min(text.Length, 78))));
    }

    /// <summary>A label/value row, padded so values line up down the report.</summary>
    public static void Item(string label, string value)
    {
        In(ConsoleColor.DarkGray, () => Sys.Write($"  {label,-26}"));
        Sys.WriteLine(value);
    }

    public static void Ok(string text) => Tagged("OK  ", ConsoleColor.Green, text);
    public static void Warn(string text) => Tagged("WARN", ConsoleColor.Yellow, text);
    public static void Fail(string text) => Tagged("FAIL", ConsoleColor.Red, text);
    public static void Info(string text) => Tagged("--  ", ConsoleColor.DarkGray, text);

    public static void Plain(string text = "") => Sys.WriteLine(text);

    /// <summary>
    /// Body text wrapped to the terminal width.
    /// <para>
    /// The report carries several paragraphs of genuine explanation — why a MAC
    /// change is invisible to websites, why a badge degraded to an overlay — and
    /// unwrapped prose in an 80-column window is unreadable, which would defeat
    /// the purpose of writing it.
    /// </para>
    /// </summary>
    public static void Paragraph(string text, int indent = 2)
    {
        var width = Width() - indent;
        if (width < 20) width = 60;

        var pad = new string(' ', indent);
        var line = new System.Text.StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                Sys.WriteLine(pad + line);
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) Sys.WriteLine(pad + line);
    }

    public static void Bullet(string text) => Paragraph("- " + text, 4);

    private static void Tagged(string tag, ConsoleColor colour, string text)
    {
        Sys.Write("  [");
        In(colour, () => Sys.Write(tag));
        Sys.Write("] ");
        Sys.WriteLine(text);
    }

    private static void In(ConsoleColor colour, Action write)
    {
        if (!UseColour) { write(); return; }

        var previous = Sys.ForegroundColor;
        try
        {
            Sys.ForegroundColor = colour;
            write();
        }
        finally
        {
            Sys.ForegroundColor = previous;
        }
    }

    /// <summary>
    /// Usable console width.
    /// <para>
    /// <c>WindowWidth</c> throws when there is no console attached (redirected
    /// output, a CI runner, a Windows service), so it is guarded rather than read
    /// directly. A crash while formatting a diagnostic report would be absurd.
    /// </para>
    /// </summary>
    private static int Width()
    {
        try
        {
            var w = Sys.WindowWidth;
            return w is > 40 and < 200 ? w : 80;
        }
        catch
        {
            return 80;
        }
    }
}
