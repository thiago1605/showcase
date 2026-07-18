namespace FellowCore.Infrastructure.Email;

public sealed class EmailOptions
{
    /// <summary>API Key do provider ativo (ex: Resend: "re_...").</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Endereço de envio (ex: "noreply@fellowpay.com.br").</summary>
    public string FromAddress { get; init; } = "noreply@fellowpay.com.br";

    /// <summary>Nome de exibição do remetente.</summary>
    public string FromName { get; init; } = "Fellow Pay";

    /// <summary>Provider ativo: "resend" (padrão) | "smtp".</summary>
    public string Provider { get; init; } = "resend";
}
