using System.Globalization;
using System.Reflection;
using MautoDesk.SharedKernel;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MautoDesk.Api;

/// <summary>
/// Structured logging, with redaction applied before anything is written.
/// </summary>
/// <remarks>
/// <para>
/// A dealership system logs against a backdrop of customer names, addresses,
/// dates of birth, driver's licence numbers, and bank details. The FTC
/// Safeguards Rule does not carve out log files, and neither does a subpoena —
/// so redaction has to be a property of the logging pipeline rather than a
/// habit at call sites.
/// </para>
/// <para>
/// Two mechanisms, because they fail differently. The destructuring policy
/// handles objects whose shape we control: anything marked
/// <see cref="SensitiveAttribute"/>, plus a name list for the ones we do not
/// own. The message scrubber handles free text — an exception quoting a row, a
/// URL carrying a token — which is where leaks actually come from, because
/// nobody chose to log them.
/// </para>
/// </remarks>
public static class LoggingConfiguration
{
    public static LoggerConfiguration ForMautoDesk(this LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration
            .Enrich.FromLogContext()
            .Enrich.With<RedactingEnricher>()
            .Destructure.With<RedactingDestructuringPolicy>()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The request log line, with the query string scrubbed.
    /// </summary>
    /// <remarks>
    /// Serilog's default request logging includes the raw query string, which is
    /// where a password-reset token or a recovery code ends up if anyone ever
    /// puts one there.
    /// </remarks>
    public static void EnrichRequest(IDiagnosticContext context, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(http);

        context.Set("QueryString", LogRedaction.ScrubQueryString(http.Request.QueryString.Value));
        context.Set("TenantScoped", http.User.Identity?.IsAuthenticated == true);
    }
}

/// <summary>
/// Replaces sensitive properties with a placeholder as objects are destructured.
/// </summary>
/// <remarks>
/// Runs at the point Serilog turns an object into a log event, so a value that
/// is never written cannot be recovered from a log file — as opposed to
/// filtering at the sink, which leaves the value in memory in every other sink's
/// path.
/// </remarks>
public sealed class RedactingDestructuringPolicy : IDestructuringPolicy
{
    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        ArgumentNullException.ThrowIfNull(propertyValueFactory);

        result = null!;

        if (value is null)
        {
            return false;
        }

        var type = value.GetType();

        // Primitives, strings, and framework types are left to Serilog. Only
        // objects with properties are worth walking.
        if (type.IsPrimitive || value is string || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
        {
            return false;
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToList();

        if (properties.Count == 0)
        {
            return false;
        }

        var destructured = new List<LogEventProperty>(properties.Count);

        foreach (var property in properties)
        {
            var redact = property.GetCustomAttribute<SensitiveAttribute>() is not null
                || LogRedaction.IsSensitiveName(property.Name);

            if (redact)
            {
                destructured.Add(new LogEventProperty(
                    property.Name,
                    new ScalarValue(LogRedaction.Placeholder)));

                continue;
            }

            object? propertyValue;

            try
            {
                propertyValue = property.GetValue(value);
            }
            catch (TargetInvocationException)
            {
                // A property that throws must not take the log line with it.
                propertyValue = null;
            }

            destructured.Add(new LogEventProperty(
                property.Name,
                propertyValueFactory.CreatePropertyValue(propertyValue, destructureObjects: true)));
        }

        result = new StructureValue(destructured, type.Name);
        return true;
    }
}

/// <summary>
/// Scrubs the patterns that leak in free text, after the message is rendered.
/// </summary>
/// <remarks>
/// Catches what the destructuring policy cannot: an exception message quoting a
/// row, a Postgres error echoing a parameter, an address pasted into a string.
/// It rewrites scalar string properties in place, so the scrubbed value is what
/// every sink receives.
/// </remarks>
public sealed class RedactingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            if (value is not ScalarValue { Value: string text })
            {
                continue;
            }

            var scrubbed = LogRedaction.Scrub(text);

            if (!string.Equals(scrubbed, text, StringComparison.Ordinal))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(scrubbed)));
            }
        }
    }
}
