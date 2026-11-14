using MautoDesk.Compliance.Application;
using MautoDesk.Compliance.Contracts;
using MautoDesk.Sales.Application;
using MautoDesk.Sales.Contracts;
using MautoDesk.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace MautoDesk.Api;

/// <summary>
/// The deal and compliance HTTP surface.
/// </summary>
/// <remarks>
/// Thin, like the inventory endpoints. An endpoint parses, calls a handler and
/// maps the result. It holds no business rules and — deliberately — no
/// authorization checks: permissions are enforced in the application layer so a
/// background job or an internal call is gated by exactly the same code.
/// </remarks>
public static class DealEndpoints
{
    /// <summary>Maps the deal, calculation and compliance endpoints.</summary>
    public static IEndpointRouteBuilder MapDealEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var deals = app.MapGroup("/api/v1/deals")
            .WithTags("Deals")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.ReadPolicy);

        deals.MapGet("/", ListDealsAsync)
            .WithName("listDeals")
            .WithMetadata(new RequiresPermissionAttribute(SalesPermissions.DealRead))
            .WithSummary("List deals")
            .Produces<PagedResult<DealSummaryDto>>(StatusCodes.Status200OK);

        deals.MapPost("/", CreateDealAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("createDeal")
            .WithMetadata(new RequiresPermissionAttribute(SalesPermissions.DealWrite))
            .WithSummary("Open a deal")
            .Produces<DealDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        deals.MapGet("/{dealId:guid}", GetDealAsync)
            .WithName("getDeal")
            .WithMetadata(new RequiresPermissionAttribute(SalesPermissions.DealRead))
            .WithSummary("Get a deal")
            .Produces<DealDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        deals.MapPost("/{dealId:guid}/status", ChangeStatusAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("changeDealStatus")
            .WithMetadata(new RequiresPermissionAttribute(SalesPermissions.DealWrite))
            .WithSummary("Change deal status")
            .Produces<DealDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // The calculation is a POST rather than a PUT because it appends a new
        // immutable snapshot rather than replacing anything. Calling it twice
        // produces two versions, both kept — which is the intended behaviour and
        // the reason it is not idempotent by default.
        deals.MapPost("/{dealId:guid}/calculate", CalculateAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("calculateDeal")
            .WithMetadata(new RequiresPermissionAttribute(SalesPermissions.DealWrite))
            .WithSummary("Price the deal and append a calculation snapshot")
            .Produces<DealCalculationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        deals.MapGet("/{dealId:guid}/calculation", GetCalculationAsync)
            .WithName("getDealCalculation")
            .WithMetadata(new RequiresPermissionAttribute(SalesPermissions.DealRead))
            .WithSummary("Get the current calculation snapshot")
            .Produces<DealCalculationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        deals.MapPost("/{dealId:guid}/evaluate", EvaluateAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("evaluateDealCompliance")
            .WithMetadata(new RequiresPermissionAttribute(CompliancePermissions.EvaluationRead))
            .WithSummary("Evaluate the deal against the rules in force on its date")
            .Produces<ComplianceRunDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        deals.MapGet("/{dealId:guid}/compliance", GetComplianceAsync)
            .WithName("getDealCompliance")
            .WithMetadata(new RequiresPermissionAttribute(CompliancePermissions.EvaluationRead))
            .WithSummary("Get the most recent compliance run")
            .Produces<ComplianceRunDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ListDealsAsync(
        [AsParameters] DealListQuery query,
        DealCommandHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var filter = new DealListFilter
        {
            Page = query.Page ?? 1,
            PageSize = query.PageSize ?? DealListFilter.DefaultPageSize,
            Statuses = query.Status?.Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries),
            CustomerId = query.CustomerId,
            VehicleId = query.VehicleId,
        };

        var result = await handler.ListAsync(filter, cancellationToken).ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> CreateDealAsync(
        CreateDealCommand command,
        DealCommandHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await handler.CreateAsync(command, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/deals/{result.Value!.Id}", result.Value)
            : result.Error!.ToProblem(context);
    }

    private static async Task<IResult> GetDealAsync(
        Guid dealId,
        DealCommandHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await handler.GetAsync(dealId, cancellationToken).ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> ChangeStatusAsync(
        Guid dealId,
        ChangeDealStatusCommand command,
        DealCommandHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await handler.ChangeStatusAsync(dealId, command, cancellationToken)
            .ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> CalculateAsync(
        Guid dealId,
        CalculateDealRequest request,
        DealCalculationHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await handler.CalculateAsync(dealId, request, cancellationToken)
            .ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> GetCalculationAsync(
        Guid dealId,
        DealCalculationHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await handler.GetCurrentAsync(dealId, cancellationToken).ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> EvaluateAsync(
        Guid dealId,
        [FromQuery] string? trigger,
        ComplianceEvaluationHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .EvaluateAsync(dealId, trigger ?? "DEAL_SOLD", cancellationToken)
            .ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> GetComplianceAsync(
        Guid dealId,
        ComplianceEvaluationHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await handler.GetLatestAsync(dealId, cancellationToken).ConfigureAwait(false);

        return result.ToHttp(context);
    }
}

/// <summary>Query-string parameters for the deal list.</summary>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page, clamped to 100.</param>
/// <param name="Status">Comma-separated statuses.</param>
/// <param name="CustomerId">Restrict to one customer.</param>
/// <param name="VehicleId">Restrict to one vehicle.</param>
public sealed record DealListQuery(
    [FromQuery] int? Page,
    [FromQuery] int? PageSize,
    [FromQuery] string? Status,
    [FromQuery] Guid? CustomerId,
    [FromQuery] Guid? VehicleId);
