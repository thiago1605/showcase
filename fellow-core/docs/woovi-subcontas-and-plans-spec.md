# Modelo Woovi (Subcontas + Split) e Planos Comerciais — Spec

Documento de referência consolidado a partir da decisão de produto/operações em
2026-05-15. Confirmações:

- **Suporte Woovi**: Morgana Miller via chat em `app.woovi.com` confirmou o
  modelo de subconta nominal + split como produto oficial para gateways.
- **Doc oficial**: `developers.openpix.com.br/docs/subaccount/split-sub-account-usecases`.

Stack: C# / .NET 10.

---

## 1. Modelo Woovi: Subcontas Nominais + Split

### 1.1 Decisão arquitetural

Operação no modelo oficial Woovi para gateways: **subconta nominal por seller +
split automático nas cobranças**.

### 1.2 Fluxo

```
1. Seller se cadastra → criamos subconta na Woovi (POST /api/v1/subaccount)
2. Seller cria produto no nosso painel
3. Comprador paga → criamos charge com splits[] apontando para a subconta do seller
4. Woovi divide automaticamente: valor do seller vai virtualmente para a subconta
   dele; nossa comissão fica na conta principal.
5. Seller solicita saque → chamamos POST /api/v1/subaccount/{pixKey}/withdraw
```

### 1.3 Endpoints Woovi críticos

**Criar subconta:**

```http
POST /api/v1/subaccount
{
  "name": "Nome do Seller",
  "pixKey": "chave-pix-do-seller",
  "type": "CPF" | "CNPJ" | "EMAIL" | "PHONE" | "EVP"
}
```

Sem KYC obrigatório no plano contratado. Apenas `name` + `pixKey`.

**Criar cobrança com split:**

```http
POST /api/v1/charge
{
  "correlationID": "uuid-do-pedido",
  "value": 19700,
  "comment": "...",
  "splits": [
    {
      "pixKey": "chave-pix-da-subconta-do-seller",
      "value": 17215,
      "splitType": "SPLIT_SUB_ACCOUNT"
    }
  ]
}
```

`charge.value − splits[0].value` é a comissão da plataforma e fica na conta
principal. **Split é transação virtual** — só sai dinheiro real no saque.

**Sacar da subconta:**

```http
POST /api/v1/subaccount/{pixKey}/withdraw
```

### 1.4 Features a ativar no painel Woovi

Solicitar à Morgana (suporte):

- `Subconta`
- `Split`

Sem ativação, endpoints retornam erro de feature não habilitada → adapter
deve tratar especificamente.

### 1.5 Limites operacionais confirmados pelo suporte

| Operação | Limite |
|---|---|
| Pix IN por transação | **R$ 800** |
| Pix OUT total diário (8h–20:59) | **R$ 48.800** |
| Pix OUT total noturno (21h–7:59) e fins de semana | **R$ 10.000** por período |
| Pix OUT por transação individual | Sem limite explícito (até saldo) |

**Custo Woovi**: R$ 0,85 fixo por Pix processado (plano fixo).

### 1.6 Tratamento de serialização

Woovi usa `correlationID` com `ID` maiúsculo. Usar `[JsonPropertyName("correlationID")]`
ou `JsonNamingPolicy` customizado.

---

## 2. Modelo de Saques

### 2.1 Regras

| Item | Regra |
|---|---|
| Saque mínimo | **R$ 50** |
| Tarifa saque < R$ 500 | **R$ 1,00** (repassada ao seller) |
| Tarifa saque ≥ R$ 500 | Sem tarifa |
| Saque D+1 (próximo dia útil) | **Grátis** |
| Saque D+0 (antecipação imediata) | **Taxa de 1%** sobre o valor |
| Limite individual inicial por saque | **R$ 5.000** |

### 2.2 Lógica esperada (pseudocódigo)

