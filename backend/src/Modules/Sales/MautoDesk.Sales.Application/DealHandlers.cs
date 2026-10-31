using System.Text.Json;
using MautoDesk.Sales.Contracts;
using MautoDesk.Sales.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Application;

/// <summary>Opens a deal.</summary>
public sealed record CreateDealCommand
{
    /// <summary>Left empty, the next number in the tenant's sequence is used.</summary>
    public string? DealNumber { get; init; }

    /// <summary>Retail unless stated otherwise.</summary>
    public string Type { get; init; } = "retail";

    /// <summary>The vehicle being sold.</summary>
    public Guid? VehicleId { get; init; }

    /// <summary>The buyer.</summary>
    public Guid? CustomerId { get; init; }

    /// <summary>The taxing jurisdiction whose rules govern.</summary>
    public Guid? JurisdictionId { get; init; }

    /// <summary>The salesperson.</summary>
    public Guid? SalespersonId { get; init; }

    /// <summary>Free text.</summary>
    public string? Notes { get; init; }
}

/// <summary>Moves a deal between states.</summary>
/// <param name="Status">The target status.</param>
/// <param name="Reason">Required when cancelling or unwinding.</param>
public sealed record ChangeDealStatusCommand(string Status, string? Reason = null);

/// <summary>
/// Deal write operations.
/// </summary>
/// <remarks>
/// <b>Authorization is enforced here, not on the endpoint.</b> An HTTP request is
/// one caller; a background job, an import and an internal call are others. A
/// check that lives in a controller protects exactly one of the four.
/// </remarks>
public sealed class DealCommandHandler
{
    private readonly IDealRepository _deals;
    private readonly IDealReadStore _readStore;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditLog _audit;
    private readonly ITenantContext _tenant;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public DealCommandHandler(
        IDealRepository deals,
        IDealReadStore readStore,
        IUnitOfWork unitOfWork,
        IAuditLog audit,
        ITenantContext tenant,
        IClock clock)
    {
        _deals = deals;
        _readStore = readStore;
        _unitOfWork = unitOfWork;
        _audit = audit;
        _tenant = tenant;
        _clock = clock;
    }

    /// <summary>Opens a deal.</summary>
    public async Task<Result<DealDto>> CreateAsync(
        CreateDealCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_tenant.HasPermission(SalesPermissions.DealWrite))
        {
            return Error.Forbidden("forbidden", "You do not have permission to create deals.");
        }

        if (!TryParseType(command.Type, out var type))
        {
            return Error.Validation("deal.invalid_type", $"'{command.Type}' is not a deal type.");
        }

        var number = string.IsNullOrWhiteSpace(command.DealNumber)
            ? await _deals.NextDealNumberAsync(cancellationToken).ConfigureAwait(false)
            : command.DealNumber.Trim();

