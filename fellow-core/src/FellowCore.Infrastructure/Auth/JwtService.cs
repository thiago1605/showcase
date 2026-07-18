using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FellowCore.Application.Modules.Auth.Interfaces;
using FellowCore.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FellowCore.Infrastructure.Auth;

public class JwtService(IOptions<JwtOptions> options) : IJwtService
{
    private readonly JwtOptions _opts = options.Value;

    public string GenerateAccessToken(User user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.Name),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("role", user.Role.ToString()),
        };

        if (user.TenantId.HasValue)
            claims.Add(new Claim("tenant_id", user.TenantId.Value.ToString()));

        if (user.SellerId.HasValue)
            claims.Add(new Claim("seller_id", user.SellerId.Value.ToString()));

        return BuildToken(claims, TimeSpan.FromMinutes(_opts.AccessTokenExpirationMinutes));
    }

    public string GenerateMfaPendingToken(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new("mfa_pending", "true"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        return BuildToken(claims, TimeSpan.FromMinutes(_opts.MfaPendingTokenExpirationMinutes));
    }

    public bool ValidateMfaPendingToken(string token, out Guid userId)
    {
        userId = Guid.Empty;
        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.SecretKey));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidIssuer = _opts.Issuer,
                ValidateAudience = true,
                ValidAudience = _opts.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            }, out _);

            var mfaPending = principal.FindFirstValue("mfa_pending");
            if (mfaPending != "true") return false;

            var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            return sub != null && Guid.TryParse(sub, out userId);
        }
        catch
        {
            return false;
        }
    }

    private string BuildToken(IEnumerable<Claim> claims, TimeSpan expiration)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(expiration),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
