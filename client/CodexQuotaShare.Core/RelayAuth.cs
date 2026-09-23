using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaShare.Core;

/// HMAC request signing shared with relay/src/auth.ts. The caller supplies canonical JSON
/// so transport formatting (whitespace/property order) cannot change the signed bytes.
public static class RelayAuth
{
    public static string CanonicalMessage(string method, string path, long timestamp, string nonce, long sequence, string canonicalPayloadJson) =>
        string.Join('\n', method.ToUpperInvariant(), path, timestamp, nonce, sequence, canonicalPayloadJson);

    public static string Sign(string base64UrlSecret, string method, string path, long timestamp, string nonce, long sequence, string canonicalPayloadJson)
    {
        var secret = Base64UrlDecode(base64UrlSecret);
        var message = Encoding.UTF8.GetBytes(CanonicalMessage(method, path, timestamp, nonce, sequence, canonicalPayloadJson));
        return Base64UrlEncode(HMACSHA256.HashData(secret, message));
    }

    public static bool Verify(string base64UrlSecret, string method, string path, long timestamp, string nonce, long sequence, string canonicalPayloadJson, string signature)
    {
        try
        {
            var expected = Base64UrlDecode(Sign(base64UrlSecret, method, path, timestamp, nonce, sequence, canonicalPayloadJson));
            var supplied = Base64UrlDecode(signature);
            return CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
        catch (FormatException) { return false; }
    }

    public static string CanonicalJson(JsonElement value)
    {
        using var document = JsonDocument.Parse(value.GetRawText());
        return WriteCanonical(document.RootElement);
    }

    public static string CanonicalJson<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return WriteCanonical(document.RootElement);
    }

    public static string CreateNonce()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Base64UrlEncode(bytes);
    }

    private static string WriteCanonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(',', value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + WriteCanonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(',', value.EnumerateArray().Select(WriteCanonical)) + "]",
        _ => value.GetRawText()
    };

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
