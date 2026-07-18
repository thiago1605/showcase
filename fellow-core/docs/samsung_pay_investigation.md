# Samsung Pay no checkout — investigação

> Solicitado por Codex (2026-05-07). Sem implementação. Apenas evidência +
> veredito.

## TL;DR

**Veredito: NÃO IMPLEMENTAR. Bloqueado.**

- `cpmt_1TUK7140eea2RCR1lVjvo4yn` é um **Custom Payment Method type** do
  Stripe-hosted Checkout. Não é compatível com a nossa arquitetura atual
  (Stripe.js + ExpressCheckoutElement + CardElement single-line).
- **Samsung Pay não é suportado nativamente pela Stripe** em nenhum produto
  web (Stripe.js, Elements, hosted Checkout) hoje.
- Implementar agora exigiria mudar arquitetura (migrar para Stripe-hosted
  Checkout) **e mesmo assim** Samsung Pay seria apenas um botão "off-Stripe"
  que redireciona pra integração própria com a Samsung — não há
  processamento pela Stripe.

Marcar no roadmap como **P2 / blocked, pending official Stripe/Samsung
support**.

---

## 1. O que é `cpmt_1TUK7140eea2RCR1lVjvo4yn`

Identificação por probes ao vivo (test mode, conta `acct_1TJxRw40eea2RCR1`):

| Endpoint | Resultado |
|---|---|
| `GET /v1/custom_payment_methods/cpmt_…` | 404 — `Unrecognized request URL` |
| `GET /v1/payment_method_configurations/cpmt_…` | 400 — `No such paymentsconfig` (prefix correto seria `pmc_`) |
| `GET /v1/payment_methods/cpmt_…` | 400 — `No such PaymentMethod` |
| `GET /v1/checkout/custom_payment_methods/cpmt_…` | 404 — `Unrecognized request URL` |

O ID **não é** `PaymentMethod`, **não é** `PaymentMethodConfiguration`, e
**não há endpoint API público que retorne ele por ID**. Ele aparece apenas
no Dashboard, na seção *Settings → Payment methods → Custom payment methods*.

