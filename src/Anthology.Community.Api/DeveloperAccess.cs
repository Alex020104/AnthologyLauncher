using System.Security.Cryptography;
using System.Text;

namespace Anthology.Community.Api;

public sealed class DeveloperAccess
{
    private readonly byte[] _tokenBytes;

    public DeveloperAccess()
    {
        var configured = Environment.GetEnvironmentVariable("ANTHOLOGY_DEVELOPER_TOKEN");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _tokenBytes = Encoding.UTF8.GetBytes(configured.Trim());
            TokenFilePath = null;
            return;
        }

        var configRoot = Path.Combine(CommunityPaths.ResolveDataRoot(), "Config");
        Directory.CreateDirectory(configRoot);
        TokenFilePath = Path.Combine(configRoot, "developer-token.txt");
        if (!File.Exists(TokenFilePath))
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var temporary = TokenFilePath + $".tmp-{Guid.NewGuid():N}";
            File.WriteAllText(temporary, token, new UTF8Encoding(false));
            File.Move(temporary, TokenFilePath);
        }

        var stored = File.ReadAllText(TokenFilePath).Trim();
        if (stored.Length < 32)
        {
            throw new InvalidDataException("Developer token is missing or too short.");
        }
        _tokenBytes = Encoding.UTF8.GetBytes(stored);
    }

    public string? TokenFilePath { get; }

    public bool IsAuthorized(HttpRequest request)
    {
        var supplied = request.Headers["X-Anthology-Developer-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return false;
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.Trim());
        return suppliedBytes.Length == _tokenBytes.Length
               && CryptographicOperations.FixedTimeEquals(_tokenBytes, suppliedBytes);
    }

    public bool IsAuthorized(HttpContext? context) =>
        context is not null && IsAuthorized(context.Request);
}
