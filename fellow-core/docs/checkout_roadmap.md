# Checkout & Portal Roadmap — P0 / P1 / P2

> Source: Codex review (2026-05-07). This document organizes the roadmap into three
> phases with checklists, acceptance criteria, and a proposed P0 execution order.
> No implementation in this document — see individual tasks for execution.

## Context & ground rules

- **Product**: Fellow Pay (frontend) / Fellow Core (.NET API).
- **Auth model**: portal already uses JWT with `seller_id` claim. Public checkout
  at `/pay/{token}` resolves anonymously.
- **Brand goal**: Fellow Pay's own checkout, visual style of Pagar.me / Iugu / Asaas.
  Light theme by default; purple as accent only.
- **Stripe surface — locked decisions**:
  - Keep **single-line `CardElement`** for manual card. Do not switch to split Elements.
  - **No real CVC flip**. Card visual stays display-only with placeholders.
  - **Wallets-first** (Apple Pay / Google Pay / Link) above card form.
  - Sensitive data (number / expiry / CVC) never leaves Stripe iframe.
- **Backend integrity**:
  - Pix / Boleto rails require name + CPF/CNPJ + email at `/pay`.
  - Card flow uses Stripe deferred-intent mode — `/pay` only fires at confirm time.
  - PaymentLink usage reservation only on real confirm, not on page load.

---

## Phase P0 — Required before real customers

**Goal**: ship the minimum needed to sell with security and a professional appearance.

### P0 checklist

- [x] **P0-1 — Public seller name on checkout** *(done 2026-05-07)*
  - Backend: `PaymentLinkResolveDto` carries `SellerName` (anonymous endpoint).
  - Resolution rule: prefer `Seller.TradeName` (nome fantasia), fallback to
    `Seller.LegalName` (razão social). No CNPJ / IDs / contact exposed.
  - `PaymentLinkService` ganhou `ISellerRepository` no construtor; resolve
    busca o seller pelo `link.TenantId + link.SellerId`.
  - Frontend: novo campo `sellerName` em `paymentLinksService.resolve` e na
    `ResolvedLink`. Painel de resumo mostra "Você está pagando **{sellerName}**"
    com classe `.sellerLine` (separador sutil acima do valor).
  - Verified: anonymous `GET /api/v1/payment-links/pay/{token}` retorna
    `{..., "sellerName": "Loja do Bruce Wayne"}`.

- [x] **P0-2 — Real installment support (or honest absence)** *(closed 2026-05-07: not supported in current setup; UI sanitized)*
  - **Investigation (live probes against the Stripe test account)**
    - Account country = `BR`, default_currency = `brl`,
      `capabilities.card_payments = active`. Pré-requisitos básicos OK.
    - Probe 1: `POST /v1/payment_intents` with `payment_method_options[card][installments][enabled]=true`
      → 200 OK. The flag is **accepted** by the account.
    - Probe 2: same PI with `payment_method=pm_card_visa` + `confirm=true`
      → status `succeeded`, but `installments.available_plans = []`.
      Even with a tokenized BR-eligible card attached and confirmed, Stripe did
      not return any installment plans for this account in test mode.
    - Backend snapshot: `StripePaymentProvider` only forwards installments as
      *metadata* (`metadata["installments"] = N`); it does **not** send
      `payment_method_options.card.installments.enabled` nor a `plan` object.
      So today the actual capture is always 1× even when the link advertises
      multiple parcelas.
  - **Decision: not supported in this setup.** The UI no longer suggests
    parcelamento until the adquirente Stripe BR returns real `available_plans`
    for this account / for the customers' card BINs.
  - **UI changes**
    - Public checkout (`/pay/[token]/page.tsx`): badge `{installments}x`
      removed from `amountMeta`. Comment in code explains why and how to
      re-enable.
    - Admin payment-link form (`/payment-links/page.tsx`): "Parcelas" input
      locked at 1× and disabled, with helper text *"Cobrança à vista.
      Parcelamento real via Stripe ainda não disponível."*.
  - **No backend change** in this iteration. Sending
    `installments.enabled=true` is a forward-compat possibility but only
    once we are confident the adquirente returns plans for real customer
    cards — otherwise we'd just collect noise.
  - **Re-enabling later** requires:
    1. Confirm with Stripe / adquirente that this account has the
       installments capability turned on for live card BINs.
    2. Extend `StripeApiClient.CreatePaymentIntentAsync` to send
       `payment_method_options[card][installments][enabled]=true` for
       credit-card BRL PIs.
    3. Extend the public checkout to retrieve `available_plans` after the
       customer enters the card (via `stripe.retrievePaymentIntent` or
       Element events) and let the customer pick a plan.
    4. Confirm with the chosen plan in `confirmCardPayment(clientSecret, {
       payment_method, payment_method_options: { card: { installments: {
       plan } } } })`.
    5. Persist `Transaction.Installments` from the actually-confirmed plan,
       not from the link's advertised maximum.

