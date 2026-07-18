using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Modules.Email.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FellowCore.Infrastructure.Email;

/// <summary>
/// Implementação de IEmailService usando a API HTTP do Resend (https://resend.com).
/// Não usa SDK — apenas HttpClient com a API REST oficial.
/// </summary>
public sealed class ResendEmailProvider(
    HttpClient httpClient,
    IOptions<EmailOptions> options,
    ILogger<ResendEmailProvider> logger) : IEmailService
{
    private readonly EmailOptions _opts = options.Value;

    private const int MaxRetries = 3;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
        {
            logger.LogWarning("Email nao enviado: Email:ApiKey nao configurado.");
            return false;
        }

        var from = string.IsNullOrWhiteSpace(_opts.FromName)
            ? _opts.FromAddress
            : $"{_opts.FromName} <{_opts.FromAddress}>";

        var attachments = message.Attachments?.Select(a => new
        {
            filename = a.FileName,
            content = Convert.ToBase64String(a.Content),
            type = a.ContentType
        }).ToArray();

        var payload = new
        {
            from,
            to    = new[] { string.IsNullOrWhiteSpace(message.ToName) ? message.To : $"{message.ToName} <{message.To}>" },
            subject = message.Subject,
            html  = message.HtmlBody,
            text  = message.PlainTextBody,
            reply_to = message.ReplyTo,
            attachments
        };

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/emails")
            {
                Content = JsonContent.Create(payload, options: new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                })
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _opts.ApiKey);

            try
            {
                var response = await httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("E-mail enviado via Resend | Assunto: {Subject}", message.Subject);
                    return true;
                }

                var body = await response.Content.ReadAsStringAsync(ct);

                // Do not retry 4xx client errors (except 429 rate limit)
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500 && (int)response.StatusCode != 429)
                {
                    logger.LogError("Resend retornou {Status} ao enviar email (não retentável). Body: {Body}",
                        (int)response.StatusCode, body);
                    return false;
                }

                logger.LogWarning(
                    "Resend retornou {Status} ao enviar email (tentativa {Attempt}/{MaxRetries}). Body: {Body}",
                    (int)response.StatusCode, attempt + 1, MaxRetries + 1, body);
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                logger.LogWarning(ex, "Falha ao enviar e-mail (tentativa {Attempt}/{MaxRetries})",
                    attempt + 1, MaxRetries + 1);
            }
            catch (Exception ex)
            {
                // Last attempt — log and swallow (never propagate email exceptions)
                logger.LogError(ex, "Falha ao enviar e-mail após {MaxRetries} tentativas", MaxRetries + 1);
                return false;
            }

            if (attempt < MaxRetries)
            {
                // Exponential backoff: 1s, 2s, 4s
                var delay = InitialDelay * Math.Pow(2, attempt);
                await Task.Delay(delay, ct);
            }
        }

        logger.LogError("Resend: todas as {MaxRetries} tentativas falharam para email com assunto: {Subject}",
            MaxRetries + 1, message.Subject);
        return false;
    }
}
