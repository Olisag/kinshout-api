namespace Kinshout.Api.Services;

public static class WhatsAppHelper
{
    public static string Normalize(string number)
    {
        var digits = new string(number.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
            throw new ArgumentException("Numéro WhatsApp invalide.");

        if (digits.StartsWith("243") && digits.Length >= 12)
            return "+" + digits;

        if (digits.Length == 9)
            return "+243" + digits;

        if (digits.Length >= 10)
            return "+" + digits;

        throw new ArgumentException("Numéro WhatsApp invalide. Utilisez le format +243 XXX XXX XXX.");
    }
}
