using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.Entities;

public class User : AggregateRoot<Guid>
{
    public Guid? TenantId { get; private set; }
    public virtual Tenant? Tenant { get; private set; }

    public Guid? SellerId { get; private set; }
    public virtual Seller? Seller { get; private set; }

    [Required]
    public string Name { get; private set; } = string.Empty;

    [Required, EmailAddress]
    public string Email { get; private set; } = string.Empty;

    /// <summary>
    /// Hash bcrypt da senha. Nullable porque users criados via SSO (Google)
    /// não têm password local. Login local exige hash não-nulo; SSO ignora.
    /// </summary>
    public string? Password { get; private set; }

    /// <summary>
    /// Google subject ("sub" do ID token) — identificador estável do user
    /// na conta Google. Permite vincular a mesma conta Google ao user mesmo
    /// se o email mudar. Null para users que nunca logaram via Google.
    /// </summary>
    public string? GoogleSubject { get; private set; }

    public UserRole Role { get; private set; } = UserRole.VIEWER;
    public DateTime? LastLogin { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public string? TotpSecret { get; private set; }
    public bool IsTotpEnabled { get; private set; }
    public string? RefreshTokenHash { get; private set; }
    public DateTime? RefreshTokenExpiry { get; private set; }
    public string? BackupCodesJson { get; private set; }
    public string? PasswordResetTokenHash { get; private set; }
    public DateTime? PasswordResetTokenExpiry { get; private set; }

    public bool IsActive { get; private set; } = true;

    public int AccessFailedCount { get; private set; }
    public DateTime? LockoutEnd { get; private set; }

    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(30);

    protected User() { }

    public static User Create(string name, string email, string passwordHash, UserRole role = UserRole.VIEWER, Guid? tenantId = null, Guid? sellerId = null)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SellerId = sellerId,
            Name = name,
            Email = email,
            Password = passwordHash,
            Role = role,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Cria um usuário a partir do payload de um ID Token do Google. Não exige
    /// password local; o vínculo é via <c>GoogleSubject</c>. Login subsequente
    /// pode ocorrer tanto via Google (sub match) quanto via local se o usuário
    /// definir uma senha depois (UpdatePassword).
    /// </summary>
    public static User CreateFromGoogle(
        string name,
        string email,
        string googleSubject,
        UserRole role = UserRole.VIEWER,
        Guid? tenantId = null,
        Guid? sellerId = null)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SellerId = sellerId,
            Name = name,
            Email = email,
            Password = null,
            GoogleSubject = googleSubject,
            Role = role,
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Vincula um Google subject ao user. Usado quando um user que já existe
    /// localmente (criado via signup com senha) faz seu primeiro login via Google.
    /// </summary>
    public void LinkGoogleAccount(string googleSubject)
    {
        if (string.IsNullOrWhiteSpace(googleSubject))
            throw new ArgumentException("Google subject obrigatório.", nameof(googleSubject));
        GoogleSubject = googleSubject;
    }

    public void AssignSeller(Guid sellerId)
    {
        SellerId = sellerId;
    }

    public void ClearSeller()
    {
        SellerId = null;
    }

    public bool IsLockedOut => LockoutEnd.HasValue && LockoutEnd.Value > DateTime.UtcNow;

    public void RecordLogin()
    {
        LastLogin = DateTime.UtcNow;
        AccessFailedCount = 0;
        LockoutEnd = null;
    }

    public void RecordFailedLogin()
    {
        AccessFailedCount++;
        if (AccessFailedCount >= MaxFailedAttempts)
            LockoutEnd = DateTime.UtcNow.Add(LockoutDuration);
    }

    public void UpdatePassword(string passwordHash)
    {
        Password = passwordHash;
    }

    public void SetTotpSecret(string base32Secret)
    {
        TotpSecret = base32Secret;
    }

    public void EnableTotp()
    {
        IsTotpEnabled = true;
    }

    public void DisableTotp()
    {
        IsTotpEnabled = false;
        TotpSecret = null;
        BackupCodesJson = null;
    }

    public void SetRefreshToken(string tokenHash, DateTime expiry)
    {
        RefreshTokenHash = tokenHash;
        RefreshTokenExpiry = expiry;
    }

    public void RevokeRefreshToken()
    {
        RefreshTokenHash = null;
        RefreshTokenExpiry = null;
    }

    public void Deactivate()
    {
        IsActive = false;
        RevokeRefreshToken();
    }

    public void SetPasswordResetToken(string tokenHash, DateTime expiry)
    {
        PasswordResetTokenHash = tokenHash;
        PasswordResetTokenExpiry = expiry;
    }

    public void ClearPasswordResetToken()
    {
        PasswordResetTokenHash = null;
        PasswordResetTokenExpiry = null;
    }

    public List<string> GenerateBackupCodes(string pepper, int count = 8)
    {
        ArgumentException.ThrowIfNullOrEmpty(pepper);

        var plainCodes = new List<string>(count);
        var hashedCodes = new List<string>(count);

        for (int i = 0; i < count; i++)
        {
            var code = $"{RandomNumberGenerator.GetInt32(10000000, 99999999)}";
            plainCodes.Add(code);
            hashedCodes.Add(HashCode(code, pepper));
        }

        BackupCodesJson = JsonSerializer.Serialize(hashedCodes);
        return plainCodes;
    }

    public bool UseBackupCode(string code, string pepper)
    {
        ArgumentException.ThrowIfNullOrEmpty(pepper);
        if (string.IsNullOrEmpty(BackupCodesJson)) return false;

        var hashes = JsonSerializer.Deserialize<List<string>>(BackupCodesJson);
        if (hashes == null || hashes.Count == 0) return false;

        var codeHash = HashCode(code, pepper);

        for (int i = 0; i < hashes.Count; i++)
        {
            var storedBytes = Encoding.UTF8.GetBytes(hashes[i]);
            var providedBytes = Encoding.UTF8.GetBytes(codeHash);

            if (CryptographicOperations.FixedTimeEquals(storedBytes, providedBytes))
            {
                hashes.RemoveAt(i);
                BackupCodesJson = hashes.Count > 0 ? JsonSerializer.Serialize(hashes) : null;
                return true;
            }
        }

        return false;
    }

    public int RemainingBackupCodes
    {
        get
        {
            if (string.IsNullOrEmpty(BackupCodesJson)) return 0;
            var hashes = JsonSerializer.Deserialize<List<string>>(BackupCodesJson);
            return hashes?.Count ?? 0;
        }
    }

    private static string HashCode(string code, string pepper)
    {
        // HMAC-SHA256 with pepper prevents rainbow table attacks on short backup codes
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(pepper), Encoding.UTF8.GetBytes(code));
        return Convert.ToHexString(hash).ToLower();
    }
}