        if (await _deals.DealNumberExistsAsync(number, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict("deal.number_taken", $"Deal number {number} is already in use.");
        }

        var created = Deal.Create(_tenant.RequireTenantId(), number, type, _clock.UtcNow);

        if (created.IsFailure)
        {
            return created.Error!;
        }

        var deal = created.Value!;
        deal.Describe(command.VehicleId, command.CustomerId, command.JurisdictionId, command.SalespersonId);
        deal.SetNotes(command.Notes);

        _deals.Add(deal);

        _audit.Record(new AuditEntry
        {
            Action = "sales.deal.created",
            EntitySchema = "sales",
            EntityType = "deal",
            EntityId = deal.Id,
            After = new { deal.DealNumber, Type = deal.Type.ToString(), Status = deal.Status.ToString() },
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var dto = await _readStore.GetAsync(deal.Id, IncludeGross(), cancellationToken).ConfigureAwait(false);

        return dto is null
            ? Error.NotFound("deal.not_found", "The deal was created but could not be read back.")
            : dto;
    }

    /// <summary>Reads a deal.</summary>
    public async Task<Result<DealDto>> GetAsync(Guid dealId, CancellationToken cancellationToken)
    {
        if (!_tenant.HasPermission(SalesPermissions.DealRead)
            && !_tenant.HasPermission(SalesPermissions.DealReadAll))
        {
            return Error.Forbidden("forbidden", "You do not have permission to view deals.");
        }

        var dto = await _readStore.GetAsync(dealId, IncludeGross(), cancellationToken).ConfigureAwait(false);

        // 404 rather than 403 for a deal belonging to another dealership. Answering
        // 403 would confirm the record exists, turning any id into an existence
        // oracle for another dealer's business.
        return dto is null
            ? Error.NotFound("deal.not_found", "That deal does not exist.")
            : dto;
    }

    /// <summary>Lists deals.</summary>
    public async Task<Result<PagedResult<DealSummaryDto>>> ListAsync(
        DealListFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (!_tenant.HasPermission(SalesPermissions.DealRead)
            && !_tenant.HasPermission(SalesPermissions.DealReadAll))
        {
            return Error.Forbidden("forbidden", "You do not have permission to view deals.");
        }

        return await _readStore.ListAsync(filter.Normalized(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves a deal between states.</summary>
    public async Task<Result<DealDto>> ChangeStatusAsync(
        Guid dealId,
        ChangeDealStatusCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!TryParseStatus(command.Status, out var target))
        {
            return Error.Validation("deal.invalid_status", $"'{command.Status}' is not a deal status.");
        }

        // Approving is a separate permission from editing. A salesperson may build
        // a deal all day; deciding it can be contracted is a manager's call.
        var required = target is DealStatus.Approved
            ? SalesPermissions.DealApprove
            : target is DealStatus.Cancelled or DealStatus.Unwound
                ? SalesPermissions.DealVoid
                : SalesPermissions.DealWrite;

        if (!_tenant.HasPermission(required))
        {
            return Error.Forbidden("forbidden", $"You do not have permission to {command.Status} a deal.");
        }

        var deal = await _deals.GetByIdAsync(dealId, cancellationToken).ConfigureAwait(false);

        if (deal is null)
        {
            return Error.NotFound("deal.not_found", "That deal does not exist.");
        }

        var previous = deal.Status;
        var moved = deal.TransitionTo(target, _clock.UtcNow, command.Reason);

        if (moved.IsFailure)
        {
            return moved.Error!;
        }

        _audit.Record(new AuditEntry
        {
            Action = "sales.deal.status_changed",
            EntitySchema = "sales",
            EntityType = "deal",
            EntityId = deal.Id,
            Before = new { Status = previous.ToString() },
            After = new { Status = deal.Status.ToString(), command.Reason },
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var dto = await _readStore.GetAsync(deal.Id, IncludeGross(), cancellationToken).ConfigureAwait(false);

        return dto is null
            ? Error.NotFound("deal.not_found", "That deal does not exist.")
            : dto;
    }

    private bool IncludeGross() => _tenant.HasPermission(SalesPermissions.GrossRead);

    private static bool TryParseType(string? value, out DealType type) =>
        Enum.TryParse(value, ignoreCase: true, out type) && Enum.IsDefined(type);

    private static bool TryParseStatus(string? value, out DealStatus status) =>
        Enum.TryParse(value, ignoreCase: true, out status) && Enum.IsDefined(status);
}

/// <summary>
/// Prices a deal and writes the snapshot.
/// </summary>
/// <remarks>
/// The seam between the pure engine and the database. Everything here is
/// orchestration: load the inputs, call the engine, persist exactly what it saw
/// and exactly what it returned. No arithmetic happens in this class, on purpose
/// — the moment a total is adjusted here, the snapshot stops being a faithful
/// record of what the engine did and the replay guarantee is gone.
/// </remarks>
public sealed class DealCalculationHandler
{
    private readonly IDealRepository _deals;
    private readonly ICalculationStore _calculations;
    private readonly ITaxRuleSetStore _ruleSets;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditLog _audit;
    private readonly ITenantContext _tenant;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public DealCalculationHandler(
        IDealRepository deals,
        ICalculationStore calculations,
        ITaxRuleSetStore ruleSets,
        IUnitOfWork unitOfWork,
        IAuditLog audit,
        ITenantContext tenant,
        IClock clock)
    {
        _deals = deals;
        _calculations = calculations;
        _ruleSets = ruleSets;
        _unitOfWork = unitOfWork;
        _audit = audit;
        _tenant = tenant;
        _clock = clock;
    }

    /// <summary>Prices the deal and appends a snapshot.</summary>
    /// <param name="dealId">The deal.</param>
    /// <param name="request">The figures being quoted.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<DealCalculationDto>> CalculateAsync(
        Guid dealId,
        CalculateDealRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_tenant.HasPermission(SalesPermissions.DealWrite))
        {
            return Error.Forbidden("forbidden", "You do not have permission to price deals.");
        }

        var deal = await _deals.GetByIdAsync(dealId, cancellationToken).ConfigureAwait(false);

        if (deal is null)
        {
            return Error.NotFound("deal.not_found", "That deal does not exist.");
        }

        if (deal.JurisdictionId is not { } jurisdictionId)
        {
            return Error.Validation(
                "deal.no_jurisdiction",
                "This deal has no taxing jurisdiction, so there is no rule set to price it with. " +
                "Set one before calculating.");
        }

        var input = request.ToInput(dealId, _clock.Today);

        if (input.IsFailure)
        {
            return input.Error!;
        }

        var row = await _ruleSets
            .FindAsync(jurisdictionId, input.Value!.DealDate, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return Error.Validation(
                "deal.no_rule_set",
                $"No tax rule set covers {input.Value.DealDate:yyyy-MM-dd} for this jurisdiction.");
        }

        var rules = TaxRuleSetSerializer.Deserialize(row);

        if (rules.IsFailure)
        {
            return rules.Error!;
        }

        // The engine's own refusals — an unapproved rule set above all — come back
        // through here untouched. They are the answer, not an obstacle to it.
        var calculated = DealEngine.Calculate(input.Value, rules.Value!);

        if (calculated.IsFailure)
        {
            return calculated.Error!;
        }

        var calculation = calculated.Value!;

        var inputJson = JsonSerializer.Serialize(input.Value, SnapshotSerialization.Options);
        var outputJson = JsonSerializer.Serialize(calculation, SnapshotSerialization.Options);

        var version = await _calculations.NextVersionAsync(dealId, cancellationToken).ConfigureAwait(false);

        var record = DealCalculationRecord.From(
            deal.TenantId, dealId, version, calculation, inputJson, outputJson,
            _clock.UtcNow, _tenant.UserId);

        _calculations.Add(record);

        // No "supersede the previous row" step: the table forbids updating one at
        // all. Moving the deal's pointer is what makes this version current, and
        // every earlier version stays exactly as it was written.
        deal.AdoptCalculation(record.Id, version);

        _audit.Record(new AuditEntry
        {
            Action = "sales.deal.calculated",
            EntitySchema = "sales",
            EntityType = "deal_calculation",
            EntityId = record.Id,
            After = new
            {
                Version = version,
                TotalSalePrice = calculation.TotalSalePrice.ToString(),
                BalanceDue = calculation.BalanceDue.ToString(),
                calculation.EngineVersion,
                RuleSetVersion = calculation.RuleSetVersion,
            },
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return DealCalculationMapper.ToDto(
            record, calculation, _tenant.HasPermission(SalesPermissions.GrossRead));
    }

    /// <summary>Reads the current snapshot for a deal.</summary>
    public async Task<Result<DealCalculationDto>> GetCurrentAsync(
        Guid dealId,
        CancellationToken cancellationToken)
    {
        if (!_tenant.HasPermission(SalesPermissions.DealRead)
            && !_tenant.HasPermission(SalesPermissions.DealReadAll))
        {
            return Error.Forbidden("forbidden", "You do not have permission to view deals.");
        }

        var record = await _calculations.GetCurrentAsync(dealId, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {
            return Error.NotFound("calculation.not_found", "This deal has not been priced yet.");
        }

        var calculation = JsonSerializer.Deserialize<DealCalculation>(record.Output, SnapshotSerialization.Options);

        return calculation is null
            ? Error.NotFound("calculation.unreadable", "The stored calculation could not be read.")
            : DealCalculationMapper.ToDto(
                record, calculation, _tenant.HasPermission(SalesPermissions.GrossRead));
    }
}
