using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class UserTests
{
    private const string TestPepper = "test-pepper-secret-key-32chars!!";

    private static User CreateValidUser(
        UserRole role = UserRole.VIEWER,
        Guid? tenantId = null) =>
        User.Create("John Doe", "john@test.com", "hashed_password", role, tenantId ?? Guid.NewGuid());

    [Fact]
    public void Create_ShouldSetAllProperties()
    {
        var tenantId = Guid.NewGuid();

        var user = User.Create("Maria", "maria@test.com", "hash123", UserRole.OWNER, tenantId);

        user.Id.Should().NotBeEmpty();
        user.Name.Should().Be("Maria");
        user.Email.Should().Be("maria@test.com");
        user.Password.Should().Be("hash123");
        user.Role.Should().Be(UserRole.OWNER);
        user.TenantId.Should().Be(tenantId);
        user.IsActive.Should().BeTrue();
        user.IsTotpEnabled.Should().BeFalse();
        user.TotpSecret.Should().BeNull();
        user.LastLogin.Should().BeNull();
        user.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        user.AccessFailedCount.Should().Be(0);
        user.LockoutEnd.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldDefaultToViewerRole()
    {
        var user = User.Create("User", "user@test.com", "hash");

        user.Role.Should().Be(UserRole.VIEWER);
    }

    [Fact]
    public void Create_ShouldAllowNullTenantId()
    {
        var user = User.Create("Admin", "admin@test.com", "hash", UserRole.SUPER_ADMIN);

        user.TenantId.Should().BeNull();
    }

    // --- Deactivate ---

    [Fact]
    public void Deactivate_ShouldSetIsActiveToFalse()
    {
        var user = CreateValidUser();

        user.Deactivate();

        user.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Deactivate_ShouldRevokeRefreshToken()
    {
        var user = CreateValidUser();
        user.SetRefreshToken("token_hash", DateTime.UtcNow.AddDays(7));

        user.Deactivate();

        user.IsActive.Should().BeFalse();
        user.RefreshTokenHash.Should().BeNull();
        user.RefreshTokenExpiry.Should().BeNull();
    }

    // --- Login tracking ---

    [Fact]
    public void RecordLogin_ShouldSetLastLoginAndResetFailedCount()
    {
        var user = CreateValidUser();
        user.RecordFailedLogin();
        user.RecordFailedLogin();

        user.RecordLogin();

        user.LastLogin.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        user.AccessFailedCount.Should().Be(0);
        user.LockoutEnd.Should().BeNull();
    }

    [Fact]
    public void RecordFailedLogin_ShouldIncrementAccessFailedCount()
    {
        var user = CreateValidUser();

        user.RecordFailedLogin();

        user.AccessFailedCount.Should().Be(1);
        user.IsLockedOut.Should().BeFalse();
    }

    // --- Lockout ---

    [Fact]
    public void RecordFailedLogin_ShouldLockOut_AfterFiveAttempts()
    {
        var user = CreateValidUser();

        for (int i = 0; i < 5; i++)
            user.RecordFailedLogin();

        user.AccessFailedCount.Should().Be(5);
        user.IsLockedOut.Should().BeTrue();
        user.LockoutEnd.Should().NotBeNull();
        user.LockoutEnd.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void IsLockedOut_ShouldReturnFalse_WhenNotLockedOut()
    {
        var user = CreateValidUser();

        user.IsLockedOut.Should().BeFalse();
    }

    [Fact]
    public void RecordLogin_ShouldClearLockout()
    {
        var user = CreateValidUser();
        for (int i = 0; i < 5; i++)
            user.RecordFailedLogin();
        user.IsLockedOut.Should().BeTrue();

        user.RecordLogin();

        user.IsLockedOut.Should().BeFalse();
        user.AccessFailedCount.Should().Be(0);
        user.LockoutEnd.Should().BeNull();
    }

    // --- Password ---

    [Fact]
    public void UpdatePassword_ShouldChangePassword()
    {
        var user = CreateValidUser();

        user.UpdatePassword("new_hash");

        user.Password.Should().Be("new_hash");
    }

    // --- TOTP ---

    [Fact]
    public void SetTotpSecret_ShouldStoreSecret()
    {
        var user = CreateValidUser();

        user.SetTotpSecret("BASE32SECRET");

        user.TotpSecret.Should().Be("BASE32SECRET");
    }

    [Fact]
    public void EnableTotp_ShouldSetFlag()
    {
        var user = CreateValidUser();
        user.SetTotpSecret("BASE32SECRET");

        user.EnableTotp();

        user.IsTotpEnabled.Should().BeTrue();
    }

    [Fact]
    public void DisableTotp_ShouldClearSecretAndBackupCodes()
    {
        var user = CreateValidUser();
        user.SetTotpSecret("BASE32SECRET");
        user.EnableTotp();
        user.GenerateBackupCodes(TestPepper);

        user.DisableTotp();

        user.IsTotpEnabled.Should().BeFalse();
        user.TotpSecret.Should().BeNull();
        user.BackupCodesJson.Should().BeNull();
    }

    // --- Refresh Token ---

    [Fact]
    public void SetRefreshToken_ShouldStoreHashAndExpiry()
    {
        var user = CreateValidUser();
        var expiry = DateTime.UtcNow.AddDays(14);

        user.SetRefreshToken("refresh_hash", expiry);

        user.RefreshTokenHash.Should().Be("refresh_hash");
        user.RefreshTokenExpiry.Should().Be(expiry);
    }

    [Fact]
    public void RevokeRefreshToken_ShouldClearHashAndExpiry()
    {
        var user = CreateValidUser();
        user.SetRefreshToken("refresh_hash", DateTime.UtcNow.AddDays(14));

        user.RevokeRefreshToken();

        user.RefreshTokenHash.Should().BeNull();
        user.RefreshTokenExpiry.Should().BeNull();
    }

    // --- Password Reset Token ---

    [Fact]
    public void SetPasswordResetToken_ShouldStoreHashAndExpiry()
    {
        var user = CreateValidUser();
        var expiry = DateTime.UtcNow.AddHours(1);

        user.SetPasswordResetToken("reset_hash", expiry);

        user.PasswordResetTokenHash.Should().Be("reset_hash");
        user.PasswordResetTokenExpiry.Should().Be(expiry);
    }

    [Fact]
    public void ClearPasswordResetToken_ShouldClearHashAndExpiry()
    {
        var user = CreateValidUser();
        user.SetPasswordResetToken("reset_hash", DateTime.UtcNow.AddHours(1));

        user.ClearPasswordResetToken();

        user.PasswordResetTokenHash.Should().BeNull();
        user.PasswordResetTokenExpiry.Should().BeNull();
    }

    // --- Backup Codes ---

    [Fact]
    public void GenerateBackupCodes_ShouldReturnEightCodes_ByDefault()
    {
        var user = CreateValidUser();

        var codes = user.GenerateBackupCodes(TestPepper);

        codes.Should().HaveCount(8);
        codes.Should().OnlyContain(c => c.Length == 8);
        user.BackupCodesJson.Should().NotBeNullOrEmpty();
        user.RemainingBackupCodes.Should().Be(8);
    }

    [Fact]
    public void GenerateBackupCodes_ShouldReturnRequestedCount()
    {
        var user = CreateValidUser();

        var codes = user.GenerateBackupCodes(TestPepper, count: 4);

        codes.Should().HaveCount(4);
        user.RemainingBackupCodes.Should().Be(4);
    }

    [Fact]
    public void GenerateBackupCodes_ShouldThrow_WhenPepperIsNull()
    {
        var user = CreateValidUser();

        var act = () => user.GenerateBackupCodes(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GenerateBackupCodes_ShouldThrow_WhenPepperIsEmpty()
    {
        var user = CreateValidUser();

        var act = () => user.GenerateBackupCodes(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UseBackupCode_ShouldReturnTrue_WhenCodeIsValid()
    {
        var user = CreateValidUser();
        var codes = user.GenerateBackupCodes(TestPepper);
        var firstCode = codes[0];

        var result = user.UseBackupCode(firstCode, TestPepper);

        result.Should().BeTrue();
        user.RemainingBackupCodes.Should().Be(7);
    }

    [Fact]
    public void UseBackupCode_ShouldReturnFalse_WhenCodeIsInvalid()
    {
        var user = CreateValidUser();
        user.GenerateBackupCodes(TestPepper);

        var result = user.UseBackupCode("00000000", TestPepper);

        result.Should().BeFalse();
        user.RemainingBackupCodes.Should().Be(8);
    }

    [Fact]
    public void UseBackupCode_ShouldReturnFalse_WhenCodeAlreadyUsed()
    {
        var user = CreateValidUser();
        var codes = user.GenerateBackupCodes(TestPepper);
        var code = codes[0];

        user.UseBackupCode(code, TestPepper).Should().BeTrue();
        user.UseBackupCode(code, TestPepper).Should().BeFalse();
    }

    [Fact]
    public void UseBackupCode_ShouldReturnFalse_WhenNoCodesGenerated()
    {
        var user = CreateValidUser();

        var result = user.UseBackupCode("12345678", TestPepper);

        result.Should().BeFalse();
    }

    [Fact]
    public void UseBackupCode_ShouldThrow_WhenPepperIsEmpty()
    {
        var user = CreateValidUser();
        user.GenerateBackupCodes(TestPepper);

        var act = () => user.UseBackupCode("12345678", string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UseBackupCode_ShouldReturnFalse_WhenWrongPepper()
    {
        var user = CreateValidUser();
        var codes = user.GenerateBackupCodes(TestPepper);

        var result = user.UseBackupCode(codes[0], "wrong-pepper-that-is-different!!");

        result.Should().BeFalse();
    }

    [Fact]
    public void UseBackupCode_ShouldSetBackupCodesJsonToNull_WhenLastCodeUsed()
    {
        var user = CreateValidUser();
        var codes = user.GenerateBackupCodes(TestPepper, count: 1);

        user.UseBackupCode(codes[0], TestPepper);

        user.BackupCodesJson.Should().BeNull();
        user.RemainingBackupCodes.Should().Be(0);
    }

    [Fact]
    public void UseAllBackupCodes_ShouldDrainToZero()
    {
        var user = CreateValidUser();
        var codes = user.GenerateBackupCodes(TestPepper, count: 3);

        foreach (var code in codes)
            user.UseBackupCode(code, TestPepper).Should().BeTrue();

        user.RemainingBackupCodes.Should().Be(0);
        user.BackupCodesJson.Should().BeNull();
    }

    // --- RemainingBackupCodes ---

    [Fact]
    public void RemainingBackupCodes_ShouldReturnZero_WhenNoCodesGenerated()
    {
        var user = CreateValidUser();

        user.RemainingBackupCodes.Should().Be(0);
    }

    // --- Lockout edge case ---

    [Fact]
    public void FourFailedLogins_ShouldNotLockOut()
    {
        var user = CreateValidUser();

        for (int i = 0; i < 4; i++)
            user.RecordFailedLogin();

        user.AccessFailedCount.Should().Be(4);
        user.IsLockedOut.Should().BeFalse();
    }

    [Fact]
    public void MoreThanFiveFailedLogins_ShouldStillBeLocked()
    {
        var user = CreateValidUser();

        for (int i = 0; i < 7; i++)
            user.RecordFailedLogin();

        user.AccessFailedCount.Should().Be(7);
        user.IsLockedOut.Should().BeTrue();
    }
}
