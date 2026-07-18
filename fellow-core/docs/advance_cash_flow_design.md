# Modelo Híbrido — Reserva de caixa e exposição financeira

## Problema

Quando o seller opta por antecipação automática (`AutoAdvanceSettlement=true` ou
`AdvanceOptIn=true` na TX), a plataforma libera o `NetAmount - AdvanceFee` em
**D+30**, antes da Stripe Connect terminar de liberar as N parcelas no provider.
O gap de caixa real:

```
Customer paga 6x R$ 100 (gross R$ 600, net R$ 561 após PSP fee)
  Plataforma cobra advance fee 3.5% sobre net = R$ 19.64
  Seller recebe R$ 541.36 em D+30 (WALLET → saca via Stripe payout)

Stripe paga a plataforma 1/6 do net a cada mês:
  D+30:  R$ 93.50   ↘
  D+60:  R$ 93.50    ╲
  D+90:  R$ 93.50     ╲ Stripe libera gradualmente
  D+120: R$ 93.50     ╱
  D+150: R$ 93.50    ╱
  D+180: R$ 93.50   ↗ Total = R$ 561 (= o que seller "deveria" ter recebido)

  Plataforma absorve advance fee (R$ 19.64) → Stripe.netAmount > seller.netAmount
```

**Gap de caixa em D+30**: R$ 541.36 (pagar seller) − R$ 93.50 (recebido da Stripe)
= **R$ 447.86 antecipado do caixa próprio da plataforma**.

Esse gap se acumula em volume:

| TXs ADVANCE/mês | Net médio | Cash advanced em D+30 | Cash exposure peak (~D+90) |
|---|---|---|---|
| 100 | R$ 500 | R$ 41.785 | R$ 167.140 |
| 1.000 | R$ 500 | R$ 417.850 | R$ 1.671.400 |
| 10.000 | R$ 500 | R$ 4.178.500 | R$ 16.714.000 |

Exposure peak é (~) `monthly_volume × 4` (4 parcelas ainda não recebidas em qualquer
momento). Sem cobertura de caixa, a plataforma quebra antes da Stripe liberar.

## Onde isso aparece hoje no código

- `ChargeAdvanceFeeAsync` em `LedgerService`: cobra o fee, vira PLATFORM_MARGIN.
  **Não checa caixa.**
- `WebhooksService.HandleStripeEventAsync` captura: gera 1 parcela D+30
  com `NetAmount - AdvanceFee`. **Não rejeita por falta de caixa.**
- `SettlementService.ProcessDailySettlementsAsync`: move FUTURE_RECEIVABLES → WALLET
  quando a installment madura. **Não checa se a Stripe já liberou de fato.**
- `WithdrawOrchestrator.ExecuteAsync`: tenta sacar via Stripe payout. **Falha em
  runtime se Stripe.available_balance < amount.** O ledger interno fica
  divergente até o seller tentar de novo / reconciliation rodar.

## Risco operacional sem mitigação

| Cenário | Sintoma | Severidade |
|---|---|---|
| Caixa próprio < antecipação acumulada | Saques Stripe falham `insufficient_funds`. Sellers veem balance no portal mas não conseguem retirar. | 🔴 Crítico |
| Chargeback em volume após antecipação | Reversão de fee + débito gross do seller. Se seller já sacou, fica WALLET negativo / fraud loss. | 🔴 Crítico |
| Anti-fraude lento | Seller fraudulento antecipa, saca, some. Plataforma fica com a TX disputada e sem o fee. | 🟠 Alto |
| Pico de volume sem alocação prévia de caixa | Ledger libera mas saque trava. Suporte explode. | 🟠 Alto |

## Opções de design

### A) Sem reserva — modo "ativista" (ATUAL)

Toda TX elegível antecipa. Plataforma cobre o gap com caixa próprio sem checagem.

- ✅ Onboarding mais simples; produto agressivo
- ❌ Quebra sem aviso quando volume ultrapassa caixa
- ❌ Risco de fraude unbounded

### B) Limite por seller — modo "linha de crédito"

Cada seller tem um `AdvanceCreditLimit` (decimal). Plataforma não antecipa além desse
teto **somando saldo já antecipado e não liquidado pela Stripe** (= advanced -
already_received_from_stripe).

Adiciona:

```csharp
// Seller
public decimal AdvanceCreditLimit { get; private set; } = 0M;
public decimal AdvanceExposureCurrent { get; private set; } = 0M;
// método AddAdvanceExposure(amount), ReduceAdvanceExposure(amount)
```

Hook na captura (antes de `MarkAsAdvanceSettlement`):
- Se `seller.AdvanceExposureCurrent + netAdvancedToSeller > seller.AdvanceCreditLimit`
  → fallback INSTALLMENT (log warning, métrica `advance_limit_exhausted_total`)

