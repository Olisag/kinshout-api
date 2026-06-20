using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Kinshout.Api.Swagger;

/// <summary>
/// Ensures upload endpoints expose multipart file fields in Swagger UI (Choose file buttons).
/// </summary>
public sealed class SwaggerFileUploadOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = "/" + (context.ApiDescription.RelativePath ?? "").Split('?')[0].TrimEnd('/');

        operation.RequestBody = path switch
        {
            "/api/uploads/images" => CreateImagesBody(),
            "/api/uploads/resume" => CreateSingleFileBody("file"),
            _ => operation.RequestBody,
        };
    }

    private static OpenApiRequestBody CreateImagesBody()
    {
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["files"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" },
                },
            },
            Required = new HashSet<string> { "files" },
        };

        return new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new OpenApiMediaType
                {
                    Schema = schema,
                    Encoding = new Dictionary<string, OpenApiEncoding>
                    {
                        ["files"] = new OpenApiEncoding
                        {
                            Style = ParameterStyle.Form,
                            Explode = true,
                            ContentType = "image/jpeg, image/png, image/webp",
                        },
                    },
                },
            },
        };
    }

    private static OpenApiRequestBody CreateSingleFileBody(string fieldName)
    {
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                [fieldName] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" },
            },
            Required = new HashSet<string> { fieldName },
        };

        return new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new OpenApiMediaType
                {
                    Schema = schema,
                    Encoding = new Dictionary<string, OpenApiEncoding>
                    {
                        [fieldName] = new OpenApiEncoding
                        {
                            Style = ParameterStyle.Form,
                            ContentType = "application/pdf, application/msword, application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        },
                    },
                },
            },
        };
    }
}
