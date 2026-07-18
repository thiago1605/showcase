# Runbook: Payout Confirmado pelo Provider sem Entrada no Ledger

**Alert:** `PayoutLedgerMissing` (Reconciliation Phase 2 / Phase 4)
**Severity:** P1
**Owner:** Finance + Backend

## Sintoma

Provider confirmou transferencia bancaria ao seller (Status=PAID, BankTransactionId preenchido), mas nenhuma entrada de debito correspondente existe no ledger interno. O saldo do seller aparece inflado.

## Alerta relacionado

- Reconciliation Phase 2: payout nao tem ledger entry de debito
- Reconciliation Phase 4: `PLATFORM_RECEIVABLE` diverge do esperado

## Impacto

- `SELLER_WALLET` com saldo incorretamente alto (fundos ja enviados ao banco, mas nao debitados internamente)
- Balanco da plataforma superestimado
- Duplo pagamento possivel se nao corrigido antes do proximo ciclo de payout

## Acao imediata

1. Identificar payouts pagos sem debito no ledger:
```sql
SELECT p.id, p.seller_id, p.tenant_id, p.amount, p.fee,
       p.bank_transaction_id, p.status, p.processed_at
FROM payouts p
WHERE p.status = 'PAID'
  AND p.processed_at > NOW() - INTERVAL '7 days'
  AND NOT EXISTS (
    SELECT 1 FROM ledger_entries le
    WHERE le.reference_id = p.id::text
      AND le.operation = 'PAYOUT'
      AND le.direction = 'DEBIT'
  )
ORDER BY p.processed_at DESC;
```

2. Verificar saldo atual do seller para quantificar divergencia:
```sql
SELECT la.type, la.balance
FROM ledger_accounts la
WHERE la.seller_id = '<seller_id>' AND la.tenant_id = '<tenant_id>'
ORDER BY la.type;
```

3. Checar se Hangfire registrou falha no PayoutService:
```
grep "PayoutService\|payout.*ledger" /var/log/fellowcore/*.log | grep -i "error\|exception" | tail -20
```

## Correcao definitiva

Inserir ledger entries de debito manualmente:

```sql
-- Debitar SELLER_WALLET pelo valor liquido do payout
INSERT INTO ledger_entries (id, ledger_account_id, amount, direction, operation, reference_id, description, created_at)
SELECT gen_random_uuid(), la.id, <payout_net_amount>, 'DEBIT', 'PAYOUT', '<payout_id>', 'Correcao manual: payout PAID sem ledger debit', NOW()
FROM ledger_accounts la
WHERE la.type = 'SELLER_WALLET'
  AND la.seller_id = '<seller_id>'
  AND la.tenant_id = '<tenant_id>';

-- Debitar SELLER_WALLET pela taxa do payout (se houver fee > 0)
INSERT INTO ledger_entries (id, ledger_account_id, amount, direction, operation, reference_id, description, created_at)
SELECT gen_random_uuid(), la.id, <payout_fee>, 'DEBIT', 'PAYOUT_FEE', '<payout_id>', 'Correcao manual: payout fee sem ledger debit', NOW()
FROM ledger_accounts la
WHERE la.type = 'SELLER_WALLET'
  AND la.seller_id = '<seller_id>'
  AND la.tenant_id = '<tenant_id>'
  AND <payout_fee> > 0;
```

Atualizar saldos das contas afetadas:
```sql
UPDATE ledger_accounts
SET balance = balance - <payout_amount>,
    updated_at = NOW()
WHERE type = 'SELLER_WALLET'
  AND seller_id = '<seller_id>'
  AND tenant_id = '<tenant_id>';
```

## Como validar

```sql
-- Confirmar entradas criadas
SELECT le.direction, le.operation, le.amount, le.created_at
FROM ledger_entries le
WHERE le.reference_id = '<payout_id>'
ORDER BY le.created_at;

-- Confirmar saldo corrigido
SELECT la.type, la.balance FROM ledger_accounts la
WHERE la.seller_id = '<seller_id>' AND la.tenant_id = '<tenant_id>';
```

Reexecutar reconciliacao para fechar o issue:
```
POST /api/v1/reconciliation/runs  { "tenantId": "<id>" }
```

## Quando escalar

- Mais de 2 payouts com o mesmo padrao em 24h — indica falha no PayoutService, abrir bug P0
- `BankTransactionId` confirmado no provider mas payout nao aparece — possivel fraude bancaria, escalar para compliance
- Valor afetado acumulado > R$50.000 — notificacao imediata ao CFO e compliance
