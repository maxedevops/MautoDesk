using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Domain;

/// <summary>What kind of transaction this is.</summary>
/// <remarks>Mirrors the check constraint on <c>sales.deal.type</c>.</remarks>
public enum DealType
{
    /// <summary>A retail sale to a consumer.</summary>
    Retail,

    /// <summary>A sale to another dealer or at auction.</summary>
    Wholesale,

    /// <summary>A lease.</summary>
    Lease,

    /// <summary>A consignment sale.</summary>
    Consignment,
}

/// <summary>Where a deal is in its life.</summary>
/// <remarks>Mirrors the check constraint on <c>sales.deal.status</c>.</remarks>
public enum DealStatus
{
    /// <summary>Being worked on the desk. Nothing is committed.</summary>
    Quote,

    /// <summary>Submitted for approval.</summary>
    Pending,

    /// <summary>Approved to contract.</summary>
    Approved,

    /// <summary>Papers signed.</summary>
    Contracted,

    /// <summary>The lender has funded.</summary>
    Funded,

    /// <summary>The customer has the car.</summary>
    Delivered,

    /// <summary>Everything is done.</summary>
    Completed,

    /// <summary>Abandoned before contracting.</summary>
    Cancelled,

    /// <summary>Reversed after contracting.</summary>
    Unwound,
}

/// <summary>
/// A deal.
/// </summary>
/// <remarks>
/// <para>
/// <b>This aggregate holds no money.</b> Not one amount, not a total, not a
/// payment. Every figure lives on an immutable <see cref="DealCalculationRecord"/>
/// and <see cref="CurrentCalculationId"/> points at the authoritative one.
/// Denormalizing a total onto this row would create two sources of truth for a
/// number that appears on a contract, and the two would diverge the first time
/// somebody forgot to update one of them (ADR-0008, and a hard rule in
/// <c>CLAUDE.md</c>).
/// </para>
/// <para>
/// <see cref="JurisdictionId"/> is resolved once and frozen. Changing the
/// customer's address after contracting requires an explicit recalculation
/// rather than a silent re-resolve, because a deal quietly re-taxing itself is
/// not a behaviour anyone would sign off.
/// </para>
/// </remarks>
public sealed class Deal : AggregateRoot
{
    private Deal(Guid id, Guid tenantId, string dealNumber, DealType type)
        : base(id, tenantId)
    {
        DealNumber = dealNumber;
        Type = type;
        Status = DealStatus.Quote;
    }

    /// <summary>Required by EF Core materialization.</summary>
    private Deal()
    {
    }

    /// <summary>The dealer-facing number, unique within the tenant.</summary>
    public string DealNumber { get; private set; } = string.Empty;

    /// <summary>Retail, wholesale, lease or consignment.</summary>
    public DealType Type { get; private set; }

    /// <summary>Where the deal is in its life.</summary>
    public DealStatus Status { get; private set; }

    /// <summary>The vehicle being sold.</summary>
    public Guid? VehicleId { get; private set; }

    /// <summary>The buyer.</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>The taxing jurisdiction whose rules govern, resolved at quote time and frozen.</summary>
    public Guid? JurisdictionId { get; private set; }

    /// <summary>The salesperson.</summary>
    public Guid? SalespersonId { get; private set; }

    /// <summary>The currently authoritative calculation snapshot.</summary>
    public Guid? CurrentCalculationId { get; private set; }

    /// <summary>When the deal was first quoted.</summary>
    public DateTimeOffset? QuotedAt { get; private set; }

    /// <summary>When it was approved.</summary>
    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>When it was cancelled.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>Why it was cancelled.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>Free text a dealer can see.</summary>
    public string? Notes { get; private set; }

    /// <summary>Opens a deal.</summary>
    /// <param name="tenantId">The dealership.</param>
    /// <param name="dealNumber">The dealer-facing number.</param>
    /// <param name="type">Retail unless stated otherwise.</param>
    /// <param name="now">The clock, injected — never read statically.</param>
    public static Result<Deal> Create(
        Guid tenantId,
        string dealNumber,
        DealType type,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(dealNumber))
        {
            return Error.Validation("deal.number_required", "A deal needs a number.");
        }

        var deal = new Deal(Guid.CreateVersion7(), tenantId, dealNumber.Trim(), type)
        {
            QuotedAt = now,
        };

