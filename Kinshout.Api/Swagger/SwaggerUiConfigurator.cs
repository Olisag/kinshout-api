namespace Kinshout.Api.Swagger;

public static class SwaggerUiConfigurator
{
    public static void ConfigureUploadInterceptor(Swashbuckle.AspNetCore.SwaggerUI.SwaggerUIOptions options)
    {
        // External script — avoids Swashbuckle parseFunction() breaking on multi-brace interceptors.
        options.InjectJavascript("/swagger/upload-request-interceptor.js");
        options.UseRequestInterceptor(
            "function(request){return kinshoutUploadRequestInterceptor(request);}");
    }
}