- [x] **P0-3 — Collect CPF/CNPJ on manual card** *(done 2026-05-07)*
  - Backend
    - `Transaction.PayerDocument` (nullable, MaxLength 14) added to the entity.
      Migration `20260507041029_AddTransactionPayerDocument`.
    - `Transaction.SetPayerInfo` now accepts `document` and strips the mask
      to digits before persisting.
    - `TransactionService.CreateAsync` forwards `request.Payer.Document` into
      `SetPayerInfo` so the value flows from `PayPaymentLinkDto` → `PayerDto`
      → `Transaction.PayerDocument`.
    - Existing placeholder substitution in `PaymentLinkService.PayAsync` only
      kicks in when the frontend sends an empty value (wallet-style call
      where no form ran). Real values pass through untouched.
  - Frontend `/pay/[token]/page.tsx`
    - New required input "CPF/CNPJ do pagador" with mask + 11/14-digit
      validation, helper text "Usado para segurança da transação e emissão
      de comprovante."
    - `CardCheckout.handleCardSubmit` validates digits before calling
      `createIntent({ payerName, payerEmail, payerDocument })`.
    - Wallet flow (`ExpressCheckoutElement.onConfirm`) **does not** request
      CPF — wallet billing flows through Stripe; backend retains placeholder
      until webhook enrichment lands.
  - Smoke (`scripts/test-stripe-webhook-capture.sh`)
    - Initiates /pay with real payer (`Joao Smoke Real`, doc `12345678909`,
      `joao.smoke@fellowpay.com.br`).
    - Asserts the persisted `PayerName` / `PayerDocument` / `PayerEmail`
      are the real ones — fails the run if placeholders leak.
    - Webhook → CAPTURED + splits + ledger remain green.

- [x] **P0-4 — Stripe webhook → status CAPTURED / FAILED** *(done 2026-05-07)*
  - All four event types are wired and tested:
    - `payment_intent.succeeded` → `CAPTURED`
    - `payment_intent.payment_failed` → `DECLINED`
    - `payment_intent.canceled` → `VOIDED`
    - `charge.refunded`, `charge.dispute.*`, `account.updated` (already wired)
  - Transaction lookup via `ProviderTxId` (= Stripe `PaymentIntent.id`).
  - HMAC signature validation in `WebhookAuthFilter` with 5-min replay window.
  - **Bug found & fixed during smoke**: `WebhooksService` used `ExecuteUpdateAsync`
    to flip status, but the in-memory tracked `Transaction` entity stayed at
    PROCESSING. The downstream `SplitProcessor.ProcessSplitsForTransactionAsync`
    re-read the same entity through the same DbContext, got the stale row, and
    logged `"não está CAPTURED, ignorando splits"`. Fix: call
    `transactionRepository.ReloadAsync(transaction)` immediately after
    `SetStatusAsync` in both Stripe and OpenPix paths so the change tracker
    matches the DB before the domain event fires.
  - `SplitProcessor` is invoked from `TransactionStatusChangedHandler` only when
    the new status is `CAPTURED`. Wrapped in try/catch with logging so a split
    failure does not break webhook ingestion.
  - End-to-end smoke (`scripts/test-stripe-webhook-capture.sh`) covers:
    - Card link without split: webhook → CAPTURED + 5 LedgerEntries + replay
      idempotent.
    - Card link with SplitRule (Alfred 30%): webhook → CAPTURED + 1
      TransactionSplit PAID + 2 SplitTransfers PAID (recipient + primary
      residual) + 8 LedgerEntries.
  - Unit coverage already in place: `ConcurrencyTests`, `SplitProcessorTests`,
    `SplitClearingAndMarginTests`, `WebhooksServiceRefundSplitTests` —
    54 tests passing post-change.

