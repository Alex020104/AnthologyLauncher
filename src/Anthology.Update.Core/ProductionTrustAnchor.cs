using System.Security.Cryptography;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public static class ProductionTrustAnchor
{
    public const string KeyId = "anthology-production-01";
    public const string PublicKeyFingerprint = "610410C11B3307D709E4452E569BAB2217E973D48A2FD0729BBD21BE124B20C2";

    public static void ValidatePublicKey(string publicKeyPath)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPath) || !File.Exists(publicKeyPath))
        {
            throw new FileNotFoundException("Не найден встроенный публичный ключ обновлений.", publicKeyPath);
        }

        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(publicKeyPath));
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException("Встроенный публичный ключ обновлений повреждён.", exception);
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        if (!string.Equals(fingerprint, PublicKeyFingerprint, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                $"Лаунчер получил неизвестный публичный ключ ({fingerprint}). Ожидался production-ключ {PublicKeyFingerprint}.");
        }
    }

    public static void ValidateManifest(SignedUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Signature.KeyId, KeyId, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                $"Манифест подписан неизвестным ключом '{manifest.Signature.KeyId}'. Ожидался '{KeyId}'.");
        }
    }

    public static void ValidateReleaseHistory(SignedReleaseHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (!string.Equals(history.Signature.KeyId, KeyId, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                $"История версий подписана неизвестным ключом '{history.Signature.KeyId}'. Ожидался '{KeyId}'.");
        }
    }
}
