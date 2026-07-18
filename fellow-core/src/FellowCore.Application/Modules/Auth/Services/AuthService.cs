using System.Security.Cryptography;
using System.Text;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Auth.DTOs;
using FellowCore.Application.Modules.Auth.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Email.Templates;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Auth.Services;

public class AuthService(
    IUserRepository userRepo,
    IJwtService jwtService,
    IPasswordHasher passwordHasher,
    ITotpService totpService,
    IEmailService emailService,
    ILoginLogRepository loginLogRepo,
    ISecurityService securityService,
    IAppMetrics appMetrics,
    IConfiguration configuration,
    ISellerRepository sellerRepo,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly string _backupCodePepper = configuration["Security:BackupCodePepper"]
        ?? throw new InvalidOperationException("Security:BackupCodePepper configuration is required.");
    private const int RefreshTokenExpirationDays = 30;
    private const int PasswordResetTokenExpirationMinutes = 60;

    public async Task<LoginResponse> LoginAsync(string email, string password, string? ipAddress = null, string? userAgent = null)
    {
        var user = await userRepo.GetByEmailAsync(email);

        if (user == null)
        {
            await RecordLoginAsync(Guid.Empty, null, email, LoginResult.INVALID_CREDENTIALS, ipAddress, userAgent);
            throw new UnauthorizedException("Auth.InvalidCredentials", "Email ou senha invalidos.");
        }

        if (!user.IsActive)
        {
            await RecordLoginAsync(user.Id, user.TenantId, email, LoginResult.INVALID_CREDENTIALS, ipAddress, userAgent);
            throw new UnauthorizedException("Auth.AccountDeactivated", "Conta desativada.");
        }

        if (user.IsLockedOut)
        {
            await RecordLoginAsync(user.Id, user.TenantId, email, LoginResult.ACCOUNT_LOCKED, ipAddress, userAgent);
            throw new UnauthorizedException("Auth.AccountLocked", "Conta bloqueada por excesso de tentativas. Tente novamente em 30 minutos.");
        }

        // Users criados via SSO (Google) não têm Password local. Login local
        // está bloqueado nesses casos — orienta para usar "Entrar com Google".
        if (string.IsNullOrEmpty(user.Password))
        {
            await RecordLoginAsync(user.Id, user.TenantId, email, LoginResult.INVALID_CREDENTIALS, ipAddress, userAgent);
            throw new UnauthorizedException(
                "Auth.SsoOnlyAccount",
                "Esta conta usa login pelo Google. Clique em \"Entrar com Google\" para acessar.");
        }

        if (!passwordHasher.Verify(password, user.Password))
        {
            user.RecordFailedLogin();
            await userRepo.SaveChangesAsync();
            await RecordLoginAsync(user.Id, user.TenantId, email, LoginResult.INVALID_CREDENTIALS, ipAddress, userAgent);
            throw new UnauthorizedException("Auth.InvalidCredentials", "Email ou senha invalidos.");
        }

        user.RecordLogin();
        await userRepo.SaveChangesAsync();

        if (user.IsTotpEnabled)
        {
            await RecordLoginAsync(user.Id, user.TenantId, email, LoginResult.MFA_REQUIRED, ipAddress, userAgent);
            var mfaToken = jwtService.GenerateMfaPendingToken(user.Id);
            return new LoginResponse(null, null, null, RequiresMfa: true, MfaToken: mfaToken);
        }

        await RecordLoginAsync(user.Id, user.TenantId, email, LoginResult.SUCCESS, ipAddress, userAgent);
        return await IssueTokensAsync(user);
    }

    public async Task<TokenResponse> VerifyMfaAsync(string mfaToken, string totpCode)
    {
        if (!jwtService.ValidateMfaPendingToken(mfaToken, out var userId))
            throw new UnauthorizedException("Auth.InvalidMfaToken", "Token MFA invalido ou expirado.");

        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new UnauthorizedException("Auth.InvalidMfaToken", "Token MFA invalido ou expirado.");

        if (!user.IsTotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
            throw new BusinessException("Auth.MfaNotEnabled", "2FA nao esta habilitado para este usuario.");

        var decryptedSecret = await securityService.DecryptAsync(user.TotpSecret);
        bool validCode = totpService.ValidateCode(decryptedSecret, totpCode);
        bool usedBackup = !validCode && user.UseBackupCode(totpCode, _backupCodePepper);

        if (!validCode && !usedBackup)
            throw new UnauthorizedException("Auth.InvalidTotpCode", "Codigo 2FA invalido.");

        if (usedBackup)
            await userRepo.SaveChangesAsync();

        var response = await IssueTokensAsync(user);
        return new TokenResponse(response.AccessToken!, response.RefreshToken!, response.UserId!.Value);
    }

    public async Task<TokenResponse> RefreshAsync(Guid userId, string refreshToken)
    {
        var tokenHash = HashToken(refreshToken);
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new UnauthorizedException("Auth.InvalidRefreshToken", "Token de refresh invalido.");

        if (!user.IsActive)
            throw new UnauthorizedException("Auth.AccountDeactivated", "Conta desativada.");

        if (user.RefreshTokenHash == null || user.RefreshTokenExpiry == null || user.RefreshTokenExpiry < DateTime.UtcNow)
            throw new UnauthorizedException("Auth.InvalidRefreshToken", "Token de refresh invalido ou expirado.");

        var storedBytes = Encoding.UTF8.GetBytes(user.RefreshTokenHash);
        var providedBytes = Encoding.UTF8.GetBytes(tokenHash);
        if (!CryptographicOperations.FixedTimeEquals(storedBytes, providedBytes))
            throw new UnauthorizedException("Auth.InvalidRefreshToken", "Token de refresh invalido.");

        var response = await IssueTokensAsync(user);
        return new TokenResponse(response.AccessToken!, response.RefreshToken!, response.UserId!.Value);
    }

    public async Task LogoutAsync(Guid userId)
    {
        var user = await userRepo.GetByIdAsync(userId);
        if (user == null) return;
        user.RevokeRefreshToken();
        await userRepo.SaveChangesAsync();
    }

    public async Task<SetupTotpResponse> SetupTotpAsync(Guid userId)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new NotFoundException("User.NotFound", "Usuario nao encontrado.");

        var secret = totpService.GenerateSecret();
        var encryptedSecret = await securityService.EncryptAsync(secret);
        user.SetTotpSecret(encryptedSecret);
        await userRepo.SaveChangesAsync();

        var qrUri = totpService.GenerateQrCodeUri(user.Email, secret);
        return new SetupTotpResponse(secret, qrUri);
    }

    public async Task<EnableTotpResponse> EnableTotpAsync(Guid userId, string totpCode)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new NotFoundException("User.NotFound", "Usuario nao encontrado.");

        if (string.IsNullOrEmpty(user.TotpSecret))
            throw new BusinessException("Auth.TotpNotSetup", "Configure o 2FA antes de habilitar. Use GET /auth/2fa/setup.");

        var decryptedSecret = await securityService.DecryptAsync(user.TotpSecret);
        if (!totpService.ValidateCode(decryptedSecret, totpCode))
            throw new UnauthorizedException("Auth.InvalidTotpCode", "Codigo 2FA invalido.");

        user.EnableTotp();
        var backupCodes = user.GenerateBackupCodes(_backupCodePepper);
        await userRepo.SaveChangesAsync();

        return new EnableTotpResponse(backupCodes);
    }

    public async Task DisableTotpAsync(Guid userId, string totpCode)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new NotFoundException("User.NotFound", "Usuario nao encontrado.");

        if (!user.IsTotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
            throw new BusinessException("Auth.MfaNotEnabled", "2FA nao esta habilitado.");

        var decryptedSecret = await securityService.DecryptAsync(user.TotpSecret);
        if (!totpService.ValidateCode(decryptedSecret, totpCode))
            throw new UnauthorizedException("Auth.InvalidTotpCode", "Codigo 2FA invalido.");

        user.DisableTotp();
        await userRepo.SaveChangesAsync();
    }

    public async Task<EnableTotpResponse> RegenerateBackupCodesAsync(Guid userId, string totpCode)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new NotFoundException("User.NotFound", "Usuario nao encontrado.");

        if (!user.IsTotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
            throw new BusinessException("Auth.MfaNotEnabled", "2FA nao esta habilitado.");

        var decryptedSecret = await securityService.DecryptAsync(user.TotpSecret);
        if (!totpService.ValidateCode(decryptedSecret, totpCode))
            throw new UnauthorizedException("Auth.InvalidTotpCode", "Codigo 2FA invalido.");

        var backupCodes = user.GenerateBackupCodes(_backupCodePepper);
        await userRepo.SaveChangesAsync();

        return new EnableTotpResponse(backupCodes);
    }

    public async Task ForgotPasswordAsync(string email)
    {
        var user = await userRepo.GetByEmailAsync(email);
        if (user == null)
        {
            appMetrics.RecordPasswordReset("ignored_not_found");
            logger.LogWarning("Password reset solicitado para email inexistente: {EmailPrefix}***", email[..Math.Min(3, email.Length)]);
            return; // não revelar se o email existe
        }

        if (!user.IsActive)
        {
            appMetrics.RecordPasswordReset("ignored_inactive");
            logger.LogWarning("Password reset solicitado para usuario desativado {UserId}", user.Id);
            return; // tratar igual a inexistente — não revelar status da conta
        }

        var token = GenerateSecureToken();
        var tokenHash = HashToken(token);
        var expiry = DateTime.UtcNow.AddMinutes(PasswordResetTokenExpirationMinutes);
        user.SetPasswordResetToken(tokenHash, expiry);
        await userRepo.SaveChangesAsync();

        var tenantName = user.Tenant?.Name ?? "Fellow Pay";
        var resetBaseUrl = configuration["Auth:PasswordResetBaseUrl"] ?? "";
        var resetUrl = string.IsNullOrEmpty(resetBaseUrl)
            ? ""
            : $"{resetBaseUrl.TrimEnd('/')}?token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";
        var htmlBody = EmailTemplates.PasswordReset(tenantName, token, resetUrl);
        var message = new EmailMessage(user.Email, user.Name, "Redefinição de senha — Fellow Pay", htmlBody);
        await emailService.SendAsync(message);

        appMetrics.RecordPasswordReset("sent");
        logger.LogInformation("Password reset token enviado para usuario {UserId}", user.Id);
    }

    public async Task ResetPasswordAsync(string email, string token, string newPassword)
    {
        var user = await userRepo.GetByEmailAsync(email);
        if (user == null)
        {
            appMetrics.RecordPasswordReset("invalid_token");
            throw new UnauthorizedException("Auth.InvalidResetToken", "Token de redefinição invalido.");
        }

        if (user.PasswordResetTokenHash == null || user.PasswordResetTokenExpiry == null)
        {
            appMetrics.RecordPasswordReset("invalid_token");
            throw new UnauthorizedException("Auth.InvalidResetToken", "Token de redefinição invalido ou expirado.");
        }

        if (user.PasswordResetTokenExpiry < DateTime.UtcNow)
        {
            appMetrics.RecordPasswordReset("expired_token");
            throw new UnauthorizedException("Auth.InvalidResetToken", "Token de redefinição invalido ou expirado.");
        }

        var tokenHash = HashToken(token);
        var storedBytes = Encoding.UTF8.GetBytes(user.PasswordResetTokenHash);
        var providedBytes = Encoding.UTF8.GetBytes(tokenHash);
        if (!CryptographicOperations.FixedTimeEquals(storedBytes, providedBytes))
        {
            appMetrics.RecordPasswordReset("invalid_token");
            throw new UnauthorizedException("Auth.InvalidResetToken", "Token de redefinição invalido.");
        }

        user.UpdatePassword(passwordHasher.Hash(newPassword));
        user.ClearPasswordResetToken();
        user.RevokeRefreshToken();
        await userRepo.SaveChangesAsync();

        appMetrics.RecordPasswordReset("success");
        logger.LogInformation("Senha redefinida com sucesso para usuario {UserId}", user.Id);
    }

    public async Task<LoginResponse> LoginWithGoogleAsync(string idToken, string? ipAddress = null, string? userAgent = null)
    {
        // 1. Valida o ID Token via biblioteca oficial Google. Verifica assinatura,
        //    expiração, issuer ("accounts.google.com" ou "https://accounts.google.com")
        //    e audience contra GoogleAuth:ClientId configurado.
        var clientId = configuration["GoogleAuth:ClientId"]
            ?? throw new InvalidOperationException(
                "GoogleAuth:ClientId não configurado — defina no appsettings.json ou env var.");

        Google.Apis.Auth.GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await Google.Apis.Auth.GoogleJsonWebSignature.ValidateAsync(
                idToken,
                new Google.Apis.Auth.GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[] { clientId },
                });
        }
        catch (Google.Apis.Auth.InvalidJwtException ex)
        {
            logger.LogWarning(ex, "Google ID Token inválido");
            await RecordLoginAsync(Guid.Empty, null, "(google)", LoginResult.INVALID_CREDENTIALS, ipAddress, userAgent);
            throw new UnauthorizedException(
                "Auth.InvalidGoogleToken",
                "Token do Google inválido ou expirado. Tente fazer login novamente.");
        }

        if (!payload.EmailVerified)
        {
            await RecordLoginAsync(Guid.Empty, null, payload.Email ?? "(google)", LoginResult.INVALID_CREDENTIALS, ipAddress, userAgent);
            throw new UnauthorizedException(
                "Auth.GoogleEmailNotVerified",
                "Sua conta Google ainda não tem email verificado. Verifique pela página do Google e tente novamente.");
        }

        // 2. Find-or-create local user. Primeiro tenta por GoogleSubject (link
        //    estável), depois fallback para email (caso o user tenha criado
        //    conta local antes e agora esteja logando via Google pela 1a vez).
        var email = payload.Email!.ToLowerInvariant();
        var user = await userRepo.GetByGoogleSubjectAsync(payload.Subject)
            ?? await userRepo.GetByEmailAsync(email);

        if (user is null)
        {
            // Novo user via Google. Em modo single-tenant (atual), associa ao
            // primeiro tenant ativo. Multi-tenant exigiria fluxo de invite ou
            // seleção explícita — TODO quando aplicável.
            var defaultTenantId = await userRepo.GetDefaultTenantIdAsync();
            user = Domain.Entities.User.CreateFromGoogle(
                name: payload.Name ?? email.Split('@')[0],
                email: email,
                googleSubject: payload.Subject,
                role: UserRole.VIEWER,
                tenantId: defaultTenantId);
            userRepo.Add(user);
            await userRepo.SaveChangesAsync();
            logger.LogInformation("Novo user criado via Google login: {UserId} {Email}", user.Id, email);
        }
        else
        {
            if (!user.IsActive)
            {
                await RecordLoginAsync(user.Id, user.TenantId, email, LoginResult.INVALID_CREDENTIALS, ipAddress, userAgent);
                throw new UnauthorizedException("Auth.AccountDeactivated", "Conta desativada.");
            }

            // Vincula o GoogleSubject ao user existente se for o primeiro login
            // via Google (user criado originalmente com email/senha).
            if (string.IsNullOrEmpty(user.GoogleSubject))
            {
                user.LinkGoogleAccount(payload.Subject);
                logger.LogInformation("Conta Google vinculada ao user {UserId}", user.Id);
            }
        }

        user.RecordLogin();
        await userRepo.SaveChangesAsync();
        await RecordLoginAsync(user.Id, user.TenantId, email, LoginResult.SUCCESS, ipAddress, userAgent);

        // 3. Pula MFA — Google já fez sua própria 2FA do account. Política
        //    poderia mudar (forçar TOTP local em users high-privilege), mas
        //    no MVP confiamos no Google.
        return await IssueTokensAsync(user);
    }

    public async Task<LoginResponse> OnboardSellerAsync(Guid userId, OnboardSellerDto request)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new NotFoundException("User.NotFound", "Usuário não encontrado.");

        if (user.SellerId.HasValue)
            throw new BusinessException(
                "Onboard.AlreadyHasSeller",
                "Sua conta já está vinculada a um produtor. Não é possível repetir o onboarding.");

        if (!user.TenantId.HasValue)
            throw new BusinessException(
                "Onboard.NoTenant",
                "Usuário sem tenant associado — contate o suporte.");

        // Validações de input
        var mode = (request.Mode ?? "").Trim().ToUpperInvariant();
        if (mode != "AFFILIATE" && mode != "PRODUCER")
            throw new BusinessException(
                "Onboard.InvalidMode",
                "Modo inválido. Use 'AFFILIATE' ou 'PRODUCER'.");

        var legalName = (request.LegalName ?? "").Trim();
        if (legalName.Length < 3)
            throw new BusinessException(
                "Onboard.InvalidName",
                "Nome legal precisa ter ao menos 3 caracteres.");

        var document = new string((request.Document ?? "").Where(char.IsDigit).ToArray());
        if (document.Length != 11 && document.Length != 14)
            throw new BusinessException(
                "Onboard.InvalidDocument",
                "Documento inválido. Informe CPF (11 dígitos) ou CNPJ (14 dígitos).");

        // Webhook secret aleatório — não usa de imediato mas o entity exige.
        var webhookSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLower();

        var seller = Domain.Entities.Seller.Create(
            tenantId: user.TenantId.Value,
            legalName: legalName,
            document: document,
            email: user.Email,
            webhookSecret: webhookSecret,
            tradeName: string.IsNullOrWhiteSpace(request.TradeName)
                ? null
                : request.TradeName.Trim());

        sellerRepo.Add(seller);
        await sellerRepo.SaveChangesAsync();

        user.AssignSeller(seller.Id);
        await userRepo.SaveChangesAsync();

        logger.LogInformation(
            "User {UserId} concluiu onboarding como {Mode} — Seller {SellerId} criado.",
            user.Id, mode, seller.Id);

        // Re-emite tokens. O AccessToken novo carrega seller_id no payload,
        // desbloqueando endpoints seller-scoped sem precisar logout/login.
        return await IssueTokensAsync(user);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<LoginResponse> IssueTokensAsync(Domain.Entities.User user)
    {
        var accessToken = jwtService.GenerateAccessToken(user);
        var refreshToken = GenerateOpaqueToken();
        var tokenHash = HashToken(refreshToken);
        var expiry = DateTime.UtcNow.AddDays(RefreshTokenExpirationDays);

        user.SetRefreshToken(tokenHash, expiry);
        await userRepo.SaveChangesAsync();

        return new LoginResponse(accessToken, refreshToken, user.Id, RequiresMfa: false, MfaToken: null);
    }

    private static string GenerateOpaqueToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLower();
    }

    private static string GenerateSecureToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private async Task RecordLoginAsync(
        Guid userId, Guid? tenantId, string email,
        LoginResult result, string? ipAddress, string? userAgent)
    {
        var log = LoginLog.Create(userId, tenantId, email, result, ipAddress, userAgent);
        loginLogRepo.Add(log);
        await loginLogRepo.SaveChangesAsync();
    }
}
