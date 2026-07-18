namespace FellowCore.Application.Modules.Auth.DTOs;

public record LoginDto(string Email, string Password);

public record VerifyMfaDto(string MfaToken, string TotpCode);

public record RefreshTokenRequestDto(Guid UserId, string RefreshToken);

public record TokenResponse(string AccessToken, string RefreshToken, Guid UserId);

public record LoginResponse(
    string? AccessToken,
    string? RefreshToken,
    Guid? UserId,
    bool RequiresMfa,
    string? MfaToken
);

public record SetupTotpResponse(string Secret, string QrCodeUri);

public record EnableTotpDto(string TotpCode);

public record EnableTotpResponse(List<string> BackupCodes);

public record DisableTotpDto(string TotpCode);

public record ForgotPasswordDto(string Email);

public record ResetPasswordDto(string Email, string Token, string NewPassword);
