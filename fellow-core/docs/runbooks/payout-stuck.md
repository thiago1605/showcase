# Runbook: Payout Stuck / Failed

**Alert:** `StalePayouts`, `FailedPayouts`
**Severity:** P1
**Owner:** Finance + Backend

## Sintoma

Payouts estao presos em PENDING/PROCESSING por mais de 24h, ou payouts falharam.

## Impacto

- Sellers nao recebem fundos
- Ledger tem debito sem correspondente payout completo
- Compensacao automatica (ReversalCreditAsync) deve ter sido executada para payouts falhados

## Diagnostico

1. Listar payouts problematicos:
```sql
SELECT p.id, p.seller_id, p.amount, p.fee, p.status,
       p.bank_transaction_id, p.failure_reason,
       p.created_at, p.processed_at
FROM payouts p
WHERE p.status IN ('PENDING', 'PROCESSING', 'FAILED')
  AND p.created_at > NOW() - INTERVAL '48 hours'
ORDER BY p.created_at DESC;
```

2. Verificar se compensacao foi executada para payouts falhados:
```sql
SELECT le.* FROM ledger_entries le
WHERE le.operation = 'REVERSAL_CREDIT'
  AND le.description LIKE '%Payout%'
  AND le.created_at > NOW() - INTERVAL '48 hours'
ORDER BY le.created_at DESC;
```

3. Verificar logs de erro:
```
grep "FALHA CRITICA.*payout" /var/log/fellowcore/*.log | tail -20
```

4. Verificar status do provider (OpenPix para payouts):
```
curl -s http://api:8080/health | jq '.entries.openpix'
```

## Mitigacao

### Payout FAILED com compensacao OK
1. Ledger ja foi compensado automaticamente (ReversalCreditAsync + ReversePayoutFeeAsync)
2. Investigar causa raiz (failure_reason no payout)
3. Corrigir configuracao do seller se necessario (ExternalAccountId, etc.)
4. Seller pode solicitar novo payout

### Payout FAILED sem compensacao (FALHA CRITICA)
1. Verificar se background retry (Hangfire) esta processando
2. Se Hangfire tambem falhou, compensar manualmente:
```sql
-- Verificar saldo atual do seller
SELECT la.type, la.balance FROM ledger_accounts la
WHERE la.seller_id = '<seller_id>' AND la.tenant_id = '<tenant_id>';
```
3. Criar entries compensatorias via API admin ou SQL direto
4. Registrar incidente

### Payout STUCK em PROCESSING
1. Verificar com provider se o payout foi processado
2. Se processado, atualizar status manualmente:
```sql
UPDATE payouts SET status = 'PAID', bank_transaction_id = '<tx_id>',
  processed_at = NOW(), updated_at = NOW()
WHERE id = '<payout_id>';
```
3. Se nao processado, marcar como FAILED e executar compensacao

## Prevencao

- PayoutService debita ledger ANTES de chamar provider (atomicidade)
- Compensacao automatica (ReversalCreditAsync) em caso de falha
- Background retry via Hangfire para compensacao falhada
- Reconciliacao diaria verifica payouts vs provider
