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
    /// <summary>Right password, but the e-mail address has not been confirmed yet.</summary>
    public const string EmailNotConfirmed = "email_not_confirmed";
    /// <summary>The confirmation link is unknown, already used or expired.</summary>
    public const string InvalidConfirmationToken = "invalid_confirmation_token";
    /// <summary>The account exists but its confirmation e-mail could not be sent; signing up again retries.</summary>
    public const string ConfirmationEmailFailed = "confirmation_email_failed";
    /// <summary>The password reset link is unknown, already used or expired.</summary>
    public const string InvalidResetToken = "invalid_reset_token";
    /// <summary>The password reset e-mail could not be sent; asking again retries.</summary>
    public const string PasswordResetEmailFailed = "password_reset_email_failed";
}