Reduce na liquidação real (settlement processor): quando a parcela mensal do Stripe
chega no platform balance, decrementa `seller.AdvanceExposureCurrent` proporcionalmente.

- ✅ Cap individual por seller, previne fraude unbounded
- ✅ Tooling: portal admin define limite (e.g. baseado em histórico/scoring)
- ⚠️ Requer tracking explícito de "stripe payouts recebidos pela plataforma" — não trivial
- ⚠️ Não previne caixa total exhaustion (1000 sellers no limite × R$ X cada > caixa)

### C) Limite global da plataforma — modo "reserve pool"

`TenantConfig.PlatformAdvanceReserveCents` (long): caixa disponível pra antecipação.
Toda captura ADVANCE decrementa; toda liquidação Stripe incrementa.

```csharp
// TenantConfig
public long PlatformAdvanceReserveCents { get; private set; }
```

Hook na captura:
- `if (tenantConfig.PlatformAdvanceReserveCents < netAdvancedToSeller * 100) →
   fallback INSTALLMENT + log critical + métrica spike`

Adiciona ledger account novo: `PLATFORM_ADVANCE_RESERVE` (Type=11). Toda captura
ADVANCE: debita RESERVE pelo netAdvancedToSeller. Toda liquidação Stripe da TX
ADVANCE: credita RESERVE de volta.

- ✅ Hard cap global — plataforma nunca opera no negativo
- ✅ Reconciliation visível: saldo RESERVE = caixa real disponível
- ⚠️ Mais código; precisa job pra creditar de volta quando Stripe paga
- ⚠️ Não substitui anti-fraude per seller (B é complementar)

### D) Combinação B+C (recomendada)

- Limite global da plataforma (proteção operacional)
- + Limite per-seller (proteção anti-fraude)
- + Métricas / alertas: `advance_reserve_remaining_cents`, `advance_reserve_pct_used`

Captura ADVANCE só rola se **ambas** as checagens passam. Caso contrário, fallback
INSTALLMENT silencioso (UX: seller recebe parcelado, sem falha).

## Implementação proposta (D)

### Domain

```csharp
// Seller
public decimal AdvanceCreditLimit { get; private set; }       // teto per seller
public decimal AdvanceExposureCurrent { get; private set; }   // ainda não liquidado
public Result IncreaseExposure(decimal amount);  // chama na captura ADVANCE
public Result DecreaseExposure(decimal amount);  // chama no settlement Stripe

// TenantConfig
public long PlatformAdvanceReserveCents { get; private set; }
public Result DebitAdvanceReserve(long cents);   // captura ADVANCE
public Result CreditAdvanceReserve(long cents);  // settlement Stripe / reversal
```

### Capture decision

```csharp
bool eligibleForAdvance = transaction.PaymentType == CREDIT_CARD
    && seller != null
    && effectiveAdvance
    && seller.CanIncreaseExposure(netAdvancedToSeller)
    && tenantConfig.HasReserveFor(netAdvancedToSellerCents);

if (!eligibleForAdvance && effectiveAdvance) {
    logger.LogWarning("[ADVANCE_THROTTLED] TX {Id} fallback pra INSTALLMENT — limit exhausted");
    appMetrics.RecordAdvanceThrottled(reason);
}
```

### Stripe settlement reconciliation

Job novo `AdvanceSettlementReconciler` (Hangfire diário):
- Lê balance transactions da Stripe (já temos `ListBalanceTransactionsAsync`)
- Pra cada `available_funds` que entrou: identifica a TX original
- Se a TX era ADVANCE: credita reserve + decrementa seller.AdvanceExposureCurrent

### Métricas

```
fellowcore_advance_reserve_remaining_cents     (gauge)
fellowcore_advance_reserve_pct_used            (gauge)
fellowcore_advance_throttled_total{reason=...} (counter)
fellowcore_seller_exposure_current_cents       (histogram, per seller)
```

### Alertas Prometheus

```yaml
# Reserve esgotando
- alert: AdvanceReserveLow
  expr: fellowcore_advance_reserve_pct_used > 0.80
  for: 10m
  annotations:
    severity: warning

# Reserve negativo (não deveria acontecer com checagem na captura)
- alert: AdvanceReserveNegative
  expr: fellowcore_advance_reserve_remaining_cents < 0
  for: 1m
  annotations:
    severity: critical
```

## Decisões pendentes (precisam input do user/produto)

