namespace Kinshout.Api.Swagger;

public static class SwaggerUiConfigurator
{
    public static void ConfigureUploadInterceptor(Swashbuckle.AspNetCore.SwaggerUI.SwaggerUIOptions options)
    {
        var interceptorPath = Path.Combine(AppContext.BaseDirectory, "Swagger", "upload-request-interceptor.js");
        if (!File.Exists(interceptorPath))
            return;

        var interceptor = File.ReadAllText(interceptorPath).Trim();
        options.UseRequestInterceptor(interceptor);
    }
}