```csharp
if (amount < 50m) throw new MinimumWithdrawException(amount);
if (amount > seller.MaxWithdrawPerRequest)
    throw new IndividualWithdrawLimitException(amount, seller.MaxWithdrawPerRequest);

var balance = await _wooviAdapter.GetSubaccountBalanceAsync(seller.PixKey, ct);
if (amount > balance.Available) throw new InsufficientBalanceException();

var todayTotal = await _withdrawRepo.GetTodayTotalAsync(ct);
if (todayTotal + amount > 48_800m) return await QueueForNextDayAsync(...);

decimal fees = 0m;
if (amount < 500m) fees += 1m;
if (type == WithdrawType.D0) fees += amount * 0.01m;

var netAmount = amount - fees;
return type == WithdrawType.D0
    ? await _wooviAdapter.WithdrawFromSubaccountAsync(seller.PixKey, netAmount, ct)
    : await _withdrawRepo.ScheduleAsync(sellerId, netAmount, NextBusinessDay(), ct);
```

`decimal` em todo lugar. Converter pra centavos `int`/`long` só na chamada Woovi.

---

## 3. Planos Comerciais (4)

### 3.1 Infoprodutor (sem mensalidade)

Cursos, ebooks, mentorias, comunidades, vendas avulsas.

| Método | % | Fixo |
|---|---|---|
| Pix | 2,99% | R$ 0,49 |
| Cartão crédito 1x | 5,99% | R$ 0,49 |
| Cartão débito | 4,49% | R$ 0,49 |
| Apple Pay / Google Pay | 5,99% | R$ 0,49 |
| Boleto | 4,99% | R$ 1,49 |

Mensalidade: R$ 0.

### 3.2 SaaS Starter (R$ 79/mês)

SaaS com até 500 clientes ativos.

| Método | % | Fixo |
|---|---|---|
| Pix | 1,49% | R$ 0,29 |
| Cartão crédito 1x | 4,99% | R$ 0,39 |
| Cartão débito | 3,99% | R$ 0,39 |
| Apple Pay / Google Pay | 4,99% | R$ 0,39 |
| Boleto | 3,99% | R$ 0,99 |

### 3.3 SaaS Growth (R$ 199/mês)

SaaS com 500+ clientes ativos.

| Método | % | Fixo |
|---|---|---|
| Pix | 0,99% | R$ 0,19 |
| Cartão crédito 1x | 4,49% | R$ 0,29 |
| Cartão débito | 3,49% | R$ 0,29 |
| Apple Pay / Google Pay | 4,49% | R$ 0,29 |
| Boleto | 3,49% | R$ 0,79 |

### 3.4 Enterprise

Volume > R$ 500k/mês. Taxas negociadas individualmente, SLA dedicado.
Implementar com valores nulos/customizáveis pelo admin.

### 3.5 Modelagem sugerida

```csharp
public sealed class Plan
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public PlanType Type { get; private set; }
    public decimal MonthlyFee { get; private set; }
    public Dictionary<PaymentMethod, FeeConfiguration> Fees { get; private set; }
}

public enum PlanType { Infoproduct, SaasStarter, SaasGrowth, Enterprise }
public enum PaymentMethod { Pix, CreditCard, DebitCard, ApplePay, GooglePay, Boleto }
public sealed record FeeConfiguration(decimal Percentage, decimal Fixed);
```

---

## 4. Anti-migração indevida

### 4.1 No cadastro

```csharp
public enum BusinessType { Infoproduct, Saas, Marketplace, Other }
```

Filtrar quais planos são apresentados por `BusinessType`.

### 4.2 Validação de migração

Plano SaaS Starter/Growth exige **≥ 30% das transações dos últimos 30 dias
sendo recorrentes**. Caso contrário, bloqueia migração com mensagem:

> "Esse plano é otimizado para cobranças recorrentes. Para vendas avulsas, o
> plano Infoprodutor é mais econômico. Quer comparar?"

### 4.3 Simulador comparativo

Endpoint recebe volume mensal estimado + mix de métodos + % recorrência →
retorna plano mais econômico + comparativo de todos.

### 4.4 Pontos de equilíbrio (referência)

- **SaaS Starter (R$ 79)** compensa Infoprodutor a partir de ~R$ 5.300/mês em Pix
- **SaaS Growth (R$ 199)** compensa Starter a partir de ~R$ 40.000/mês em Pix

---

## 5. Posicionamento e limites

### 5.1 Posicionamento oficial

> "A plataforma mais barata do Brasil para vendas até R$ 800 e assinaturas
> recorrentes."

