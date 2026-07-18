using FellowCore.Application.Modules.Auth.Interfaces;
using OtpNet;

namespace FellowCore.Infrastructure.Auth;

public class TotpService : ITotpService
{
    public string GenerateSecret()
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        return Base32Encoding.ToString(key);
    }

    public string GenerateQrCodeUri(string email, string base32Secret, string issuer = "FellowCore")
    {
        var encodedIssuer = Uri.EscapeDataString(issuer);
        var encodedAccount = Uri.EscapeDataString(email);
        return $"otpauth://totp/{encodedIssuer}:{encodedAccount}?secret={base32Secret}&issuer={encodedIssuer}&algorithm=SHA1&digits=6&period=30";
    }

    public bool ValidateCode(string base32Secret, string code)
    {
        try
        {
            var keyBytes = Base32Encoding.ToBytes(base32Secret);
            var totp = new Totp(keyBytes);
            return totp.VerifyTotp(code, out _, new VerificationWindow(previous: 1, future: 1));
        }
        catch
        {
            return false;
        }
    }
}
