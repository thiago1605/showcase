namespace FellowCore.Application.Modules.Auth.Interfaces;

public interface ITotpService
{
    string GenerateSecret();
    string GenerateQrCodeUri(string email, string base32Secret, string issuer = "FellowCore");
    bool ValidateCode(string base32Secret, string code);
}
