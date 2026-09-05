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
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static SignedPackageIntegrityCatalog Sign(
        PackageIntegrityCatalog catalog,
        ECDsa privateKey,
        string keyId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        var signature = privateKey.SignData(
            GetCanonicalBytes(catalog),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignedPackageIntegrityCatalog(
            catalog,
            new ManifestSignature(Algorithm, keyId, Convert.ToBase64String(signature)));
    }

    public static bool Verify(SignedPackageIntegrityCatalog catalog, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(publicKey);
        if (!string.Equals(catalog.Signature.Algorithm, Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var signature = Convert.FromBase64String(catalog.Signature.Value);
            return publicKey.VerifyData(
                GetCanonicalBytes(catalog.Payload),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static SignedReleaseHistory Sign(
        ReleaseHistoryCatalog catalog,
        ECDsa privateKey,
        string keyId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        var signature = privateKey.SignData(
            GetCanonicalBytes(catalog),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignedReleaseHistory(
            catalog,
            new ManifestSignature(Algorithm, keyId, Convert.ToBase64String(signature)));
    }

    public static bool Verify(SignedReleaseHistory history, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(publicKey);
        if (!string.Equals(history.Signature.Algorithm, Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var signature = Convert.FromBase64String(history.Signature.Value);
            return publicKey.VerifyData(
                GetCanonicalBytes(history.Payload),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static byte[] GetCanonicalBytes(UpdateManifest manifest)
        => JsonSerializer.SerializeToUtf8Bytes(manifest, CanonicalJsonOptions);

    public static byte[] GetCanonicalBytes(PackageIntegrityCatalog catalog)
        => JsonSerializer.SerializeToUtf8Bytes(catalog, CanonicalJsonOptions);

    public static byte[] GetCanonicalBytes(ReleaseHistoryCatalog catalog)
        => JsonSerializer.SerializeToUtf8Bytes(catalog, CanonicalJsonOptions);
}
