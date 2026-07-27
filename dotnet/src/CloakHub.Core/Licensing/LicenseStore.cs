using System.Runtime.InteropServices;
using System.Text;

namespace CloakHub.Core.Licensing;

/// <summary>
/// Where the license key lives on disk, and how it is read and written.
/// <para>
/// The path is <c>~/.cloakbrowser/license.key</c> — <b>not</b> the Hub's own data
/// directory. That is deliberate: it is the exact location the <c>cloakbrowser</c>
/// CLI and the stealth binary read, so a key activated in the Hub also works for
/// every script on the machine, and a key the user already had works in the Hub
/// with no action at all.
/// </para>
/// <para>
/// The corollary is that this file is a <i>shared contract</i>, not private state.
/// Everything written here is plain UTF-8 with a single trailing newline, because
/// the other readers do a bare UTF-8 read and would choke on anything else.
/// </para>
/// </summary>
public sealed class LicenseStore
{
    /// <summary>Environment variable that relocates the whole CloakBrowser cache.</summary>
    public const string CacheVariable = "CLOAKBROWSER_CACHE_DIR";

    /// <summary>Environment variable holding a key directly, which wins over the file.</summary>
    public const string KeyVariable = "CLOAKBROWSER_LICENSE_KEY";

    private readonly Func<string, string?> _env;

    public LicenseStore(Func<string, string?>? env = null)
    {
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>
    /// The CloakBrowser cache directory, honouring <see cref="CacheVariable"/>.
    /// <para>
    /// Honoured rather than ignored so a user who already relocated the cache — to
    /// a bigger disk, typically, because the binaries are hundreds of megabytes —
    /// keeps one source of truth instead of the Hub reading a key from a directory
    /// nothing else uses.
    /// </para>
    /// </summary>
    public string CacheDir
    {
        get
        {
            var custom = _env(CacheVariable);
            if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cloakbrowser");
        }
    }

    public string KeyFile => Path.Combine(CacheDir, "license.key");

    /// <summary>
    /// The key currently in effect, or null.
    /// <para>
    /// The environment variable always wins, because the wrapper resolves it first:
    /// reporting the file's key while a variable is set would show the user a key
    /// that is not the one their launches actually use.
    /// </para>
    /// </summary>
    public string? Read()
    {
        var fromEnv = LicenseKeyFile.Normalise(_env(KeyVariable) ?? "");
        if (fromEnv.Length > 0) return fromEnv;

        try
        {
            var (key, _) = LicenseKeyFile.ReadFile(File.ReadAllBytes(KeyFile));
            return key.Length > 0 ? key : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the key comes from the environment and the file is being ignored.</summary>
    public bool KeyFromEnvironment => LicenseKeyFile.Normalise(_env(KeyVariable) ?? "").Length > 0;

    /// <summary>
    /// Persist a key as plain UTF-8, readable only by this user.
    /// <para>
    /// The 0600 mode is applied on Unix because the file is a bearer credential —
    /// anyone who can read it can consume the account's session seats. Windows
    /// inherits the user profile's ACL, which is already user-only.
    /// </para>
    /// </summary>
    public void Save(string key)
    {
        var normalised = LicenseKeyFile.Normalise(key);
        if (normalised.Length == 0)
            throw new ArgumentException("Refusing to save an empty license key.", nameof(key));

        Directory.CreateDirectory(CacheDir);
        File.WriteAllBytes(KeyFile, LicenseKeyFile.CanonicalBytes(normalised));
        Protect(KeyFile);
    }

    public void Clear()
    {
        try
        {
            File.Delete(KeyFile);
        }
        catch
        {
            // Already gone, or read-only. Either way there is nothing the user can
            // usefully do about it and the in-memory state is already cleared.
        }
    }

    /// <summary>
    /// Rewrite the key file as plain UTF-8 when it is stored in another encoding.
    /// <para>
    /// Worth doing because the damage is not confined to the Hub. PowerShell 5.1's
    /// <c>Set-Content</c> and <c>&gt;</c> both default to UTF-16LE, so a user who
    /// saved their key that way has a file that the CLI and the binary <i>also</i>
    /// misread — and the symptom is "invalid or expired key" for a key that is
    /// perfectly good. Fixing it in memory only would leave them with a working Hub
    /// and a CLI that still mysteriously fails.
    /// </para>
    /// <para>
    /// Returns true only when the bytes were actually changed, so the UI can say so
    /// rather than silently editing a file the user did not ask it to touch.
    /// </para>
    /// </summary>
    public bool RepairEncoding()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(KeyFile);
        }
        catch
        {
            return false; // no file is not a problem to fix
        }

        var (key, needsRepair) = LicenseKeyFile.ReadFile(bytes);
        if (key.Length == 0 || !needsRepair) return false;

        try
        {
            File.WriteAllBytes(KeyFile, LicenseKeyFile.CanonicalBytes(key));
            Protect(KeyFile);
            return true;
        }
        catch
        {
            // Read-only file or a permissions problem. The in-memory key still
            // works, so this must not be fatal — the user is no worse off than
            // before the repair was attempted.
            return false;
        }
    }

    private static void Protect(string file)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Some filesystems (exFAT on a relocated cache dir, a mounted share)
            // do not carry Unix modes. The key is still saved and usable.
        }
    }

    /// <summary>
    /// A key shortened for display.
    /// <para>
    /// A license key is a bearer credential, so it is never shown whole: the user
    /// needs to recognise <i>which</i> key is active, which the ends give them,
    /// and nothing more. A short key is fully masked rather than partly revealed,
    /// because keeping four characters of a twelve-character secret is a
    /// meaningful fraction of it.
    /// </para>
    /// </summary>
    public static string Mask(string? secret, int keep = 6)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        if (secret.Length <= keep * 2) return new string('\u2022', secret.Length);
        return $"{secret[..keep]}\u2026{secret[^keep..]}";
    }
}
