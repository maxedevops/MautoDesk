using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace MautoDesk.Api;

/// <summary>
/// Marks the permission an endpoint's handler requires.
/// </summary>
/// <remarks>
/// <b>Documentation, not enforcement.</b> The check lives in the application
/// layer so that jobs and internal callers are gated too (docs/02-architecture.md
/// §5); this attribute only surfaces the same permission in the generated
/// contract so a client author can see what a token needs. Both read the same
/// <c>InventoryPermissions</c> constant, so they cannot disagree on spelling.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequiresPermissionAttribute : Attribute
{
    public RequiresPermissionAttribute(string permission) => Permission = permission;

    public string Permission { get; }
}

/// <summary>Fills in the document-level metadata ASP.NET cannot infer.</summary>
/// <remarks>
/// Everything here used to be hand-maintained in <c>contracts/openapi.design.yaml</c>.
/// Moving it into code is what makes ADR-0010 real: the published contract is a
/// build output, so it cannot drift from the endpoints it describes.
/// </remarks>
internal sealed class MautoDeskDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Info = new OpenApiInfo
        {
            Title = "MautoDesk API",
            Version = "1.0.0",
            Description =
                "Multi-tenant dealership management platform.\n\n" +
                "**Authentication.** Bearer access tokens. Tenant scope is carried in the token's " +
                "`tenant` claim; supplying a tenant identifier in a header, query string, or body " +
                "has no effect and is never honoured.\n\n" +
                "**Errors.** All non-2xx responses are RFC 9457 `application/problem+json`. A " +
                "resource belonging to another tenant is reported as `404 Not Found` — the API " +
                "does not confirm the existence of records outside the caller's tenant.\n\n" +
                "**Money** is transported as a decimal string (`\"28995.00\"`), never a JSON " +
                "number, so no client can round it through a floating-point value.",
            License = new OpenApiLicense
            {
                Name = "Proprietary",
                Url = new Uri("https://mautodesk.com/legal/api-terms"),
            },
        };

        document.Servers =
        [
            new OpenApiServer { Url = "https://api.mautodesk.com", Description = "Production" },
            new OpenApiServer { Url = "https://staging-api.mautodesk.com", Description = "Staging" },
            new OpenApiServer { Url = "http://localhost:5080", Description = "Local development" },
        ];

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes["bearerAuth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description =
                "Access token from /auth/login. The tenant is carried in the token's `tenant` " +
                "claim and cannot be overridden by any request parameter.",
        };

        document.SecurityRequirements.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Id = "bearerAuth",
                    Type = ReferenceType.SecurityScheme,
                },
            }] = [],
        });

        return Task.CompletedTask;
    }
}

/// <summary>Adds the permission extension and the shared error responses.</summary>
internal sealed class MautoDeskOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var permission = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<RequiresPermissionAttribute>()
            .FirstOrDefault();

        if (permission is not null)
        {
            operation.Extensions["x-permission"] = new OpenApiString(permission.Permission);
        }

        // 401/403/404 apply to every tenant-scoped operation and were previously
        // repeated by hand on ~90 operations in the design contract. Declaring
        // them once here means they can never fall out of sync.
        AddIfMissing(operation, "401", "Missing, expired, or invalid credentials.");
        AddIfMissing(operation, "403", "The principal lacks the required permission.");

        if (context.Description.ParameterDescriptions.Any(p => p.Source.Id == "Path"))
        {
            AddIfMissing(
                operation,
                "404",
                "Not found, or the resource belongs to another tenant. These cases are " +
                "deliberately indistinguishable: a 403 would confirm the record exists.");
        }

        return Task.CompletedTask;
    }

    private static void AddIfMissing(OpenApiOperation operation, string statusCode, string description)
    {
        if (operation.Responses.ContainsKey(statusCode))
        {
            return;
        }

        operation.Responses[statusCode] = new OpenApiResponse
        {
            Description = description,
            Content =
            {
                ["application/problem+json"] = new OpenApiMediaType(),
            },
        };
    }
}
