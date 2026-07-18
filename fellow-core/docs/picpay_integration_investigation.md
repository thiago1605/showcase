# PicPay — investigação de integração

> Solicitado por Codex em 2026-05-07. Sem implementação.
> Saída: relatório com mapeamento técnico, comparação com providers atuais
> e veredito sobre quando integrar.

## TL;DR

- **PicPay deve ser um novo `PaymentProvider` (`PICPAY`), ao lado de
  Stripe e OpenPix.** Não é "método dentro da Stripe" — não há integração
  oficial Stripe ↔ PicPay.
- **API recomendada**: PicPay E-commerce *Checkout Transparente* (server-to-server).
  Retorna `qrcode` (image + content) + `paymentUrl`. Confirmação **assíncrona**
  via webhook.
- **Surface preferida no checkout**: ou Lightbox embedado (mantém o cliente na
  Fellow Pay) ou QR code Pix-style igual ao nosso atual. **Não** Redirect Padrão
  — sai do nosso domínio e quebra brand consistency.
- **Veredito de prioridade**: **P3** (não P2). Não integrar antes de:
  1. P1-6 follow-up (email/recibo) resolvido;
  2. Pix via OpenPix validado em produção;
  3. Stripe BR validado em produção;
  4. Decisão de produto sobre Pix-via-PicPay vs Pix-via-OpenPix (conflito).
- Custo estimado: ~1,5–2 sprints (~10–14 dias) entre código, testes,
  reconciliação e homologação com o PicPay.

---

## 1. O que a Stripe **não** faz

