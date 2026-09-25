using System.Security.Cryptography;
using System.Text;

namespace RealmForge.Bridge.Security;

/// <summary>
/// A random per-install secret. CORS only decides whether a page may READ a response; it does not stop a page on any
/// site from SENDING a request to localhost. The token does: only the dashboard (and the worker) know it.
/// </summary>
public sealed class AccessToken
{
    public const string HeaderName = "X-RealmForge-Token";

    readonly byte[] expected;

    public string FilePath { get; }

    public AccessToken(BridgeOptions options)
    {
        FilePath = options.TokenFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RealmForge", "bridge-token.txt");

        string token = File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : "";
        if (token.Length < 32)
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, token);
        }
        expected = Encoding.UTF8.GetBytes(token);
    }

    public bool Matches(string? presented) =>
        presented is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), expected);
}