### 5.2 Limite R$ 800 — comunicação

- **Home/landing**: NÃO mencionar (foco em "Pix mais barato")
- **Página de planos**: rodapé com nota explicativa
- **Onboarding seller**: explicar claramente antes de cadastrar
- **Checkout**: se valor > R$ 800, desabilitar Pix e mostrar:

> "Para valores acima de R$ 800, utilize cartão de crédito ou boleto."

Domain invariante:

```csharp
public sealed class PixChargeSpecification
{
    public const decimal MaxAmountPerTransaction = 800m;
    public bool IsSatisfiedBy(Charge charge)
        => charge.PaymentMethod != PaymentMethod.Pix
        || charge.Amount <= MaxAmountPerTransaction;
}
```

### 5.3 Sem parcelamento — comunicação

Cartão crédito apenas 1x. Posicionar recorrência como **alternativa**:

> "Em vez de parcelar em 12x, ofereça assinatura mensal por 12 meses com
> renovação automática no cartão."

Campos no checkout:
- "Vender como pagamento único"
- "Vender como assinatura recorrente (alternativa de parcelamento)"

---

## 6. Entidades a verificar/ajustar

### `Seller`
- [ ] `BusinessType` (enum)
- [ ] `PlanId` (FK)
- [ ] `WooviSubaccountId` ou `WooviPixKey`
- [ ] `MaxWithdrawPerRequest` (default 5000m)

### `Plan`
- [ ] `Type` (PlanType enum)
- [ ] `MonthlyFee` (decimal)
- [ ] Taxas por método

### `Charge`
- [ ] `PlatformFee` calculado conforme plano
- [ ] `SellerAmount`
- [ ] `CorrelationId`
- [ ] `ProviderChargeId`

### `Withdraw`
- [ ] `Type` (D0 / D1)
- [ ] `Fee` (tarifa + antecipação)
- [ ] `NetAmount`
- [ ] `Status` (Pending / Processing / Completed / Failed / Queued)

### `DailyWithdrawCounter` (novo)
- [ ] Cache (`IDistributedCache`/Redis) ou tabela
- [ ] Reset 00:00
- [ ] Valida limite Woovi R$ 48.800

---

## 7. Webhooks Woovi

| Evento | Ação |
|---|---|
| `OPENPIX:CHARGE_COMPLETED` | Marcar charge paga, atualizar saldo virtual, notificar |
| `OPENPIX:CHARGE_EXPIRED` | Marcar expirada |
| `OPENPIX:TRANSACTION_RECEIVED` | Backup |

Idempotência via `event.id` Woovi:

```csharp
public sealed class WebhookEvent
{
    public Guid Id { get; private set; }
    public string Provider { get; private set; }
    public string EventId { get; private set; }
    public string EventType { get; private set; }
    public string PayloadJson { get; private set; }
    public bool Processed { get; private set; }
    public DateTime ReceivedAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
}
```

Unique index em `(Provider, EventId)`.

---

## 8. Cálculo de comissão (central)

```csharp
public interface IPlatformFeeCalculator
{
    PlatformFeeResult Calculate(decimal amount, PaymentMethod method, Plan plan);
}

public sealed record PlatformFeeResult(decimal PlatformFee, decimal SellerAmount);

public sealed class PlatformFeeCalculator : IPlatformFeeCalculator
{
    public PlatformFeeResult Calculate(decimal amount, PaymentMethod method, Plan plan)
    {
        var config = plan.Fees[method];
        var platformFee = Math.Round(amount * config.Percentage / 100m + config.Fixed, 2);
        var sellerAmount = amount - platformFee;
        return new PlatformFeeResult(platformFee, sellerAmount);
    }
}
```

DI: `AddScoped<IPlatformFeeCalculator, PlatformFeeCalculator>()`.

Usado em:
1. Criação da charge (define split)
2. Painel seller (preview)
3. Relatórios e simuladores

---

## 9. Prioridades

### Alta
- Adapter Woovi com `splitType: "SPLIT_SUB_ACCOUNT"`
- Criação de subconta no onboarding
- Validação limite R$ 800 Pix IN
- Controle diário saques R$ 48.800
- 4 planos no banco
- `IPlatformFeeCalculator`
- D+0 (taxa 1%) e D+1 (grátis)

