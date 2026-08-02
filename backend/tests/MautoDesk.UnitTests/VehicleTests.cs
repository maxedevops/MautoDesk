using FluentAssertions;
using MautoDesk.Inventory.Domain;
using MautoDesk.SharedKernel;
using Xunit;

namespace MautoDesk.UnitTests;

public sealed class VinTests
{
    /// <summary>A real VIN from a 2021 F-150, used across the test suite.</summary>
    private const string ValidVin = "1FTFW1ET5MFA48219";

    [Fact]
    public void Accepts_a_well_formed_vin()
    {
        var result = Vin.Create(ValidVin);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(ValidVin);
    }

    [Fact]
    public void Normalizes_case_and_surrounding_whitespace()
    {
        var result = Vin.Create("  1ftfw1et5mfa48219  ");

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(ValidVin);
    }

    [Theory]
    [InlineData("1FTFW1ET5MFA4821")]      // 16
    [InlineData("1FTFW1ET5MFA482199")]    // 18
    [InlineData("")]
    public void Rejects_anything_that_is_not_seventeen_characters(string candidate)
    {
        var result = Vin.Create(candidate);

        result.IsFailure.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKind.Validation);
    }

    /// <summary>
    /// I, O and Q are excluded by ISO 3779 because they are confusable with 1
    /// and 0 — which is exactly the mistake a dealer makes reading a dirty
    /// windshield plate, so the message has to say so.
    /// </summary>
    [Theory]
    [InlineData('I')]
    [InlineData('O')]
    [InlineData('Q')]
    public void Rejects_letters_that_are_never_in_a_vin(char excluded)
    {
        var candidate = excluded + ValidVin[1..];

        var result = Vin.Create(candidate);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("vin.excluded_letter");
        result.Error.Message.Should().Contain("I, O or Q");
    }

    [Fact]
    public void Exposes_the_last_six_because_that_is_how_a_vin_is_spoken_on_a_lot()
    {
        Vin.Create(ValidVin).Value.Last6.Should().Be("A48219");
    }

    /// <summary>
    /// The check digit is advisory, never a rejection.
    /// </summary>
    /// <remarks>
    /// Pre-1981 vehicles, imports and some trailers legitimately fail it. A DMS
    /// that refuses to book a car physically sitting on the lot is worse than
    /// one that accepts a questionable VIN and flags it.
    /// </remarks>
    [Fact]
    public void A_bad_check_digit_is_reported_but_does_not_reject_the_vin()
    {
        var tampered = string.Concat(ValidVin.AsSpan(0, 8), "0", ValidVin.AsSpan(9));

        var result = Vin.Create(tampered);

        result.IsSuccess.Should().BeTrue("a real vehicle can have a VIN that fails the check digit");
        result.Value.HasValidCheckDigit.Should().BeFalse();
    }
}

public sealed class MoneyTests
{
    [Fact]
    public void Parses_the_decimal_string_used_on_the_wire()
    {
        var result = Money.TryParse("28995.00");

        result.IsSuccess.Should().BeTrue();
        result.Value.Amount.Should().Be(28995.00m);
        result.Value.ToString().Should().Be("28995.00");
    }

    /// <summary>
    /// Group separators are rejected rather than silently reinterpreted.
    /// </summary>
    /// <remarks>
    /// This is the important one. Under a permissive parse, "28,995.00" can
    /// become 28.995 — a $28,995 truck priced at twenty-nine dollars. Failing
    /// loudly is the only safe behaviour.
    /// </remarks>
    [Fact]
    public void Rejects_a_thousands_separator_instead_of_guessing()
    {
        var result = Money.TryParse("28,995.00");

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("money.format");
    }

    [Fact]
    public void Rounds_half_away_from_zero_not_to_even()
    {
        // .NET's default is banker's rounding, which would give 0.12 here and
        // would not match how a dealer's paperwork or a state tax table rounds.
        Money.FromDecimal(0.125m).Amount.Should().Be(0.13m);
        Money.FromDecimal(0.135m).Amount.Should().Be(0.14m);
    }

    [Fact]
    public void Arithmetic_stays_exact_where_binary_floating_point_would_not()
    {
        var tenth = Money.FromDecimal(0.10m);
        var total = tenth + tenth + tenth;

        // 0.1 + 0.1 + 0.1 == 0.30000000000000004 in double. Not here.
        total.Amount.Should().Be(0.30m);
    }

