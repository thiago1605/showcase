# Stripe BR installments — investigação detalhada

> Solicitado por Codex em 2026-05-07. Objetivo: descobrir conclusivamente
> por que parcelamento real via Stripe não está disponível neste setup,
> sem implementar UI de parcelas até termos certeza.

## Resumo executivo

**Suporta? Tecnicamente sim. Funcionalmente: ainda não há evidência.**

- A conta Stripe (`acct_1TJxRw40eea2RCR1`, country=BR, currency=BRL) **aceita**
  o parâmetro `payment_method_options[card][installments][enabled]=true` sem erro.
- Mas em **20 PaymentIntents recentes**, zero tiveram `available_plans` não-vazio.
  Em **10 charges recentes**, zero tiveram `payment_method_details.card.installments`
  populado.
- O erro retornado quando forçamos um plano fixo é específico do **payment method**
  utilizado (US `pm_card_visa`), não da conta — sugerindo que a conta poderia
  oferecer planos para cartões BR, mas não conseguimos provar empiricamente
  porque a Stripe não expõe payment methods BR pré-tokenizados.
- A conta **não tem** uma capability dedicada de installments. Apenas
  `boleto_payments`, `card_payments` e `transfers` estão ativas.
- Nosso backend **não** está enviando `payment_method_options.card.installments`
  hoje. Apenas grava `installments` como metadata.

**Conclusão prática**: não existe, neste momento, evidência de que a conta
oferecerá planos para cartões reais brasileiros. **Manter UI sanitizada**
(P0-2 já fechado dessa forma) até que a Stripe BR / suporte confirmem.

---

## 1. O que dizem as docs oficiais Stripe

Referência: https://docs.stripe.com/payments/installments e
https://docs.stripe.com/api/payment_intents/object#payment_intent_object-payment_method_options-card-installments

Pontos-chave (relevantes pra nosso caso):

1. **Disponibilidade**: installments é uma feature dos *Brazilian merchants*
   (`account.country = "BR"`). Funciona em moeda `brl`.
2. **Acquirer**: depende da relação acquirer da conta Stripe Brasil. Stripe
   normalmente oferece via Cielo / Stone — varia por contrato.
3. **Card BIN**: planos só são retornados para **cartões emitidos no Brasil**
   (BR BINs). Cartões internacionais (US Visa etc) sempre retornam
   `available_plans: []`.
4. **Tempo de conta**: conforme a doc da Stripe, "Installments are available
   for users in Brazil whose live mode account is at least 30 days old." —
   isso pode bloquear test mode em contas muito novas.
5. **Valor mínimo**: tipicamente R$ 5 por parcela. Cobranças menores não
   geram plans mesmo com BIN BR.
6. **Fluxo correto**:
   1. Backend cria PaymentIntent com
      `payment_method_options[card][installments][enabled]=true`.
   2. Frontend coleta o cartão via Stripe Elements; o BIN do cartão é
      analisado pela Stripe.
   3. Após o customer fornecer o cartão, o frontend pode chamar
      `stripe.retrievePaymentIntent(clientSecret)` ou consumir o resultado
      do `confirmCardPayment` (sem plan setado) pra ler
      `available_plans`.
   4. Frontend mostra opções, customer escolhe, frontend chama
      `confirmCardPayment` de novo (ou `confirmPayment`) com
      `payment_method_options.card.installments.plan = { count, interval, type }`.
   5. Stripe captura parcelado conforme o plano. Plano selecionado fica
      gravado no PaymentIntent e nas charges resultantes.

---

## 2. O que o nosso código faz hoje (auditoria)

| Camada | Estado |
|---|---|
| `StripePaymentProvider.cs:54` | Apenas grava `metadata["installments"] = N` quando `Installments > 1`. **Não** seta `payment_method_options.card.installments.enabled`. |
| `StripeApiClient.CreatePaymentIntentAsync` | Aceita `Amount`, `Currency`, `PaymentMethodTypes`, `Description`, `Customer`, `ApplicationFeeAmount`, `TransferData`, `Metadata`, `AutomaticPaymentMethods`. **Não** suporta `payment_method_options` no shape do form data. |
| `Transaction.Installments` | Persiste o número configurado no link, mas a Stripe nunca recebe pedido de parcelamento. Capture acontece à vista, sempre 1×. |
| Frontend `/pay/[token]/page.tsx` | Não pede installments à Stripe nem em deferred mode (`elements.create({ mode, amount, currency })` não passa `payment_method_options`). Não consulta `available_plans`. Não mostra seletor. (P0-2 removeu o badge "Nx" enganoso do resumo.) |

