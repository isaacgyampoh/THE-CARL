using System.Security.Cryptography;
using System.Text;

namespace TheCarl.Domain;

/// <summary>
/// Identity a device assigns to a transaction it creates while completely offline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format:</b> <c>CTX-{deviceTag}-{ulid}</c>, for example
/// <c>CTX-9f3a1c07-01HQ8Z7K3M4N5P6Q7R8S9T0V1W</c>. Total length is 39 characters.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>deviceTag</b> — 8 lowercase hex characters, the leading 4 bytes of SHA-256 over the
/// device's installation identifier. Two devices generating in the same millisecond still
/// differ here, so the device is the first line of collision defence rather than luck.
/// </description></item>
/// <item><description>
/// <b>ulid</b> — 26 characters of Crockford base32: a 48-bit big-endian millisecond
/// timestamp followed by 80 bits from a cryptographic RNG.
/// </description></item>
/// </list>
/// <para><b>Why not a timestamp alone:</b> device clocks are unsynchronised, users change
/// them, and two transactions can share a millisecond. A timestamp alone collides in all
/// three cases. The 80 random bits make collision negligible even if two devices share a
/// tag and a millisecond.</para>
/// <para><b>Why not a bare GUID:</b> the timestamp prefix makes ids lexicographically
/// sortable by creation time, which keeps the outbox in submission order and keeps the
/// database index append-ordered rather than randomly distributed.</para>
/// <para><b>Restart and reboot safety:</b> nothing is derived from a counter or from
/// in-memory state, so no sequence needs to survive a process kill. The timestamp may go
/// backwards after a clock change without affecting uniqueness, because uniqueness rests on
/// the random component and the device tag.</para>
/// <para><b>Long offline periods:</b> generation never contacts the server, so a device can
/// create ids for weeks offline. The server treats the value as an opaque, untrusted string
/// and enforces uniqueness itself.</para>
/// </remarks>
public static class ClientTransactionId
{
    public const string Prefix = "CTX";
    public const int DeviceTagLength = 8;
    public const int UlidLength = 26;
    public const int TotalLength = 39;

    /// <summary>Crockford base32: no I, L, O or U, so transcription is unambiguous.</summary>
    private const string Base32Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// Derives the stable 8-character tag for a device installation identifier.
    /// The identifier itself is never embedded, so an id does not disclose device identity.
    /// </summary>
    public static string DeviceTag(string deviceInstallationId)
    {
        if (string.IsNullOrWhiteSpace(deviceInstallationId))
        {
            throw new ArgumentException("A device installation identifier is required.", nameof(deviceInstallationId));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceInstallationId.Trim()));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>Generates a new client transaction id for the given device.</summary>
    public static string Create(string deviceInstallationId, DateTimeOffset? nowUtc = null)
    {
        var timestamp = (nowUtc ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        if (timestamp < 0)
        {
            timestamp = 0;
        }

        return $"{Prefix}-{DeviceTag(deviceInstallationId)}-{CreateUlid(timestamp)}";
    }

    /// <summary>
    /// Validates the shape of a client-supplied id. Shape only: the server never trusts a
    /// client id for anything beyond replay detection, and uniqueness is enforced by a
    /// database constraint rather than by this check.
    /// </summary>
    public static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != TotalLength)
        {
            return false;
        }

        var parts = value.Split('-');
        if (parts.Length != 3 || parts[0] != Prefix)
        {
            return false;
        }

        if (parts[1].Length != DeviceTagLength
            || !parts[1].All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')))
        {
            return false;
        }

        return parts[2].Length == UlidLength
            && parts[2].All(c => Base32Alphabet.Contains(c, StringComparison.Ordinal));
    }

    private static string CreateUlid(long timestampMilliseconds)
    {
        Span<byte> bytes = stackalloc byte[16];

        // 48-bit big-endian timestamp.
        bytes[0] = (byte)(timestampMilliseconds >> 40);
        bytes[1] = (byte)(timestampMilliseconds >> 32);
        bytes[2] = (byte)(timestampMilliseconds >> 24);
        bytes[3] = (byte)(timestampMilliseconds >> 16);
        bytes[4] = (byte)(timestampMilliseconds >> 8);
        bytes[5] = (byte)timestampMilliseconds;

        RandomNumberGenerator.Fill(bytes[6..]);

        return EncodeBase32(bytes);
    }

    /// <summary>Encodes 16 bytes (128 bits) as 26 base32 characters, most significant first.</summary>
    private static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        var result = new char[UlidLength];

        // 3 bits for the leading character plus 5 bits for each of the remaining 25 covers
        // exactly the 128 bits available, so the leading character is always '0'–'7'.
        var bitPosition = 0;
        for (var index = 0; index < UlidLength; index++)
        {
            var bitsToTake = index == 0 ? 3 : 5;
            var value = 0;

            for (var bit = 0; bit < bitsToTake; bit++)
            {
                var absoluteBit = bitPosition + bit;
                var byteIndex = absoluteBit / 8;
                var bitInByte = 7 - (absoluteBit % 8);
                var bitValue = byteIndex < bytes.Length
                    ? (bytes[byteIndex] >> bitInByte) & 1
                    : 0;
                value = (value << 1) | bitValue;
            }

            result[index] = Base32Alphabet[value];
            bitPosition += bitsToTake;
        }

        return new string(result);
    }
}
