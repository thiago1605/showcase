# Runbook: Refund Confirmado pelo Provider sem Entrada no Ledger

**Alert:** `REFUND_PROVIDER_SUCCESS_LEDGER_MISSING` (Reconciliation Phase 8)
**Severity:** P1
**Owner:** Finance + Backend

## Sintoma

Provider (Stripe/OpenPix) confirmou o reembolso ao cliente, mas nenhuma entrada correspondente existe no ledger interno. O saldo do seller permanece incorretamente debitado.

## Alerta relacionado

- `ReconciliationIssue.Type = REFUND_PROVIDER_SUCCESS_LEDGER_MISSING`
- Webhook `charge.refund.updated` ou `REFUND_CONFIRMED` processado com erro silencioso

## Impacto

- Saldo do seller (`SELLER_WALLET`) incorretamente baixo
- `PLATFORM_RECEIVABLE` nao revertido — plataforma com receita fantasma
- Risco regulatorio: cliente foi reembolsado mas contabilidade interna diverge

## Acao imediata

1. Identificar RefundIntents concluidos sem ledger:
```sql
SELECT ri.id, ri.transaction_id, ri.amount, ri.status,
       ri.provider_refund_id, ri.created_at, ri.completed_at
FROM refund_intents ri
WHERE ri.status = 'COMPLETED'
  AND ri.completed_at > NOW() - INTERVAL '7 days'
  AND NOT EXISTS (
    SELECT 1 FROM ledger_entries le
    WHERE le.reference_id = ri.id::text
      AND le.operation = 'REFUND'
  )
ORDER BY ri.completed_at DESC;
```

2. Confirmar valor da transacao original:
```sql
SELECT t.id, t.amount, t.net_amount, t.platform_fee_amount,
       t.provider_cost_amount, t.status, t.tenant_id, t.seller_id
FROM transactions t
WHERE t.id = '<transaction_id>';
```

3. Verificar se o webhook foi recebido mas falhou no handler:
```
grep "refund.*ledger\|REFUND.*error" /var/log/fellowcore/*.log | tail -30
```

## Correcao definitiva

Aplicar correcao manual via `ReconciliationService.ApplyManualCorrectionAsync`:

```csharp
// Chame via endpoint admin autenticado com role PLATFORM_ADMIN
POST /api/v1/reconciliation/issues/{issueId}/resolve
{
  "resolution": "RESOLVED",
  "note": "Entrada manual aplicada via ApplyManualCorrectionAsync — refund confirmado pelo provider em <data>"
}
```

Se a API de correcao nao estiver disponivel, inserir ledger entries manualmente:
```sql
-- Reverter PLATFORM_RECEIVABLE (debito)
INSERT INTO ledger_entries (id, ledger_account_id, amount, direction, operation, reference_id, description, created_at)
SELECT gen_random_uuid(), la.id, <amount>, 'DEBIT', 'REFUND', '<refund_intent_id>', 'Correcao manual: refund provider OK sem ledger', NOW()
FROM ledger_accounts la
WHERE la.type = 'PLATFORM_RECEIVABLE' AND la.tenant_id = '<tenant_id>';

-- Creditar SELLER_WALLET (credito de devolucao)
INSERT INTO ledger_entries (id, ledger_account_id, amount, direction, operation, reference_id, description, created_at)
SELECT gen_random_uuid(), la.id, <amount>, 'CREDIT', 'REFUND', '<refund_intent_id>', 'Correcao manual: refund provider OK sem ledger', NOW()
FROM ledger_accounts la
WHERE la.type = 'SELLER_WALLET' AND la.seller_id = '<seller_id>' AND la.tenant_id = '<tenant_id>';
```

## Como validar

```sql
-- Confirmar que entradas foram criadas
SELECT le.direction, le.operation, le.amount, le.created_at
FROM ledger_entries le
WHERE le.reference_id = '<refund_intent_id>'
ORDER BY le.created_at;

-- Confirmar saldo do seller apos correcao
SELECT la.type, la.balance FROM ledger_accounts la
WHERE la.seller_id = '<seller_id>' AND la.tenant_id = '<tenant_id>';
```

Reexecutar reconciliacao para confirmar que o issue foi fechado:
```
POST /api/v1/reconciliation/runs  { "tenantId": "<id>" }
```

## Quando escalar

- Mais de 3 RefundIntents com mesmo padrao em 24h — indica bug no webhook handler
- Valor total afetado > R$10.000 — notificar compliance imediatamente
- `provider_refund_id` nao encontrado no provider — possivel fraude, escalar para seguranca
