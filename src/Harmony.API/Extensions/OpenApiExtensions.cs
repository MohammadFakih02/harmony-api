using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Harmony.API.Extensions;

/// <summary>
/// Declares the JWT bearer scheme on the generated OpenAPI document.
/// </summary>
/// <remarks>
/// <para>
/// Without this the document describes the endpoints but carries no security scheme, so the docs
/// UI renders no "Authorize" control and every call to an authenticated route returns 401 — the
/// endpoints are readable but not exercisable.
/// </para>
/// <para>
/// The requirement is applied at the document level rather than per-operation. That marks
/// anonymous routes (login, register, the invite preview) as secured too, which is cosmetically
/// imprecise but harmless: sending a bearer token to an endpoint that ignores it changes nothing.
/// The alternative — an operation transformer inspecting each endpoint's authorization metadata —
/// is more code for a purely presentational gain.
/// </para>
/// <para>
/// Note this targets the Microsoft.OpenApi v2 surface that ships with .NET 10, where
/// <c>OpenApiDocument.SecurityRequirements</c> became <c>Security</c> and references are expressed
/// via <see cref="OpenApiSecuritySchemeReference"/> rather than an inline reference object.
/// </para>
/// </remarks>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description =
                "Paste the access token returned by POST /api/auth/login. "
                + "The \"Bearer \" prefix is added automatically.",
        };

        document.Security ??= [];
        document.Security.Add(
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
            }
        );

        return Task.CompletedTask;
    }
}
