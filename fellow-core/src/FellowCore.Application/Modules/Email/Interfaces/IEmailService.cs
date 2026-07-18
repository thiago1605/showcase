namespace FellowCore.Application.Modules.Email.Interfaces;

public interface IEmailService
{
    /// <summary>
    /// Tenta enviar o email. Retorna true se o provedor confirmou aceite (200 OK
    /// da API do Resend), false caso contrário — incluindo ApiKey ausente, erro 4xx
    /// não-retentável, ou todas as tentativas de retry esgotadas. Nunca lança.
    ///
    /// Callers que rastreiam "enviado com sucesso" (ex: Receipt.IsCustomerEmailSent)
    /// devem só persistir esse estado quando o retorno é true.
    /// </summary>
    Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed record EmailMessage(
    string To,
    string ToName,
    string Subject,
    string HtmlBody,
    string? PlainTextBody = null,
    string? ReplyTo = null,
    List<EmailAttachment>? Attachments = null
);

public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType);
