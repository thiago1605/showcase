# Runbook: Divergencia de Custo do Provider (Provider Cost Mismatch)

**Alert:** `PROVIDER_COST_MISMATCH` (Reconciliation Phase 5)
**Severity:** P2
**Owner:** Finance + Backend

## Sintoma

O custo real cobrado pelo provider (Stripe/OpenPix) difere do custo estimado registrado no momento da transacao. `ProviderCostActualAmount` e `ProviderCostAmount` divergem alem da tolerancia aceitavel.

## Alerta relacionado

- `ReconciliationIssue.Type = PROVIDER_COST_MISMATCH`
- Pode indicar mudanca de tabela de precos do provider sem atualizacao do `ProviderCostSchedule`

## Impacto

- `PLATFORM_MARGIN` incorreto — lucro da plataforma super ou subestimado
- `PROVIDER_COST` no ledger nao reflete o custo real
- Possivel margem negativa sistematica se custo real for consistentemente maior

## Acao imediata

1. Listar transacoes com divergencia de custo:
```sql
SELECT t.id, t.tenant_id, t.seller_id, t.payment_type,
       t.amount, t.provider_cost_amount AS estimated,
       t.provider_cost_actual_amount AS actual,
       t.provider_cost_actual_amount - t.provider_cost_amount AS delta,
       t.created_at
FROM transactions t
WHERE t.provider_cost_actual_amount IS NOT NULL
  AND ABS(t.provider_cost_actual_amount - t.provider_cost_amount) > 1
  AND t.created_at > NOW() - INTERVAL '7 days'
ORDER BY ABS(t.provider_cost_actual_amount - t.provider_cost_amount) DESC
LIMIT 50;
```

2. Calcular impacto total no periodo:
```sql
SELECT t.payment_type,
       COUNT(*) AS tx_count,
       SUM(t.provider_cost_actual_amount - t.provider_cost_amount) AS total_delta_cents
FROM transactions t
WHERE t.provider_cost_actual_amount IS NOT NULL
  AND ABS(t.provider_cost_actual_amount - t.provider_cost_amount) > 1
  AND t.created_at > NOW() - INTERVAL '30 days'
GROUP BY t.payment_type
ORDER BY total_delta_cents DESC;
```

3. Verificar tabela de custos configurada:
```sql
SELECT pcs.provider, pcs.payment_type, pcs.percentage_fee,
       pcs.fixed_fee_cents, pcs.min_fee_cents, pcs.max_fee_cents,
       pcs.effective_from, pcs.effective_until
FROM provider_cost_schedules pcs
WHERE pcs.is_active = true
ORDER BY pcs.provider, pcs.payment_type;
```

## Correcao definitiva

### Passo 1 — Corrigir custo real via ReconciliationService

```csharp
// Via endpoint admin autenticado
POST /api/v1/reconciliation/transactions/{transactionId}/apply-actual-cost
{
  "actualProviderCostAmount": 123,  // valor real em centavos
  "note": "Correcao apos fatura Stripe de <mes/ano>"
}
```

Isso executa `ReconciliationService.ApplyActualProviderCostAsync`, que:
- Atualiza `Transaction.ProviderCostActualAmount`
- Cria ledger adjustment via `RecordCostAdjustmentAsync` (usa `ForceDebit` em `PLATFORM_MARGIN`)

### Passo 2 — Atualizar ProviderCostSchedule se tabela de precos mudou

```sql
-- Desativar schedule desatualizado
UPDATE provider_cost_schedules
SET is_active = false, effective_until = NOW(), updated_at = NOW()
WHERE provider = '<STRIPE|OPENPIX>' AND payment_type = '<type>' AND is_active = true;

-- Inserir novo schedule com valores corretos
INSERT INTO provider_cost_schedules (id, provider, payment_type, percentage_fee, fixed_fee_cents, min_fee_cents, max_fee_cents, is_active, effective_from, created_at)
VALUES (gen_random_uuid(), '<provider>', '<type>', <pct>, <fixed>, <min>, <max>, true, NOW(), NOW());
```

## Como validar

```sql
-- Confirmar que ProviderCostActualAmount foi atualizado
SELECT t.id, t.provider_cost_amount, t.provider_cost_actual_amount,
       t.platform_margin_amount
FROM transactions t
WHERE t.id = '<transaction_id>';

-- Verificar ledger adjustment criado
SELECT le.operation, le.direction, le.amount, le.description, le.created_at
FROM ledger_entries le
WHERE le.reference_id = '<transaction_id>'
  AND le.operation IN ('COST_ADJUSTMENT', 'MARGIN_ADJUSTMENT')
ORDER BY le.created_at DESC;
```

Reexecutar reconciliacao para confirmar fechamento do issue:
```
POST /api/v1/reconciliation/runs  { "tenantId": "<id>" }
```

## Quando escalar

- Delta sistematico > 5% em todas as transacoes do mesmo tipo por mais de 3 dias — provider pode ter alterado fees sem aviso
- Impacto financeiro acumulado > R$5.000 em 30 dias — escalar para CFO e renegociar contrato
- `ProviderCostActualAmount` < 0 — dado invalido, investigar integracao com provider
