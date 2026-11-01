using System.Text.Json;
using System.Text.Json.Serialization;

namespace MautoDesk.Sales.Application;

/// <summary>
/// How a calculation is written into, and read back out of, a snapshot.
/// </summary>
/// <remarks>
/// <para>
/// One place, deliberately. The stored bytes are hashed into
/// <c>sales.deal_calculation.content_hash</c>, so the serializer options are part
/// of the identity of every snapshot ever written — not a formatting preference.
/// A second set of options used by one caller would produce records that hash
/// differently for identical deals.
/// </para>
/// <para>
/// camelCase to match the wire convention, and <b>not</b> indented. Changing
/// either would change every future hash and make old and new snapshots
/// incomparable, so treat this as a versioned decision: if it ever has to change,
/// <c>DealEngine.Version</c> changes with it.
/// </para>
/// </remarks>
public static class SnapshotSerialization
{
    /// <summary>The one set of options a snapshot is ever written or read with.</summary>
    /// <remarks>
    /// <para>
    /// The converters are not optional. <c>Money</c> and <c>Percentage</c> are
    /// get-only structs with private constructors: without them the serializer
    /// emits a JSON <em>number</em> and then reads it back as zero, so a stored
    /// deal replays as worth nothing — correct on the way in, silently wrong on
    /// the way out.
    /// </para>
    /// <para>
    /// Enums are written as names rather than ordinals so the stored record stays
    /// readable, and so that inserting a new enum member in the middle does not
    /// change what every historical snapshot means.
    /// </para>
    /// </remarks>
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };

        options.Converters.Add(new MoneyJsonConverter());
        options.Converters.Add(new PercentageJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }
}