- [x] **P0-5 — BR-market visual standard on checkout** *(done 2026-05-07)*
  - Visual baseline already shipped during the first wave of P0-1..P0-4 work
    (light theme, white panels, purple accent only, two-column layout ≥ 760px,
    `CardElement` single-line, wallets on top, "Pagamento seguro" badge,
    "Processado por Fellow Pay" footer, no emoji, no neon).
  - Final QA pass on 2026-05-07:
    - Copy honesty: "Você receberá um comprovante por email em instantes" →
      "Quando o pagamento for processado, um comprovante será enviado por
      email." Removes the unconditional promise; matches what the system
      actually does (handler dispatches email; delivery depends on the email
      service).
    - Success icon stroke aligned to the design palette (`#16a34a`, the
      `--success` token from `checkout.module.css`).
    - Accessibility:
      - `role="alert"` + `aria-live="assertive"` on `invalid`/`error` panels.
      - `role="status"` + `aria-live="polite"` on `loading`, `card_init`
        and `AwaitingNotice` (Pix/Boleto waiting state).
      - `aria-live="polite"` wrapper on the success state.
      - Inline error paragraphs and `.alertError` divs gain `role="alert"`.
      - Decorative SVGs (logo, lock, check, spinner inner) marked
        `aria-hidden="true"`.
    - TypeScript: `event.billingDetails` cast to a typed shape so wallets'
      `name`/`email` are read without `any`. `npx tsc --noEmit` is clean.
    - Lint: `eslint src/app/pay/[token]/page.tsx` returns 0 issues. The
      project-wide lint warnings are pre-existing in unrelated files.
    - Smoke (`scripts/test-stripe-webhook-capture.sh`) re-run after edits:
      no-split + split scenarios still pass.

### P0 acceptance criteria

- Customer opens the payment link and clearly knows **whom** they are paying.
- Customer can pay (or initiate Pix/Boleto) with a UX that does not look amateur.
- Card flow does not display fake installments. If shown, they are real and
  matched to what Stripe will charge.
- Manual card flow collects CPF/CNPJ + email + holder name; values arrive in
  the DB (not placeholders).
- A real card test (`4242 4242 4242 4242`) results in a transaction whose
  webhook flips it to `CAPTURED`, and split/ledger run correctly.
- The page does not look like an admin dashboard.

---

## Phase P1 — Important post-base improvements

**Goal**: improve conversion, trust, and payment tracking.

### P1 checklist

> **Numbering note**: Codex's 2026-05-07 P1 prompt re-ordered items relative to
> the original list. The numbering below follows Codex's order
> (Termos → retry → 3DS → bandeiras → Pix polling → recibo).

- [x] **P1-1 — Terms & LGPD notice** *(done 2026-05-07)*
  - Discreet line under each primary action (card form + BRL form):
    "Ao continuar, você concorda com os Termos de Uso e a Política de Privacidade."
  - URLs configurable via `NEXT_PUBLIC_TERMS_URL` and `NEXT_PUBLIC_PRIVACY_URL`
    (defaults: `https://fellowpay.com.br/termos` and `/privacidade`).
  - Links open in a new tab with `target="_blank" rel="noopener noreferrer"`.
  - No blocking checkbox — using the page implies aceite.