A Stripe Brasil
([support.stripe.com/questions/accepted-payment-methods-in-brazil](https://support.stripe.com/questions/accepted-payment-methods-in-brazil?locale=pt-BR))
lista oficialmente: **cartão, Apple Pay, Google Pay, Pix, Boleto**. Nada de
PicPay.

- Não há integração nativa Stripe ↔ PicPay.
- O `cpmt_*` do Stripe (Custom Payment Method type) só é botão de redirect
  off-Stripe em Stripe-hosted Checkout. **Já analisado e bloqueado** em
  `samsung_pay_investigation.md` — mesma limitação aqui.
- Logo, **não cabe** "PicPay como wallet do ExpressCheckoutElement" nem
  "PicPay como método do PaymentElement".

PicPay deve ser tratado como **provider próprio**, com seu próprio rail,
webhook, ledger, refund e reconciliação.

---

## 2. APIs PicPay relevantes

Fontes oficiais consultadas:

- *Checkout Transparente* — API E-commerce moderna:
  [developers-business.picpay.com/checkout/docs/transparent-checkout/api](https://developers-business.picpay.com/checkout/docs/transparent-checkout/api)
- *API E-commerce* (public docs / FAQ):
  [ajudanegocios.picpay.com/hc/pt-br/articles/22746698316443](https://ajudanegocios.picpay.com/hc/pt-br/articles/22746698316443-O-que-%C3%A9-a-API-E-commerce-do-PicPay)

Resumo das três opções de integração:

| Modo | UX | Pros | Cons |
|---|---|---|---|
| **Redirect (Checkout Padrão)** | Cliente sai pra `app.picpay.com/...` | mais simples, menos código no nosso lado | quebra brand consistency, conversão menor |
| **Lightbox** | Modal embedado na nossa página, com PicPay JS | mantém cliente na Fellow Pay, conversão maior | exige integração JS adicional + careful CSP |
| **Checkout Transparente (server-to-server)** | Geramos QR + copia-e-cola, igual ao Pix | 100% controle visual, mesmo padrão UX do Pix atual | mais código backend; depende exclusivamente de webhook pra confirmar |

**Recomendação**: começar por *Checkout Transparente* — server-to-server,
mesmo padrão UX do Pix do Fellow Pay (QR + copia-e-cola + AwaitingNotice +
polling). Reaproveita 80% da nossa infra Pix.

---

## 3. Fluxo PicPay Checkout Transparente

### 3.1 Autenticação

- **Header** `x-picpay-token: {seller_token}` em cada request.
- Token é gerado no portal do PicPay para Negócios. Sandbox e produção
  têm tokens distintos.
- Token é **per-merchant**, não per-account. Multi-tenant: cada Seller
  precisaria do seu próprio token armazenado encriptado (igual ao
  `Seller.EncryptedAccessToken` que já existe pro OpenPix).

### 3.2 Criar pagamento

```
POST https://appws.picpay.com/ecommerce/public/payments
Header: x-picpay-token: {seller_token}
Body:
  referenceId   (string, único, idempotency)
  callbackUrl   (URL do nosso webhook)
  returnUrl     (opcional — pra modo Redirect)
  value         (decimal, R$)
  expiresAt     (ISO 8601)
  buyer.firstName / lastName / document / email / phone
```

Resposta:

```
{
  "referenceId": "...",
  "paymentUrl": "https://app.picpay.com/checkout/...",
  "expiresAt":  "...",
  "qrcode": {
    "content":  "...",   // string copia-e-cola
    "base64":   "data:image/png;base64,..."  // QR image
  }
}
```

### 3.3 Confirmação via webhook

- PicPay POST → `callbackUrl` quando status muda.
- Body inclui `referenceId` e `authorizationId` (PicPay tx id).
- Status no PicPay: `created`, `expired`, `analysis`, `paid`, `completed`,
  `refunded`, `chargeback`.
- Mapeamento sugerido para nosso enum:
  - `created` → `CREATED`
  - `analysis` → `PROCESSING`
  - `paid` / `completed` → `CAPTURED`
  - `expired` → `DECLINED`
  - `refunded` → `REFUNDED`
  - `chargeback` → `CHARGEBACKERROR`
- Webhook **não** vem assinado por padrão; segurança baseada em validar
  via GET `/ecommerce/public/payments/{referenceId}/status` após receber
  notificação. Esse double-check é obrigatório.

### 3.4 Refund

```
POST https://appws.picpay.com/ecommerce/public/payments/{referenceId}/refunds
Body: authorizationId, value (parcial opcional)
```

---

## 4. Mapeamento contra nossa arquitetura atual

### 4.1 Como Stripe e OpenPix se encaixam hoje

| Componente | Arquivo | Observação |
|---|---|---|
| Interface comum | `Modules/Transactions/Interfaces/IPaymentProvider.cs` | `ProcessPaymentAsync`, `RefundAsync`, opcionais |
| Factory | `Modules/Transactions/Interfaces/IPaymentProviderFactory.cs` | resolve provider por enum |
| Stripe impl | `Providers/Stripe/StripePaymentProvider.cs` + `StripeApiClient.cs` | |
| OpenPix impl | `Providers/OpenPix/OpenPixPaymentProvider.cs` + `OpenPixApiClient.cs` | |
| Rails | `Rails/StripeCardRail.cs`, `StripeBoletoRail.cs`, `OpenPixRail.cs` | calcula fees + settlement |
| Roteamento | `Rails/RailRouter.cs` | `ResolveRail(method, tenant, seller)` |
| Webhooks | `Modules/Webhooks/Services/WebhooksService.cs` | dispatch `if Provider == STRIPE/OPENPIX` |
| Webhook controller | `FellowCore.Api/Controllers/WebhooksController.cs` | `POST /api/webhooks/{stripe,openpix}` |
| Webhook auth | `FellowCore.Api/Filters/WebhookAuthFilter.cs` | `[WebhookProvider(...)]` attribute |
| Enum | `Domain/Enums/Enums.cs` → `PaymentProvider { STRIPE, OPENPIX, SANDBOX }` | |
| Tenant config | `Tenant.Config.ActivePixProvider` / `ActiveCreditProvider` | per-tenant routing |
| Seller config | `Seller.EncryptedAccessToken` | per-seller credentials (encrypted) |

A arquitetura **suporta um terceiro provider sem refactor estrutural**.
A interface `IPaymentProvider` é estável; o factory + RailRouter já
encapsulam a escolha.

### 4.2 Arquivos / classes impactados pra adicionar PicPay

**Backend** — código novo:

1. `Domain/Enums/Enums.cs` — adicionar `PICPAY` ao enum `PaymentProvider`
   e ao `PaymentRailType`.
2. `Domain/Enums/Enums.cs` — decidir se entra um novo `PaymentType.PICPAY_WALLET`
   ou se reusamos `PIX` rotando via `Tenant.Config.ActivePixProvider = PICPAY`.
   **Recomendação**: novo `PICPAY_WALLET` — semântica diferente, settlement
   diferente, fees diferentes. Não deveria competir com Pix puro.
3. `Application/Modules/Transactions/Providers/PicPay/`:
   - `IPicPayApiClient.cs`
   - `PicPayApiClient.cs` (HttpClient + retry policy igual ao OpenPix)
   - `PicPayPaymentProvider.cs : IPaymentProvider`
   - `Models/PicPayModels.cs` (DTOs request/response)
4. `Application/Modules/Transactions/Rails/PicPayRail.cs : IPaymentRail`
   (calcula fees PicPay + settlement timeline).
5. `Application/Modules/Transactions/Rails/RailRouter.cs` — adicionar
   case `PICPAY_WALLET → PICPAY`.
6. `Application/Modules/Transactions/Services/TransactionService.cs` —
   adicionar `PaymentRailType.PICPAY_WALLET → PaymentProvider.PICPAY` no
   switch da linha ~92.
7. `Application/Modules/Webhooks/Services/WebhooksService.cs` —
   `HandlePicPayEventAsync` (espelhar `HandleOpenPixEventAsync` que já
   trata Pix assíncrono — bem similar).
8. `Application/Modules/Webhooks/DTOs/PicPayWebhookDto.cs` (novo).
9. `FellowCore.Api/Controllers/WebhooksController.cs` —
   `[HttpPost("picpay")] [WebhookProvider(PaymentProvider.PICPAY)]`.
10. `FellowCore.Api/Filters/WebhookAuthFilter.cs` — `ValidatePicPay`
    (double-check via GET `/payments/{ref}/status`).
11. `Application/Common/Interfaces/IProviderCostService.cs` — fee tier
    PicPay (taxa adquirente PicPay típica).
12. `FellowCore.Api/HealthChecks/PicPayHealthCheck.cs` — health probe.
13. `FellowCore.Api/Extensions/ServiceCollectionExtensions.cs` —
    `AddHttpClient<IPicPayApiClient, PicPayApiClient>`.
14. `appsettings.json` — `"PicPay": { "BaseUrl": "...", "DefaultToken": "..." }`.

**Banco de dados** — migrations:

15. `AddPicPayWalletPaymentType` — extender enum no DB se for usar enum int
    (não precisamos se PaymentType permanece int, só string-mapping).
16. `Seller.EncryptedPicPayToken` (string?, nullable) — per-seller PicPay
    token. Reusar `EncryptedAccessToken` como hoje no OpenPix? Risco:
    OpenPix e PicPay têm tokens diferentes; melhor coluna dedicada.

**Frontend** (`fellow-pay`):

17. `services/payment-links.service.ts` — novo `PaymentType` no enum
    (`paymentTypeIndex`/`paymentTypeLabel` em `lib/formatters/enums.ts`).
18. `app/(admin)/payment-links/page.tsx` — opção "PicPay" no select de
    paymentType (admin form).
19. `app/pay/[token]/page.tsx` — novo branch igual ao Pix:
    `BrlPayerForm` (nome/CPF/email obrigatórios) → `paymentLinksService.pay()`
    → `PicPayInstrument` (QR + copia-e-cola + AwaitingNotice). **Reuse do
    Pix polling P1-5** — endpoint `getTransactionStatus` já é genérico
    (retorna status string), então o polling automaticamente cobre PicPay
    assim que o backend setar `Transaction.Status` via webhook.
20. `services/payment-links.service.ts` — `PayResult.payment` ganha
    `picpayQrCode: string | null` + `picpayQrImageUrl: string | null`
    (espelho do Pix).

### 4.3 Reconciliação / split / ledger

PicPay segue o mesmo padrão do OpenPix Pix no nosso modelo:

- Webhook `paid/completed` → `WebhooksService` flips status para `CAPTURED`.
- `ReloadAsync` no tracked entity (correção do bug P0-4).
- `ledgerService.CreditSplitClearingAsync` ou `RecordIncomingFundsAsync`
  conforme `hasSplits`.
- `TransactionStatusChangedHandler` invoca `SplitProcessor`.
- **Settlement** PicPay típico é D+30 (configurável). Precisamos confirmar
  no contrato e ajustar `PicPayRail.CalculateSettlementDate`.
- **Refund** parcial suportado via API. Precisamos espelhar o handler de
  `charge.refunded` da Stripe — mais simples porque PicPay é wallet
  (sem dispute/chargeback complexo, apenas `refunded`/`chargeback` status).

### 4.4 Pontos de atenção

1. **Conflito Pix-via-PicPay vs Pix-via-OpenPix**: PicPay também oferece Pix
   no Checkout Transparente. Decisão obrigatória antes de implementar:
   - Manter OpenPix como provedor único de Pix puro;
   - Usar PicPay **apenas** para wallet PicPay (botão "Pagar com PicPay");
   - Ou habilitar Pix-via-PicPay como provider alternativo, configurável
     em `Tenant.Config.ActivePixProvider`.

   **Recomendação**: começar com PicPay = wallet PicPay only. Pix continua
   em OpenPix. Reduz superfície de teste e contratos.

2. **Webhook não assinado**: o callback PicPay tradicional não tem
   assinatura HMAC como Stripe. Defesa: após receber notificação, fazer
   `GET /payments/{referenceId}/status` antes de mover ledger. Isso é
   padrão recomendado pela própria PicPay e proteção contra spoof.

3. **Idempotência**: `referenceId` nosso (`Transaction.Id`) é a chave
   idempotente do PicPay. Mesmo `referenceId` retornado pela criação =
   PI duplicada não é criada.

4. **LGPD / dados do payer**: `buyer.document` (CPF/CNPJ) é obrigatório,
   igual ao Pix/Boleto. Já temos esse pipeline pronto após P0-3.

5. **Test mode / sandbox**: PicPay tem sandbox em
   `appws.picpay.com/ecommerce` com tokens dedicados. Smoke test via
   `scripts/test-picpay-webhook.sh` (espelho do `test-stripe-webhook-capture.sh`)
   — feasible.

6. **Política de fees**: PicPay cobra fee diferente da Stripe / OpenPix.
   Precisamos do contrato comercial Fellow Pay ↔ PicPay antes de
   `PicPayRail.CalculateFees` ser real.

---

## 5. Critérios mínimos antes de oferecer ao seller

Antes de qualquer seller poder cobrar via PicPay em produção:

- [ ] `PicPayPaymentProvider.ProcessPaymentAsync` testado em sandbox PicPay.
- [ ] Webhook handler validado: criar PI sandbox → marcar paid manualmente
      no console PicPay → confirmar status flip CAPTURED.
- [ ] Replay do webhook 2× → idempotência (mesmo padrão do P0-4 Stripe).
- [ ] SplitProcessor disparado após CAPTURED PicPay → `SplitTransfers` PAID.
- [ ] LedgerEntries criadas com fee tier PicPay correto (do contrato real).
- [ ] Refund integral testado via API.
- [ ] Refund parcial testado.
- [ ] Webhook ack via double-check (`GET /payments/{ref}/status`) — anti-spoof.
- [ ] Settlement reconciliação: `Transaction.SettlementDate` reflete D+N do
      contrato real.
- [ ] `PicPayHealthCheck` reportando ok.
- [ ] Tenant config + Seller credential seed flow testado (multi-tenant).
- [ ] Frontend `/pay/[token]` renderiza PicPay igual ao Pix (QR + copia-e-cola
      + polling de status reaproveitando o endpoint do P1-5).
- [ ] Documentação: como o seller cadastra o token PicPay no portal admin.

---

## 6. Comparação rápida com OpenPix (já implementado)

| Aspecto | OpenPix | PicPay |
|---|---|---|
| Auth | header `Authorization: token` | header `x-picpay-token: token` |
| Idempotency | `correlationID` no body | `referenceId` no body |
| Confirmação | webhook `OPENPIX:CHARGE_COMPLETED` | webhook `paid` / `completed` |
| Webhook signature | per-seller token validation | sem signature; double-check via GET status |
| QR / copy-paste | `brCode` + `qrCodeImage` | `qrcode.content` + `qrcode.base64` |
| Refund | `POST /charge/{id}/refund` | `POST /payments/{ref}/refunds` |
| Settlement | depende do banco do seller | D+30 padrão (configurável) |
| Sandbox | sim, `api.openpix.com.br` (mesmo host, token sandbox) | sim, host dedicado |

Estimativa: **~70% do código do OpenPix pode servir de molde**. Trocar host,
campos de DTO, e o webhook handler. Spec idêntico em estrutura.

---

## 7. Riscos técnicos / compliance

1. **Webhook spoof**: PicPay sem assinatura HMAC. Mitigação obrigatória:
   double-check via GET status.
2. **Race condition idempotência**: webhook chega 2× muito rápido. Já
   coberto pelo padrão `if (transaction.Status == newStatus) return;` +
   `Transaction.IsValidTransition` (mesmo do Stripe/OpenPix).
3. **Token leak**: per-seller token criptografado em DB, igual ao OpenPix.
4. **Conflito acquirer**: se um seller já está em Stripe Connect com
   acquirer X, e PicPay liquida em conta diferente, precisamos de
   reconciliação cruzada. Ledger atual está pronto pra isso (cada
   transaction carrega `Provider`).
5. **Fee accuracy**: PicPay cobra MDR diferente. Sem o contrato comercial
   real, qualquer hard-code é estimativa. Bloqueador comercial, não técnico.
6. **LGPD**: `buyer.document` (CPF) é coletado e enviado pra PicPay. Termos
   de privacidade do Fellow Pay precisam mencionar PicPay como subprocessor.
7. **PIX-by-PicPay regulatório**: Bacen + PicPay têm regras específicas; se
   habilitarmos esse modo, precisa validar com jurídico. Razão extra pra
   começar com **wallet only**.

---

## 8. Credenciais a solicitar ao PicPay

Para abrir a integração, precisamos do seguinte do PicPay Negócios:

1. **Conta sandbox** (developers-business.picpay.com): credenciais de teste
   e console pra simular pagamento.
2. **Conta produção** (apps Fellow Pay como conta merchant primária ou
   por seller): processo varia conforme modelo de relacionamento.
3. **Token x-picpay-token sandbox** (pelo menos 1 pra desenvolvimento).
4. **Token produção** assim que homologação for aprovada.
5. **Fee schedule oficial** assinado (MDR PicPay para Fellow Pay).
6. **SLA de webhook** (latência típica, retry policy do lado deles).
7. **Política de chargeback PicPay** (se aplicável a wallet).
8. **Settlement timeline** real (D+N) com base no contrato.
9. **Limite de transações** sandbox/produção.
10. **URL do webhook console** (pra simular/replay manualmente em dev).

---

## 9. Veredito

> **P3 — Não integrar agora. Não bloqueia checkout.**

Razões:

1. **Pré-requisitos** ainda abertos:
   - P1-6 follow-up (entrega real de email/recibo) precisa fechar antes.
   - Pix via OpenPix em produção real (ainda em sandbox / `appID inválido`
     no health check atual).
   - Stripe BR validado com cliente real.
2. **Decisão de produto** sobre Pix-via-PicPay vs Pix-via-OpenPix precisa
   acontecer antes de qualquer linha de código.
3. **Contrato comercial** (MDR + acquirer + sandbox credentials) precisa
   estar resolvido. Sem fee schedule real, qualquer implementação fica
   genérica e precisa retrabalho depois.
4. **Custo**: ~1,5–2 sprints. Não é trivial, mas é menor do que parece
   por causa do molde OpenPix.
5. **ROI**: PicPay é diferencial brasileiro real (~50M usuários no Brasil),
   muito mais plausível que Samsung Pay. Vale o investimento **depois** que o
   checkout core estiver maduro.

### Quando reabrir

Reabrir esta task quando **todos** os abaixo forem verdadeiros:

- [ ] P1-6 (recibo email) funcional e testado.
- [ ] OpenPix Pix testado em produção com pelo menos 5 transações reais.
- [ ] Stripe Card capturando em produção com pelo menos 5 transações reais.
- [ ] Decisão de produto: PicPay = wallet only, ou também Pix-by-PicPay?
- [ ] Contrato Fellow Pay ↔ PicPay assinado (MDR + acquirer).
- [ ] Sandbox credentials PicPay obtidas.

Quando esses 6 itens estiverem checados, esta investigação vira backlog
implementável de ~1,5 sprints.

---

## 10. Recomendação para o roadmap

Adicionar à seção P3 (criar se não existir) do `checkout_roadmap.md`:

> **P3-PICPAY — Provider PicPay (wallet)** *(2026-05-07, candidate)*.
> Adicionar PicPay como `PaymentProvider.PICPAY` separado, com rail próprio,
> webhook handler espelhando OpenPix, e novo `PaymentType.PICPAY_WALLET`.
> Não tratar como método Stripe. Bloqueado em pré-requisitos: P1-6 fechado,
> Stripe + OpenPix em produção, decisão de produto sobre Pix-by-PicPay,
> contrato comercial PicPay com MDR. Investigação completa em
> `picpay_integration_investigation.md`.
