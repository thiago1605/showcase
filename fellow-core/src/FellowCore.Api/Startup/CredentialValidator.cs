using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Api.Startup;

public static class CredentialValidator
{
    // Known dev/default values that must never reach Production.
    private static readonly string[] KnownDevJwtSecrets =
    [
        "super-secret-key-for-development-only-change-in-production",
        "CHANGE_ME_IN_PRODUCTION_USE_AT_LEAST_32_CHARS",
        "change_me",
        "secret",
        "dev",
    ];

    /// <summary>
    /// Validates that well-known development credentials are not present in the Production
    /// environment. Logs a CRITICAL message and throws <see cref="InvalidOperationException"/>
    /// on the first violation found so the host process fails fast before accepting traffic.
    /// </summary>
    /// <param name="config">The application configuration.</param>
    /// <param name="logger">Logger used to emit the CRITICAL message before throwing.</param>
    /// <exception cref="InvalidOperationException">Thrown when a default/dev credential is detected.</exception>
    public static void ValidateProductionCredentials(IConfiguration config, ILogger logger)
    {
        var jwtSecret = config["Jwt:Secret"] ?? config["Jwt:SecretKey"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(jwtSecret) || IsKnownDevJwtSecret(jwtSecret) || jwtSecret.Length < 32)
        {
            const string msg =
                "[SECURITY] FATAL: Jwt:Secret is missing, uses a known dev placeholder, or is shorter than " +
                "32 characters. Set a strong random secret before deploying to Production.";
            logger.LogCritical(msg);
            throw new InvalidOperationException(msg);
        }

        var stripeSecretKey = config["Stripe:SecretKey"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stripeSecretKey) || stripeSecretKey.StartsWith("sk_test_", StringComparison.Ordinal))
        {
            const string msg =
                "[SECURITY] FATAL: Stripe:SecretKey is empty or uses a test key (sk_test_*). " +
                "Provide a live Stripe secret key for Production.";
            logger.LogCritical(msg);
            throw new InvalidOperationException(msg);
        }

        var openPixAppId = config["OpenPix:AppId"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(openPixAppId) ||
            string.Equals(openPixAppId, "sandbox", StringComparison.OrdinalIgnoreCase))
        {
            const string msg =
                "[SECURITY] FATAL: OpenPix:AppId is empty or set to the sandbox placeholder. " +
                "Provide a real OpenPix AppId for Production.";
            logger.LogCritical(msg);
            throw new InvalidOperationException(msg);
        }

        var connectionString = config.GetConnectionString("DefaultConnection") ?? string.Empty;
        if (connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("127.0.0.1", StringComparison.Ordinal))
        {
            const string msg =
                "[SECURITY] FATAL: The database connection string points to localhost/127.0.0.1. " +
                "Use a production database host in Production.";
            logger.LogCritical(msg);
            throw new InvalidOperationException(msg);
        }
    }

    private static bool IsKnownDevJwtSecret(string secret)
    {
        foreach (var knownDevSecret in KnownDevJwtSecrets)
        {
            if (string.Equals(secret, knownDevSecret, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
