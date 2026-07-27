using System.Text;
using CloakHub.Core.Binaries;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace CloakHub.Core.Tests;

/// <summary>
/// The manifest is what stands between the user and a tampered browser binary.
/// <para>
/// These tests exist because every failure in this file is silent by nature: a
/// signature check that accepts everything looks exactly like one that works, right
/// up until someone serves a modified archive. So the cases asserted here are the
/// ones where a broken implementation still <i>appears</i> to function.
/// </para>
/// </summary>
public class ReleaseManifestTests
{
    // RFC 8032 section 7.1, test vector 2. A known-good signature over a known
    // message: if the verifier cannot accept this, it accepts nothing, and if it
    // accepts the mutations below it accepts anything.
    private static readonly byte[] Rfc8032PublicKey =
        Convert.FromHexString("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");

    private static readonly byte[] Rfc8032Message = [0x72];

    private static readonly byte[] Rfc8032Signature = Convert.FromHexString(
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da" +
        "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");

    private static byte[] SigFile(byte[] signature) =>
        Encoding.UTF8.GetBytes(Convert.ToBase64String(signature));

    private static string KeyB64(byte[] key) => Convert.ToBase64String(key);

    [Fact]
    public void A_genuine_signature_verifies()
    {
        // The load-bearing assertion. Ed25519 came from a library rather than being
        // hand-written precisely so this holds; the test proves the wiring — key
        // encoding, byte order, detached-signature format — is right too.
        Assert.True(ReleaseManifest.VerifySignature(
            Rfc8032Message, SigFile(Rfc8032Signature), [KeyB64(Rfc8032PublicKey)]));
    }

    [Fact]
    public void A_tampered_manifest_is_rejected()
    {
        // The actual attack: the archive is swapped, so the checksum line changes,
        // so the manifest bytes no longer match the signature.
        Assert.False(ReleaseManifest.VerifySignature(
            [0x73], SigFile(Rfc8032Signature), [KeyB64(Rfc8032PublicKey)]));
    }

    [Fact]
    public void A_signature_from_an_unpinned_key_is_rejected()
    {
        // Someone signs their own manifest with their own key. Well-formed in every
        // respect — the only thing wrong with it is that we do not trust the signer,
        // which is the entire point of pinning.
        //
        // The seed must not be RFC 8032's, because that seed's public key IS the
        // pinned one above: signing with it produces a signature that *should*
        // verify, and an earlier version of this test asserted the opposite and
        // failed. A distinct seed is what actually makes the signer untrusted.
        var attacker = new Ed25519PrivateKeyParameters(
            Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"), 0);

        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, attacker);
        signer.BlockUpdate(Rfc8032Message, 0, Rfc8032Message.Length);
        var forged = signer.GenerateSignature();

        Assert.False(ReleaseManifest.VerifySignature(
            Rfc8032Message, SigFile(forged), [KeyB64(Rfc8032PublicKey)]));
    }

    [Fact]
    public void A_damaged_signature_file_is_rejected_rather_than_decoded_loosely()
    {
        // Lenient base64 decoders drop invalid characters instead of failing, which
        // would turn "your .sig is corrupted" into "your binary may be tampered
        // with" — the same refusal, but pointing the user at the wrong problem.
        var damaged = Encoding.UTF8.GetBytes(Convert.ToBase64String(Rfc8032Signature) + "!!");

        Assert.False(ReleaseManifest.VerifySignature(
            Rfc8032Message, damaged, [KeyB64(Rfc8032PublicKey)]));
    }

    [Fact]
    public void No_pinned_keys_means_no_trust()
    {
        // Fails closed. If the pinned-key list were ever empty — a bad merge, a
        // stripped resource — the safe answer is to verify nothing, not everything.
        Assert.False(ReleaseManifest.VerifySignature(
            Rfc8032Message, SigFile(Rfc8032Signature), []));
    }

    [Fact]
    public void The_binary_mode_marker_is_not_part_of_the_filename()
    {
        // coreutils writes "hash *name" for binary mode. Keeping the asterisk would
        // mean no entry ever matched an archive name, so every download would be
        // refused as unlisted — a total outage that looks like a server problem.
        var sums = ReleaseManifest.ParseChecksums(
            $"{new string('a', 64)} *cloakbrowser-linux-x64.tar.gz\n");

        Assert.True(sums.ContainsKey("cloakbrowser-linux-x64.tar.gz"));
    }

    [Fact]
    public void The_version_line_is_read_so_an_old_manifest_cannot_be_replayed()
    {
        // Without this binding, a genuine signed manifest from an older release
        // certifies that release's archive — a downgrade attack built entirely from
        // legitimate signed bytes.
        var text = $"version=146.0.7680.177.5\n{new string('b', 64)}  cloakbrowser-linux-x64.tar.gz\n";

        Assert.Equal("146.0.7680.177.5", ReleaseManifest.ParseVersion(text));
    }

    [Fact]
    public void A_manifest_without_a_version_line_reports_null_rather_than_guessing()
    {
        Assert.Null(ReleaseManifest.ParseVersion($"{new string('c', 64)}  archive.tar.gz\n"));
    }
}
