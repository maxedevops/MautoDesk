using System.Security.Cryptography;
using System.Text;
using MautoDesk.Identity.Application;

namespace MautoDesk.Identity.Infrastructure;

/// <summary>
/// Generates and hashes MFA recovery codes.
/// </summary>
/// <remarks>
/// The code is what a user reads off a printout at the worst moment of their
/// week, so the alphabet excludes every pair that gets mistyped over a phone —
/// no 0/O, no 1/I/L, no U/V confusion — and the code is grouped for legibility.
/// Ten characters of a 30-symbol alphabet is ~49 bits, which is far past
/// guessable through a rate-limited, lockout-backed endpoint.
/// </remarks>
public sealed class RecoveryCodeService : IRecoveryCodeService
{
    /// <summary>Crockford-style alphabet with the confusable symbols removed.</summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    private const int CodeLength = 10;

    private const int GroupSize = 5;

    public string Generate()
    {
        // GetItems draws uniformly from the alphabet without the modulo bias a
        // hand-rolled index into a CSPRNG byte would introduce.
        Span<char> drawn = stackalloc char[CodeLength];
        RandomNumberGenerator.GetItems(Alphabet.AsSpan(), drawn);

        // Grouped with a dash for legibility. Normalize strips it again, so a
        // user who types the code with or without the dash is accepted either
        // way.
        var builder = new StringBuilder(CodeLength + 1);

        for (var i = 0; i < CodeLength; i++)
        {
            if (i > 0 && i % GroupSize == 0)
            {
                builder.Append('-');
            }

            builder.Append(drawn[i]);
        }

        return builder.ToString();
    }

    public string Hash(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexStringLower(digest);
    }

    public string Normalize(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var builder = new StringBuilder(CodeLength);

        foreach (var character in code)
        {
            var upper = char.ToUpperInvariant(character);

            // Anything that is not part of the alphabet — dashes, spaces, the
            // stray whitespace a paste brings with it — is dropped rather than
            // rejected. A code that fails should fail because it is wrong, not
            // because of how it was pasted.
            if (Alphabet.Contains(upper, StringComparison.Ordinal))
            {
                builder.Append(upper);
            }
        }

        return builder.ToString();
    }
}
