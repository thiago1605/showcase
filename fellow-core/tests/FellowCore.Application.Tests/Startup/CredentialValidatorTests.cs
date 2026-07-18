using FellowCore.Api.Startup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FellowCore.Application.Tests.Startup;

public class CredentialValidatorTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static IConfiguration BuildConfig(
        string? jwtSecret = null,
        string? stripeSecretKey = null,
        string? openPixAppId = null,
        string? connectionString = null)
    {
        var values = new Dictionary<string, string?>();

        if (jwtSecret is not null)
            values["Jwt:Secret"] = jwtSecret;

        if (stripeSecretKey is not null)
            values["Stripe:SecretKey"] = stripeSecretKey;

        if (openPixAppId is not null)
            values["OpenPix:AppId"] = openPixAppId;

        if (connectionString is not null)
            values["ConnectionStrings:DefaultConnection"] = connectionString;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private IConfiguration ValidConfig() => BuildConfig(
        jwtSecret: "a-very-strong-production-jwt-secret-32c",
        stripeSecretKey: "sk_live_abcdef1234567890",
        openPixAppId: "live-openpix-appid",
        connectionString: "Host=db.prod.example.com;Database=fellowpay;");

    // ---------------------------------------------------------------------------
    // Jwt:Secret
    // ---------------------------------------------------------------------------

    [Fact]
    public void ValidateProductionCredentials_ShouldThrow_WhenJwtSecretIsKnownDevValue()
    {
        var config = BuildConfig(
            jwtSecret: "super-secret-key-for-development-only-change-in-production",
            stripeSecretKey: "sk_live_abcdef1234567890",
            openPixAppId: "live-openpix-appid",
            connectionString: "Host=db.prod.example.com;Database=fellowpay;");

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:Secret*");
    }

    [Fact]
    public void ValidateProductionCredentials_ShouldThrow_WhenJwtSecretIsChangeMePlaceholder()
    {
        var config = BuildConfig(
            jwtSecret: "CHANGE_ME_IN_PRODUCTION_USE_AT_LEAST_32_CHARS",
            stripeSecretKey: "sk_live_abcdef1234567890",
            openPixAppId: "live-openpix-appid",
            connectionString: "Host=db.prod.example.com;Database=fellowpay;");

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:Secret*");
    }

    [Fact]
    public void ValidateProductionCredentials_ShouldThrow_WhenJwtSecretIsTooShort()
    {
        var config = BuildConfig(
            jwtSecret: "tooshort",
            stripeSecretKey: "sk_live_abcdef1234567890",
            openPixAppId: "live-openpix-appid",
            connectionString: "Host=db.prod.example.com;Database=fellowpay;");

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:Secret*");
    }

    [Fact]
    public void ValidateProductionCredentials_ShouldThrow_WhenJwtSecretIsEmpty()
    {
        var config = BuildConfig(
            jwtSecret: "",
            stripeSecretKey: "sk_live_abcdef1234567890",
            openPixAppId: "live-openpix-appid",
            connectionString: "Host=db.prod.example.com;Database=fellowpay;");

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:Secret*");
    }

    // ---------------------------------------------------------------------------
    // Stripe:SecretKey
    // ---------------------------------------------------------------------------

    [Fact]
    public void ValidateProductionCredentials_ShouldThrow_WhenStripeKeyIsTestKey()
    {
        var config = BuildConfig(
            jwtSecret: "a-very-strong-production-jwt-secret-32c",
            stripeSecretKey: "sk_test_abcdef1234567890",
            openPixAppId: "live-openpix-appid",
            connectionString: "Host=db.prod.example.com;Database=fellowpay;");

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Stripe:SecretKey*");
    }

    [Fact]
    public void ValidateProductionCredentials_ShouldThrow_WhenStripeKeyIsEmpty()
    {
        var config = BuildConfig(
            jwtSecret: "a-very-strong-production-jwt-secret-32c",
            stripeSecretKey: "",
            openPixAppId: "live-openpix-appid",
            connectionString: "Host=db.prod.example.com;Database=fellowpay;");

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Stripe:SecretKey*");
    }

    // ---------------------------------------------------------------------------
    // OpenPix:AppId
    // ---------------------------------------------------------------------------

    [Fact]
    public void ValidateProductionCredentials_ShouldThrow_WhenOpenPixAppIdIsSandbox()
    {
        var config = BuildConfig(
            jwtSecret: "a-very-strong-production-jwt-secret-32c",
            stripeSecretKey: "sk_live_abcdef1234567890",
            openPixAppId: "sandbox",
            connectionString: "Host=db.prod.example.com;Database=fellowpay;");

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OpenPix:AppId*");
    }

    [Fact]
    public void ValidateProductionCredentials_ShouldThrow_WhenOpenPixAppIdIsSandboxCaseInsensitive()
    {
        var config = BuildConfig(
            jwtSecret: "a-very-strong-production-jwt-secret-32c",
            stripeSecretKey: "sk_live_abcdef1234567890",
            openPixAppId: "SANDBOX",
            connectionString: "Host=db.prod.example.com;Database=fellowpay;");

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OpenPix:AppId*");
    }

    [Fact]
    public void ValidateProductionCredentials_ShouldThrow_WhenOpenPixAppIdIsEmpty()
    {
        var config = BuildConfig(
            jwtSecret: "a-very-strong-production-jwt-secret-32c",
            stripeSecretKey: "sk_live_abcdef1234567890",
            openPixAppId: "",
            connectionString: "Host=db.prod.example.com;Database=fellowpay;");

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OpenPix:AppId*");
    }

    // ---------------------------------------------------------------------------
    // Connection string
    // ---------------------------------------------------------------------------

    [Fact]
    public void ValidateProductionCredentials_ShouldThrow_WhenConnectionStringContainsLocalhost()
    {
        var config = BuildConfig(
            jwtSecret: "a-very-strong-production-jwt-secret-32c",
            stripeSecretKey: "sk_live_abcdef1234567890",
            openPixAppId: "live-openpix-appid",
            connectionString: "Host=localhost;Database=fellowpay;");

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*localhost*");
    }

    [Fact]
    public void ValidateProductionCredentials_ShouldThrow_WhenConnectionStringContains127001()
    {
        var config = BuildConfig(
            jwtSecret: "a-very-strong-production-jwt-secret-32c",
            stripeSecretKey: "sk_live_abcdef1234567890",
            openPixAppId: "live-openpix-appid",
            connectionString: "Host=127.0.0.1;Database=fellowpay;");

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*127.0.0.1*");
    }

    // ---------------------------------------------------------------------------
    // Happy path — valid production credentials must not throw
    // ---------------------------------------------------------------------------

    [Fact]
    public void ValidateProductionCredentials_ShouldNotThrow_WhenAllCredentialsAreValid()
    {
        var config = ValidConfig();

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateProductionCredentials_ShouldNotThrow_WhenJwtSecretIsReadFromJwtSecretKeyFallback()
    {
        // Program.cs resolves Jwt:Secret first, then falls back to Jwt:SecretKey.
        // Verify the fallback key also works.
        var values = new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "a-very-strong-production-jwt-secret-32c",
            ["Stripe:SecretKey"] = "sk_live_abcdef1234567890",
            ["OpenPix:AppId"] = "live-openpix-appid",
            ["ConnectionStrings:DefaultConnection"] = "Host=db.prod.example.com;Database=fellowpay;",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var act = () => CredentialValidator.ValidateProductionCredentials(config, _logger);

        act.Should().NotThrow();
    }
}
