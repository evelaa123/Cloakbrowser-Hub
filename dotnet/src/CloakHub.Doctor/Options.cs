using CloakHub.Core.Branding;
using CloakHub.Core.Platform;

namespace CloakHub.Doctor;

/// <summary>
/// Command-line options.
/// <para>
/// Hand-parsed rather than pulling in a parsing library. The surface is four
/// switches, and the point of this tool is to be a small self-contained
/// executable a user can download and run — every dependency is weight in that
/// download for no benefit here.
/// </para>
/// </summary>
public sealed record Options
{
    /// <summary>Where generated assets go.</summary>
    public string OutputDir { get; init; } = Path.Combine(Directory.GetCurrentDirectory(), "cloakhub-doctor");

    /// <summary>
    /// OS to report for, overriding detection.
    /// <para>
    /// Exists so the Windows and macOS branches can be inspected from any machine.
    /// Every platform decision in the core takes the OS as a parameter precisely so
    /// this is possible, and being able to show a user on Linux what their Windows
    /// build will do is worth exposing.
    /// </para>
    /// </summary>
    public BadgeOs? ForceOs { get; init; }

    /// <summary>Whether to write files at all. False makes the run read-only.</summary>
    public bool WriteAssets { get; init; } = true;

    public bool ShowHelp { get; init; }

    /// <summary>Parse argv. Returns null on a bad argument, having explained why.</summary>
    public static Options? Parse(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    return options with { ShowHelp = true };

                case "-o" or "--out" or "--output":
                    if (i + 1 >= args.Length)
                    {
                        Fail($"{arg} needs a directory.");
                        return null;
                    }
                    options = options with { OutputDir = Path.GetFullPath(args[++i]) };
                    break;

                case "--os":
                    if (i + 1 >= args.Length)
                    {
                        Fail("--os needs one of: windows, linux, macos.");
                        return null;
                    }
                    var parsed = ParseOs(args[++i]);
                    if (parsed is null)
                    {
                        Fail($"\"{args[i]}\" is not one of: windows, linux, macos.");
                        return null;
                    }
                    options = options with { ForceOs = parsed };
                    break;

                case "--no-write" or "--dry-run":
                    options = options with { WriteAssets = false };
                    break;

                case "--no-colour" or "--no-color":
                    Console.Output.UseColour = false;
                    break;

                default:
                    Fail($"Unrecognised argument \"{arg}\". Try --help.");
                    return null;
            }
        }

        return options;
    }

    /// <summary>The OS this run reports for.</summary>
    public BadgeOs ResolvedOs => ForceOs ?? HostOs.Current;

    /// <summary>True when the report describes a different OS than the host.</summary>
    public bool IsSimulated => ForceOs is not null && ForceOs != HostOs.Current;

    private static BadgeOs? ParseOs(string value) => value.Trim().ToLowerInvariant() switch
    {
        "windows" or "win" or "w" => BadgeOs.Windows,
        "linux" or "l" => BadgeOs.Linux,
        "macos" or "mac" or "osx" or "darwin" or "m" => BadgeOs.MacOs,
        _ => null,
    };

    // Errors go to stderr so a redirected report file stays clean, and so a shell
    // pipeline can separate the two.
    private static void Fail(string message) => System.Console.Error.WriteLine($"cloakhub-doctor: {message}");

    public static void PrintUsage()
    {
        System.Console.WriteLine("""
            CloakBrowser Hub — diagnostics

            Reports what the Hub will do on this machine and generates the real
            per-instance badge icons so they can be inspected before any browser runs.
            Nothing is installed and no system setting is changed.

            Usage:
              cloakhub-doctor [options]

            Options:
              -o, --out <dir>     Where to write generated assets.
                                  Default: ./cloakhub-doctor
                  --os <name>     Report for windows, linux or macos instead of
                                  detecting. Useful for previewing another platform.
                  --no-write      Report only; create no files.
                  --no-colour     Disable coloured output.
              -h, --help          This text.

            Examples:
              cloakhub-doctor
              cloakhub-doctor --out C:\Temp\hub-check
              cloakhub-doctor --os macos --no-write
            """);
    }
}