### Média
- Filtro `BusinessType`
- Validação anti-migração
- Bloqueio Pix > R$ 800 no checkout
- Comunicação limites no onboarding

### Baixa
- Simulador planos
- Cálculo ponto de equilíbrio
- Fila FIFO saques > R$ 48.800

---

## 10. Convenções

- C# 13 / .NET 10
- Clean Architecture (ou padrão já existente)
- Exceptions customizadas pra erros de domínio
- `ILogger<T>` / Serilog
- Async sempre com `CancellationToken`
- `IHttpClientFactory` + Polly
- `System.Text.Json` (atenção `correlationID`)
- `decimal` pra dinheiro (nunca `double`/`float`)
- Idempotência via `CorrelationId` / `EventId`
- `record` pra DTOs/VOs, `class` pra entidades
- Nullable reference types habilitados

---

## 11. Decisões pendentes (não implementar)

- Antifraude próprio vs. terceiro (Konduto, ClearSale)
- Stripe Connect vs. transferências manuais
- Retenção D+30 inicial para sellers novos

---

## Origem

Decisão registrada por Thiago Reis em conversa Claude Code, 2026-05-15.
Confirmação Woovi via Morgana Miller (suporte oficial).

---

## 12. Status de implementação (2026-05-15)

Implementação completa das fases 1-5. Suite total: **1.150 testes verdes**
(Domain 352 + Application 798/814, 16 skipped).

### Fase 1 — Domain + planos (entregue)

| Item | Arquivo |
|------|---------|
| Enums `PlanType`/`BusinessType`/`WithdrawType` | `src/FellowCore.Domain/Enums/FellowPayEnums.cs` |
| `PricingPlan.Type` + `WalletPercent/Fixed` + `BoletoPercent` | `src/FellowCore.Domain/Entities/PricingPlan.cs` |
| `Seller.BusinessType` + `MaxWithdrawPerRequest` | `src/FellowCore.Domain/Entities/Seller.cs` |
| `Payout.Type` + `ScheduledFor` | `src/FellowCore.Domain/Entities/Payout.cs` |
| `InboundWebhookEvent` (idempotência inbound) | `src/FellowCore.Domain/Entities/InboundWebhookEvent.cs` |
| `PixLimits` (R$ 800 IN / R$ 48.800 OUT) + `WithdrawRules` | `src/FellowCore.Domain/ValueObjects/PixLimits.cs` |
| 5 exceptions: `PixAmountLimitExceededException`, `MinimumWithdrawException`, `IndividualWithdrawLimitException`, `InsufficientBalanceException`, `WooviFeatureDisabledException` | `src/FellowCore.Application/Exceptions/AppException.cs` |
| `PlanEligibilityService` reescrito (BusinessType + recorrência + volume) | `src/FellowCore.Application/Modules/Pricing/Services/PlanEligibilityService.cs` |
| `MonthlyPlanBillingProcessor` genérico (substitui `ScalaBillingProcessor`) | `src/FellowCore.Infrastructure/Workers/Processors/MonthlyPlanBillingProcessor.cs` |
| Migration `WooviPlansAndSubcontas` (hard cut) | `src/FellowCore.Infrastructure/Database/Migrations/20260515205042_*.cs` |
| Seed dos 4 planos novos | `src/FellowCore.Infrastructure/Database/Seeding/DatabaseSeeder.cs` |

### Fase 2 — Woovi splits + auto-subconta (entregue)

| Item | Arquivo |
|------|---------|
| `OpenPixChargeRequest.Splits` + `OpenPixChargeSplit (SPLIT_SUB_ACCOUNT)` | `src/FellowCore.Application/Modules/Transactions/Providers/OpenPix/Models/OpenPixModels.cs` |
| `OpenPixPaymentProvider` envia splits + fallback legacy | `src/FellowCore.Application/Modules/Transactions/Providers/OpenPix/OpenPixPaymentProvider.cs` |
| Hook auto-create subconta no `SellerService.CreateAsync` | `src/FellowCore.Application/Modules/Sellers/Services/SellerService.cs` |
| `IInboundWebhookEventRepository` + impl Postgres | `src/FellowCore.Domain/Interfaces/IInboundWebhookEventRepository.cs`, `src/FellowCore.Infrastructure/Repositories/InboundWebhookEventRepository.cs` |
| Dedup webhook OpenPix em `WebhooksService.HandleOpenPixEventAsync` | `src/FellowCore.Application/Modules/Webhooks/Services/WebhooksService.cs` |

