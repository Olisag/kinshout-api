using Kinshout.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Kinshout.Api.Swagger;

/// <summary>
/// Applies ClientToken (and Bearer when required) so Swagger UI sends the same headers as Postman.
/// </summary>
public sealed class SwaggerSecurityOperationFilter : IOperationFilter
{
    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/client",
        "/api/health",
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var relativePath = context.ApiDescription.RelativePath ?? "";
        var path = "/" + relativePath.Split('?')[0].TrimEnd('/');

        if (PublicPaths.Contains(path))
        {
            operation.Security = [];
            return;
        }

        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        var allowAnonymous = metadata.OfType<IAllowAnonymous>().Any();
        var requiresUser = metadata.OfType<IAuthorizeData>().Any() && !allowAnonymous;

        var requirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("ClientToken", context.Document)] = [],
        };

        if (requiresUser)
            requirement[new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [];

        operation.Security = [requirement];
    }
}
