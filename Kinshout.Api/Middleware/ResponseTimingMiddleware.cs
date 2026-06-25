using System.Diagnostics;

namespace Kinshout.Api.Middleware;

/// <summary>Exposes server processing time and logs slow API responses.</summary>
public sealed class ResponseTimingMiddleware(RequestDelegate next, ILogger<ResponseTimingMiddleware> logger)
{
    private const int SlowRequestThresholdMs = 50;

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            context.Response.Headers["X-Response-Time-Ms"] = elapsedMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

            if (context.Request.Path.StartsWithSegments("/api")
                && elapsedMs > SlowRequestThresholdMs
                && !IsExcludedPath(context))
            {
                logger.LogWarning(
                    "Slow API {Method} {Path} completed in {ElapsedMs:0} ms (target <= {TargetMs} ms)",
                    context.Request.Method,
                    context.Request.Path,
                    elapsedMs,
                    SlowRequestThresholdMs);
            }
        }
    }

    private static bool IsExcludedPath(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/api/search", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/categorize", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/uploads", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.StartsWith("/api/adverts", StringComparison.OrdinalIgnoreCase)
            && !HttpMethods.IsGet(context.Request.Method))
            return true;

        return false;
    }
}
