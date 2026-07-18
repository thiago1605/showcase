using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Serilog;

namespace FellowCore.Api.Startup;

public static class SecretsManagerSetup
{
    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source in Production/Staging.
    /// Secrets are stored as a flat JSON object with keys matching appsettings structure
    /// (e.g., "Jwt:Secret", "Stripe:SecretKey", "ConnectionStrings:DefaultConnection").
    /// Falls back gracefully if AWS is not configured, allowing env vars to provide secrets.
    /// </summary>
    public static IConfigurationBuilder AddAwsSecretsManager(
        this IConfigurationBuilder builder,
        IHostEnvironment env)
    {
        if (env.IsDevelopment())
            return builder;

        try
        {
            var secretName = Environment.GetEnvironmentVariable("AWS_SECRET_NAME")
                             ?? "fellowpay/production";
            var region = Environment.GetEnvironmentVariable("AWS_REGION")
                         ?? "sa-east-1";

            var client = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));

            var response = client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secretName
            }).GetAwaiter().GetResult();

            var secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(response.SecretString);
            if (secrets is not null)
            {
                builder.AddInMemoryCollection(secrets!);
            }

            Log.Information(
                "[SECRETS] AWS Secrets Manager configured — secret={SecretName}, region={Region}",
                secretName, region);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[SECRETS] AWS Secrets Manager is not available — falling back to environment variables. " +
                "This is expected in local/CI environments. Error: {Message}",
                ex.Message);
        }

        return builder;
    }
}
