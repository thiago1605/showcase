using System.Security.Claims;
using FellowCore.Application.Modules.Auth.DTOs;
using FellowCore.Application.Modules.Auth.Interfaces;
using FellowCore.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting("fixed")]
public class AuthController(IAuthService authService, ILoginLogRepository loginLogRepo) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-sensitive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = Request.Headers.UserAgent.ToString();
        var result = await authService.LoginAsync(dto.Email, dto.Password, ip, ua);
        return Ok(result);
    }

    [HttpPost("verify-mfa")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-sensitive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> VerifyMfa([FromBody] VerifyMfaDto dto)
    {
        var result = await authService.VerifyMfaAsync(dto.MfaToken, dto.TotpCode);
        return Ok(result);
    }

    /// <summary>
    /// Login via Google Identity Services (botão "Entrar com Google" no frontend).
    /// Recebe o ID Token JWT emitido pelo Google após o user aprovar; backend
    /// valida assinatura + audience + expiração, find-or-create user local e
    /// emite tokens internos. Não passa por MFA — Google já fez a sua 2FA.
    /// </summary>
    [HttpPost("google-login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-sensitive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.IdToken))
            return BadRequest(new { error = "Auth.MissingIdToken", message = "ID Token do Google é obrigatório." });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = Request.Headers.UserAgent.ToString();
        var result = await authService.LoginWithGoogleAsync(dto.IdToken, ip, ua);
        return Ok(result);
    }

    /// <summary>
    /// Onboarding pós-SSO: user logado sem sellerId cria um Seller mínimo
    /// (Afiliado ou Produtor) e fica vinculado. Backend re-emite tokens com
    /// o sellerId novo no payload — frontend deve substituir o accessToken
    /// armazenado para que endpoints seller-scoped passem a funcionar sem
    /// novo login.
    /// </summary>
    [HttpPost("onboard")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> OnboardSeller([FromBody] OnboardSellerDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!);
        var result = await authService.OnboardSellerAsync(userId, dto);
        return Ok(result);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-sensitive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequestDto dto)
    {
        var result = await authService.RefreshAsync(dto.UserId, dto.RefreshToken);
        return Ok(result);
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout()
    {
        var userId = Guid.Parse(User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!);
        await authService.LogoutAsync(userId);
        return NoContent();
    }

    [HttpGet("2fa/setup")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetupTotp()
    {
        var userId = Guid.Parse(User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!);
        var result = await authService.SetupTotpAsync(userId);
        return Ok(result);
    }

    [HttpPost("2fa/enable")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> EnableTotp([FromBody] EnableTotpDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!);
        var result = await authService.EnableTotpAsync(userId, dto.TotpCode);
        return Ok(new { message = "2FA habilitado com sucesso.", backupCodes = result.BackupCodes });
    }

    [HttpPost("2fa/backup-codes")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RegenerateBackupCodes([FromBody] EnableTotpDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!);
        var result = await authService.RegenerateBackupCodesAsync(userId, dto.TotpCode);
        return Ok(new { backupCodes = result.BackupCodes });
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-sensitive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto)
    {
        await authService.ForgotPasswordAsync(dto.Email);
        return Ok(new { message = "Se o e-mail estiver cadastrado, você receberá instruções para redefinir a senha." });
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-sensitive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto)
    {
        await authService.ResetPasswordAsync(dto.Email, dto.Token, dto.NewPassword);
        return Ok(new { message = "Senha redefinida com sucesso." });
    }

    [HttpPost("2fa/disable")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DisableTotp([FromBody] DisableTotpDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!);
        await authService.DisableTotpAsync(userId, dto.TotpCode);
        return Ok(new { message = "2FA desabilitado com sucesso." });
    }

    [HttpGet("login-history")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> LoginHistory([FromQuery] int limit = 50, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        limit = Math.Clamp(limit, 1, 100);
        var userId = Guid.Parse(User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!);
        var logs = await loginLogRepo.GetByUserAsync(userId, limit, from, to);
        return Ok(logs.Select(l => new
        {
            l.Id,
            l.Email,
            result = l.Result.ToString(),
            l.IpAddress,
            l.UserAgent,
            l.CreatedAt
        }));
    }
}