**Conclusão da auditoria**: nosso fluxo nunca informou installments à Stripe.
Mesmo se a conta suportasse 100%, a Stripe não tinha como saber que o
merchant queria oferecer parcelas. A captura sempre é 1×.

---

## 3. Probes ao vivo (2026-05-07, contra a API Stripe real)

Todos rodados com a Secret Key de test mode da conta `acct_1TJxRw40eea2RCR1`.

### 3.1 Account capabilities

```
account.id         = acct_1TJxRw40eea2RCR1
country            = BR
default_currency   = brl
business_type      = company
charges_enabled    = True
payouts_enabled    = True

capabilities (todas):
  app_distribution    unrequested
  boleto_payments     active
  card_payments       active
  transfers           active

capabilities com substring "install" → NENHUMA
account.requirements (currently_due/eventually_due/past_due) → todas vazias
```

**Sinal**: a conta tem `card_payments` ativo (ok pra cobrança simples), mas
não há nenhuma capability ligada a installments. Em algumas contas Stripe BR
documentadas com installments habilitado, internamente isto é "aprovado"
sem capability separada — acquirer-level. Mas externamente não temos uma
flag clara via API que diga *"installments=on"*.

### 3.2 PaymentIntent + installments enabled (sem PM)

```
POST /v1/payment_intents
  amount=10000, currency=brl
  payment_method_types[0]=card
  payment_method_options[card][installments][enabled]=true

→ 200 OK
   id: pi_3TUJNy40eea2RCR10p6rbM0k
   payment_method_options.card.installments = {
     "available_plans": [],
     "enabled": true,
     "plan": null
   }
```

**Sinal**: Stripe **aceitou** o flag sem reclamar de capability ausente.
`enabled=true` permanece. Mas `available_plans=[]` porque não há PM ainda.

### 3.3 PaymentIntent + installments enabled + PM tokenizado US (`pm_card_visa`) + confirm=true

```
POST /v1/payment_intents
  amount=50000, currency=brl
  payment_method_types[0]=card
  payment_method=pm_card_visa
  payment_method_options[card][installments][enabled]=true
  confirm=true, return_url=https://example.com

→ 200 OK
   status=succeeded
   payment_method_options.card.installments = {
     "available_plans": [],
     "enabled": true,
     "plan": null
   }
```

**Sinal**: mesmo com PM attachado e PI confirmado, `available_plans=[]`. Porém
`pm_card_visa` é card.country=`US` — então é coerente: a doc diz que
plans só são oferecidos pra BR BINs.

### 3.4 PaymentIntent + plan FIXO de 3x (forçando)

```
POST /v1/payment_intents
  amount=30000, currency=brl
  payment_method=pm_card_visa
  payment_method_options[card][installments][plan][count]=3
  payment_method_options[card][installments][plan][interval]=month
  payment_method_options[card][installments][plan][type]=fixed_count
  confirm=true

→ 400 Bad Request
   code: payment_intent_invalid_parameter
   message: "The selected installment plan is not supported for this payment
            method. You can retrieve valid installment plans on the
            PaymentIntent in `payment_method_options[card][installments][plans]`."
```

**Sinal mais informativo**: o erro não diz "not supported for this account"
nem "capability missing". Diz **"not supported for this payment method"** —
ou seja, o problema declarado pela Stripe é o BIN US específico, não a conta.

### 3.5 Token raw com cartão BR (`4000 0076 0000 0002`)

```
POST /v1/tokens
  card[number]=4000007600000002, exp=12/30, cvc=123

→ 400
   "Sending credit card numbers directly to the Stripe API is generally unsafe.
    [...] To enable testing raw card data APIs, see ..."
```

Não conseguimos injetar BIN BR via API server. A única forma de testar com
cartão BR é via Stripe Elements no frontend, com o customer digitando o
número (ou via `stripe.createPaymentMethod` no browser).

### 3.6 Histórico recente

```
20 PaymentIntents recentes:
  - com installments.enabled=true            : 3 (todos os probes acima)
  - com available_plans não-vazio            : 0

10 Charges recentes:
  - com payment_method_details.card.installments populado : 0
```

**Sinal**: zero evidência de parcelamento real funcionando neste setup nos
últimos PIs/charges. Tudo capturado à vista.

### 3.7 Payment Method Configurations

```
GET /v1/payment_method_configurations
→ 1 config "Default", active=true
   card.display_preference: { preference: "on", value: "on" }
   (sem chave installments visível)
```

Não há um toggle "installments" exposto no PM Configurations dessa conta.
O que existe é o flag por PaymentIntent.

---

## 4. Diagnóstico

A questão "por que installments não está disponível" tem **três camadas**:

### Camada A — Backend nosso

