using FluentAssertions;
using MautoDesk.Identity.Domain;
using MautoDesk.Identity.Infrastructure;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// The code format and hashing behind MFA recovery.
/// </summary>
/// <remarks>
/// These assertions are about a credential a user reads off paper under stress,
/// so they cover the two failure modes that actually happen: a code that is hard
/// to transcribe, and a code that is rejected because of how it was typed rather
/// than because it was wrong.
/// </remarks>
public sealed class RecoveryCodeServiceTests
{
    private readonly RecoveryCodeService _codes = new();

    [Fact]
    public void Generates_a_grouped_ten_character_code()
    {
        var code = _codes.Generate();

        code.Should().MatchRegex("^[A-Z2-9]{5}-[A-Z2-9]{5}$");
        _codes.Normalize(code).Should().HaveLength(10);
    }

    /// <summary>
    /// The alphabet omits every character that gets misread on paper.
    /// </summary>
    /// <remarks>
    /// 0/O and 1/I/L are the pairs support tickets are made of. Generating a
    /// thousand codes is enough to catch an alphabet edited without thinking.
    /// </remarks>
    [Fact]
    public void Never_generates_a_confusable_character()
    {
        for (var i = 0; i < 1_000; i++)
        {
            _codes.Generate().Should().NotContainAny("0", "O", "1", "I", "L", "U");
        }
    }

    [Fact]
    public void Generates_distinct_codes()
    {
        var generated = Enumerable.Range(0, 500).Select(_ => _codes.Generate()).ToHashSet(StringComparer.Ordinal);

        // A repeat inside 500 draws of ~49 bits means the RNG is not what it
        // claims to be — a collision by chance is astronomically unlikely.
        generated.Should().HaveCount(500);
    }

    [Theory]
    [InlineData("abcde-fghjk", "ABCDEFGHJK")]
    [InlineData("ABCDE FGHJK", "ABCDEFGHJK")]
    [InlineData("  abcdefghjk  ", "ABCDEFGHJK")]
    [InlineData("ABCDE—FGHJK", "ABCDEFGHJK")]
    public void Normalizes_however_the_user_typed_it(string typed, string expected) =>
        _codes.Normalize(typed).Should().Be(expected);

    [Fact]
    public void Hashes_deterministically_so_a_lookup_can_find_the_row()
    {
        var code = _codes.Normalize(_codes.Generate());

        _codes.Hash(code).Should().Be(_codes.Hash(code));
    }

    [Fact]
    public void Hashes_differently_for_different_codes()
    {
        _codes.Hash("ABCDEFGHJK").Should().NotBe(_codes.Hash("ABCDEFGHJM"));
    }

    /// <summary>The stored value must not be the code itself.</summary>
    [Fact]
    public void Hash_does_not_contain_the_plaintext()
    {
        var code = "ABCDEFGHJK";

        _codes.Hash(code).Should().NotContain(code);
    }
}

public sealed class MfaRecoveryCodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_code_is_usable()
    {
        var code = MfaRecoveryCode.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), "hash", Now);

        code.IsUsable.Should().BeTrue();
        code.UsedAt.Should().BeNull();
    }

    [Fact]
    public void Redeeming_spends_the_code()
    {
        var code = MfaRecoveryCode.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), "hash", Now);

        code.Redeem(Now).IsSuccess.Should().BeTrue();
        code.UsedAt.Should().Be(Now);
        code.IsUsable.Should().BeFalse();
    }

    /// <summary>
    /// The single-use guarantee, asserted at the only place that enforces it.
    /// </summary>
    /// <remarks>
    /// A recovery code that works twice is a static password with extra steps —
    /// and the printout it came from is usually still in a drawer.
    /// </remarks>
    [Fact]
    public void A_code_cannot_be_redeemed_twice()
    {
        var code = MfaRecoveryCode.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), "hash", Now);
        code.Redeem(Now);

        var second = code.Redeem(Now.AddMinutes(1));

        second.IsFailure.Should().BeTrue();
        second.Error!.Code.Should().Be("auth.recovery_code_used");
        code.UsedAt.Should().Be(Now, "the original redemption time must not be overwritten");
    }
}
