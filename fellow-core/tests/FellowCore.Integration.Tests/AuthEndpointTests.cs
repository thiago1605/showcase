using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace FellowCore.Integration.Tests;

public class AuthEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Login_ValidCredentials_ReturnsTokens()
    {
        await SeedUserAsync();

        var response = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = TestDataHelper.TestUserPassword
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);

        body.GetProperty("data").GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("data").GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("data").GetProperty("requiresMfa").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Login_InvalidPassword_Returns401()
    {
        await SeedUserAsync();

        var response = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = "WrongPassword!"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_NonExistentUser_Returns401()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "nonexistent@test.com",
            password = "whatever"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_ValidToken_ReturnsNewTokens()
    {
        await SeedUserAsync();

        var loginResponse = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = TestDataHelper.TestUserPassword
        });

        var loginBody = await DeserializeAsync(loginResponse);
        var data = loginBody.GetProperty("data");
        var refreshToken = data.GetProperty("refreshToken").GetString()!;
        var userId = data.GetProperty("userId").GetString()!;

        var refreshResponse = await Client.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            userId,
            refreshToken
        });

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshBody = await DeserializeAsync(refreshResponse);
        refreshBody.GetProperty("data").GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Refresh_InvalidToken_Returns401()
    {
        await SeedUserAsync();

        // Login to get a valid userId
        var loginResponse = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = TestDataHelper.TestUserPassword
        });
        var data = (await DeserializeAsync(loginResponse)).GetProperty("data");
        var userId = data.GetProperty("userId").GetString()!;

        var refreshResponse = await Client.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            userId,
            refreshToken = "totally-invalid-token"
        });

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_WithValidToken_Returns204()
    {
        await SeedUserAsync();

        var loginBody = await LoginAndGetDataAsync();
        var accessToken = loginBody.GetProperty("accessToken").GetString()!;

        // Use a fresh client without X-Api-Key to avoid ApiKeyAuth filter confusion
        var client = CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.PostAsync("/api/v1/auth/logout", null);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, because: body);
    }

    [Fact]
    public async Task Logout_WithoutToken_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.PostAsync("/api/v1/auth/logout", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetupTotp_Authenticated_ReturnsSecretAndUri()
    {
        await SeedUserAsync();
        var data = await LoginAndGetDataAsync();
        var accessToken = data.GetProperty("accessToken").GetString()!;

        var client = CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync("/api/v1/auth/2fa/setup");
        var bodyText = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: bodyText);

        var body = await DeserializeAsync(response);
        body.GetProperty("data").GetProperty("secret").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("data").GetProperty("qrCodeUri").GetString().Should().Contain("otpauth://totp/");
    }

    [Fact]
    public async Task SetupTotp_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/auth/2fa/setup");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Forgot Password ──

    [Fact]
    public async Task ForgotPassword_ValidEmail_ReturnsOk()
    {
        await SeedUserAsync();

        var response = await Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new
        {
            email = TestDataHelper.TestUserEmail
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        body.GetProperty("data").GetProperty("message").GetString().Should().Contain("receberá instruções");
    }

    [Fact]
    public async Task ForgotPassword_NonExistentEmail_StillReturnsOk()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new
        {
            email = "nonexistent@test.com"
        });

        // Should return 200 even if email not found (security: no email enumeration)
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Login History ──

    [Fact]
    public async Task LoginHistory_Authenticated_ReturnsOk()
    {
        await SeedUserAsync();
        var data = await LoginAndGetDataAsync();
        var accessToken = data.GetProperty("accessToken").GetString()!;

        var client = CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync("/api/v1/auth/login-history");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LoginHistory_WithDateFilter_ReturnsOk()
    {
        await SeedUserAsync();
        var data = await LoginAndGetDataAsync();
        var accessToken = data.GetProperty("accessToken").GetString()!;

        var client = CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var from = DateTime.UtcNow.AddDays(-1).ToString("o");
        var to = DateTime.UtcNow.AddDays(1).ToString("o");
        var response = await client.GetAsync($"/api/v1/auth/login-history?from={from}&to={to}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LoginHistory_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/auth/login-history");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Password Reset Flow ──

    [Fact]
    public async Task ResetPassword_FullFlow_CanLoginWithNewPassword()
    {
        await SeedUserAsync();

        // 1. Request password reset
        var forgotResponse = await Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new
        {
            email = TestDataHelper.TestUserEmail
        });
        forgotResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Extract token from FakeEmailService
        var token = ExtractResetTokenFromEmail();
        token.Should().NotBeNullOrWhiteSpace();

        // 3. Reset password with token
        var newPassword = "NewStr0ng!P@ss99";
        var resetResponse = await Client.PostAsJsonAsync("/api/v1/auth/reset-password", new
        {
            email = TestDataHelper.TestUserEmail,
            token,
            newPassword
        });
        var resetBody = await resetResponse.Content.ReadAsStringAsync();
        resetResponse.StatusCode.Should().Be(HttpStatusCode.OK, because: resetBody);

        // 4. Login with new password works
        var loginResponse = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = newPassword
        });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 5. Login with old password fails
        var oldLoginResponse = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = TestDataHelper.TestUserPassword
        });
        oldLoginResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResetPassword_InvalidToken_Returns401()
    {
        await SeedUserAsync();

        // Request forgot password first so token fields are set
        await Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new
        {
            email = TestDataHelper.TestUserEmail
        });

        var resetResponse = await Client.PostAsJsonAsync("/api/v1/auth/reset-password", new
        {
            email = TestDataHelper.TestUserEmail,
            token = "totally-invalid-token",
            newPassword = "NewStr0ng!P@ss99"
        });

        resetResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResetPassword_TokenCannotBeReused()
    {
        await SeedUserAsync();

        await Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new
        {
            email = TestDataHelper.TestUserEmail
        });

        var token = ExtractResetTokenFromEmail();

        // First reset succeeds
        var first = await Client.PostAsJsonAsync("/api/v1/auth/reset-password", new
        {
            email = TestDataHelper.TestUserEmail,
            token,
            newPassword = "NewStr0ng!P@ss99"
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second reset with same token fails
        var second = await Client.PostAsJsonAsync("/api/v1/auth/reset-password", new
        {
            email = TestDataHelper.TestUserEmail,
            token,
            newPassword = "AnotherP@ss123!"
        });
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Login History (extended) ──

    [Fact]
    public async Task LoginHistory_ContainsLoginEntry()
    {
        await SeedUserAsync();
        var data = await LoginAndGetDataAsync();
        var accessToken = data.GetProperty("accessToken").GetString()!;

        var client = CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync("/api/v1/auth/login-history");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await DeserializeAsync(response);
        // login-history returns an array wrapped in data
        body.GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LoginHistory_WithLimit_RespectsLimit()
    {
        await SeedUserAsync();
        var data = await LoginAndGetDataAsync();
        var accessToken = data.GetProperty("accessToken").GetString()!;

        var client = CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync("/api/v1/auth/login-history?limit=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await DeserializeAsync(response);
        // login-history returns an array wrapped in data
        body.GetProperty("data").GetArrayLength().Should().BeLessThanOrEqualTo(1);
    }

    // ── 2FA Enable / Disable with real TOTP ──

    [Fact]
    public async Task Enable2FA_WithValidTotpCode_ReturnsBackupCodes()
    {
        await SeedUserAsync();
        var (client, _) = await LoginAndGetAuthClientAsync();

        // 1. Setup TOTP — get secret
        var setupResponse = await client.GetAsync("/api/v1/auth/2fa/setup");
        setupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var setupBody = await DeserializeAsync(setupResponse);
        var secret = setupBody.GetProperty("data").GetProperty("secret").GetString()!;

        // 2. Generate valid TOTP code using OtpNet
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var code = totp.ComputeTotp();

        // 3. Enable 2FA with valid code
        var enableResponse = await client.PostAsJsonAsync("/api/v1/auth/2fa/enable", new { totpCode = code });
        var enableBody = await enableResponse.Content.ReadAsStringAsync();
        enableResponse.StatusCode.Should().Be(HttpStatusCode.OK, because: enableBody);

        var enableData = (await DeserializeAsync(enableResponse)).GetProperty("data");
        enableData.GetProperty("backupCodes").GetArrayLength().Should().Be(8);
    }

    [Fact]
    public async Task Enable2FA_WithInvalidCode_Returns401()
    {
        await SeedUserAsync();
        var (client, _) = await LoginAndGetAuthClientAsync();

        // Setup TOTP first
        await client.GetAsync("/api/v1/auth/2fa/setup");

        // Try to enable with invalid code — FluentValidation passes (6 digits), but TOTP validation fails
        var enableResponse = await client.PostAsJsonAsync("/api/v1/auth/2fa/enable", new { totpCode = "000000" });
        // Returns 401 because the TOTP code is invalid
        enableResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_With2FAEnabled_RequiresMfaAndVerifySucceeds()
    {
        await SeedUserAsync();
        var (client, _) = await LoginAndGetAuthClientAsync();

        // Setup + enable 2FA
        var setupBody = await DeserializeAsync(await client.GetAsync("/api/v1/auth/2fa/setup"));
        var secret = setupBody.GetProperty("data").GetProperty("secret").GetString()!;
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var code = totp.ComputeTotp();
        var enableResp = await client.PostAsJsonAsync("/api/v1/auth/2fa/enable", new { totpCode = code });
        enableResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Now login again — should require MFA
        var loginResponse = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = TestDataHelper.TestUserPassword
        });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginData = (await DeserializeAsync(loginResponse)).GetProperty("data");
        loginData.GetProperty("requiresMfa").GetBoolean().Should().BeTrue();
        var mfaToken = loginData.GetProperty("mfaToken").GetString()!;

        // Verify MFA with fresh TOTP code
        var freshCode = totp.ComputeTotp();
        var verifyResponse = await Client.PostAsJsonAsync("/api/v1/auth/verify-mfa", new
        {
            mfaToken,
            totpCode = freshCode
        });
        var verifyBody = await verifyResponse.Content.ReadAsStringAsync();
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK, because: verifyBody);

        var verifyData = (await DeserializeAsync(verifyResponse)).GetProperty("data");
        verifyData.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Disable2FA_WithValidCode_AllowsLoginWithoutMfa()
    {
        await SeedUserAsync();
        var (client, _) = await LoginAndGetAuthClientAsync();

        // Setup + enable 2FA
        var setupBody = await DeserializeAsync(await client.GetAsync("/api/v1/auth/2fa/setup"));
        var secret = setupBody.GetProperty("data").GetProperty("secret").GetString()!;
        var totp = new Totp(Base32Encoding.ToBytes(secret));

        var enableResp = await client.PostAsJsonAsync("/api/v1/auth/2fa/enable", new { totpCode = totp.ComputeTotp() });
        enableResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Disable 2FA with valid code
        var disableResp = await client.PostAsJsonAsync("/api/v1/auth/2fa/disable", new { totpCode = totp.ComputeTotp() });
        var disableBody = await disableResp.Content.ReadAsStringAsync();
        disableResp.StatusCode.Should().Be(HttpStatusCode.OK, because: disableBody);

        // Login again — should NOT require MFA
        var loginResponse = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = TestDataHelper.TestUserPassword
        });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginData = (await DeserializeAsync(loginResponse)).GetProperty("data");
        loginData.GetProperty("requiresMfa").GetBoolean().Should().BeFalse();
        loginData.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task VerifyMfa_WithBackupCode_Succeeds()
    {
        await SeedUserAsync();

        // 1. Login and enable 2FA
        var loginResp1 = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = TestDataHelper.TestUserPassword
        });
        var token1 = (await DeserializeAsync(loginResp1)).GetProperty("data").GetProperty("accessToken").GetString()!;

        var authClient = CreateUnauthenticatedClient();
        authClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token1);

        var setupBody = await DeserializeAsync(await authClient.GetAsync("/api/v1/auth/2fa/setup"));
        var secret = setupBody.GetProperty("data").GetProperty("secret").GetString()!;
        var totp = new Totp(Base32Encoding.ToBytes(secret));

        var enableResp = await authClient.PostAsJsonAsync("/api/v1/auth/2fa/enable", new { totpCode = totp.ComputeTotp() });
        var enableBody = await enableResp.Content.ReadAsStringAsync();
        enableResp.StatusCode.Should().Be(HttpStatusCode.OK, because: enableBody);
        var enableData = (await DeserializeAsync(enableResp)).GetProperty("data");
        var backupCode = enableData.GetProperty("backupCodes")[0].GetString()!;

        // 2. Login again — should require MFA
        var loginResp2 = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = TestDataHelper.TestUserPassword
        });
        var loginData = (await DeserializeAsync(loginResp2)).GetProperty("data");
        loginData.GetProperty("requiresMfa").GetBoolean().Should().BeTrue();
        var mfaToken = loginData.GetProperty("mfaToken").GetString()!;

        // 3. Verify with backup code
        var verifyResponse = await Client.PostAsJsonAsync("/api/v1/auth/verify-mfa", new
        {
            mfaToken,
            totpCode = backupCode
        });
        var verifyBody = await verifyResponse.Content.ReadAsStringAsync();
        // If MFA token validation fails, it's likely a test infrastructure issue
        // The working test Login_With2FAEnabled uses the same pattern
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK, because: verifyBody);
    }

    // ── helpers ──

    private async Task SeedUserAsync()
    {
        await TestDataHelper.SeedUserAsync(Factory.Services, TenantId);
    }

    private async Task<JsonElement> LoginAndGetDataAsync()
    {
        var loginResponse = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = TestDataHelper.TestUserPassword
        });
        var body = await DeserializeAsync(loginResponse);
        return body.GetProperty("data");
    }

    private async Task<(HttpClient Client, string AccessToken)> LoginAndGetAuthClientAsync()
    {
        var data = await LoginAndGetDataAsync();
        var accessToken = data.GetProperty("accessToken").GetString()!;

        var client = CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return (client, accessToken);
    }

    private string ExtractResetTokenFromEmail()
    {
        var emailService = Factory.Services.GetRequiredService<IEmailService>() as FakeEmailService;
        emailService.Should().NotBeNull();
        emailService!.SentMessages.Should().NotBeEmpty();

        var lastEmail = emailService.SentMessages[^1];
        // Token is displayed in the email HTML inside a styled code block
        // Pattern: monospace font block containing the base64url token
        var match = Regex.Match(lastEmail.HtmlBody, @"font-family:\s*monospace[^>]*>([A-Za-z0-9_\-]+)<");
        if (match.Success) return match.Groups[1].Value;

        // Fallback: look for any base64url-like token (32+ chars without spaces)
        match = Regex.Match(lastEmail.HtmlBody, @"([A-Za-z0-9_\-]{32,})");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static async Task<JsonElement> DeserializeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
    }
}