Conclusão: é um **Custom Payment Method type** — feature exclusiva do
Stripe-hosted Checkout (`checkout.stripe.com`). Documentação oficial:
[stripe.com/docs/payments/checkout/custom-payment-methods](https://docs.stripe.com/payments/checkout/custom-payment-methods)

> *"Custom Payment Methods are merchant-defined payment options that appear
> in Checkout. When a customer selects a custom payment method, Stripe
> redirects them to a URL you provide [...]. Stripe doesn't process these
> payments — your integration handles fulfillment off-Stripe."*

Resumo prático:
- Funciona apenas em Stripe-hosted Checkout Sessions (com
  `mode=payment` + `custom_payment_methods` na criação da Session).
- O cliente vê um **botão extra** no Checkout da Stripe e, ao clicar, é
  **redirecionado** pra uma URL externa que o merchant define.
- Stripe **não processa** o pagamento. Stripe não recebe sucesso/falha;
  o merchant atualiza o estado da Session via API depois que o pagamento
  externo concluir.

---

## 2. Samsung Pay no Stripe — estado oficial

A Stripe lista oficialmente os métodos / wallets suportados em
[stripe.com/docs/payments/payment-methods/payment-method-support](https://docs.stripe.com/payments/payment-methods/payment-method-support).

Wallets web atualmente suportadas pela Stripe:

- Apple Pay
- Google Pay
- Link (Stripe)
- Amazon Pay
- Cash App Pay
- WeChat Pay
- Alipay
- Klarna / Afterpay / Affirm (BNPL)
- PayPal (mais recente)

**Samsung Pay não consta na lista.** Stripe não tem integração nativa com
Samsung Pay para web. Confirmação adicional: o
`payment_method_configuration` da nossa conta lista 5 métodos
(`apple_pay, boleto, card, google_pay, pix`). Nenhum mencionando Samsung.

Caminho real para suportar Samsung Pay no web:
- **Samsung Pay JS SDK** direto (samsungpay.com), com integração própria
  fora da Stripe. Liquidação via gateway que processa o token Samsung Pay
  (Cielo, Stone etc. no Brasil) — **não** via Stripe.
- Ou via Custom Payment Method em Stripe-hosted Checkout, como botão de
  redirect — mesma arquitetura paralela.

---

## 3. ExpressCheckoutElement / PaymentElement — compatibilidade com `cpmt_`

| Surface | Suporta `cpmt_*`? | Suporta Samsung Pay? |
|---|---|---|
| Stripe.js `ExpressCheckoutElement` (que usamos) | **Não** | **Não** |
| Stripe.js `PaymentElement` (versão moderna unificada) | **Não** | **Não** — `customPaymentMethods` em Elements ainda é beta/closed e não inclui Samsung Pay |
| Stripe-hosted Checkout (`checkout.stripe.com`) | **Sim**, via `custom_payment_methods` na Session | Apenas como botão de redirect; sem processamento Stripe |

ExpressCheckoutElement decide quais wallets renderiza com base no método
nativo do navegador/dispositivo + capabilities da PI. **Não há gancho** para
plugar um `cpmt_` aí. PaymentElement listou suporte a Custom Payment Methods
em beta para algumas integrações Stripe-hosted, mas isso continua atrelado
ao redirect off-Stripe e não cabe no nosso fluxo Element-only com captura
via PaymentIntent.

---

## 4. Mapeamento da nossa arquitetura atual

```
Frontend  →  loadStripe(pk)
          →  Elements({ mode: 'payment', amount, currency, paymentMethodCreation: 'manual' })
          →  ExpressCheckoutElement   (Apple Pay / Google Pay / Link)
          →  CardElement single-line  (cartão manual)
          →  stripe.confirmPayment / confirmCardPayment(clientSecret, ...)

Backend   →  Stripe API: PaymentIntent + capture
          →  payment_intent.succeeded → CAPTURED → split / ledger
```

Premissas dessa arquitetura (decisões já travadas em P0/P1):

- **CardElement single-line preservado** — não migrar pra PaymentElement.
- **Wallets = ExpressCheckoutElement nativo** (Apple, Google, Link).
- **Captura processada pela Stripe** — split/ledger dependem de
  `payment_intent.succeeded`.

Custom Payment Method (`cpmt_`) não conversa com nenhum desses
ingredientes. Ele depende de Stripe-hosted Checkout.

---

## 5. Veredito

> **Não implementar.**

Razões cumulativas:

1. `cpmt_1TUK7140eea2RCR1lVjvo4yn` só funciona em Stripe-hosted Checkout
   (verificado via 4 probes ao vivo + docs Stripe). Nossa arquitetura é
   Stripe.js Elements direto.
2. Mesmo trocando pra PaymentElement, Samsung Pay não está na lista de
   wallets/métodos nativos suportados pela Stripe. Não é só "configurar e
   ativar".
3. A única forma de exibir Samsung Pay no nosso checkout hoje seria como
   **botão fake** (off-Stripe redirect) — Codex pediu explicitamente para
   não fazer isso.
4. A captura/split/ledger atuais dependem de processamento Stripe. Um
   pagamento off-Stripe quebra essa cadeia: precisaríamos de integração
   gateway alternativa (Cielo/Stone/Samsung) e duplicar reconciliação.

---

## 6. O que escrever no roadmap (P2 / blocked)

Adicionar à seção P2 do `checkout_roadmap.md`:

> **Samsung Pay — blocked.** Pending official Stripe/Samsung support
> confirmation. The `cpmt_*` Custom Payment Method ID provisioned in the
> Stripe Dashboard only works in Stripe-hosted Checkout (`checkout.stripe.com`)
> as an off-Stripe redirect button — it is incompatible with the current
> ExpressCheckoutElement + CardElement single-line architecture, and
> Samsung Pay is not in Stripe's list of natively supported web wallets.
> Re-evaluate when one of the following changes:
> 1. Stripe adds Samsung Pay to ExpressCheckoutElement / PaymentElement
>    natively, or
> 2. The product decides to migrate the public checkout to Stripe-hosted
>    Checkout (which would also require revisiting CardElement single-line,
>    split/ledger reconciliation, and visual identity).

---

## 7. O que NÃO fazer

- **Não** colocar botão "Samsung Pay" estático no checkout que finja chamar
  Stripe.
- **Não** trocar `CardElement` single-line por `PaymentElement` "só pra
  testar Samsung Pay" — decisão de produto explícita contra.
- **Não** mexer no `cpmt_` provisioned. Ele não atrapalha nada na
  arquitetura atual; pode ficar lá ocioso até a feature ser viável.

---

## 8. Caso o produto decida priorizar Samsung Pay no futuro

Caminho viável (não decidido aqui):

1. Confirmar suporte oficial da Stripe a Samsung Pay (reabrir esta
   investigação em ~6 meses ou monitorar
   [stripe.com/docs/payments/payment-methods/payment-method-support](https://docs.stripe.com/payments/payment-methods/payment-method-support)).
2. Se não, avaliar integração direta com Samsung Pay JS SDK + acquirer BR
   (Cielo / Stone). Implica: novo provider em `Modules/Transactions/Providers/`,
   novo rail em `Rails/`, novo webhook handler, e split/ledger paralelo.
3. Decisão produto sobre se o ROI justifica a complexidade dual-provider.

Custo estimado (chute alto-nível): ~2–3 semanas de engenharia, mais
homologação com o acquirer e Samsung. Não recomendado sem demanda real
mensurada.
