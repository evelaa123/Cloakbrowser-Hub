using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace CloakHub.Core.Binaries;

/// <summary>
/// The signed <c>SHA256SUMS</c> that certifies a download.
/// <para>
/// The download origin alone certifies nothing: whoever can serve a tampered
/// archive can serve a tampered checksum file alongside it. So the manifest carries
/// a detached Ed25519 signature, verified against keys pinned into this binary, and
/// the signature is checked <b>before</b> any hash in the manifest is trusted.
/// </para>
/// <para>
/// The manifest also names the version it describes. Without that binding an
/// attacker could replay a genuine, correctly-signed manifest from an older release
/// to certify that release's archive — a downgrade to a build with known holes,
/// using nothing but legitimate signed bytes.
/// </para>
/// </summary>
public static partial class ReleaseManifest
{
    /// <summary>
    /// Parse <c>&lt;hash&gt;  &lt;filename&gt;</c> lines into a lookup.
    /// <para>
    /// The <c>*</c> before a filename is coreutils' binary-mode marker and is not
    /// part of the name; leaving it in would mean no entry ever matched an archive
    /// name and every download would be rejected as unlisted.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var match = ChecksumLine().Match(line);
            if (!match.Success) continue;

            result[match.Groups[2].Value] = match.Groups[1].Value.ToLowerInvariant();
        }

        return result;
    }

    /// <summary>
    /// The <c>version=&lt;v&gt;</c> line, or null when the manifest predates it.
    /// <para>
    /// Written without internal whitespace so that older parsers, which accept only
    /// <c>hash  filename</c> lines, ignore it rather than choking.
    /// </para>
    /// </summary>
    public static string? ParseVersion(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("version=", StringComparison.Ordinal))
            {
                var value = line["version=".Length..].Trim();
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Verify a detached signature over the manifest bytes.
    /// <para>
    /// Fails closed: an unparsable signature, an unparsable pinned key, or a
    /// signature that no pinned key validates all produce false. There is
    /// deliberately no environment variable that skips this — a checksum can be
    /// skipped when a user knows their mirror is fine, but a signature check that
    /// can be turned off is not a signature check.
    /// </para>
    /// <para>
    /// Ed25519 comes from BouncyCastle rather than the base library: .NET 8 has no
    /// public Ed25519 API (it arrived in .NET 10). Writing the curve arithmetic by
    /// hand was the alternative and it is the wrong trade at any size — a subtly
    /// wrong implementation does not fail loudly, it quietly accepts signatures it
    /// should reject, which turns the tamper check into decoration.
    /// </para>
    /// </summary>
    public static bool VerifySignature(
        byte[] manifestBytes,
        byte[] signatureFileBytes,
        IEnumerable<string>? pinnedKeys = null)
    {
        byte[] signature;
        try
        {
            var text = Encoding.UTF8.GetString(signatureFileBytes).Trim();

            // Round-tripped rather than merely decoded. Several base64 decoders
            // silently drop invalid characters, so a corrupted .sig would decode to
            // *something* and then simply fail verification — reported to the user
            // as "the binary may be tampered with" when the truth is "the signature
            // file is damaged". Those call for different actions.
            signature = Convert.FromBase64String(text);
            if (!string.Equals(Convert.ToBase64String(signature), text, StringComparison.Ordinal))
                return false;
        }
        catch
        {
            return false;
        }

        // Ed25519 signatures are exactly 64 bytes. Checking here keeps the loop
        // below from depending on how each backend reacts to a wrong length.
        if (signature.Length != 64) return false;

        foreach (var keyB64 in pinnedKeys ?? BinaryCatalog.SigningPublicKeys)
        {
            byte[] publicKey;
            try
            {
                publicKey = Convert.FromBase64String(keyB64.Trim());
            }
            catch
            {
                continue; // an unparsable pinned key — another may still validate
            }

            if (publicKey.Length != 32) continue;

            try
            {
                var verifier = new Ed25519Signer();
                verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(publicKey, 0));
                verifier.BlockUpdate(manifestBytes, 0, manifestBytes.Length);
                if (verifier.VerifySignature(signature)) return true;
            }
            catch
            {
                // Wrong key, or a malformed one that makes the verifier throw
                // rather than return false. Either way this key did not match, so
                // try the next and ultimately fail closed below.
            }
        }

        return false;
    }

    /// <summary>SHA-256 of a file, lowercase hex, streamed so a 400 MB archive is not buffered.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancel = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [GeneratedRegex(@"^([a-fA-F0-9]{64})\s+\*?(.+)$")]
    private static partial Regex ChecksumLine();
}
