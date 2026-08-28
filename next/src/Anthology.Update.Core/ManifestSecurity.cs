using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public static class ManifestSecurity
{
    public const string Algorithm = "ecdsa-p256-sha256";
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new(ManifestJson.Options)
    {
        WriteIndented = false,
    };

    public static SignedUpdateManifest Sign(UpdateManifest manifest, ECDsa privateKey, string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        var signature = privateKey.SignData(
            GetCanonicalBytes(manifest),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return new SignedUpdateManifest(
            manifest,
            new ManifestSignature(Algorithm, keyId, Convert.ToBase64String(signature)));
    }

    public static bool Verify(SignedUpdateManifest manifest, ECDsa publicKey)
    {
        if (!string.Equals(manifest.Signature.Algorithm, Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var signature = Convert.FromBase64String(manifest.Signature.Value);
            return publicKey.VerifyData(
                GetCanonicalBytes(manifest.Payload),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static byte[] GetCanonicalBytes(UpdateManifest manifest)
        => JsonSerializer.SerializeToUtf8Bytes(manifest, CanonicalJsonOptions);
}
