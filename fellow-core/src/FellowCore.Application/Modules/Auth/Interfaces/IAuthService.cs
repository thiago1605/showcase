using FellowCore.Application.Modules.Auth.DTOs;

namespace FellowCore.Application.Modules.Auth.Interfaces;

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(string email, string password, string? ipAddress = null, string? userAgent = null);

    /// <summary>
    /// Login via Google Identity Services. <paramref name="idToken"/> é o JWT
    /// assinado pelo Google recebido no callback do frontend. Backend valida
    /// e emite tokens internos (mesma forma do login local). Skip MFA — Google
    /// já fez a sua. Cria o user automaticamente no primeiro login.
    /// </summary>
    Task<LoginResponse> LoginWithGoogleAsync(string idToken, string? ipAddress = null, string? userAgent = null);

    /// <summary>
    /// Onboarding pós-SSO: user logado SEM sellerId cria um Seller mínimo
    /// (Afiliado ou Produtor) e fica vinculado. Re-emite tokens internos com
    /// o novo sellerId no payload para desbloquear endpoints seller-scoped
    /// sem precisar de logout/login.
    /// </summary>
    Task<LoginResponse> OnboardSellerAsync(Guid userId, OnboardSellerDto request);
    Task<TokenResponse> VerifyMfaAsync(string mfaToken, string totpCode);
    Task<TokenResponse> RefreshAsync(Guid userId, string refreshToken);
    Task LogoutAsync(Guid userId);
    Task<SetupTotpResponse> SetupTotpAsync(Guid userId);
    Task<EnableTotpResponse> EnableTotpAsync(Guid userId, string totpCode);
    Task DisableTotpAsync(Guid userId, string totpCode);
    Task<EnableTotpResponse> RegenerateBackupCodesAsync(Guid userId, string totpCode);
    Task ForgotPasswordAsync(string email);
    Task ResetPasswordAsync(string email, string token, string newPassword);
}
