using System.Text.RegularExpressions;

namespace MautoDesk.SharedKernel;

/// <summary>
/// Marks a value that must never appear in a log, a trace, or an error report.
/// </summary>
/// <remarks>
/// <para>
/// Two things consume this. The OpenAPI document emits <c>x-sensitive: true</c>
/// for the property, so a client author can see the obligation; and the log
/// redaction policy replaces the value with a placeholder wherever a marked
/// object is written to a log.
/// </para>
/// <para>
/// Mark the field at its definition rather than remembering at each call site.
/// A rule that depends on the person writing the log statement is a rule that
/// holds until someone is in a hurry.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SensitiveAttribute : Attribute
{
}

/// <summary>
/// Removes personal and secret data from text on its way to a log.
/// </summary>
/// <remarks>
/// <para>
/// The attribute handles structured values whose shape we control.
/// This handles the rest: an exception message quoting a row, a database error
/// echoing parameters, a URL with a token in the query string. Those are the
/// paths that actually leak, because nobody chose to log them.
/// </para>
/// <para>
/// <b>Patterns, not a promise.</b> This catches the formats that recur in a
/// dealership system — social security numbers, card numbers, bearer tokens,
/// email addresses. It is a net, not a proof, and it does not excuse logging a
/// customer object.
/// </para>
/// </remarks>
public static partial class LogRedaction
{
    public const string Placeholder = "[redacted]";

    /// <summary>Property names redacted wherever they appear, marked or not.</summary>
    /// <remarks>
    /// A backstop for objects this codebase does not own — a third-party model,
    /// an anonymous type thrown together at a call site — where there is no
    /// property to attribute.
    /// </remarks>
    public static readonly IReadOnlySet<string> SensitiveNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "password", "currentPassword", "newPassword", "passwordHash",
            "token", "accessToken", "refreshToken", "challengeToken", "idToken",
            "secret", "clientSecret", "enrolmentSecret", "totpSecret", "apiKey",
            "recoveryCode", "recoveryCodes", "code",
            "ssn", "socialSecurityNumber", "taxId",
            "driversLicense", "driversLicenceNumber", "licenseNumber",
            "bankAccount", "accountNumber", "routingNumber", "cardNumber", "cvv",
            "dateOfBirth", "dob",
            "authorization", "cookie", "setCookie",
        };

    public static bool IsSensitiveName(string? name) =>
        name is not null && SensitiveNames.Contains(name);

    /// <summary>Masks the patterns that leak most often in free text.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var scrubbed = SocialSecurityNumber().Replace(text, Placeholder);
        scrubbed = PaymentCard().Replace(scrubbed, Placeholder);
        scrubbed = BearerToken().Replace(scrubbed, $"Bearer {Placeholder}");
        scrubbed = JsonWebToken().Replace(scrubbed, Placeholder);

        // Local part masked, domain kept: "which dealership was this?" stays
        // answerable while the individual does not appear in the log.
        scrubbed = EmailAddress().Replace(scrubbed, match => $"[redacted]@{match.Groups[1].Value}");

        return scrubbed;
    }

    /// <summary>Rewrites a query string so a token or code in it is not logged.</summary>
    public static string ScrubQueryString(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return queryString ?? string.Empty;
        }

        var query = queryString.StartsWith('?') ? queryString[1..] : queryString;

        var parts = query.Split('&').Select(pair =>
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                return pair;
            }

            var name = pair[..separator];
            return IsSensitiveName(Uri.UnescapeDataString(name)) ? $"{name}={Placeholder}" : pair;
        });

        return $"?{string.Join('&', parts)}";
    }

    // 123-45-6789, with or without separators. Bounded by non-digits so a
    // nine-digit stock reference in a longer number is not mangled.
    [GeneratedRegex(@"(?<!\d)\d{3}[- ]?\d{2}[- ]?\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SocialSecurityNumber();

    // 13-19 digits in card-like grouping.
    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PaymentCard();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex JsonWebToken();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@([A-Za-z0-9.\-]+\.[A-Za-z]{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddress();
}