Nosso código nunca pediu installments à Stripe. Mesmo se a conta suportasse,
a Stripe captura 1× porque a gente não envia
`payment_method_options.card.installments.enabled=true`. **Esta é uma falha
nossa**, fixável.

### Camada B — Stripe Account / acquirer

A conta:
- aceita `installments.enabled=true` sem erro de "capability missing";
- não tem capability separada `card_installments_payments` listada;
- não retorna `available_plans` em test mode com `pm_card_visa` (esperado,
  BIN US).

Sem testar com BIN BR, **não conseguimos provar nem refutar** se a Stripe
retornaria planos. A doc oficial Stripe diz que installments para BR
exige:
- account ≥ 30 dias em live mode (verificar com Stripe Dashboard);
- BIN BR;
- valor mínimo R$ 5 por parcela;
- moeda BRL.

### Camada C — Test mode vs Live mode

A doc da Stripe sugere que installments BR pode estar **menos populado em
test mode** do que em live mode, justamente porque depende de relação
acquirer real. Empiricamente: 20 PIs em test mode → 0 com plans. Sem
chamar suporte ou rodar live mode com cartão real, não dá pra saber se a
conta **tem** o plano de parcelamento ativado pelo acquirer.

---

## 5. Plano de ação para confirmar

Pra mover de "não há evidência" pra "sim, suporta" ou "não suporta":

1. **Stripe Dashboard** → Settings → Payments → Payment methods.
   Verificar se há uma seção "Installments" e o status dela. Print/screenshot.
2. **Stripe Dashboard** → Settings → Account → idade da conta em live mode.
   Confirmar se está há ≥ 30 dias.
3. **Suporte Stripe BR**: abrir ticket perguntando literalmente:
   > Account: acct_1TJxRw40eea2RCR1
   > 1. Esta conta suporta `payment_method_options.card.installments` para
   >    BRL/cartões emitidos no Brasil hoje?
   > 2. Se sim, em test mode também ou apenas live mode?
   > 3. Quais BINs/test cards retornam `available_plans` em test mode?
   > 4. Existe algo a configurar no Dashboard ou contratual antes?
4. **Teste manual** (depois do ticket): se Stripe confirmar suporte, modificar
   o backend pra enviar `installments.enabled=true`, modificar o frontend
   pra ler `available_plans` após o cartão ser fornecido, e testar com
   um cartão BR real (live mode) por R$ 100+ e verificar se planos aparecem.

---

## 6. O que NÃO fazer enquanto isso

- **Não implementar** seletor de parcelas no checkout.
- **Não voltar** o badge "Nx" no resumo.
- **Não habilitar** o input de Parcelas no admin payment-link form.
- **Não enviar** `installments.enabled=true` no backend ainda — sem confirmar
  o suporte real, o flag é no-op e suja métricas.

---

## 7. O que fazer quando confirmado

Sequência mínima para reativar (após resposta positiva da Stripe):

1. `StripeApiClient.CreatePaymentIntentAsync` aceita `installments_enabled` e o
   serializa como `payment_method_options[card][installments][enabled]=true`.
2. `StripePaymentProvider` passa `installments_enabled = true` para PIs de
   crédito (`PaymentType.CREDIT_CARD`) em BRL, condicional ao
   `link.Installments > 1`.
3. Frontend `/pay/[token]/page.tsx`:
   - Após `elements.submit()` no card flow, chama
     `stripe.retrievePaymentIntent(clientSecret)` para ler
     `payment_method_options.card.installments.available_plans`.
   - Se `available_plans.length > 0` **e** `link.installments > 1`, mostra
     seletor 1× a `min(link.installments, max_count_disponível)`.
   - User escolhe → `stripe.confirmCardPayment(clientSecret, { payment_method, payment_method_options: { card: { installments: { plan } } } })`.
   - Se `available_plans.length === 0` → não mostra seletor (cobrança
     à vista), igual ao comportamento atual.
4. Backend persiste o plano efetivamente confirmado (count) em
   `Transaction.Installments` — não o link's advertised maximum. Receipt e
   relatórios refletem o plano real.
5. Reativar input "Parcelas" no admin form e badge no resumo público
   apenas quando o backend confirmar plan na captura.

---

## 8. TL;DR para o Codex

> Stripe BR installments é tecnicamente possível e a API aceita o flag, mas
> não temos evidência de que esta conta retorna `available_plans` para cartões
> reais. Nosso backend nem está pedindo installments. Antes de implementar
> qualquer UI, precisamos:
> (a) confirmação explícita do suporte Stripe / Dashboard;
> (b) teste com BIN BR real (preferencialmente live mode).
>
> Até lá, **manter UI sanitizada** (estado atual de P0-2). O risco de
> implementar e descobrir depois que captura é à vista é o cenário que
> Codex pediu para evitar.