    [Fact]
    public void Refuses_to_combine_different_currencies()
    {
        var usd = Money.FromDecimal(100m);
        var other = Money.FromDecimal(100m, "CAD");

        var combine = () => _ = usd + other;

        combine.Should().Throw<InvalidOperationException>();
    }
}

public sealed class VehicleTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly DateOnly Today = new(2026, 8, 1);

    private static Vehicle NewVehicle(string stock = "A-1001", string? vin = null)
    {
        var stockNumber = StockNumber.Create(stock).Value;
        Vin? parsedVin = vin is null ? null : Vin.Create(vin).Value;
        return Vehicle.Create(TenantA, stockNumber, parsedVin, Today).Value;
    }

    /// <summary>
    /// The single most important behaviour in this class: a vehicle saves with
    /// almost nothing filled in, because that is the state a salesperson has
    /// when they are standing next to it.
    /// </summary>
    [Fact]
    public void Can_be_created_with_nothing_but_a_stock_number()
    {
        var result = Vehicle.Create(TenantA, StockNumber.Create("A-1001").Value, null, Today);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(VehicleStatus.Acquired);
        result.Value.Vin.Should().BeNull();
        result.Value.ListPrice.Should().BeNull();
    }

    [Fact]
    public void Raises_a_created_event_for_the_outbox()
    {
        var vehicle = NewVehicle();

        vehicle.DomainEvents.Should().ContainSingle()
            .Which.EventType.Should().Be("inventory.vehicle.created");
    }

    [Fact]
    public void Cannot_be_created_outside_a_tenant()
    {
        var create = () => Vehicle.Create(Guid.Empty, StockNumber.Create("A-1").Value, null, Today);

        create.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A decode fills gaps and never overwrites what a human entered.
    /// </summary>
    /// <remarks>
    /// If a dealer corrected the trim because they are reading the window
    /// sticker, a later decode — or a provider changing its data years from now —
    /// must not silently undo that.
    /// </remarks>
    [Fact]
    public void Decode_fills_gaps_but_never_overwrites_dealer_input()
    {
        var vehicle = NewVehicle();
        vehicle.SetIdentity(2021, "Ford", "F-150", "King Ranch", null, null);

        vehicle.ApplyDecode(new VehicleDecodeResult(
            2021, "Ford", "F-150", "XLT", "Pickup", "4WD", "3.5L V6", "Gasoline", "Automatic"));

        vehicle.Trim.Should().Be("King Ranch", "the dealer read the window sticker; vPIC guessed");
        vehicle.BodyStyle.Should().Be("Pickup", "the decode still fills what was empty");
    }

    [Fact]
    public void Publishing_requires_a_photo()
    {
        var vehicle = NewVehicle(vin: "1FTFW1ET5MFA48219");
        vehicle.ChangeStatus(VehicleStatus.Available, Today);
        vehicle.SetListPrice(Money.FromDecimal(38450m));

        var result = vehicle.Publish(photoCount: 0);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("vehicle.publish.no_photos");
        vehicle.IsPublished.Should().BeFalse();
    }

    [Fact]
    public void Publishing_requires_a_price()
    {
        var vehicle = NewVehicle();
        vehicle.ChangeStatus(VehicleStatus.Available, Today);

        var result = vehicle.Publish(photoCount: 4);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("vehicle.publish.no_price");
    }

    [Fact]
    public void Publishes_when_it_has_a_photo_and_a_price()
    {
        var vehicle = NewVehicle();
        vehicle.ChangeStatus(VehicleStatus.Available, Today);
        vehicle.SetListPrice(Money.FromDecimal(38450m));

        var result = vehicle.Publish(photoCount: 6);

        result.IsSuccess.Should().BeTrue();
        vehicle.IsPublished.Should().BeTrue();
    }

    /// <summary>A deal falling through is ordinary, so the status must reverse.</summary>
    [Fact]
    public void Pending_sale_can_go_back_to_available()
    {
        var vehicle = NewVehicle();
        vehicle.ChangeStatus(VehicleStatus.Available, Today);
        vehicle.ChangeStatus(VehicleStatus.PendingSale, Today);

        var result = vehicle.ChangeStatus(VehicleStatus.Available, Today);

        result.IsSuccess.Should().BeTrue("deals fall through all the time");
    }

    [Fact]
    public void Cannot_jump_straight_from_acquired_to_sold()
    {
        var vehicle = NewVehicle();

        var result = vehicle.ChangeStatus(VehicleStatus.Sold, Today);

        result.IsFailure.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKind.Conflict);
    }

    [Fact]
    public void Selling_unpublishes_and_stamps_the_sold_date()
    {
        var vehicle = NewVehicle();
        vehicle.ChangeStatus(VehicleStatus.Available, Today);
        vehicle.SetListPrice(Money.FromDecimal(38450m));
        vehicle.Publish(photoCount: 3);
        vehicle.ChangeStatus(VehicleStatus.PendingSale, Today);

        vehicle.ChangeStatus(VehicleStatus.Sold, Today);

        vehicle.IsPublished.Should().BeFalse("a sold car must come off the website immediately");
        vehicle.SoldAt.Should().Be(Today);
    }

    /// <summary>
    /// Days-to-front-line is measured from the first time a unit became
    /// sellable, so a trip back through recon must not restart the clock.
    /// </summary>
    [Fact]
    public void Available_date_is_stamped_once_and_survives_a_return_to_recon()
    {
        var vehicle = NewVehicle();
        vehicle.ChangeStatus(VehicleStatus.Available, Today);
        var firstAvailable = vehicle.AvailableAt;

        vehicle.ChangeStatus(VehicleStatus.InRecon, Today);
        vehicle.ChangeStatus(VehicleStatus.Available, Today.AddDays(14));

        vehicle.AvailableAt.Should().Be(firstAvailable);
    }

    [Fact]
    public void A_sold_vehicle_cannot_be_deleted()
    {
        var vehicle = NewVehicle();
        vehicle.ChangeStatus(VehicleStatus.Available, Today);
        vehicle.ChangeStatus(VehicleStatus.PendingSale, Today);
        vehicle.ChangeStatus(VehicleStatus.Sold, Today);

        var result = vehicle.Delete(DateTimeOffset.UtcNow, null);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("vehicle.delete.sold");
    }

    [Fact]
    public void The_vin_is_frozen_once_the_vehicle_is_sold()
    {
        var vehicle = NewVehicle(vin: "1FTFW1ET5MFA48219");
        vehicle.ChangeStatus(VehicleStatus.Available, Today);
        vehicle.ChangeStatus(VehicleStatus.PendingSale, Today);
        vehicle.ChangeStatus(VehicleStatus.Sold, Today);

        var result = vehicle.SetVin(Vin.Create("2HGFC2F59LH551903").Value);

        result.IsFailure.Should().BeTrue(
            "the VIN identifies the unit on a signed contract and a title application");
    }

    /// <summary>
    /// AI copy cannot become the published description on its own.
    /// </summary>
    /// <remarks>
    /// The mechanical guarantee behind ADR-0004: advertising equipment a car
    /// does not have is a consumer-protection problem, so approval is a separate,
    /// named, permission-gated act.
    /// </remarks>
    [Fact]
    public void An_ai_draft_does_not_become_the_description_until_it_is_approved()
    {
        var vehicle = NewVehicle();

        vehicle.ProposeAiDescription("Loaded with every option imaginable!");

        vehicle.Description.Should().BeNull();
        vehicle.AiDescriptionDraft.Should().NotBeNull();

        vehicle.ApproveAiDescription(DateTimeOffset.UtcNow);

        vehicle.Description.Should().Be("Loaded with every option imaginable!");
        vehicle.AiDescriptionApprovedAt.Should().NotBeNull();
    }

    [Fact]
    public void Approving_nothing_is_an_error_rather_than_a_silent_no_op()
    {
        var result = NewVehicle().ApproveAiDescription(DateTimeOffset.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("vehicle.ai.no_draft");
    }

    [Fact]
    public void Readiness_reports_what_is_missing_rather_than_blocking_the_save()
    {
        var vehicle = NewVehicle();

        var readiness = vehicle.GetPublishReadiness(photoCount: 0);

        readiness.IsReady.Should().BeFalse();
        readiness.Satisfied.Should().BeLessThan(readiness.Total);
        readiness.Missing.Should().Contain("At least one photo");
        readiness.Missing.Should().Contain("Price");
    }

    [Fact]
    public void A_price_change_raises_an_event_only_when_the_price_actually_moves()
    {
        var vehicle = NewVehicle();
        vehicle.ClearDomainEvents();

        vehicle.SetListPrice(Money.FromDecimal(38450m));
        vehicle.SetListPrice(Money.FromDecimal(38450m));

        vehicle.DomainEvents.Should().ContainSingle(e => e.EventType == "inventory.vehicle.price_changed");
    }
}
