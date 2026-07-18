# Runbook: Margem da Plataforma Negativa

**Alert:** `PLATFORM_MARGIN_NEGATIVE` (Reconciliation Phase 8)
**Severity:** P2
**Owner:** Finance + Product

## Sintoma

`PlatformMarginAmount` de transacoes esta negativo — a plataforma esta subsidiando transacoes, ou seja, o custo do provider excede a taxa cobrada ao seller.

## Alerta relacionado

- `ReconciliationIssue.Type = PLATFORM_MARGIN_NEGATIVE`
- Conta `PLATFORM_MARGIN` no ledger com saldo negativo
- Dashboard: `GET /api/v1/dashboard/financial-health` retornando margem negativa

## Impacto

- Plataforma perde dinheiro em cada transacao afetada
- Se sistematico, risco de insolvencia operacional no medio prazo
- `PLATFORM_MARGIN` ledger account pode ficar com saldo negativo (tecnicamente permitido via `ForceDebit`, mas sinal de alerta)

## Acao imediata

1. Identificar transacoes com margem negativa:
```sql
SELECT t.id, t.tenant_id, t.seller_id, t.payment_type,
       t.amount, t.platform_fee_amount,
       t.provider_cost_amount, t.provider_cost_actual_amount,
       t.platform_margin_amount,
       t.created_at
FROM transactions t
WHERE t.platform_margin_amount < 0
  AND t.created_at > NOW() - INTERVAL '30 days'
ORDER BY t.platform_margin_amount ASC
LIMIT 50;
```

2. Calcular prejuizo total por tipo de pagamento:
```sql
SELECT t.payment_type,
       COUNT(*) AS tx_count,
       SUM(t.platform_margin_amount) AS total_margin_cents,
       AVG(t.platform_margin_amount) AS avg_margin_cents,
       SUM(t.amount) AS total_volume_cents
FROM transactions t
WHERE t.platform_margin_amount < 0
  AND t.created_at > NOW() - INTERVAL '30 days'
GROUP BY t.payment_type
ORDER BY total_margin_cents ASC;
```

3. Identificar sellers e planos de pricing afetados:
```sql
SELECT s.id AS seller_id, s.name, pp.name AS pricing_plan,
       COUNT(t.id) AS tx_count,
       SUM(t.platform_margin_amount) AS total_margin_cents
FROM transactions t
JOIN sellers s ON s.id = t.seller_id
LEFT JOIN pricing_plans pp ON pp.id = s.pricing_plan_id
WHERE t.platform_margin_amount < 0
  AND t.created_at > NOW() - INTERVAL '30 days'
GROUP BY s.id, s.name, pp.name
ORDER BY total_margin_cents ASC;
```

4. Verificar saldo da conta de margem:
```sql
SELECT la.type, la.balance, la.tenant_id
FROM ledger_accounts la
WHERE la.type = 'PLATFORM_MARGIN'
ORDER BY la.balance ASC;
```

## Correcao definitiva

### Causa: fees do plano abaixo do custo do provider

Ajustar `PricingPlan` para garantir margem positiva:

```sql
-- Ver fees atuais do plano
SELECT pp.id, pp.name, pp.pix_fee_percentage, pp.pix_fee_fixed_cents,
       pp.card_fee_percentage, pp.card_fee_fixed_cents,
       pp.boleto_fee_fixed_cents
FROM pricing_plans pp
WHERE pp.id = '<plan_id>';

-- Atualizar fees (ajustar conforme novo calculo de margem minima)
UPDATE pricing_plans
SET pix_fee_percentage = <novo_valor>,
    card_fee_percentage = <novo_valor>,
    updated_at = NOW()
WHERE id = '<plan_id>';
```

### Causa: custo real do provider maior que o estimado

Seguir runbook `provider-cost-mismatch.md` para aplicar `ApplyActualProviderCostAsync`.

### Notificar produto

- Revisar politica de pricing para tipos de pagamento com margem negativa sistematica
- Considerar fee minimo por transacao para volumes baixos
- Avaliar elegibilidade de sellers para planos superiores (CRESCA/SCALA)

## Como validar

```sql
-- Verificar que novas transacoes tem margem positiva
SELECT AVG(t.platform_margin_amount), MIN(t.platform_margin_amount)
FROM transactions t
WHERE t.created_at > NOW() - INTERVAL '1 hour';
```

Monitorar `GET /api/v1/dashboard/financial-health` apos ajuste de pricing.

## Quando escalar

- Margem negativa em > 20% das transacoes do mes — escalar imediatamente para CFO e produto
- Saldo de `PLATFORM_MARGIN` acumulado negativo > R$1.000 — acionar revisao emergencial de pricing
- Causa nao identificada apos 2h de investigacao — escalar para engenharia sênior
