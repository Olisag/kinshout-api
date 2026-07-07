using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using Kinshout.Api.Auth;
using Kinshout.Api.Configuration;
using Kinshout.Api.Data;
using Kinshout.Api.Middleware;
using Kinshout.Api.Models;
using Kinshout.Api.Services;
using Kinshout.Api.Swagger;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.Configure<OpenAiSettings>(builder.Configuration.GetSection(OpenAiSettings.SectionName));
builder.Services.Configure<OAuthSettings>(builder.Configuration.GetSection(OAuthSettings.SectionName));
builder.Services.Configure<CorsSettings>(builder.Configuration.GetSection(CorsSettings.SectionName));
builder.Services.Configure<UploadStorageSettings>(builder.Configuration.GetSection(UploadStorageSettings.SectionName));
builder.Services.Configure<ImportSettings>(builder.Configuration.GetSection(ImportSettings.SectionName));

var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
    ?? throw new InvalidOperationException("Jwt settings are required.");
var corsSettings = builder.Configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>() ?? new CorsSettings();
var clientAuthSettings = builder.Configuration.GetSection(ClientAuthSettings.SectionName).Get<ClientAuthSettings>() ?? new ClientAuthSettings();

builder.Services.AddMemoryCache();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

builder.Services.AddDbContext<KinshoutDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    if (connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        options.UseSqlite(connectionString);
    else
        options.UseSqlServer(connectionString, sql =>
            sql.EnableRetryOnFailure())
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

builder.Services.AddHttpClient("OpenAI");
builder.Services.AddHttpClient("ExternalImageMirror", client =>
{
    var mirrorTimeout = builder.Configuration.GetSection(ImportSettings.SectionName).Get<ImportSettings>()?.MirrorImageTimeoutSeconds ?? 30;
    client.Timeout = TimeSpan.FromSeconds(mirrorTimeout);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("KinshoutImporter/1.0");
});

builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IClientAuthService, ClientAuthService>();
builder.Services.AddScoped<IFacebookAuthValidator, FacebookGraphAuthValidator>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUserProfileService, UserProfileService>();
builder.Services.AddSingleton<IPasswordHasher<ApiClient>, PasswordHasher<ApiClient>>();
builder.Services.AddScoped<IOpenAiService, OpenAiService>();
builder.Services.AddScoped<IAdvertService, AdvertService>();
builder.Services.AddScoped<ISavedAdvertService, SavedAdvertService>();
builder.Services.AddScoped<ILikedDiscussionService, LikedDiscussionService>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<IDiscussionService, DiscussionService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IUploadUrlResolver, UploadUrlResolver>();
builder.Services.AddSingleton<LocalUploadStorage>();
builder.Services.AddSingleton<AzureBlobUploadStorage>();
builder.Services.AddSingleton<IUploadStorage>(sp =>
{
    var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<UploadStorageSettings>>().Value;
    return settings.UseAzureBlob
        ? sp.GetRequiredService<AzureBlobUploadStorage>()
        : sp.GetRequiredService<LocalUploadStorage>();
});
builder.Services.AddScoped<IUploadService, UploadService>();
builder.Services.AddSingleton<IAdvertImageProcessor, AdvertImageProcessor>();
builder.Services.AddSingleton<IAdvertDtoMapper, AdvertDtoMapper>();
builder.Services.AddSingleton<IAdvertImageVariantBackfillScheduler, AdvertImageVariantBackfillScheduler>();
builder.Services.AddScoped<IExternalAdvertImageMirrorService, ExternalAdvertImageMirrorService>();
builder.Services.AddSingleton<IExternalAdvertImageMirrorBackfillScheduler, ExternalAdvertImageMirrorBackfillScheduler>();
builder.Services.AddScoped<IExternalAdvertImportEnrichmentService, ExternalAdvertImportEnrichmentService>();
builder.Services.AddScoped<IAdvertModerationService, AdvertModerationService>();
builder.Services.AddScoped<IExternalAdvertImportService, ExternalAdvertImportService>();
builder.Services.AddScoped<IExternalDiscussionTransformService, ExternalDiscussionTransformService>();
builder.Services.AddScoped<IExternalDiscussionImportService, ExternalDiscussionImportService>();
builder.Services.AddSingleton<IDiscussionTopicBackfillScheduler, DiscussionTopicBackfillScheduler>();
builder.Services.AddHostedService<KinshoutStartupHostedService>();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.UserAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
            ClockSkew = TimeSpan.FromMinutes(2),
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    var message = context.Error == "invalid_token"
                        ? "Invalid or expired user session token."
                        : "User sign-in required. Send Authorization: Bearer with a user JWT.";
                    return context.Response.WriteAsJsonAsync(new { error = message });
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthConstants.UserPolicy, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(AuthConstants.TokenTypeClaim, AuthConstants.UserTokenType);
    });
});
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.AllowTrailingCommas = true;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Kinshout API",
        Version = "v1",
        Description = """
            Kinshout marketplace API for Kinshasa — adverts, discussions, and OpenAI-powered search.

            **Security (two layers)**
            1. **Frontend client** — `X-Kinshout-Client-Token` on every `/api/*` call (except `/api/auth/client` and `/api/health`). Obtained via `POST /api/auth/client` with `clientId` + `clientSecret`.
            2. **End user** — `Authorization: Bearer <user-jwt>` for posting, profile, and other signed-in actions. Issued after Google, Apple, or Facebook sign-in.

            **Typical flow**
            1. Frontend calls `POST /api/auth/client` → client JWT
            2. User signs in via `POST /api/auth/google` or `/apple` → user JWT
            3. Browse with client token; post/create with both tokens

            **Swagger:** use **Authorize** → `ClientToken` (from step 1) for most endpoints; add `Bearer` for signed-in routes.
            Upload endpoints (`POST /api/uploads/*`) require both — Swagger auto-injects the client token into the multipart body.
            """,
    });

    var xmlPath = Path.Combine(AppContext.BaseDirectory, "Kinshout.Api.xml");
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath);

    options.AddSecurityDefinition("ClientToken", new OpenApiSecurityScheme
    {
        Name = AuthConstants.ClientTokenHeader,
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description =
            "Frontend client JWT from POST /api/auth/client. Paste the clientToken value only (no Bearer prefix).",
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "User JWT from POST /api/auth/google, /api/auth/apple, or /api/auth/facebook.",
    });

    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("ClientToken", document)] = [],
    });

    options.OperationFilter<SwaggerSecurityOperationFilter>();
    options.OperationFilter<SwaggerFileUploadOperationFilter>();
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("Kinshout", policy =>
    {
        if (clientAuthSettings.AllowAnyOrigin)
        {
            policy.AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
        else
        {
            policy.SetIsOriginAllowed(origin => OriginMatcher.IsAllowed(origin, corsSettings.AllowedOrigins))
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KinshoutDbContext>();
    var conn = builder.Configuration.GetConnectionString("DefaultConnection") ?? "";

    try
    {
        if (conn.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.EnsureCreatedAsync();
            try
            {
                _ = await db.ApiClients.AnyAsync();
            }
            catch
            {
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
            }
        }
        else
            await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Database migration failed — schema patcher will attempt repairs.");
    }

    try
    {
        await DbSchemaPatcher.ApplyAsync(db);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Database schema patch failed — discussion endpoints may error.");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseHttpsRedirection();
}

app.UseResponseCompression();
app.UseCors("Kinshout");
app.UseStaticFiles();

app.UseSwagger(options =>
{
    options.RouteTemplate = "swagger/{documentName}/swagger.json";
});
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Kinshout API v1");
    options.RoutePrefix = "swagger";
    options.DocumentTitle = "Kinshout API — Swagger";
    options.EnablePersistAuthorization();
    SwaggerUiConfigurator.ConfigureUploadInterceptor(options);
});

app.UseMiddleware<ResponseTimingMiddleware>();
app.UseMiddleware<ClientAuthMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();
