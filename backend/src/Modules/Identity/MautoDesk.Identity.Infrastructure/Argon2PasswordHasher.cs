using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using MautoDesk.Identity.Application;

namespace MautoDesk.Identity.Infrastructure;

/// <summary>
/// Argon2id password hashing.
/// </summary>
/// <remarks>
/// <para>
/// Argon2id is the OWASP first choice: it resists both GPU cracking (via memory
/// hardness) and side-channel attacks (via the hybrid data-independent first
/// pass). The parameters below are OWASP's current minimum — 19 MiB, 2
/// iterations, 1 degree of parallelism.
/// </para>
/// <para>
/// The encoded format carries its own parameters, so a stored hash can always be
/// verified even after the defaults are raised, and
/// <see cref="NeedsRehash"/> reports when an upgrade is due. That is what lets
/// the whole user base migrate to stronger settings on next login without a
/// password reset.
/// </para>
/// </remarks>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    /// <summary>Memory cost in KiB. OWASP minimum for Argon2id.</summary>
    public const int MemoryKib = 19 * 1024;

    public const int Iterations = 2;

    public const int Parallelism = 1;

    public const int SaltBytes = 16;

    public const int HashBytes = 32;

    private const string Prefix = "$argon2id$";

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, MemoryKib, Iterations, Parallelism);

        // Self-describing: $argon2id$v=19$m=19456,t=2,p=1$<salt>$<hash>
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}v=19$m={MemoryKib},t={Iterations},p={Parallelism}$" +
            $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public bool Verify(string password, string encodedHash)
    {
        if (string.IsNullOrEmpty(password) || !TryParse(encodedHash, out var parsed))
        {
            return false;
        }

        var computed = Derive(password, parsed.Salt, parsed.Memory, parsed.Iterations, parsed.Parallelism);

        // Constant-time. A byte-by-byte comparison that returns early leaks how
        // many leading bytes matched, which is enough to forge a hash one byte
        // at a time given enough attempts.
        return CryptographicOperations.FixedTimeEquals(computed, parsed.Hash);
    }

    public bool NeedsRehash(string encodedHash) =>
        !TryParse(encodedHash, out var parsed) ||
        parsed.Memory < MemoryKib ||
        parsed.Iterations < Iterations;

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };

        return argon2.GetBytes(HashBytes);
    }

    private static bool TryParse(string? encoded, out ParsedHash parsed)
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(encoded) || !encoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // $argon2id$v=19$m=..,t=..,p=..$salt$hash
        var segments = encoded.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 5)
        {
            return false;
        }

        var costs = segments[2].Split(',');
        if (costs.Length != 3)
        {
            return false;
        }

        try
        {
            parsed = new ParsedHash(
                ParseCost(costs[0], "m="),
                ParseCost(costs[1], "t="),
                ParseCost(costs[2], "p="),
                Convert.FromBase64String(segments[3]),
                Convert.FromBase64String(segments[4]));

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int ParseCost(string segment, string prefix) =>
        int.Parse(segment[prefix.Length..], CultureInfo.InvariantCulture);

    private readonly record struct ParsedHash(
        int Memory,
        int Iterations,
        int Parallelism,
        byte[] Salt,
        byte[] Hash);
}