        deal.Raise(new DealCreated(deal.Id, tenantId, deal.DealNumber));

        return deal;
    }

    /// <summary>Attaches the vehicle, customer and governing jurisdiction.</summary>
    public void Describe(Guid? vehicleId, Guid? customerId, Guid? jurisdictionId, Guid? salespersonId)
    {
        VehicleId = vehicleId;
        CustomerId = customerId;
        JurisdictionId = jurisdictionId;
        SalespersonId = salespersonId;
    }

    /// <summary>Records free text.</summary>
    public void SetNotes(string? notes) => Notes = notes;

    /// <summary>
    /// Points the deal at a newly written calculation.
    /// </summary>
    /// <remarks>
    /// The pointer moves; the previous snapshot stays exactly where it was. That
    /// is what makes "what were the numbers on the fifteenth" answerable.
    /// </remarks>
    public void AdoptCalculation(Guid calculationId, int version)
    {
        CurrentCalculationId = calculationId;
        Raise(new DealCalculated(Id, TenantId, calculationId, version));
    }

    /// <summary>
    /// Moves the deal to a new status, if the transition is one that exists.
    /// </summary>
    /// <remarks>
    /// The permitted moves are listed rather than inferred. A state machine that
    /// allows anything is not a state machine, and "funded" appearing before
    /// "contracted" is the sort of thing that is discovered during an audit.
    /// </remarks>
    public Result<Deal> TransitionTo(DealStatus target, DateTimeOffset now, string? reason = null)
    {
        if (target == Status)
        {
            return this;
        }

        if (!PermittedFrom(Status).Contains(target))
        {
            return Error.Conflict(
                "deal.invalid_transition",
                $"A deal cannot go from {Status} to {target}.");
        }

        if (target is DealStatus.Cancelled or DealStatus.Unwound && string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(
                "deal.reason_required",
                "Cancelling or unwinding a deal needs a reason. It will be read later by somebody " +
                "trying to understand what happened.");
        }

        var previous = Status;
        Status = target;

        switch (target)
        {
            case DealStatus.Approved:
                ApprovedAt = now;
                break;

            case DealStatus.Cancelled:
            case DealStatus.Unwound:
                CancelledAt = now;
                CancelReason = reason;
                break;

            default:
                break;
        }

        Raise(new DealStatusChanged(Id, TenantId, previous.ToString(), target.ToString()));

        return this;
    }

    private static HashSet<DealStatus> PermittedFrom(DealStatus status) => status switch
    {
        DealStatus.Quote => new HashSet<DealStatus>
            { DealStatus.Pending, DealStatus.Approved, DealStatus.Cancelled },
        DealStatus.Pending => new HashSet<DealStatus>
            { DealStatus.Approved, DealStatus.Quote, DealStatus.Cancelled },
        DealStatus.Approved => new HashSet<DealStatus>
            { DealStatus.Contracted, DealStatus.Pending, DealStatus.Cancelled },

        // Past contracting the exit is an unwind, not a cancellation. The
        // distinction matters: a cancelled deal never happened, an unwound one
        // did and was reversed, and they are accounted for differently.
        DealStatus.Contracted => new HashSet<DealStatus>
            { DealStatus.Funded, DealStatus.Delivered, DealStatus.Unwound },
        DealStatus.Funded => new HashSet<DealStatus>
            { DealStatus.Delivered, DealStatus.Completed, DealStatus.Unwound },
        DealStatus.Delivered => new HashSet<DealStatus>
            { DealStatus.Funded, DealStatus.Completed, DealStatus.Unwound },
        _ => new HashSet<DealStatus>(),
    };
}

/// <summary>A deal was opened.</summary>
public sealed record DealCreated(Guid DealId, Guid TenantId, string DealNumber) : DomainEvent
{
    /// <inheritdoc />
    public override string EventType => "sales.deal.created";
}

/// <summary>A new calculation snapshot became authoritative.</summary>
public sealed record DealCalculated(
    Guid DealId, Guid TenantId, Guid CalculationId, int Version) : DomainEvent
{
    /// <inheritdoc />
    public override string EventType => "sales.deal.calculated";
}

/// <summary>A deal moved between states.</summary>
public sealed record DealStatusChanged(
    Guid DealId, Guid TenantId, string FromStatus, string ToStatus) : DomainEvent
{
    /// <inheritdoc />
    public override string EventType => "sales.deal.status_changed";
}
