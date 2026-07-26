using System.Text;
using System.Text.RegularExpressions;

namespace CloakHub.Core.Licensing;

/// <summary>
/// Reading and normalising the CloakBrowser license-key file.
/// <para>
/// The file is very often <i>not</i> written by this app. <c>Set-Content</c> in
/// Windows PowerShell 5.1 defaults to UTF-16LE, so <c>... &gt; license.key</c>
/// produces a UTF-16 file with a BOM. Decoding that as UTF-8 yields a key with a
/// U+FFFD replacement char followed by every character separated by NULs — which
/// still passes a non-empty check and gets sent to the server, where it is
/// rejected. The user sees "invalid or expired" for a key that is perfectly good,
/// with no hint that an encoding is to blame.
/// </para>
/// <para>
/// <b>Where .NET is genuinely better than the Node original:</b> the manual BOM
/// sniffer needed there is unnecessary here. <c>StreamReader</c> with
/// <c>detectEncodingFromByteOrderMarks</c> handles UTF-8, UTF-16LE and UTF-16BE
/// natively, so only the no-BOM UTF-16 case needs hand-rolling.
/// </para>
/// </summary>
public static partial class LicenseKeyFile
{
    /// <summary>
    /// Decode key-file bytes into text.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";

        // No BOM, but NUL bytes at odd offsets mean UTF-16LE written without one —
        // `printf '%s' key | iconv -t UTF-16LE` and several editors do exactly
        // this. StreamReader cannot detect it, so it is handled explicitly.
        // Checked before the BOM path because a BOM-less file has no marker for
        // StreamReader to find and would silently decode as mojibake UTF-8.
        var hasBom =
            (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) ||
            (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) ||
            (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        if (!hasBom && bytes.Length >= 4 && bytes[1] == 0x00 && bytes[3] == 0x00)
            return Encoding.Unicode.GetString(bytes);

        // Same, for BOM-less UTF-16BE (NULs at even offsets).
        if (!hasBom && bytes.Length >= 4 && bytes[0] == 0x00 && bytes[2] == 0x00)
            return Encoding.BigEndianUnicode.GetString(bytes);

        using var ms = new MemoryStream(bytes);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Normalise a pasted or file-read key.
    /// <para>
    /// One function for both paths, so activation and file reads cannot disagree
    /// about what counts as the same key: a value that works when pasted must
    /// also work after being saved and read back.
    /// </para>
    /// <para>Handles what people actually have in these files:</para>
    /// <list type="bullet">
    ///   <item>a trailing newline, or CRLF from any Windows editor;</item>
    ///   <item>surrounding quotes, from <c>echo "KEY" &gt; license.key</c>;</item>
    ///   <item><c>CLOAKBROWSER_LICENSE_KEY=KEY</c>, from pasting an env-var line;</item>
    ///   <item>extra lines, e.g. a comment above the key — first non-empty wins;</item>
    ///   <item>stray NULs and the U+FEFF BOM left by an encoding conversion.</item>
    /// </list>
    /// </summary>
    public static string Normalise(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var text = raw.Replace("\u0000", "").Replace("\uFEFF", "");

        var line = text
            .Split('\n')
            .Select(l => l.Trim('\r', ' ', '\t'))
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        if (line is null) return "";

        text = line;

        var eq = EnvVarLine().Match(text);
        if (eq.Success) text = eq.Groups[1].Value.Trim();

        // Strip matching quotes only. An unpaired quote is more likely part of a
        // mistyped key than a quoting artefact, and silently removing it would
        // send a different key than the user believes they entered.
        if (text.Length >= 2 &&
            ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            text = text[1..^1].Trim();

        return text;
    }

    /// <summary>
    /// Read and normalise a key file, reporting whether it needs rewriting as UTF-8.
    /// <para>
    /// Rewriting matters beyond this app: the upstream CLI and the binary read the
    /// same file as UTF-8, so a UTF-16 file breaks them too. Repairing it in place
    /// fixes all three at once instead of only the Hub.
    /// </para>
    /// </summary>
    public static (string Key, bool NeedsRepair) ReadFile(byte[] bytes)
    {
        var key = Normalise(Decode(bytes));
        if (key.Length == 0) return ("", false);

        // A file already in plain UTF-8 with no BOM and no trailing junk needs no
        // repair. Comparing round-tripped bytes is stricter than checking the BOM:
        // it also catches a stray env-var prefix or quotes that other readers
        // would choke on.
        var canonical = Encoding.UTF8.GetBytes(key + "\n");
        return (key, !bytes.AsSpan().SequenceEqual(canonical));
    }

    /// <summary>Bytes to write for a repaired file: plain UTF-8, no BOM, one trailing newline.</summary>
    public static byte[] CanonicalBytes(string key) => Encoding.UTF8.GetBytes(key + "\n");

    [GeneratedRegex(@"^[A-Z_]*LICENSE[A-Z_]*\s*=\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EnvVarLine();
}
