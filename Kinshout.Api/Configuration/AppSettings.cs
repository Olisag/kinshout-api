namespace Kinshout.Api.Configuration;

public class JwtSettings
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "kinshout-api";
    public string Audience { get; set; } = "kinshout-app";
    public string ClientAudience { get; set; } = "kinshout-client";
    public string UserAudience { get; set; } = "kinshout-user";
    public string SecretKey { get; set; } = string.Empty;
    public int ExpirationMinutes { get; set; } = 10080;
    public int ClientExpirationMinutes { get; set; } = 1440;
}

public class OpenAiSettings
{
    public const string SectionName = "OpenAI";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-mini";
    public string VisionModel { get; set; } = "gpt-4o-mini";
    public string ModerationModel { get; set; } = "omni-moderation-latest";
}

public class OAuthSettings
{
    public const string SectionName = "OAuth";
    public GoogleOAuthSettings Google { get; set; } = new();
    public AppleOAuthSettings Apple { get; set; } = new();
    public FacebookOAuthSettings Facebook { get; set; } = new();
}

public class GoogleOAuthSettings
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}

public class AppleOAuthSettings
{
    public string ClientId { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
}

public class FacebookOAuthSettings
{
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
}

public class CorsSettings
{
    public const string SectionName = "Cors";
    public string[] AllowedOrigins { get; set; } = ["https://kinshout.vercel.app", "http://localhost:5173"];
}

public class ClientAuthSettings
{
    public const string SectionName = "ClientAuth";
    public string KinshoutWebSecret { get; set; } = string.Empty;
    /// <summary>When true, any Origin is accepted for POST /api/auth/client (dev / Swagger).</summary>
    public bool AllowAnyOrigin { get; set; } = true;
}
