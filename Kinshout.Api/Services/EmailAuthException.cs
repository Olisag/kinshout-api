namespace Kinshout.Api.Services;

/// <summary>
/// Thrown when an e-mail sign-up or sign-in is refused. <see cref="Code"/> is one of
/// <see cref="EmailAuthErrorCodes"/> so clients can show their own message.
/// </summary>
public class EmailAuthException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static class EmailAuthErrorCodes
{
    public const string EmailInUse = "email_in_use";
    public const string InvalidCredentials = "invalid_credentials";
    public const string InvalidDisplayName = "invalid_display_name";
    public const string InvalidEmail = "invalid_email";
    public const string PasswordTooShort = "password_too_short";
    /// <summary>The account was created with Google, Apple or Facebook and has no password.</summary>
    public const string SocialAccount = "social_account";
}
