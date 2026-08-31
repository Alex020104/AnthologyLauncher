using System.Security.Cryptography;

namespace Anthology.Releaser.Core;

/// <summary>
/// Pins every public A.N.T.H.O.L.O.G.Y release to the original production key.
/// The private key is never embedded in the application or repository.
/// </summary>
public static class ProductionSigningKeyPolicy
{
    public const string KeyId = "anthology-production-01";

    // SHA-256 of the canonical DER SubjectPublicKeyInfo, not of PEM text.
    // This remains stable when PEM line endings or formatting change.
    public const string PublicKeyFingerprint = "610410C11B3307D709E4452E569BAB2217E973D48A2FD0729BBD21BE124B20C2";

    public static void Validate(ReleaserMachineSettings machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (!string.Equals(machine.KeyId?.Trim(), KeyId, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                $"Публичные релизы разрешено подписывать только закреплённым ключом {KeyId}.");
        }

        var privatePath = RequireExistingPath(machine.PrivateKeyPath, "закрытый production-ключ");
        var publicPath = RequireExistingPath(machine.PublicKeyPath, "публичный production-ключ");

        using var privateKey = ECDsa.Create();
        using var publicKey = ECDsa.Create();
        try
        {
            privateKey.ImportFromPem(File.ReadAllText(privatePath));
            publicKey.ImportFromPem(File.ReadAllText(publicPath));
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException("Файлы production-ключей повреждены или имеют неверный формат PEM.", exception);
        }

        var derivedPublicKey = privateKey.ExportSubjectPublicKeyInfo();
        var configuredPublicKey = publicKey.ExportSubjectPublicKeyInfo();
        if (!CryptographicOperations.FixedTimeEquals(derivedPublicKey, configuredPublicKey))
        {
            throw new CryptographicException(
                "Закрытый и публичный production-ключи не образуют одну пару. Публикация остановлена.");
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(configuredPublicKey));
        if (!string.Equals(fingerprint, PublicKeyFingerprint, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                $"Выбран другой production-ключ ({fingerprint}). Ожидался закреплённый ключ {PublicKeyFingerprint}.");
        }
    }

    public static void RestorePublicKey(string privateKeyPath, string publicKeyPath)
    {
        var privatePath = RequireExistingPath(privateKeyPath, "закрытый production-ключ");
        var publicPath = Path.GetFullPath(publicKeyPath);
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(File.ReadAllText(privatePath));

        var exported = privateKey.ExportSubjectPublicKeyInfo();
        var fingerprint = Convert.ToHexString(SHA256.HashData(exported));
        if (!string.Equals(fingerprint, PublicKeyFingerprint, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                "Из выбранного закрытого ключа нельзя восстановить production-ключ: это другая ключевая пара.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
        File.WriteAllText(publicPath, privateKey.ExportSubjectPublicKeyInfoPem());
    }

    private static string RequireExistingPath(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileNotFoundException($"Не указан {description}. Восстановите текущий ключ из резервной копии; новый ключ создавать нельзя.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Не найден {description}. Восстановите текущий ключ из резервной копии; новый ключ создавать нельзя.",
                fullPath);
        }
        return fullPath;
    }
}
