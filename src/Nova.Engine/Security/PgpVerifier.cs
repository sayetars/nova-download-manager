using System.Text;
using Nova.Domain;
using Nova.Persistence;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Utilities.IO;

namespace Nova.Engine.Security;

/// <summary>Outcome of one PGP verification attempt.</summary>
public enum PgpVerifyOutcome
{
    /// <summary>Signature verified against a key we trust (imported in the keyring).</summary>
    Verified,
    /// <summary>Signature valid but signed by a key that is NOT in our keyring.</summary>
    NoKey,
    /// <summary>Signature did not match the file content (tampered or wrong file).</summary>
    Invalid,
    /// <summary>Signature/key data malformed.</summary>
    Malformed,
}

/// <summary>
/// v1.5.0 (G26): OpenPGP detached-signature verification for Metalink downloads
/// (&lt;signature type="pgp"&gt; per RFC 5854, plus .sig/.asc sidecars). Verification only —
/// Nova never signs, and only against keys the user explicitly imported.
/// </summary>
public static class PgpVerifier
{
    /// <summary>Verifies a detached signature (armored or binary) over the file bytes
    /// using the user's keyring; tries every key until one verifies.</summary>
    public static PgpVerifyOutcome VerifyDetached(byte[] data, string signatureText, IReadOnlyList<string> keyring)
    {
        PgpSignatureList sigs;
        try { sigs = ParseSignatures(signatureText); }
        catch (Exception) { return PgpVerifyOutcome.Malformed; }

        if (keyring.Count == 0) return PgpVerifyOutcome.NoKey;

        var sawKnownKey = false;
        foreach (var armored in keyring)
        {
            PgpPublicKeyRingBundle keys;
            try { keys = ParseKeyring(armored); }
            catch { continue; } // one broken key must not break the whole ring

            for (var i = 0; i < sigs.Count; i++)
            {
                var sig = sigs[i];
                foreach (Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKeyRing ring in keys.GetKeyRings())
                {
                    foreach (Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKey key in ring.GetPublicKeys())
                    {
                        if (!IsValidVerifier(key, sig.KeyId)) continue;
                        sawKnownKey = true;
                        try
                        {
                            sig.InitVerify(key);
                            sig.Update(data, 0, data.Length);
                            if (sig.Verify()) return PgpVerifyOutcome.Verified;
                        }
                        catch { /* try the next key */ }
                    }
                }
            }
        }
        return sawKnownKey ? PgpVerifyOutcome.Invalid : PgpVerifyOutcome.NoKey;
    }

    private static bool IsValidVerifier(Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKey key, long keyId) => key.KeyId == keyId;

    /// <summary>Extracts the primary-key fingerprint (40 uppercase hex) from an armored key.</summary>
    public static string? FingerprintOf(string armoredKey)
    {
        try
        {
            var bundle = ParseKeyring(armoredKey);
            foreach (Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKeyRing ring in bundle.GetKeyRings())
            {
                var primary = ring.GetPublicKey();
                if (primary is null) continue;
                return Convert.ToHexString(primary.GetFingerprint()).ToUpperInvariant();
            }
        }
        catch { /* caller reports the error */ }
        return null;
    }

    /// <summary>True when the armored text is shaped like a public-key block.</summary>
    public static bool LooksLikePublicKey(string armored) =>
        armored.Contains("-----BEGIN PGP PUBLIC KEY BLOCK-----", StringComparison.OrdinalIgnoreCase);

    private static PgpSignatureList ParseSignatures(string signatureText)
    {
        var trimmed = signatureText.Trim();
        if (trimmed.Contains("-----BEGIN PGP SIGNATURE-----", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(trimmed));
            using var arm = new ArmoredInputStream(ms);
            using var decoded = new MemoryStream();
            Streams.PipeAll(arm, decoded);
            return DecodeSignatures(decoded.ToArray());
        }
        return DecodeSignatures(Convert.FromBase64String(FixBase64(trimmed)));
    }

    private static PgpSignatureList DecodeSignatures(byte[] raw)
    {
        using var ms = new MemoryStream(raw);
        var factory = new PgpObjectFactory(ms);
        while (factory.NextPgpObject() is { } obj)
        {
            switch (obj)
            {
                case PgpSignatureList list when list.Count > 0:
                    return list;
                case PgpCompressedData comp:
                {
                    using var compIn = comp.GetDataStream();
                    var inner = new PgpObjectFactory(compIn);
                    while (inner.NextPgpObject() is { } innerObj)
                        if (innerObj is PgpSignatureList innerList && innerList.Count > 0)
                            return innerList;
                    break;
                }
            }
        }
        throw new FormatException("no signature packet found");
    }

    private static PgpPublicKeyRingBundle ParseKeyring(string armored)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(armored));
        using var arm = new ArmoredInputStream(ms);
        return new PgpPublicKeyRingBundle(arm);
    }

    /// <summary>Metalink &lt;signature&gt; content may be base64 of the armored text — try both.</summary>
    public static string NormalizeSignaturePayload(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Contains("-----BEGIN PGP SIGNATURE-----", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        // base64 decode → maybe armored, maybe binary signature
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(FixBase64(trimmed)));
            if (decoded.Contains("-----BEGIN PGP SIGNATURE-----", StringComparison.OrdinalIgnoreCase))
                return decoded;
        }
        catch { /* fallthrough: keep the original (binary sig base64) */ }
        return trimmed;
    }

    private static string FixBase64(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }
}

/// <summary>
/// v1.5.0 (G26): engine-side keyring — thin service over the pgp_keyring table with
/// fingerprint extraction on save, so the UI never has to parse OpenPGP.
/// Policy setting: engine.metalink_pgp = off | warn | require (default "warn").
/// </summary>
public sealed class PgpKeyringService
{
    public const string PolicySetting = "engine.metalink_pgp";

    private readonly PgpKeyringRepository _repo;

    public PgpKeyringService(PgpKeyringRepository repo) => _repo = repo;

    public IReadOnlyList<string> ArmoredKeys() => _repo.List().Select(k => k.ArmoredKey).ToList();

    public IReadOnlyList<Nova.Domain.PgpPublicKey> List() => _repo.List();

    /// <summary>Saves an armored public key; returns a UI-safe error or null on success.</summary>
    public string? Import(string label, string armored)
    {
        if (!PgpVerifier.LooksLikePublicKey(armored))
            return "The text is not an ASCII-armored PGP public key.";
        var fp = PgpVerifier.FingerprintOf(armored);
        if (fp is null)
            return "The key could not be parsed — check that the full block was pasted.";
        _repo.Save(new Nova.Domain.PgpPublicKey { Label = string.IsNullOrWhiteSpace(label) ? fp[..16] : label.Trim(), Fingerprint = fp, ArmoredKey = armored.Trim() });
        return null;
    }

    public bool Delete(long id) => _repo.Delete(id);
}

/// <summary>Policy for metalink/sidecar PGP verification results.</summary>
public enum PgpPolicy
{
    Off = 0,
    Warn = 1,
    Require = 2,
}

public static class PgpPolicyExtensions
{
    public static PgpPolicy Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "off" => PgpPolicy.Off,
        "require" => PgpPolicy.Require,
        _ => PgpPolicy.Warn,
    };
}
