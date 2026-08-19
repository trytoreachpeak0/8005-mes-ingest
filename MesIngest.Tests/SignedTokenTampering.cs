namespace MesIngest.Tests;

/// <summary>
/// Signed snapshot references and cursors are <c>base64url(payload).base64url(signature)</c>
/// with the <c>=</c> padding trimmed.
///
/// Flipping the token's <em>last</em> character is not a reliable way to tamper with
/// one. A 32-byte HMAC encodes to a final group of two bytes in three characters, so
/// the last character's low two bits are padding that decodes to nothing: when the
/// token happens to end in 'A' or 'B', an 'A'/'B' flip leaves the decoded bytes
/// identical and the host accepts the token, exactly as it should. That made the
/// tampering assertions fail in roughly one run in thirty.
///
/// The first character of the signature carries six significant bits, so changing it
/// always changes the signature the host verifies.
/// </summary>
internal static class SignedTokenTampering
{
    public static string TamperSignature(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var separator = token.LastIndexOf('.');
        if (separator < 0 || separator == token.Length - 1)
        {
            throw new ArgumentException(
                "A signed token must carry a non-empty signature segment.",
                nameof(token));
        }

        var firstSignatureCharacter = token[separator + 1];
        return string.Concat(
            token.AsSpan(0, separator + 1),
            firstSignatureCharacter == 'A' ? "B" : "A",
            token.AsSpan(separator + 2));
    }
}
