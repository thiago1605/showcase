namespace FellowCore.Application.Modules.Auth.DTOs;

public record LoginDto(string Email, string Password);

/// <summary>
/// Payload do login via Google. IdToken é o JWT assinado pelo Google
/// (recebido no callback do Google Identity Services do frontend).
/// Backend valida assinatura + audience contra GoogleAuth:ClientId
/// configurado e usa o `sub` do payload como identificador estável.
/// </summary>
public record GoogleLoginDto(string IdToken);

/// <summary>
/// Payload do onboarding pós-SSO. User logado via Google (sem SellerId ainda)
/// escolhe entre criar Seller como Afiliado (mínimo: nome+CPF) ou Produtor
/// (mínimo: nome+CPF/CNPJ+nome fantasia). Backend cria o Seller, vincula ao
/// user, e re-emite tokens com sellerId no payload pra desbloquear endpoints
/// seller-scoped imediatamente.
/// </summary>
public record OnboardSellerDto(
    /// <summary>"AFFILIATE" ou "PRODUCER" — apenas semântico no MVP, ambos
    /// criam o mesmo tipo de Seller. Persistido em metadata para futura
    /// diferenciação de UX (ex: afiliado não precisa preencher dados bancários
    /// imediatamente; produtor é guiado pelo wizard completo).</summary>
    string Mode,
    /// <summary>Razão social (CNPJ) ou nome completo (CPF).</summary>
    string LegalName,
    /// <summary>CPF (11 dígitos) ou CNPJ (14 dígitos), só números.</summary>
    string Document,
    /// <summary>Nome fantasia opcional (Produtor pode preencher; Afiliado normalmente não).</summary>
    string? TradeName = null
);

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
