# Runbook: Ledger Global Imbalance

**Alert:** `LedgerGlobalImbalance`
**Severity:** P0
**Owner:** Finance + Backend

## Sintoma

A soma de todos os debitos nao iguala a soma de todos os creditos no ledger. O metrica `fellowcore_ledger_global_imbalance_cents` esta diferente de zero.

## Impacto

- Saldo de sellers pode estar incorreto
- Reconciliacao falhara
- Risco financeiro direto

## Diagnostico

1. Verificar metrica:
```
curl -s http://api:8080/metrics | grep fellowcore_ledger_global_imbalance
```

2. Rodar reconciliacao manual:
```sql
SELECT
  SUM(CASE WHEN type = 'DEBIT' THEN amount ELSE 0 END) as total_debits,
  SUM(CASE WHEN type = 'CREDIT' THEN amount ELSE 0 END) as total_credits,
  SUM(CASE WHEN type = 'DEBIT' THEN amount ELSE 0 END) -
  SUM(CASE WHEN type = 'CREDIT' THEN amount ELSE 0 END) as imbalance
FROM ledger_entries
WHERE tenant_id = '<tenant_id>';
```

3. Identificar entries orfas (sem contra-entry):
```sql
SELECT le.*
FROM ledger_entries le
WHERE le.contra_entry_id IS NULL
  AND le.created_at > NOW() - INTERVAL '24 hours'
ORDER BY le.created_at DESC;
```

4. Verificar se houve falha em `ReversalCreditAsync` (logs com `FALHA CRITICA`):
```
grep "FALHA CRITICA" /var/log/fellowcore/*.log
```

## Mitigacao

1. **NAO deletar entries** — toda correcao deve ser via entries compensatorias
2. Identificar a transacao root cause (olhar `transaction_id` das entries desbalanceadas)
3. Criar entry compensatoria manual via API admin ou SQL direto:
```sql
-- Exemplo: credito compensatorio para seller
INSERT INTO ledger_entries (id, account_id, type, amount, description, operation, transaction_id, created_at)
VALUES (gen_random_uuid(), '<account_id>', 'CREDIT', <amount>, 'Compensacao manual — incidente #XXX', 'MANUAL_ADJUSTMENT', '<tx_id>', NOW());
```
4. Verificar que a metrica voltou a zero
5. Registrar incidente no log de auditoria

## Prevencao

- Todas as operacoes de ledger usam double-entry com `LinkContraEntry()`
- `ExecuteWithRetryAsync` com optimistic concurrency garante atomicidade
- Background job de compensacao (ReversalCreditAsync) para falhas de payout
- Reconciliacao diaria (04:30 UTC) detecta desvios
