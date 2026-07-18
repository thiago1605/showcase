namespace FellowCore.Infrastructure.Auth;

public class JwtOptions
{
    public string SecretKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "fellowpay";
    public string Audience { get; set; } = "fellowpay-dashboard";
    public int AccessTokenExpirationMinutes { get; set; } = 15;
    public int MfaPendingTokenExpirationMinutes { get; set; } = 5;
}
