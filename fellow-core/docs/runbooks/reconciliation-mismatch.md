# Runbook: Reconciliation Mismatch

**Alert:** `ReconciliationCriticalIssues`
**Severity:** P0
**Owner:** Finance

## Sintoma

Reconciliacao detectou discrepancias entre dados internos (ledger) e dados externos (Stripe/OpenPix settlement reports).

## Impacto

- Dinheiro pode estar faltando ou sobrando no ledger
- Saldo de sellers pode estar incorreto
- Risco de perda financeira ou fraude nao detectada

## Diagnostico

1. Verificar issues recentes:
```sql
SELECT ri.id, ri.issue_type, ri.severity, ri.internal_transaction_id,
       ri.provider_transaction_id, ri.internal_amount_cents, ri.provider_amount_cents,
       ri.description, ri.created_at
FROM reconciliation_issues ri
JOIN reconciliation_runs rr ON ri.run_id = rr.id
WHERE ri.severity = 'CRITICAL'
  AND ri.created_at > NOW() - INTERVAL '24 hours'
ORDER BY ri.created_at DESC;
```

2. Classificar por tipo:
   - `AMOUNT_MISMATCH`: Valor interno != valor do provider
   - `MISSING_IN_LEDGER`: Item no provider sem correspondente interno
   - `MISSING_IN_STRIPE`: Item interno sem correspondente no provider
   - `DOUBLE_CAPTURE`: PaymentIntent capturado mais de uma vez
   - `DISPUTE_ORPHAN`: Disputa sem transacao correspondente
   - `REFUND_TOTAL_MISMATCH`: Soma de refunds != RefundedAmount da transacao

3. Para AMOUNT_MISMATCH — verificar rounding:
```sql
-- Tolerancia de 1 cent e esperada (RoundingPolicy.ToleranceCents)
SELECT ABS(ri.internal_amount_cents - ri.provider_amount_cents) as diff_cents
FROM reconciliation_issues ri
WHERE ri.issue_type = 'AMOUNT_MISMATCH';
```

4. Para MISSING_IN_LEDGER — verificar se webhook foi recebido:
```sql
SELECT * FROM webhook_deliveries
WHERE payload LIKE '%<provider_tx_id>%'
ORDER BY created_at DESC;
```

## Mitigacao

### AMOUNT_MISMATCH (1-2 cents)
- Se diferenca <= 1 cent: provavelmente arredondamento float64 vs decimal. Aceitar como tolerancia.
- Se diferenca > 1 cent: investigar transacao especifica. Comparar fee e net calculations.

### MISSING_IN_LEDGER
1. Verificar se a transacao existe no provider (Stripe Dashboard / OpenPix Dashboard)
2. Se existe e nao foi processada internamente:
   - Verificar se webhook foi recebido e falhado
   - Reprocessar webhook manualmente se necessario
3. Se nao existe: pode ser item de ajuste do provider (APPLICATION_FEE, ADJUSTMENT)

### MISSING_IN_STRIPE (item interno sem correspondente no provider)
1. Verificar se a transacao foi estornada ou cancelada no provider
2. Se foi: atualizar status interno
3. Se nao foi: pode ser delay no settlement report

### DOUBLE_CAPTURE
1. Verificar PaymentIntent — deve ter apenas um CapturedTransactionId
2. Se houver duplicata, uma delas precisa ser estornada
3. Verificar idempotency — a deduplicacao por PaymentIntent deveria prevenir

## Prevencao

- Reconciliacao diaria automatica (04:30 UTC)
- Settlement reconciliation com import Stripe/OpenPix
- 5-phase reconciliation: ledger, TX 1:1, payout, platform balance, cross-rail
- RoundingPolicy.WithinTolerance para tolerancia de 1 cent
