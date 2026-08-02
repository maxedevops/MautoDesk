using FluentAssertions;
using MautoDesk.Api;
using MautoDesk.SharedKernel;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// What must never reach a log file.
/// </summary>
/// <remarks>
/// A dealership's logs sit against a backdrop of names, addresses, dates of
/// birth, licence numbers, and bank details. The FTC Safeguards Rule does not
/// exempt log files and neither does discovery, so these assertions are about a
/// legal obligation as much as a technical one.
/// </remarks>
public sealed class LogRedactionTests
{
    [Theory]
    [InlineData("Customer SSN is 123-45-6789 on file", "123-45-6789")]
    [InlineData("ssn 123456789 recorded", "123456789")]
    [InlineData("card 4111 1111 1111 1111 declined", "4111 1111 1111 1111")]
    [InlineData("card 4111111111111111 declined", "4111111111111111")]
    public void Scrubs_the_identifiers_that_leak_in_free_text(string message, string secret)
    {
        LogRedaction.Scrub(message).Should().NotContain(secret);
    }

    /// <summary>
    /// A token in an exception message is a session someone else can use.
    /// </summary>
    [Fact]
    public void Scrubs_bearer_tokens_and_json_web_tokens()
    {
        const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.abcdefghijklmnop";

        LogRedaction.Scrub($"Authorization: Bearer {Jwt}").Should().NotContain(Jwt);
        LogRedaction.Scrub($"token was {Jwt}").Should().NotContain(Jwt);
    }

    /// <summary>
    /// The address goes, the domain stays.
    /// </summary>
    /// <remarks>
    /// "Which dealership was this?" stays answerable from the logs while the
    /// individual does not appear in them.
    /// </remarks>
    [Fact]
    public void Masks_the_local_part_of_an_email_and_keeps_the_domain()
    {
        var scrubbed = LogRedaction.Scrub("login failed for sam.taylor@northsidemotors.com");

        scrubbed.Should().NotContain("sam.taylor");
        scrubbed.Should().Contain("northsidemotors.com");
    }

    [Fact]
    public void Leaves_ordinary_text_alone()
    {
        const string Message = "Vehicle IT-A-100 moved from acquired to available";

        LogRedaction.Scrub(Message).Should().Be(Message);
    }

    [Fact]
    public void Redacts_sensitive_query_parameters_and_keeps_the_rest()
    {
        var scrubbed = LogRedaction.ScrubQueryString("?page=2&token=abc123&status=available");

        scrubbed.Should().NotContain("abc123");
        scrubbed.Should().Contain("page=2");
        scrubbed.Should().Contain("status=available");
    }
}

public sealed class RedactingDestructuringPolicyTests
{
    /// <summary>A stand-in for a request body: one marked field, one not.</summary>
    private sealed record LoginBody(string Email, [property: Sensitive] string Password);

    /// <summary>No attribute at all — the name list is the backstop.</summary>
    private sealed record ThirdPartyBody(string AccountNumber, string Make);

    [Fact]
    public void Replaces_a_marked_property_and_keeps_the_others()
    {
        var rendered = Render(new LoginBody("sam@dealer.test", "correct-horse-battery-staple"));

        rendered.Should().NotContain("correct-horse-battery-staple");
        rendered.Should().Contain(LogRedaction.Placeholder);
    }

    /// <summary>
    /// The name list catches what nobody could attribute.
    /// </summary>
    /// <remarks>
    /// An anonymous type at a call site, or a model from a package — there is no
    /// property to mark, and those are exactly the objects someone logs in a
    /// hurry while debugging.
    /// </remarks>
    [Fact]
    public void Redacts_by_name_when_there_is_no_attribute_to_read()
    {
        var rendered = Render(new ThirdPartyBody("12345678901234", "Ford"));

        rendered.Should().NotContain("12345678901234");
        rendered.Should().Contain("Ford", "only the sensitive field is removed");
    }

    /// <summary>Renders a log event through the real policy and returns the output.</summary>
    private static string Render(object payload)
    {
        var sink = new CapturingSink();

        using var logger = new LoggerConfiguration()
            .Destructure.With<RedactingDestructuringPolicy>()
            .Enrich.With<RedactingEnricher>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("Handling {@Payload}", payload);

        return sink.Rendered;
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public string Rendered { get; private set; } = string.Empty;

        public void Emit(LogEvent logEvent)
        {
            using var writer = new StringWriter();
            logEvent.RenderMessage(writer, System.Globalization.CultureInfo.InvariantCulture);
            Rendered = writer.ToString();
        }
    }
}
