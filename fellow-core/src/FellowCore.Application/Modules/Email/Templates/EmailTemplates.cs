namespace FellowCore.Application.Modules.Email.Templates;

/// <summary>
/// Templates HTML inline para os e-mails transacionais da Fellow Pay.
/// Sem dependências externas — HTML puro compatível com os principais clientes de e-mail.
/// </summary>
public static class EmailTemplates
{
    private const string BaseColor = "#1a1a2e";
    private const string AccentColor = "#7c3aed";
    private const string SuccessColor = "#16a34a";
    private const string DangerColor = "#dc2626";

    private static string Wrap(string tenantName, string content) => $"""
        <!DOCTYPE html>
        <html lang="pt-BR">
        <head>
          <meta charset="UTF-8" />
          <meta name="viewport" content="width=device-width,initial-scale=1" />
          <title>Fellow Pay</title>
        </head>
        <body style="margin:0;padding:0;background:#f4f4f5;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;">
          <table width="100%" cellpadding="0" cellspacing="0" style="background:#f4f4f5;padding:32px 16px;">
            <tr><td align="center">
              <table width="600" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:8px;overflow:hidden;max-width:600px;">
                <!-- Header -->
                <tr>
                  <td style="background:{BaseColor};padding:24px 32px;">
                    <span style="color:#ffffff;font-size:20px;font-weight:700;letter-spacing:-0.5px;">Fellow Pay</span>
                    {(string.IsNullOrEmpty(tenantName) ? "" : $"<span style='color:#a5b4fc;font-size:13px;margin-left:8px;'>| {tenantName}</span>")}
                  </td>
                </tr>
                <!-- Body -->
                <tr><td style="padding:32px;">{content}</td></tr>
                <!-- Footer -->
                <tr>
                  <td style="background:#f9fafb;padding:20px 32px;border-top:1px solid #e5e7eb;">
                    <p style="margin:0;font-size:12px;color:#6b7280;text-align:center;">
                      Fellow Pay · Grupo Fellow · Salvador-BA<br/>
                      Este é um e-mail automático, não responda a esta mensagem.
                    </p>
                  </td>
                </tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;

    public static string TenantWelcome(string tenantName, string ownerEmail, string apiKey) =>
        Wrap(tenantName, $"""
            <h2 style="margin:0 0 16px;color:{BaseColor};font-size:22px;">Bem-vindo à Fellow Pay! 🎉</h2>
            <p style="color:#374151;line-height:1.6;">Olá, sua conta <strong>{tenantName}</strong> foi criada com sucesso. Você já pode começar a processar pagamentos.</p>
            <table cellpadding="0" cellspacing="0" style="background:#f3f4f6;border-radius:6px;padding:16px 20px;margin:20px 0;width:100%;">
              <tr><td><p style="margin:0 0 4px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Sua API Key</p>
              <code style="font-size:14px;color:{AccentColor};word-break:break-all;">{apiKey}</code></td></tr>
            </table>
            <p style="color:#374151;line-height:1.6;">Guarde sua chave em local seguro. Ela dá acesso total à sua conta.</p>
            <p style="color:#374151;line-height:1.6;">Dúvidas? Fale conosco em <a href="mailto:suporte@grupofellow.com.br" style="color:{AccentColor};">suporte@grupofellow.com.br</a></p>
            """);

    public static string SellerWelcome(string sellerLegalName, string sellerEmail, string tenantName) =>
        Wrap(tenantName, $"""
            <h2 style="margin:0 0 16px;color:{BaseColor};font-size:22px;">Conta criada com sucesso</h2>
            <p style="color:#374151;line-height:1.6;">Olá! A sub-conta de <strong>{sellerLegalName}</strong> foi cadastrada na plataforma <strong>{tenantName}</strong>.</p>
            <p style="color:#374151;line-height:1.6;">Você poderá receber pagamentos via Pix e cartão através da plataforma. Em breve você receberá mais informações sobre como acessar seu painel.</p>
            <p style="color:#374151;line-height:1.6;">Em caso de dúvidas, entre em contato com <strong>{tenantName}</strong>.</p>
            """);

    public static string TransactionCompleted(string tenantName, string txId, decimal amount, string paymentType, DateTime date) =>
        Wrap(tenantName, $"""
            <h2 style="margin:0 0 16px;color:{SuccessColor};font-size:22px;">Pagamento confirmado ✓</h2>
            <p style="color:#374151;line-height:1.6;">Uma transação foi concluída com sucesso em sua conta.</p>
            <table cellpadding="0" cellspacing="0" style="border:1px solid #e5e7eb;border-radius:6px;width:100%;margin:20px 0;">
              <tr style="background:#f9fafb;"><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">ID</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{txId}</td></tr>
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Valor</td><td style="padding:12px 16px;font-size:18px;font-weight:700;color:{SuccessColor};">{amount:C2}</td></tr>
              <tr style="background:#f9fafb;"><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Método</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{paymentType}</td></tr>
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Data</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{date:dd/MM/yyyy HH:mm} UTC</td></tr>
            </table>
            """);

    public static string PayoutCompleted(string tenantName, string sellerName, decimal amount, decimal netAmount, DateTime date) =>
        Wrap(tenantName, $"""
            <h2 style="margin:0 0 16px;color:{SuccessColor};font-size:22px;">Saque realizado com sucesso ✓</h2>
            <p style="color:#374151;line-height:1.6;">O saque de <strong>{sellerName}</strong> foi processado.</p>
            <table cellpadding="0" cellspacing="0" style="border:1px solid #e5e7eb;border-radius:6px;width:100%;margin:20px 0;">
              <tr style="background:#f9fafb;"><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Valor solicitado</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{amount:C2}</td></tr>
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Valor líquido</td><td style="padding:12px 16px;font-size:18px;font-weight:700;color:{SuccessColor};">{netAmount:C2}</td></tr>
              <tr style="background:#f9fafb;"><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Data</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{date:dd/MM/yyyy HH:mm} UTC</td></tr>
            </table>
            """);

    public static string PayoutFailed(string tenantName, string sellerName, decimal amount, string reason, DateTime date) =>
        Wrap(tenantName, $"""
            <h2 style="margin:0 0 16px;color:{DangerColor};font-size:22px;">Falha no saque</h2>
            <p style="color:#374151;line-height:1.6;">O saque de <strong>{sellerName}</strong> não pôde ser processado.</p>
            <table cellpadding="0" cellspacing="0" style="border:1px solid #fecaca;border-radius:6px;width:100%;margin:20px 0;background:#fef2f2;">
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Valor</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{amount:C2}</td></tr>
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Motivo</td><td style="padding:12px 16px;font-size:13px;color:{DangerColor};font-weight:600;">{reason}</td></tr>
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Data</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{date:dd/MM/yyyy HH:mm} UTC</td></tr>
            </table>
            <p style="color:#374151;line-height:1.6;">Por favor, verifique o saldo e as configurações da conta e tente novamente.</p>
            """);

    public static string PasswordReset(string tenantName, string resetToken, string resetUrl) =>
        Wrap(tenantName, $"""
            <h2 style="margin:0 0 16px;color:{BaseColor};font-size:22px;">Redefinição de senha</h2>
            <p style="color:#374151;line-height:1.6;">Recebemos uma solicitação para redefinir a senha da sua conta.</p>
            <p style="color:#374151;line-height:1.6;">Use o código abaixo para redefinir sua senha. Ele é válido por 1 hora.</p>
            <table cellpadding="0" cellspacing="0" style="background:#f3f4f6;border-radius:6px;padding:16px 20px;margin:20px 0;width:100%;">
              <tr><td><p style="margin:0 0 4px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Código de redefinição</p>
              <code style="font-size:18px;color:{AccentColor};font-weight:700;letter-spacing:2px;">{resetToken}</code></td></tr>
            </table>
            {(string.IsNullOrEmpty(resetUrl) ? "" : $"<p style='color:#374151;line-height:1.6;'>Ou <a href='{resetUrl}' style='color:{AccentColor};font-weight:600;'>clique aqui</a> para redefinir.</p>")}
            <p style="color:#6b7280;font-size:13px;line-height:1.6;">Se você não solicitou esta redefinição, ignore este e-mail.</p>
            """);

    public static string PaymentReceiptCustomer(string tenantName, string payerName, string txId, decimal amount, string paymentType, DateTime date, string? receiptUrl = null) =>
        Wrap(tenantName, $"""
            <h2 style="margin:0 0 16px;color:{SuccessColor};font-size:22px;">Pagamento confirmado ✓</h2>
            <p style="color:#374151;line-height:1.6;">Olá, <strong>{payerName}</strong>! Seu pagamento foi processado com sucesso.</p>
            <table cellpadding="0" cellspacing="0" style="border:1px solid #e5e7eb;border-radius:6px;width:100%;margin:20px 0;">
              <tr style="background:#f9fafb;"><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Comprovante</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{txId[..Math.Min(8, txId.Length)]}...</td></tr>
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Valor</td><td style="padding:12px 16px;font-size:18px;font-weight:700;color:{SuccessColor};">R$ {amount:N2}</td></tr>
              <tr style="background:#f9fafb;"><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Método</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{paymentType}</td></tr>
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Data</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{date:dd/MM/yyyy HH:mm} UTC</td></tr>
            </table>
            {(string.IsNullOrEmpty(receiptUrl) ? "" : $"<p style='text-align:center;margin:24px 0;'><a href='{receiptUrl}' style='display:inline-block;background:{AccentColor};color:#fff;padding:12px 24px;border-radius:6px;text-decoration:none;font-weight:600;'>Ver comprovante</a></p>")}
            <p style="color:#6b7280;font-size:13px;line-height:1.6;">Este é um comprovante automático. Guarde-o para sua referência.</p>
            """);

    public static string SubscriptionCreated(string tenantName, string description, decimal amount, string interval, DateTime nextBilling) =>
        Wrap(tenantName, $"""
            <h2 style="margin:0 0 16px;color:{BaseColor};font-size:22px;">Nova assinatura criada</h2>
            <p style="color:#374151;line-height:1.6;">Uma nova assinatura recorrente foi criada com sucesso.</p>
            <table cellpadding="0" cellspacing="0" style="border:1px solid #e5e7eb;border-radius:6px;width:100%;margin:20px 0;">
              <tr style="background:#f9fafb;"><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Descricao</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{description}</td></tr>
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Valor</td><td style="padding:12px 16px;font-size:13px;color:#111827;">R$ {amount:N2}</td></tr>
              <tr style="background:#f9fafb;"><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Intervalo</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{interval}</td></tr>
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Proxima cobranca</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{nextBilling:dd/MM/yyyy}</td></tr>
            </table>
            """);

    /// <summary>
    /// Email enviado pro comprador imediatamente após captura do pagamento de um
    /// produto do marketplace. Diferente do PaymentReceiptCustomer (que é só
    /// comprovante fiscal): este foca em ENTREGA — leva o usuário direto pro
    /// link de acesso (`deliveryUrl`) do produto digital.
    ///
    /// Se `deliveryUrl` é null, o produto não tem entrega automatizada
    /// (físico/serviço/agendamento) — caímos numa variante que confirma a
    /// compra mas sinaliza que o produtor entrará em contato.
    /// </summary>
    public static string ProductDeliveryToBuyer(
        string tenantName,
        string buyerName,
        string productName,
        string? deliveryUrl,
        string producerName,
        decimal amount) =>
        Wrap(tenantName, $"""
            <h2 style="margin:0 0 16px;color:{SuccessColor};font-size:22px;">Sua compra está confirmada ✓</h2>
            <p style="color:#374151;line-height:1.6;font-size:15px;">Olá, <strong>{buyerName}</strong>!</p>
            <p style="color:#374151;line-height:1.6;font-size:15px;">Sua compra de <strong>{productName}</strong> foi processada com sucesso. {(string.IsNullOrEmpty(deliveryUrl) ? $"<strong>{producerName}</strong> entrará em contato com as próximas etapas." : "Você já pode acessar o conteúdo agora:")}</p>
            {(string.IsNullOrEmpty(deliveryUrl) ? "" : $"""
              <p style="text-align:center;margin:32px 0;">
                <a href="{deliveryUrl}" style="display:inline-block;background:{AccentColor};color:#fff;padding:16px 32px;border-radius:8px;text-decoration:none;font-weight:600;font-size:16px;">Acessar o produto</a>
              </p>
              <p style="color:#6b7280;font-size:12px;line-height:1.6;word-break:break-all;">Se o botão não funcionar, copie e cole esta URL no navegador:<br/><a href="{deliveryUrl}" style="color:{AccentColor};">{deliveryUrl}</a></p>
            """)}
            <table cellpadding="0" cellspacing="0" style="border:1px solid #e5e7eb;border-radius:6px;width:100%;margin:24px 0;">
              <tr style="background:#f9fafb;"><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Produto</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{productName}</td></tr>
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Valor</td><td style="padding:12px 16px;font-size:14px;font-weight:600;color:{SuccessColor};">R$ {amount:N2}</td></tr>
              <tr style="background:#f9fafb;"><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Vendedor</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{producerName}</td></tr>
            </table>
            <p style="color:#6b7280;font-size:13px;line-height:1.6;">Em caso de problemas com o acesso, responda este e-mail e o vendedor entrará em contato.</p>
            """);

    public static string SubscriptionExpired(string tenantName, string description, int cycleCount, DateTime date) =>
        Wrap(tenantName, $"""
            <h2 style="margin:0 0 16px;color:{BaseColor};font-size:22px;">Assinatura encerrada</h2>
            <p style="color:#374151;line-height:1.6;">A assinatura <strong>"{description}"</strong> atingiu o número máximo de ciclos e foi encerrada automaticamente.</p>
            <table cellpadding="0" cellspacing="0" style="border:1px solid #e5e7eb;border-radius:6px;width:100%;margin:20px 0;">
              <tr style="background:#f9fafb;"><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Total de ciclos</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{cycleCount}</td></tr>
              <tr><td style="padding:12px 16px;font-size:12px;color:#6b7280;text-transform:uppercase;letter-spacing:0.5px;">Data de encerramento</td><td style="padding:12px 16px;font-size:13px;color:#111827;">{date:dd/MM/yyyy HH:mm} UTC</td></tr>
            </table>
            """);
}
