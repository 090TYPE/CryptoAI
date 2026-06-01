using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoAITerminal.LicenseBot;

/// <summary>
/// License payload + the same signed-token format the terminal validates
/// (see CryptoAITerminal.TerminalUI/Services/LicenseService.cs):
/// <c>base64url(payloadJson) "." base64(RSA-SHA256 signature)</c>.
/// Field names are camelCase so the terminal deserializes them into its
/// own <c>LicenseInfo</c> record.
/// </summary>
public sealed record LicenseInfo(
    string Name,
    string Edition,
    DateTime? Expires,
    string? Machine,
    DateTime Issued);

public static class LicenseSigner
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Sign a license with the seller's RSA private key (PEM).</summary>
    public static string CreateToken(LicenseInfo info, string privateKeyPem)
    {
        var json = JsonSerializer.Serialize(info, JsonOpts);
        var payloadBytes = Encoding.UTF8.GetBytes(json);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var sig = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Base64UrlEncode(payloadBytes) + "." + Convert.ToBase64String(sig);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