### Fase 3 — Withdraw flow (entregue)

| Item | Arquivo |
|------|---------|
| `IWithdrawService` + impl com regras spec | `src/FellowCore.Application/Modules/Payouts/Interfaces/IWithdrawService.cs`, `src/FellowCore.Application/Modules/Payouts/Services/WithdrawService.cs` |
| `PayoutRepository.GetTodayTotalGrossAsync` + `GetScheduledDueAsync` | `src/FellowCore.Infrastructure/Repositories/PayoutRepository.cs` |
| `IWithdrawQueueProcessor` + impl Hangfire (cron `*/5 * * * *`) | `src/FellowCore.Application/Modules/Payouts/Interfaces/IWithdrawQueueProcessor.cs`, `src/FellowCore.Infrastructure/Workers/Processors/WithdrawQueueProcessor.cs` |
| `POST /api/v1/payouts/withdraw` + `CreateWithdrawDto` | `src/FellowCore.Api/Controllers/PayoutsController.cs`, `src/FellowCore.Application/Modules/Payouts/DTOs/PayoutDTOs.cs` |
| 9 testes cobrindo todos os caminhos | `tests/FellowCore.Application.Tests/Services/WithdrawServiceTests.cs` |

### Fase 4 — Plan simulator + anti-migração (entregue)

| Item | Arquivo |
|------|---------|
| `IPlanSimulatorService` + impl | `src/FellowCore.Application/Modules/Pricing/Interfaces/IPlanSimulatorService.cs`, `src/FellowCore.Application/Modules/Pricing/Services/PlanSimulatorService.cs` |
| `IPlanMigrationValidator` + impl (Enterprise=manual, SaaS=30% recorrência) | `src/FellowCore.Application/Modules/Pricing/Services/PlanMigrationValidator.cs` |
| `POST /api/v1/pricing-plans/simulate` (público) + `GET /migration-eligibility/{code}` (JWT) | `src/FellowCore.Api/Controllers/PricingPlansController.cs` |

### Fase 5 — UI checkout (entregue parcial)

| Item | Arquivo |
|------|---------|
| Checkout filtra PIX automaticamente quando `link.amount > R$ 800` | `fellow-pay/src/app/pay/[token]/page.tsx` |
| Mensagem clara quando link só aceita PIX mas valor excede | mesmo arquivo |

### Tech debt registrado

- **UI portal seller**: página "Meu plano" + simulador embedded + selector de migração — requer design dedicado. Backend pronto.
- **Onboarding seller no portal**: `BusinessType` selector + filtro de planos — fellow-pay não tem fluxo de cadastro de seller hoje (feito via API).
- **`Transaction.SubscriptionId` link**: hoje `ITransactionRepository.GetRecurringCountAsync` retorna 0 stub. Comportamento conservador (nunca sugere migração SaaS automaticamente). Quando adicionar o FK, trocar pelo count real.
- **Saldo da subconta Woovi**: `WithdrawService` debita o ledger LOCAL antes de chamar provider, mas não consulta saldo REAL da subconta na Woovi pré-validação. Próxima iteração: chamar `GetSubAccountAsync` antes pra antecipar erro de saldo insuficiente do provider.
- **Feriados BR**: `NextBusinessDay()` no `WithdrawService` skip apenas fim de semana. Feriados nacionais não tratados — saques caem no próximo dia útil pela lógica do processor.

### Pendente (próxima sessão)

- Aplicar migration no dev DB (autorização obrigatória — destrutivo)
- Solicitar ativação `Subconta` + `Split` no painel Woovi via Morgana
- Configurar `OpenPix:AppId` no `appsettings.Development.json` com AppID sandbox
- Smoke test E2E real após features ativadas: criar subconta → charge com split → simular pagamento → webhook → verificar dedup