1. **Valor inicial da reserve por tenant** — começa em 0 (todas TXs fallback) ou em N? Operação seed?
2. **Limite default per seller** — 0 (opt-in admin), ou base no scoring/histórico?
3. **Quem cobre o caixa de fato** — conta operacional da plataforma, linha bancária, ou capital próprio?
4. **Política de fallback** — silencioso (UX preservada) ou erro 422 (transparente pro seller)?
5. **Anti-fraude per-TX** — alguma regra além do limite (geo, valor, novo seller)?

## Resumo

- Estado atual: **opção A** (sem reserve) — funciona mas com risco operacional crescente com volume
- Recomendação: **opção D** (B + C combinados)
- Esforço estimado: ~3-5 dias dev + métricas/alertas
- Bloqueador: decisões 1-5 acima precisam ser feitas pelo time de produto/finance

Tech debt acompanhada do código:
- `LedgerService.ChargeAdvanceFeeAsync` — sem checagem de reserve
- `WebhooksService.HandleStripeEventAsync` captura hook — sem fallback throttled
- Não existe `AdvanceSettlementReconciler` ainda
- `SellerService` / `TenantConfigService` precisam dos toggles administrativos

---

## Implementação concluída (2026-05-15)

Opção **D** entregue integralmente, com extras de anti-fraude e observabilidade.
Tudo no commit `feat: Hybrid Settlement model + accumulated portal/multi-rail work`.

### Schema (8 migrations aplicadas)

| Migration | Tabela/Coluna |
|-----------|---------------|
| `AddHybridSettlementFields` | `Transaction.SettlementMode`, `AdvanceFeeAmount` |
| `AddTransactionAdvanceOptIn` | `Transaction.AdvanceOptIn` (nullable, per-TX override) |
| `AddAdvanceReserveAndSellerLimit` | `TenantConfig.PlatformAdvanceReserveCents`, `Seller.AdvanceCreditLimit`, `AdvanceExposureCurrent`, `AutoAdvanceSettlement` |
| `AddAdvanceRecoveredCount` | `Transaction.AdvanceRecoveredInstallmentCount` (Stripe reconciler) |
| `AddPaymentLinkAdvanceOptIn` | `PaymentLink.AdvanceOptIn` (force_on/force_off/null=inherit) |
| `AddSellerRiskProfile` | nova entidade `SellerRiskProfile` (chargeback rate, captured count, risk score) |
| `AddStripeReconcilerFields` | `TenantConfig.LastStripeAdvanceReconcileAt` (cursor) + `Transaction.StripeChargeId` |
| `AddSellerExposureAlertThreshold` | `Seller.AdvanceExposureAlertThresholdCents` (per-seller override de alerta) |

### Capture flow (TransactionService)

```
1. Resolve effectiveAdvance = TX.AdvanceOptIn ?? Link.AdvanceOptIn ?? Seller.AutoAdvanceSettlement
2. AdvanceRiskEvaluator.Evaluate(seller, profile, request) → APPROVED | DENIED(reason)
3. Reserve check: TenantConfig.HasReserveFor(netAdvancedCents)
4. Seller check: seller.CanIncreaseExposure(netAdvancedToSeller)
5. Se 2 ✅ 3 ✅ 4 ✅ → MarkAsAdvanceSettlement + DebitReserve + IncreaseExposure
   Caso contrário → silencioso fallback INSTALLMENT + counter advance_throttled{reason}
```

### Anti-fraude (AdvanceRiskEvaluator)

Configurado via `AdvanceRisk:*`:
- `MinSellerAgeDays` (default 30) — bloqueia sellers novíssimos
- `MaxRiskScore` (default 70/100) — score composto do SellerRiskProfile
- `MaxChargebackRate` (default 0.01) — 1% nas últimas 90d
- `MinCapturedSampleSize` (default 10) — chargeback rate ignorado se amostra pequena

`SellerRiskProfileRefreshProcessor` (Hangfire diário 03:00 UTC) computa o perfil:
- Captured count/volume 90d
- Chargebacks ganhos/perdidos 90d
- Risk score (1-100) baseado em chargeback rate + idade + volume

### Reservas + reconciliação

**3 caminhos pra creditar reserve / decrementar exposure**:

1. **AdvanceSettlementReconciler** (Hangfire diário, sempre on)
   - Time-proxy: TX ADVANCE D+30 dias → assume liquidação Stripe
   - Conservador, não chama API externa
   - Default fallback se `AdvanceReconciler:UseStripe=false`

2. **StripeAdvanceReconciler** (Hangfire horário, opt-in via `AdvanceReconciler:UseStripe`)
   - Cursor `TenantConfig.LastStripeAdvanceReconcileAt`
   - `IStripeApiClient.ListBalanceTransactionsAsync(createdGte=cursor, type=charge)`
   - Por charge: `fraction = bt.amount / TX.NetAmount` → `parcelasNovas = floor(fraction × Installments)`
   - Idempotente (cursor + `AdvanceRecoveredInstallmentCount` monotônico)
   - **Ativo em Development**, default `false` em prod (vira `true` quando confiável em volume)

