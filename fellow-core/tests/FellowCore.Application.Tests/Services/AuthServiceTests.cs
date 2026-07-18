using FluentAssertions;
using NSubstitute;
using NSubstitute.Extensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Auth.DTOs;
using FellowCore.Application.Modules.Auth.Interfaces;
using FellowCore.Application.Modules.Auth.Services;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class AuthServiceTests
{
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IJwtService _jwtService = Substitute.For<IJwtService>();
    private readonly IPasswordHasher _passwordHasher = Substitute.For<IPasswordHasher>();
    private readonly ITotpService _totpService = Substitute.For<ITotpService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly ILoginLogRepository _loginLogRepo = Substitute.For<ILoginLogRepository>();
    private readonly ISecurityService _securityService = Substitute.For<ISecurityService>();
    private readonly IAppMetrics _appMetrics = Substitute.For<IAppMetrics>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly AuthService _sut;

    private const string TestPepper = "test-pepper-value";
    private const string TestAccessToken = "test-access-token";
    private const string TestRefreshToken = "test-refresh-token";
    private const string TestMfaToken = "test-mfa-token";

    public AuthServiceTests()
    {
        _configuration["Security:BackupCodePepper"].Returns(TestPepper);
        _jwtService.GenerateAccessToken(Arg.Any<User>()).Returns(TestAccessToken);

        _sut = new AuthService(
            _userRepo,
            _jwtService,
            _passwordHasher,
            _totpService,
            _emailService,
            _loginLogRepo,
            _securityService,
            _appMetrics,
            _configuration,
            Substitute.For<ILogger<AuthService>>());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static User CreateTestUser(
        string name = "Test User",
        string email = "test@example.com",
        string passwordHash = "hashed-password",
        UserRole role = UserRole.OWNER,
        Guid? tenantId = null)
    {
        return User.Create(name, email, passwordHash, role, tenantId ?? Guid.NewGuid());
    }

    private void SetupPasswordVerification(bool result)
    {
        _passwordHasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1. LoginAsync — valid credentials returns tokens
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsTokensAndRecordsSuccess()
    {
        var user = CreateTestUser();
        _userRepo.GetByEmailAsync("test@example.com").Returns(user);
        SetupPasswordVerification(true);
        _jwtService.GenerateAccessToken(user).Returns(TestAccessToken);

        var result = await _sut.LoginAsync("test@example.com", "correct-password", "127.0.0.1", "TestAgent");

        result.AccessToken.Should().Be(TestAccessToken);
        result.RefreshToken.Should().NotBeNullOrEmpty();
        result.UserId.Should().Be(user.Id);
        result.RequiresMfa.Should().BeFalse();
        result.MfaToken.Should().BeNull();
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_RecordsLoginOnUser()
    {
        var user = CreateTestUser();
        _userRepo.GetByEmailAsync("test@example.com").Returns(user);
        SetupPasswordVerification(true);

        await _sut.LoginAsync("test@example.com", "correct-password");

        user.LastLogin.Should().NotBeNull();
        user.AccessFailedCount.Should().Be(0);
        await _userRepo.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_SetsRefreshTokenOnUser()
    {
        var user = CreateTestUser();
        _userRepo.GetByEmailAsync("test@example.com").Returns(user);
        SetupPasswordVerification(true);

        await _sut.LoginAsync("test@example.com", "correct-password");

        user.RefreshTokenHash.Should().NotBeNullOrEmpty();
        user.RefreshTokenExpiry.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_RecordsLoginLog()
    {
        var user = CreateTestUser();
        _userRepo.GetByEmailAsync("test@example.com").Returns(user);
        SetupPasswordVerification(true);

        await _sut.LoginAsync("test@example.com", "correct-password", "192.168.1.1", "Mozilla/5.0");

        _loginLogRepo.Received().Add(Arg.Is<LoginLog>(l =>
            l.Email == "test@example.com" &&
            l.Result == LoginResult.SUCCESS));
        await _loginLogRepo.Received().SaveChangesAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. LoginAsync — wrong password records failed login
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LoginAsync_WrongPassword_ThrowsUnauthorized()
    {
        var user = CreateTestUser();
        _userRepo.GetByEmailAsync("test@example.com").Returns(user);
        SetupPasswordVerification(false);

        var act = () => _sut.LoginAsync("test@example.com", "wrong-password");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidCredentials");
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_IncrementsFailedCount()
    {
        var user = CreateTestUser();
        _userRepo.GetByEmailAsync("test@example.com").Returns(user);
        SetupPasswordVerification(false);

        var act = () => _sut.LoginAsync("test@example.com", "wrong-password");
        await act.Should().ThrowAsync<UnauthorizedException>();

        user.AccessFailedCount.Should().Be(1);
        await _userRepo.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_RecordsInvalidCredentialsLog()
    {
        var user = CreateTestUser();
        _userRepo.GetByEmailAsync("test@example.com").Returns(user);
        SetupPasswordVerification(false);

        var act = () => _sut.LoginAsync("test@example.com", "wrong-password", "10.0.0.1", "TestAgent");
        await act.Should().ThrowAsync<UnauthorizedException>();

        _loginLogRepo.Received().Add(Arg.Is<LoginLog>(l =>
            l.Result == LoginResult.INVALID_CREDENTIALS));
    }

    [Fact]
    public async Task LoginAsync_NonExistentEmail_ThrowsUnauthorized()
    {
        _userRepo.GetByEmailAsync("unknown@example.com").Returns((User?)null);

        var act = () => _sut.LoginAsync("unknown@example.com", "any-password");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidCredentials");
    }

    [Fact]
    public async Task LoginAsync_NonExistentEmail_RecordsLoginLogWithEmptyGuid()
    {
        _userRepo.GetByEmailAsync("unknown@example.com").Returns((User?)null);

        var act = () => _sut.LoginAsync("unknown@example.com", "any-password");
        await act.Should().ThrowAsync<UnauthorizedException>();

        _loginLogRepo.Received().Add(Arg.Is<LoginLog>(l =>
            l.UserId == Guid.Empty &&
            l.Result == LoginResult.INVALID_CREDENTIALS));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. LoginAsync — locked out user throws
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LoginAsync_LockedOutUser_ThrowsUnauthorized()
    {
        var user = CreateTestUser();
        // Simulate lockout by recording 5 failed logins
        for (int i = 0; i < 5; i++)
            user.RecordFailedLogin();
        user.IsLockedOut.Should().BeTrue();

        _userRepo.GetByEmailAsync("test@example.com").Returns(user);

        var act = () => _sut.LoginAsync("test@example.com", "correct-password");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.AccountLocked");
    }

    [Fact]
    public async Task LoginAsync_LockedOutUser_RecordsAccountLockedLog()
    {
        var user = CreateTestUser();
        for (int i = 0; i < 5; i++)
            user.RecordFailedLogin();

        _userRepo.GetByEmailAsync("test@example.com").Returns(user);

        var act = () => _sut.LoginAsync("test@example.com", "correct-password");
        await act.Should().ThrowAsync<UnauthorizedException>();

        _loginLogRepo.Received().Add(Arg.Is<LoginLog>(l =>
            l.Result == LoginResult.ACCOUNT_LOCKED));
    }

    [Fact]
    public async Task LoginAsync_LockedOutUser_DoesNotVerifyPassword()
    {
        var user = CreateTestUser();
        for (int i = 0; i < 5; i++)
            user.RecordFailedLogin();

        _userRepo.GetByEmailAsync("test@example.com").Returns(user);

        var act = () => _sut.LoginAsync("test@example.com", "correct-password");
        await act.Should().ThrowAsync<UnauthorizedException>();

        _passwordHasher.DidNotReceive().Verify(Arg.Any<string>(), Arg.Any<string>());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. LoginAsync — deactivated user throws (IsActive=false)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LoginAsync_DeactivatedUser_ThrowsUnauthorized()
    {
        var user = CreateTestUser();
        user.Deactivate();

        _userRepo.GetByEmailAsync("test@example.com").Returns(user);

        var act = () => _sut.LoginAsync("test@example.com", "correct-password");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.AccountDeactivated");
    }

    [Fact]
    public async Task LoginAsync_DeactivatedUser_DoesNotVerifyPassword()
    {
        var user = CreateTestUser();
        user.Deactivate();

        _userRepo.GetByEmailAsync("test@example.com").Returns(user);

        var act = () => _sut.LoginAsync("test@example.com", "correct-password");
        await act.Should().ThrowAsync<UnauthorizedException>();

        _passwordHasher.DidNotReceive().Verify(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task LoginAsync_DeactivatedUser_RecordsInvalidCredentialsLog()
    {
        var user = CreateTestUser();
        user.Deactivate();

        _userRepo.GetByEmailAsync("test@example.com").Returns(user);

        var act = () => _sut.LoginAsync("test@example.com", "correct-password");
        await act.Should().ThrowAsync<UnauthorizedException>();

        _loginLogRepo.Received().Add(Arg.Is<LoginLog>(l =>
            l.Result == LoginResult.INVALID_CREDENTIALS));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. LoginAsync — user with 2FA returns MFA required
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LoginAsync_UserWith2FA_ReturnsMfaRequired()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-totp-secret");
        user.EnableTotp();

        _userRepo.GetByEmailAsync("test@example.com").Returns(user);
        SetupPasswordVerification(true);
        _jwtService.GenerateMfaPendingToken(user.Id).Returns(TestMfaToken);

        var result = await _sut.LoginAsync("test@example.com", "correct-password");

        result.RequiresMfa.Should().BeTrue();
        result.MfaToken.Should().Be(TestMfaToken);
        result.AccessToken.Should().BeNull();
        result.RefreshToken.Should().BeNull();
        result.UserId.Should().BeNull();
    }

    [Fact]
    public async Task LoginAsync_UserWith2FA_RecordsMfaRequiredLog()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-totp-secret");
        user.EnableTotp();

        _userRepo.GetByEmailAsync("test@example.com").Returns(user);
        SetupPasswordVerification(true);
        _jwtService.GenerateMfaPendingToken(user.Id).Returns(TestMfaToken);

        await _sut.LoginAsync("test@example.com", "correct-password");

        _loginLogRepo.Received().Add(Arg.Is<LoginLog>(l =>
            l.Result == LoginResult.MFA_REQUIRED));
    }

    [Fact]
    public async Task LoginAsync_UserWith2FA_DoesNotIssueRefreshToken()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-totp-secret");
        user.EnableTotp();

        _userRepo.GetByEmailAsync("test@example.com").Returns(user);
        SetupPasswordVerification(true);
        _jwtService.GenerateMfaPendingToken(user.Id).Returns(TestMfaToken);

        await _sut.LoginAsync("test@example.com", "correct-password");

        // RefreshTokenHash should remain null since tokens are not issued until MFA verification
        user.RefreshTokenHash.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. VerifyMfaAsync — valid TOTP code returns tokens
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyMfaAsync_ValidTotpCode_ReturnsTokens()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-secret");
        user.EnableTotp();

        var userId = user.Id;
        _jwtService.ValidateMfaPendingToken(TestMfaToken, out Arg.Any<Guid>())
            .Returns(x => { x[1] = userId; return true; });
        _userRepo.GetByIdAsync(userId).Returns(user);
        _securityService.DecryptAsync("encrypted-secret").Returns("decrypted-secret");
        _totpService.ValidateCode("decrypted-secret", "123456").Returns(true);
        _jwtService.GenerateAccessToken(user).Returns(TestAccessToken);

        var result = await _sut.VerifyMfaAsync(TestMfaToken, "123456");

        result.AccessToken.Should().Be(TestAccessToken);
        result.RefreshToken.Should().NotBeNullOrEmpty();
        result.UserId.Should().Be(user.Id);
    }

    [Fact]
    public async Task VerifyMfaAsync_ValidTotpCode_SetsRefreshTokenOnUser()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-secret");
        user.EnableTotp();

        var userId = user.Id;
        _jwtService.ValidateMfaPendingToken(TestMfaToken, out Arg.Any<Guid>())
            .Returns(x => { x[1] = userId; return true; });
        _userRepo.GetByIdAsync(userId).Returns(user);
        _securityService.DecryptAsync("encrypted-secret").Returns("decrypted-secret");
        _totpService.ValidateCode("decrypted-secret", "123456").Returns(true);

        await _sut.VerifyMfaAsync(TestMfaToken, "123456");

        user.RefreshTokenHash.Should().NotBeNullOrEmpty();
        user.RefreshTokenExpiry.Should().BeAfter(DateTime.UtcNow);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7. VerifyMfaAsync — backup code works and is consumed
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyMfaAsync_ValidBackupCode_ReturnsTokensAndConsumesCode()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-secret");
        user.EnableTotp();
        var backupCodes = user.GenerateBackupCodes(TestPepper);
        var firstBackupCode = backupCodes[0];
        var initialCount = user.RemainingBackupCodes;

        var userId = user.Id;
        _jwtService.ValidateMfaPendingToken(TestMfaToken, out Arg.Any<Guid>())
            .Returns(x => { x[1] = userId; return true; });
        _userRepo.GetByIdAsync(userId).Returns(user);
        _securityService.DecryptAsync("encrypted-secret").Returns("decrypted-secret");
        // TOTP validation fails, triggering backup code check
        _totpService.ValidateCode("decrypted-secret", firstBackupCode).Returns(false);

        var result = await _sut.VerifyMfaAsync(TestMfaToken, firstBackupCode);

        result.AccessToken.Should().Be(TestAccessToken);
        result.UserId.Should().Be(user.Id);
        user.RemainingBackupCodes.Should().Be(initialCount - 1);
        // SaveChangesAsync called for backup code consumption + token issuance
        await _userRepo.Received(2).SaveChangesAsync();
    }

    [Fact]
    public async Task VerifyMfaAsync_BackupCodeUsedTwice_SecondAttemptFails()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-secret");
        user.EnableTotp();
        var backupCodes = user.GenerateBackupCodes(TestPepper);
        var firstBackupCode = backupCodes[0];

        var userId = user.Id;
        _jwtService.ValidateMfaPendingToken(TestMfaToken, out Arg.Any<Guid>())
            .Returns(x => { x[1] = userId; return true; });
        _userRepo.GetByIdAsync(userId).Returns(user);
        _securityService.DecryptAsync("encrypted-secret").Returns("decrypted-secret");
        _totpService.ValidateCode("decrypted-secret", firstBackupCode).Returns(false);

        // First use succeeds
        await _sut.VerifyMfaAsync(TestMfaToken, firstBackupCode);

        // Second use with the same code should fail
        var act = () => _sut.VerifyMfaAsync(TestMfaToken, firstBackupCode);
        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidTotpCode");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8. VerifyMfaAsync — invalid code throws
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyMfaAsync_InvalidTotpCode_ThrowsUnauthorized()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-secret");
        user.EnableTotp();

        var userId = user.Id;
        _jwtService.ValidateMfaPendingToken(TestMfaToken, out Arg.Any<Guid>())
            .Returns(x => { x[1] = userId; return true; });
        _userRepo.GetByIdAsync(userId).Returns(user);
        _securityService.DecryptAsync("encrypted-secret").Returns("decrypted-secret");
        _totpService.ValidateCode("decrypted-secret", "000000").Returns(false);

        var act = () => _sut.VerifyMfaAsync(TestMfaToken, "000000");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidTotpCode");
    }

    [Fact]
    public async Task VerifyMfaAsync_InvalidMfaToken_ThrowsUnauthorized()
    {
        _jwtService.ValidateMfaPendingToken("invalid-token", out Arg.Any<Guid>())
            .Returns(false);

        var act = () => _sut.VerifyMfaAsync("invalid-token", "123456");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidMfaToken");
    }

    [Fact]
    public async Task VerifyMfaAsync_UserNotFound_ThrowsUnauthorized()
    {
        var userId = Guid.NewGuid();
        _jwtService.ValidateMfaPendingToken(TestMfaToken, out Arg.Any<Guid>())
            .Returns(x => { x[1] = userId; return true; });
        _userRepo.GetByIdAsync(userId).Returns((User?)null);

        var act = () => _sut.VerifyMfaAsync(TestMfaToken, "123456");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidMfaToken");
    }

    [Fact]
    public async Task VerifyMfaAsync_TotpNotEnabled_ThrowsBusinessException()
    {
        var user = CreateTestUser();
        // TotpSecret is null and IsTotpEnabled is false by default

        var userId = user.Id;
        _jwtService.ValidateMfaPendingToken(TestMfaToken, out Arg.Any<Guid>())
            .Returns(x => { x[1] = userId; return true; });
        _userRepo.GetByIdAsync(userId).Returns(user);

        var act = () => _sut.VerifyMfaAsync(TestMfaToken, "123456");

        await act.Should().ThrowAsync<BusinessException>()
            .Where(e => e.Error.Code == "Auth.MfaNotEnabled");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 9. RefreshAsync — valid token returns new tokens
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshAsync_ValidToken_ReturnsNewTokens()
    {
        var user = CreateTestUser();
        // We need to set a known refresh token hash on the user.
        // The service hashes the refresh token with SHA256 and compares.
        var refreshToken = "known-refresh-token";
        var tokenHash = ComputeSha256Hash(refreshToken);
        user.SetRefreshToken(tokenHash, DateTime.UtcNow.AddDays(30));

        _userRepo.GetByIdAsync(user.Id).Returns(user);

        var result = await _sut.RefreshAsync(user.Id, refreshToken);

        result.AccessToken.Should().Be(TestAccessToken);
        result.RefreshToken.Should().NotBeNullOrEmpty();
        result.UserId.Should().Be(user.Id);
    }

    [Fact]
    public async Task RefreshAsync_ValidToken_RotatesRefreshToken()
    {
        var user = CreateTestUser();
        var refreshToken = "known-refresh-token";
        var tokenHash = ComputeSha256Hash(refreshToken);
        user.SetRefreshToken(tokenHash, DateTime.UtcNow.AddDays(30));
        var oldHash = user.RefreshTokenHash;

        _userRepo.GetByIdAsync(user.Id).Returns(user);

        await _sut.RefreshAsync(user.Id, refreshToken);

        // Refresh token hash should have been rotated
        user.RefreshTokenHash.Should().NotBe(oldHash);
        await _userRepo.Received().SaveChangesAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 10. RefreshAsync — deactivated user throws
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshAsync_DeactivatedUser_ThrowsUnauthorized()
    {
        var user = CreateTestUser();
        var refreshToken = "known-refresh-token";
        var tokenHash = ComputeSha256Hash(refreshToken);
        user.SetRefreshToken(tokenHash, DateTime.UtcNow.AddDays(30));
        user.Deactivate();

        _userRepo.GetByIdAsync(user.Id).Returns(user);

        var act = () => _sut.RefreshAsync(user.Id, refreshToken);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.AccountDeactivated");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 11. RefreshAsync — expired token throws
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshAsync_ExpiredToken_ThrowsUnauthorized()
    {
        var user = CreateTestUser();
        var refreshToken = "known-refresh-token";
        var tokenHash = ComputeSha256Hash(refreshToken);
        user.SetRefreshToken(tokenHash, DateTime.UtcNow.AddDays(-1)); // expired yesterday

        _userRepo.GetByIdAsync(user.Id).Returns(user);

        var act = () => _sut.RefreshAsync(user.Id, refreshToken);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidRefreshToken");
    }

    [Fact]
    public async Task RefreshAsync_WrongToken_ThrowsUnauthorized()
    {
        var user = CreateTestUser();
        var refreshToken = "known-refresh-token";
        var tokenHash = ComputeSha256Hash(refreshToken);
        user.SetRefreshToken(tokenHash, DateTime.UtcNow.AddDays(30));

        _userRepo.GetByIdAsync(user.Id).Returns(user);

        var act = () => _sut.RefreshAsync(user.Id, "wrong-refresh-token");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidRefreshToken");
    }

    [Fact]
    public async Task RefreshAsync_NullRefreshTokenHash_ThrowsUnauthorized()
    {
        var user = CreateTestUser();
        // RefreshTokenHash is null by default

        _userRepo.GetByIdAsync(user.Id).Returns(user);

        var act = () => _sut.RefreshAsync(user.Id, "any-token");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidRefreshToken");
    }

    [Fact]
    public async Task RefreshAsync_UserNotFound_ThrowsUnauthorized()
    {
        var userId = Guid.NewGuid();
        _userRepo.GetByIdAsync(userId).Returns((User?)null);

        var act = () => _sut.RefreshAsync(userId, "any-token");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidRefreshToken");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 12. EnableTotpAsync — valid code enables and returns backup codes
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnableTotpAsync_ValidCode_EnablesTotpAndReturnsBackupCodes()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-secret");

        _userRepo.GetByIdAsync(user.Id).Returns(user);
        _securityService.DecryptAsync("encrypted-secret").Returns("decrypted-secret");
        _totpService.ValidateCode("decrypted-secret", "123456").Returns(true);

        var result = await _sut.EnableTotpAsync(user.Id, "123456");

        result.BackupCodes.Should().HaveCount(8);
        result.BackupCodes.Should().AllSatisfy(code => code.Should().NotBeNullOrEmpty());
        user.IsTotpEnabled.Should().BeTrue();
        await _userRepo.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task EnableTotpAsync_InvalidCode_ThrowsUnauthorized()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-secret");

        _userRepo.GetByIdAsync(user.Id).Returns(user);
        _securityService.DecryptAsync("encrypted-secret").Returns("decrypted-secret");
        _totpService.ValidateCode("decrypted-secret", "000000").Returns(false);

        var act = () => _sut.EnableTotpAsync(user.Id, "000000");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidTotpCode");
    }

    [Fact]
    public async Task EnableTotpAsync_NoTotpSecretSetup_ThrowsBusinessException()
    {
        var user = CreateTestUser();
        // TotpSecret is null by default

        _userRepo.GetByIdAsync(user.Id).Returns(user);

        var act = () => _sut.EnableTotpAsync(user.Id, "123456");

        await act.Should().ThrowAsync<BusinessException>()
            .Where(e => e.Error.Code == "Auth.TotpNotSetup");
    }

    [Fact]
    public async Task EnableTotpAsync_UserNotFound_ThrowsNotFoundException()
    {
        var userId = Guid.NewGuid();
        _userRepo.GetByIdAsync(userId).Returns((User?)null);

        var act = () => _sut.EnableTotpAsync(userId, "123456");

        await act.Should().ThrowAsync<NotFoundException>()
            .Where(e => e.Error.Code == "User.NotFound");
    }

    [Fact]
    public async Task EnableTotpAsync_BackupCodesAreUnique()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-secret");

        _userRepo.GetByIdAsync(user.Id).Returns(user);
        _securityService.DecryptAsync("encrypted-secret").Returns("decrypted-secret");
        _totpService.ValidateCode("decrypted-secret", "123456").Returns(true);

        var result = await _sut.EnableTotpAsync(user.Id, "123456");

        result.BackupCodes.Should().OnlyHaveUniqueItems();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 13. ForgotPasswordAsync — non-existent email does not throw
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ForgotPasswordAsync_NonExistentEmail_DoesNotThrow()
    {
        _userRepo.GetByEmailAsync("unknown@example.com").Returns((User?)null);

        var act = () => _sut.ForgotPasswordAsync("unknown@example.com");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ForgotPasswordAsync_NonExistentEmail_DoesNotSendEmail()
    {
        _userRepo.GetByEmailAsync("unknown@example.com").Returns((User?)null);

        await _sut.ForgotPasswordAsync("unknown@example.com");

        await _emailService.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForgotPasswordAsync_ExistingUser_SendsResetEmail()
    {
        var user = CreateTestUser(email: "user@example.com");
        _userRepo.GetByEmailAsync("user@example.com").Returns(user);

        await _sut.ForgotPasswordAsync("user@example.com");

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "user@example.com"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForgotPasswordAsync_ExistingUser_SetsResetTokenOnUser()
    {
        var user = CreateTestUser(email: "user@example.com");
        _userRepo.GetByEmailAsync("user@example.com").Returns(user);

        await _sut.ForgotPasswordAsync("user@example.com");

        user.PasswordResetTokenHash.Should().NotBeNullOrEmpty();
        user.PasswordResetTokenExpiry.Should().BeAfter(DateTime.UtcNow);
        await _userRepo.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task ForgotPasswordAsync_DeactivatedUser_DoesNotSendEmail()
    {
        var user = CreateTestUser(email: "deactivated@example.com");
        user.Deactivate();
        _userRepo.GetByEmailAsync("deactivated@example.com").Returns(user);

        await _sut.ForgotPasswordAsync("deactivated@example.com");

        await _emailService.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        user.PasswordResetTokenHash.Should().BeNull();
    }

    [Fact]
    public async Task ForgotPasswordAsync_DeactivatedUser_RecordsIgnoredInactiveMetric()
    {
        var user = CreateTestUser(email: "inactive@example.com");
        user.Deactivate();
        _userRepo.GetByEmailAsync("inactive@example.com").Returns(user);

        await _sut.ForgotPasswordAsync("inactive@example.com");

        _appMetrics.Received(1).RecordPasswordReset("ignored_inactive");
    }

    [Fact]
    public async Task ForgotPasswordAsync_NonExistentEmail_RecordsIgnoredNotFoundMetric()
    {
        _userRepo.GetByEmailAsync("ghost@example.com").Returns((User?)null);

        await _sut.ForgotPasswordAsync("ghost@example.com");

        _appMetrics.Received(1).RecordPasswordReset("ignored_not_found");
    }

    [Fact]
    public async Task ForgotPasswordAsync_ExistingUser_RecordsSentMetric()
    {
        var user = CreateTestUser(email: "user@example.com");
        _userRepo.GetByEmailAsync("user@example.com").Returns(user);

        await _sut.ForgotPasswordAsync("user@example.com");

        _appMetrics.Received(1).RecordPasswordReset("sent");
    }

    [Fact]
    public async Task ResetPasswordAsync_ValidToken_RecordsSuccessMetric()
    {
        var user = CreateTestUser(email: "user@example.com");
        var token = "reset-token-value";
        var tokenHash = ComputeSha256Hash(token);
        user.SetPasswordResetToken(tokenHash, DateTime.UtcNow.AddMinutes(60));
        _userRepo.GetByEmailAsync("user@example.com").Returns(user);
        _passwordHasher.Hash("NewPassword1!").Returns("hashed");

        await _sut.ResetPasswordAsync("user@example.com", token, "NewPassword1!");

        _appMetrics.Received(1).RecordPasswordReset("success");
    }

    [Fact]
    public async Task ResetPasswordAsync_InvalidToken_RecordsInvalidTokenMetric()
    {
        var user = CreateTestUser(email: "user@example.com");
        user.SetPasswordResetToken(ComputeSha256Hash("real-token"), DateTime.UtcNow.AddMinutes(60));
        _userRepo.GetByEmailAsync("user@example.com").Returns(user);

        var act = () => _sut.ResetPasswordAsync("user@example.com", "wrong-token", "NewPassword1!");

        await act.Should().ThrowAsync<UnauthorizedException>();
        _appMetrics.Received(1).RecordPasswordReset("invalid_token");
    }

    [Fact]
    public async Task ResetPasswordAsync_ExpiredToken_RecordsExpiredTokenMetric()
    {
        var user = CreateTestUser(email: "user@example.com");
        user.SetPasswordResetToken(ComputeSha256Hash("token"), DateTime.UtcNow.AddMinutes(-1));
        _userRepo.GetByEmailAsync("user@example.com").Returns(user);

        var act = () => _sut.ResetPasswordAsync("user@example.com", "token", "NewPassword1!");

        await act.Should().ThrowAsync<UnauthorizedException>();
        _appMetrics.Received(1).RecordPasswordReset("expired_token");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 14. ResetPasswordAsync — valid token resets password
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResetPasswordAsync_ValidToken_ResetsPassword()
    {
        var user = CreateTestUser(email: "user@example.com");
        var token = "reset-token-value";
        var tokenHash = ComputeSha256Hash(token);
        user.SetPasswordResetToken(tokenHash, DateTime.UtcNow.AddMinutes(60));
        var oldPassword = user.Password;

        _userRepo.GetByEmailAsync("user@example.com").Returns(user);
        _passwordHasher.Hash("new-strong-password").Returns("new-hashed-password");

        await _sut.ResetPasswordAsync("user@example.com", token, "new-strong-password");

        user.Password.Should().Be("new-hashed-password");
        user.Password.Should().NotBe(oldPassword);
    }

    [Fact]
    public async Task ResetPasswordAsync_ValidToken_ClearsResetTokenAndRefreshToken()
    {
        var user = CreateTestUser(email: "user@example.com");
        var token = "reset-token-value";
        var tokenHash = ComputeSha256Hash(token);
        user.SetPasswordResetToken(tokenHash, DateTime.UtcNow.AddMinutes(60));
        user.SetRefreshToken("some-refresh-hash", DateTime.UtcNow.AddDays(30));

        _userRepo.GetByEmailAsync("user@example.com").Returns(user);
        _passwordHasher.Hash("new-strong-password").Returns("new-hashed-password");

        await _sut.ResetPasswordAsync("user@example.com", token, "new-strong-password");

        user.PasswordResetTokenHash.Should().BeNull();
        user.PasswordResetTokenExpiry.Should().BeNull();
        user.RefreshTokenHash.Should().BeNull();
        user.RefreshTokenExpiry.Should().BeNull();
        await _userRepo.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task ResetPasswordAsync_ExpiredToken_ThrowsUnauthorized()
    {
        var user = CreateTestUser(email: "user@example.com");
        var token = "reset-token-value";
        var tokenHash = ComputeSha256Hash(token);
        user.SetPasswordResetToken(tokenHash, DateTime.UtcNow.AddMinutes(-1)); // expired

        _userRepo.GetByEmailAsync("user@example.com").Returns(user);

        var act = () => _sut.ResetPasswordAsync("user@example.com", token, "new-password");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidResetToken");
    }

    [Fact]
    public async Task ResetPasswordAsync_WrongToken_ThrowsUnauthorized()
    {
        var user = CreateTestUser(email: "user@example.com");
        var token = "correct-token";
        var tokenHash = ComputeSha256Hash(token);
        user.SetPasswordResetToken(tokenHash, DateTime.UtcNow.AddMinutes(60));

        _userRepo.GetByEmailAsync("user@example.com").Returns(user);

        var act = () => _sut.ResetPasswordAsync("user@example.com", "wrong-token", "new-password");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidResetToken");
    }

    [Fact]
    public async Task ResetPasswordAsync_UserNotFound_ThrowsUnauthorized()
    {
        _userRepo.GetByEmailAsync("unknown@example.com").Returns((User?)null);

        var act = () => _sut.ResetPasswordAsync("unknown@example.com", "any-token", "new-password");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidResetToken");
    }

    [Fact]
    public async Task ResetPasswordAsync_NoResetTokenSet_ThrowsUnauthorized()
    {
        var user = CreateTestUser(email: "user@example.com");
        // PasswordResetTokenHash is null by default

        _userRepo.GetByEmailAsync("user@example.com").Returns(user);

        var act = () => _sut.ResetPasswordAsync("user@example.com", "any-token", "new-password");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .Where(e => e.Error.Code == "Auth.InvalidResetToken");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Additional edge-case tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LogoutAsync_ValidUser_RevokesRefreshToken()
    {
        var user = CreateTestUser();
        user.SetRefreshToken("some-hash", DateTime.UtcNow.AddDays(30));
        _userRepo.GetByIdAsync(user.Id).Returns(user);

        await _sut.LogoutAsync(user.Id);

        user.RefreshTokenHash.Should().BeNull();
        user.RefreshTokenExpiry.Should().BeNull();
        await _userRepo.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task LogoutAsync_UserNotFound_DoesNotThrow()
    {
        var userId = Guid.NewGuid();
        _userRepo.GetByIdAsync(userId).Returns((User?)null);

        var act = () => _sut.LogoutAsync(userId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetupTotpAsync_ValidUser_ReturnSecretAndQrUri()
    {
        var user = CreateTestUser(email: "user@example.com");
        _userRepo.GetByIdAsync(user.Id).Returns(user);
        _totpService.GenerateSecret().Returns("JBSWY3DPEHPK3PXP");
        _securityService.EncryptAsync("JBSWY3DPEHPK3PXP").Returns("encrypted-JBSWY3DPEHPK3PXP");
        _totpService.GenerateQrCodeUri("user@example.com", "JBSWY3DPEHPK3PXP", "FellowCore")
            .Returns("otpauth://totp/FellowCore:user@example.com?secret=JBSWY3DPEHPK3PXP");

        var result = await _sut.SetupTotpAsync(user.Id);

        result.Secret.Should().Be("JBSWY3DPEHPK3PXP");
        result.QrCodeUri.Should().Contain("otpauth://totp/");
        user.TotpSecret.Should().Be("encrypted-JBSWY3DPEHPK3PXP");
    }

    [Fact]
    public async Task SetupTotpAsync_UserNotFound_ThrowsNotFoundException()
    {
        var userId = Guid.NewGuid();
        _userRepo.GetByIdAsync(userId).Returns((User?)null);

        var act = () => _sut.SetupTotpAsync(userId);

        await act.Should().ThrowAsync<NotFoundException>()
            .Where(e => e.Error.Code == "User.NotFound");
    }

    [Fact]
    public async Task DisableTotpAsync_ValidCode_DisablesTotp()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-secret");
        user.EnableTotp();

        _userRepo.GetByIdAsync(user.Id).Returns(user);
        _securityService.DecryptAsync("encrypted-secret").Returns("decrypted-secret");
        _totpService.ValidateCode("decrypted-secret", "123456").Returns(true);

        await _sut.DisableTotpAsync(user.Id, "123456");

        user.IsTotpEnabled.Should().BeFalse();
        user.TotpSecret.Should().BeNull();
        await _userRepo.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task DisableTotpAsync_MfaNotEnabled_ThrowsBusinessException()
    {
        var user = CreateTestUser();

        _userRepo.GetByIdAsync(user.Id).Returns(user);

        var act = () => _sut.DisableTotpAsync(user.Id, "123456");

        await act.Should().ThrowAsync<BusinessException>()
            .Where(e => e.Error.Code == "Auth.MfaNotEnabled");
    }

    [Fact]
    public async Task RegenerateBackupCodesAsync_ValidCode_ReturnsNewCodes()
    {
        var user = CreateTestUser();
        user.SetTotpSecret("encrypted-secret");
        user.EnableTotp();
        var oldCodes = user.GenerateBackupCodes(TestPepper);

        _userRepo.GetByIdAsync(user.Id).Returns(user);
        _securityService.DecryptAsync("encrypted-secret").Returns("decrypted-secret");
        _totpService.ValidateCode("decrypted-secret", "123456").Returns(true);

        var result = await _sut.RegenerateBackupCodesAsync(user.Id, "123456");

        result.BackupCodes.Should().HaveCount(8);
        // New codes should differ from old ones (with extremely high probability)
        result.BackupCodes.Should().NotBeEquivalentTo(oldCodes);
        await _userRepo.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task LoginAsync_MultipleFailedAttempts_LocksAccount()
    {
        var user = CreateTestUser();
        _userRepo.GetByEmailAsync("test@example.com").Returns(user);
        SetupPasswordVerification(false);

        // 5 failed attempts should lock the account
        for (int i = 0; i < 5; i++)
        {
            var act = () => _sut.LoginAsync("test@example.com", "wrong-password");
            await act.Should().ThrowAsync<UnauthorizedException>();
        }

        user.IsLockedOut.Should().BeTrue();
        user.AccessFailedCount.Should().Be(5);
    }

    // ── SHA256 helper (mirrors the private HashToken in AuthService) ──────

    private static string ComputeSha256Hash(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLower();
    }
}