- [x] **P1-2 — Retry on recoverable error** *(done 2026-05-07)*
  - Inline form errors (CardCheckout / BrlPayerForm): the form stays mounted
    so email / nome / CPF / telefone are preserved and the user can re-submit
    immediately. Submit buttons disable while a confirm is in flight, blocking
    double submit.
  - Dead-end `error` panel ("Não foi possível continuar") gains a
    "Tentar novamente" button that clears `errorMessage` and re-runs
    `card_init` / `brl_form` based on the link's payment type.
  - Card flow uses Stripe deferred intent — each retry creates a fresh
    PaymentIntent server-side, so we don't reuse a stale clientSecret.

- [x] **P1-3 — 3DS / bank-auth state UI** *(done 2026-05-07)*
  - While `confirmCardPayment` (or the wallet's `confirmPayment`) is
    in-flight — which includes any 3DS challenge that Stripe surfaces in its
    own iframe — the card form button label flips to *"Verificando com seu
    banco…"* and is disabled.
  - Below the button, a `processingHint` line appears: *"Pode levar alguns
    segundos. Não feche nem atualize a página."* with `role="status"` +
    `aria-live="polite"`.
  - Stripe.js doesn't expose a "3DS started" event for CardElement, so we use
    the `submitting` flag as the proxy. This is honest: the message stays
    accurate for both 3DS and non-3DS cards (the flow is the same loading
    state from the customer's POV).

- [x] **P1-4 — Accepted card brands shown** *(done 2026-05-07)*
  - Text-only line below the CardElement: *"Aceitamos Visa · Mastercard · Elo
    · American Express · Hipercard"*. Class `acceptedBrands`, discreet.
  - We avoid bitmap brand marks to side-step trademark/asset licensing
    questions for an MVP. Can be replaced with proper SVG marks later.

- [x] **P1-5 — Pix real-time status / polling** *(done 2026-05-07)*
  - Backend: anonymous endpoint
    `GET /api/v1/payment-links/pay/{token}/status/{transactionId:guid}` returns
    `{ status, isTerminal }`. Tenant-scoped via the link's token; cross-tenant
    probing returns 404. Added `PaymentLinkTransactionStatusDto` +
    `IPaymentLinkService.GetTransactionStatusAsync`.
  - Frontend: in `brl_awaiting` for `PIX` only (Boleto excluded — compensação
    leva 1–2 dias), polls every 4 s up to ~10 min. On `CAPTURED` flips to
    `success`; on other terminal states surfaces `setStage("error")` with a
    friendly Pix-specific message; transient network errors are tolerated.
    Cleanup on unmount / stage change.

- [~] **P1-6 — Confirmed receipt email** *(MITIGADO 2026-05-07 — não concluído funcionalmente)*
  - **Status**: only the customer-facing copy was sanitized. The actual email
    delivery is **still broken** in this setup. Codex (2026-05-07) explicitly
    asked us to keep this open as a follow-up rather than mark "done".
  - **Investigation**: provider is `ResendEmailProvider` registered as
    `IEmailService`. When `Email:ApiKey` (env `Email__ApiKey`) is missing, the
    provider logs a warning and returns silently — emails are not sent.
    Confirmed in the running container: no `Email__*` env vars set.
  - **Bug found** in `TransactionStatusChangedHandler.SendCustomerReceiptEmailAsync`:
    it calls `receipt.MarkCustomerEmailSent()` after `await emailService.SendAsync(...)`,
    but `SendAsync` returns without throwing when the provider is unconfigured.
    Net effect: the receipt row is marked "sent" even though no email was
    delivered.
  - **Copy fix shipped**: success card no longer promises a receipt by email.
    Old: *"Quando o pagamento for processado, um comprovante será enviado por
    email."* → New: *"Anote o número da transação abaixo para sua referência."*
  - **P1-6 follow-up — required to mark this item as functionally done**
    (Codex 2026-05-07):
    1. Configure `Email__ApiKey` (Resend) on the real environment.
    2. Fix `ResendEmailProvider.SendAsync` to surface a typed
       `EmailNotConfiguredException` (or similar) instead of returning
       silently when `ApiKey` is missing.
    3. Update `TransactionStatusChangedHandler` so `receipt.MarkCustomerEmailSent()`
       runs **only** after the provider acknowledges acceptance. Failures must
       persist via `receipt.RecordCustomerEmailFailure(reason)` and not flip
       the "sent" flag.
    4. Add an integration/unit test for the success path: provider returns
       OK → receipt row marked sent.
    5. Add an integration/unit test for the failure path (missing ApiKey,
       provider 4xx/5xx, network exception) → receipt row stays "not sent"
       and the failure reason is recorded.
    6. After the four points above land and a real email actually arrives at
       a test inbox, restore the email-promise copy on the success card and
       flip this checklist entry to `[x]`.

### P1 acceptance criteria

- Customer paying via Pix sees the screen flip to "Pago" without a manual reload.
- Errors offer a one-click retry that preserves what is safe to preserve.
- 3DS challenges are clearly communicated.
- The terms / privacy notice is present and links work.
- The receipt promise matches what the system actually does.

---

## Phase P2 — Non-blocking polish & internal tooling

**Goal**: polish product, operations, and advanced features without blocking MVP.

### P2 checklist

- [ ] **P2-1 — Dev-only capture endpoint**
  - Endpoint exposed **only** when `ASPNETCORE_ENVIRONMENT=Development`.
  - Useful to exercise `SplitProcessor` / ledger without depending on the
    Stripe webhook landing.
  - Must never be wired into Production (guard at registration time, not just
    at request time).

- [ ] **P2-2 — Light card-visual polish (PCI-safe)**
  - Keep Stripe CardElement single-line.
  - Do **not** add separate Elements.
  - Do **not** add a real CVC flip.
  - Already shipped: holder name reflects input, brand reflects Stripe `change`
    event, focused state lifts the card. Keep that scope.
  - Optional: replace brand wordmark text with SVG brand marks.

- [ ] **P2-3 — Edit split rule**
  - `PATCH /split-rules/{id}` for the rule's owner seller.
  - Rules already used by past transactions do **not** alter the snapshot
    stored on those transactions.
  - Consider versioning if a rule's logical identity needs to evolve over time.

- [ ] **P2-4 — Manual transaction creation in the portal**
  - Portal form to create a one-off charge.
  - Allow `splitRuleId` to be applied.
  - Same JWT seller scope as the rest of the seller-only endpoints.

- [ ] **P2-5 — Move PaymentLinks list filter to repository**
  - Today the seller filter is applied in-memory after fetch.
  - Push it into the repository / service layer for efficiency at scale.
  - Tracked as `P1 follow-up` task #28 in TaskList.

- [ ] **P2-6 — Adversarial seller test on `POST /transactions`**
  - Close the e2e test that today is parked as a follow-up (#29).
  - Send a full payload with an attacker-owned `sellerId` from a different
    tenant — must always 403 / 404.

- [⛔] **P2-7 — Samsung Pay no checkout — BLOCKED** *(2026-05-07, Codex aprovou veredito)*
  - **Veredito**:
    - O ID `cpmt_1TUK7140eea2RCR1lVjvo4yn` é um Custom Payment Method type
      (`cpmt_...`), não uma Payment Method Configuration (`pmc_...`).
    - Custom Payment Methods não funcionam no nosso fluxo atual com
      ExpressCheckoutElement + CardElement single-line.
    - Samsung Pay não é wallet nativa suportada pela Stripe no web checkout
      atual.
    - Não existe caminho oficial sem mudar arquitetura.
    - Mesmo com PaymentElement, isso não vira Samsung Pay nativo processado
      pela Stripe.
    - Implementar Samsung Pay exigiria integração direta/off-Stripe com
      Samsung Pay JS SDK + adquirente/PSP compatível, criando novo fluxo de
      liquidação, split, reconciliação, webhook e ledger.
  - **Decisão**:
    - Não implementar agora.
    - Não criar botão fake.
    - Não trocar `CardElement` por `PaymentElement`.
    - Não mexer no checkout atual.
    - Manter `cpmt_1TUK7140eea2RCR1lVjvo4yn` ocioso na Stripe; ele não
      bloqueia nada.
  - **Critério para reabrir**:
    - Evidência oficial da Stripe de suporte nativo a Samsung Pay web no
      Brasil; ou
    - Decisão explícita de produto/arquitetura para criar um rail
      off-Stripe com Samsung Pay + adquirente compatível.
  - **Evidência completa** em
    [`samsung_pay_investigation.md`](./samsung_pay_investigation.md).

### P2 acceptance criteria

- Improvements landed without changing the "single CardElement" decision.
- No P2 item delays P0 / P1.
- No dev-only feature ever reaches production.

---

## Phase P3 — Additional rails (post-stabilization)

**Goal**: rails opcionais que só fazem sentido **depois** que os rails
atuais (Stripe + OpenPix) estiverem maduros em produção.

### P3 checklist

- [⛔] **P3-PICPAY — PicPay Provider Integration — BLOCKED** *(2026-05-07, Codex aprovou classificação P3)*

  **Status**:
  - Não integrar agora.
  - Classificação: P3.
  - PicPay deve ser tratado como provider/rail próprio, não como método
    Stripe.
  - Não criar botão fake no checkout.
  - Não misturar Pix PicPay com OpenPix sem decisão explícita de produto.

  **Decisão técnica**:
  - Surface preferida futura: Checkout Transparente server-to-server.
  - Motivo: retorna QR Code/base64 e reaproveita boa parte do modelo
    OpenPix + Pix polling já implementado.
  - Redirect/Lightbox ficam fora do MVP porque quebram consistência visual
    ou exigem JS/CSP adicional.
  - Começar, se reaberto, com wallet PicPay only; Pix continua em OpenPix
    até decisão comercial.

  **Bloqueadores para reabrir**:
  1. P1-6 follow-up fechado: recibo por email real funcionando.
  2. Stripe produção validado.
  3. OpenPix produção validado, ou decisão formal de não ofertar Pix no
     launch.
  4. Contrato comercial PicPay assinado.
  5. Fee schedule/MDR PicPay confirmado.
  6. Credenciais sandbox e produção recebidas.
  7. Política de webhook/status double-check definida, já que o relatório
     indica ausência de HMAC.
  8. Decisão de produto: PicPay Wallet only vs Pix via PicPay.

  **Critérios mínimos antes de ofertar**:
  - `PicPayPaymentProvider` implementado atrás de `IPaymentProvider`.
  - Criação de pagamento sandbox com idempotência.
  - QR Code/render no checkout.
  - Polling/status double-check.
  - Webhook idempotente.
  - Replay de webhook sem duplicação.
  - Status `PROCESSING` → `CAPTURED`/`FAILED`/`CANCELED`.
  - Ledger criado corretamente.
  - SplitProcessor executado após CAPTURED.
  - Refund integral/parcial, se suportado.
  - Healthcheck/observabilidade.
  - Teste multi-tenant.
  - Runbook operacional.

  **Referência**: detalhes completos em
  [`picpay_integration_investigation.md`](./picpay_integration_investigation.md).

### P3 acceptance criteria

- Nenhum item P3 entra antes de P0/P1 estarem concluídos funcionalmente
  (incluindo P1-6 real, não apenas mitigado).
- Cada novo provider passa pela mesma régua de Stripe/OpenPix:
  IPaymentProvider, rail, webhook idempotente, ledger, SplitProcessor,
  refund, healthcheck, runbook.
- Decisões comerciais (contrato, MDR, settlement) precisam estar fechadas
  antes de qualquer linha de código.

### Priority sequence (Codex 2026-05-07)

1. Fechar P1-6 recibo por email real.
2. Validar Stripe produção.
3. Validar ou remover OpenPix do lançamento.
4. Só depois pensar em rails adicionais como PicPay.

PicPay pode ser bom produto no Brasil, mas agora seria dispersão: mais
webhook, mais reconciliação, mais disputa, mais settlement, mais suporte.

---

## Limitations confirmed in current setup

- **Real installments via Stripe BR (P0-2)**: probed live on 2026-05-07. The
  account accepts the `payment_method_options.card.installments.enabled` flag
  but returns no `available_plans` even after attaching `pm_card_visa` and
  confirming the PaymentIntent. Until the adquirente Stripe BR returns real
  plans for live cards, parcelamento UI stays hidden in both the public
  checkout and the admin payment-link form (locked at 1×). Detalhes em
  [`installments_investigation.md`](./installments_investigation.md).
- **Samsung Pay (P2-7)**: blocked. `cpmt_*` é Custom Payment Method type
  do Stripe-hosted Checkout, incompatível com a arquitetura atual
  (ExpressCheckoutElement + CardElement single-line). Samsung Pay não
  está na lista oficial de wallets nativas da Stripe no web. Não há
  caminho oficial sem trocar arquitetura. Detalhes em
  [`samsung_pay_investigation.md`](./samsung_pay_investigation.md).

## Out-of-scope decisions (logged so we don't reopen)

- **Separate Stripe Elements (CardNumber / Expiry / CVC)** — rejected.
  Current single-line CardElement matches Pagar.me / Mercado Pago and is fine.
- **Real CVC flip animation on card visual** — rejected. Single-line CardElement
  does not expose per-subfield focus events; faking it would either need
  custom inputs (PCI risk) or a misleading flip.
- **Mirroring real card number on the visual** — rejected (PCI scope creep).
- **Dark mode for public checkout** — rejected. Portal can be dark; checkout
  stays light by default.

---

## Current state snapshot (2026-05-07)

| Item | Status |
|------|--------|
| Visual claro / 2-col / mobile-first | Done |
| Wallets above card | Done |
| CardElement single-line | Done |
| Card visual com placeholders + bandeira + holder name | Done |
| Deferred intent — `/pay` só no confirm | Done |
| Pix/Boleto form-first com guard server-side | Done |
| Header "Pagamento seguro" + footer "Processado por Fellow Pay" | Done |
| Logo light-mode no-bg | Done |
| Cloudflare tunnel `checkout.fellowpay.com.br` + Apple Pay verified | Done |
| Seller name na resolve | Done (P0-1, 2026-05-07) |
| Parcelamento real | Closed (P0-2, 2026-05-07) — UI sanitized, not supported |
| CPF/CNPJ no cartão | Done (P0-3, 2026-05-07) |
| Webhook Stripe → CAPTURED + Split | Done (P0-4, 2026-05-07) |
| Termos/LGPD | Done (P1-1, 2026-05-07) |
| Retry em erro | Done (P1-2, 2026-05-07) |
| 3DS / verificando banco | Done (P1-3, 2026-05-07) |
| Bandeiras aceitas | Done (P1-4, 2026-05-07) |
| Pix polling | Done (P1-5, 2026-05-07) |
| Recibo por email | **Mitigado (P1-6, 2026-05-07) — copy honesta; envio real ainda pendente, ver follow-up** |
| Samsung Pay no checkout | **Blocked (P2-7, 2026-05-07) — incompatível com arquitetura atual; não implementar** |
| PicPay Provider Integration | **Blocked (P3-PICPAY, 2026-05-07) — viável tecnicamente; bloqueado em pré-requisitos (P1-6, Stripe/OpenPix prod, contrato, credenciais)** |

---

## P0 execution order — APPROVED (Codex 2026-05-07)

1. **P0-1 (seller name on resolve)** — first. Small backend + frontend pair,
   no Stripe coupling. Quick trust-signal win and helps later visual QA.
2. **P0-4 (webhook + split end-to-end)** — closes the real financial loop.
   Validate `payment_intent.succeeded`/`payment_failed` flips Transaction,
   raises `TransactionStatusChangedEvent`, runs `SplitProcessor`.
3. **P0-3 (CPF/CNPJ on manual card)** — removes the placeholder
   (`Cliente Fellow Pay` / `00000000000`) and improves BR adherence.
4. **P0-2 (real installments)** — last among functional items because it
   depends on confirming Stripe account/setup support and has the largest
   contract risk.
5. **P0-5 (visual standard)** — already largely shipped; treat as final QA
   pass after P0-1..P0-4 land.

After P0 lands, regroup before starting P1 to confirm scope is still aligned.