3. **Refund/Dispute** (síncrono no webhook)
   - `WebhooksService.HandleStripeRefundAsync` + `HandleStripeDisputeAsync`
   - Reverte advance fee, credita reserve, decrementa exposure
   - Logado como `LogCritical` se falhar (seller cobrado em duplicidade = ressarcir manualmente)

### Endpoints (api-key, audit-logged)

```
GET    /api/v1/advance-reserve                          → saldo atual + R$
POST   /api/v1/advance-reserve/topup                    → admin top-up { amountCents }
GET    /api/v1/sellers/{id}/advance-limit               → limit + exposure + headroom
PATCH  /api/v1/sellers/{id}/advance-limit               → { advanceCreditLimit }
PATCH  /api/v1/sellers/{id}/advance-alert-threshold     → { thresholdCents } (null = volta pro default)
```

Smoke validado em `2026-05-15` — todos os 4 endpoints respondem 200 e persistem o estado.

### PaymentLink override

`POST /api/v1/payment-links` e `PATCH /api/v1/payment-links/{id}` aceitam `advanceOptIn`:
- `true` → força ADVANCE no consumo (mesmo que seller seja INSTALLMENT por default)
- `false` → força INSTALLMENT
- `null` → herda do seller (`AutoAdvanceSettlement`)

UI: dropdown 3-opções (`inherit | force_on | force_off`) só visível quando `PaymentType=CREDIT_CARD`,
tanto no create quanto no edit.

### Métricas (FellowCoreMetrics.cs)

```
fellowcore_advance_reserve_remaining_cents              (gauge)
fellowcore_advance_reserve_capacity_cents               (gauge, peak histórico)
fellowcore_seller_exposure_current_cents{seller_id}     (gauge, só sellers >0)
fellowcore_seller_exposure_threshold_cents{seller_id}   (gauge, override ou default)
fellowcore_advance_throttled_total{reason}              (counter)
fellowcore_advance_recovered_installments_total         (counter)
fellowcore_advance_reversal_total{kind}                 (counter: refund | dispute_lost)
```

### Alertas (infra/alerts/fellowcore-alerts.yml)

`AdvanceSellerExposureHigh` agora compara per-series:
```yaml
expr: fellowcore_seller_exposure_current_cents
        > on(seller_id) fellowcore_seller_exposure_threshold_cents
for: 1h
```

Permite override custom per seller (sellers legítimos high-volume não disparam o
default global de R$ 1.000 à toa).

### Configuração

```jsonc
// appsettings.json (default seguro pra prod)
"AdvanceRisk": {
  "MinSellerAgeDays": 30,
  "MaxRiskScore": 70,
  "MaxChargebackRate": 0.01,
  "MinCapturedSampleSize": 10
},
"AdvanceReconciler": {
  "UseStripe": false  // time-proxy é o default
},
"AdvanceAlert": {
  "DefaultSellerExposureThresholdCents": 100000  // R$ 1.000
}

// appsettings.Development.json
"AdvanceReconciler": { "UseStripe": true }
```

### Testes

| Suite | Tests Hybrid-related |
|-------|----------------------|
| Domain | `AdvanceReserveTests`, `TransactionInstallmentTests` |
| Application | `AdvanceRiskEvaluatorTests`, `SellerRiskProfileRefreshProcessorTests` (6), `StripeAdvanceReconcilerTests` (5), `RefundCalculatorTests` |
| Integration | refund/dispute reversal coberto via `AuditGapTests` |

Suite total pós-implementação: **1.404 passed, 16 skipped, 0 failed** (Domain 352 + Application 790 + Integration 262).

### Decisões originalmente pendentes — resolvidas

1. **Valor inicial da reserve** → começa em 0; top-up via endpoint `POST /api/v1/advance-reserve/topup`
2. **Limit default per seller** → 0 (opt-in admin via `PATCH /advance-limit`)
3. **Quem cobre o caixa** → tenant declara explícito via top-up (espelha aporte real)
4. **Política de fallback** → silenciosa (UX preservada); counter `advance_throttled_total` rastreia
5. **Anti-fraude per-TX** → `AdvanceRiskEvaluator` (idade, risk score, chargeback rate)

### Tech debt aceito

- StripeReconciler usa cursor por timestamp (sem paginação completa de balance_transactions). OK pra MVP. Reavaliar se >100 charges/hora.
- Time-proxy de D+30 dias é genérico — Stripe varia o liberation schedule por país/produto. Stripe reconciler é a fonte da verdade quando habilitado.
