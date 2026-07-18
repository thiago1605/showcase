# Runbook: Chargeback Perdido (Dispute Resolvido como Perda)

**Alert:** `ChargebackLost`
**Severity:** P1
**Owner:** Finance + Backend

## Sintoma

Dispute junto ao provider foi resolvido em favor do cliente (`charge.dispute.closed` com `status=lost`). Transacao com `Status=CHARGEBACKERROR`. Verificar se todas as reversoes contabeis foram aplicadas corretamente.

## Alerta relacionado

- Webhook `charge.dispute.closed` com `status=lost` processado
- `Transaction.Status = CHARGEBACKERROR`
- `Dispute.Status = LOST`

## Impacto

- Seller perde o valor da venda
- Plataforma perde margem e possivelmente fees do provider
- Se split estava ativo, todos os destinatarios tiveram saldos revertidos
- Provider pode cobrar fee adicional de chargeback (tipicamente R$15-50)

## Acao imediata

1. Localizar transacao e dispute:
```sql
SELECT t.id AS tx_id, t.amount, t.net_amount, t.platform_fee_amount,
       t.platform_margin_amount, t.status AS tx_status,
       d.id AS dispute_id, d.status AS dispute_status,
       d.amount AS dispute_amount, d.reason, d.resolved_at
FROM transactions t
JOIN disputes d ON d.transaction_id = t.id
WHERE t.status = 'CHARGEBACKERROR'
  AND d.resolved_at > NOW() - INTERVAL '7 days'
ORDER BY d.resolved_at DESC;
```

2. Verificar se reversao do ledger foi aplicada:
```sql
SELECT le.operation, le.direction, le.amount, le.description, le.created_at
FROM ledger_entries le
WHERE le.reference_id IN (
  SELECT id::text FROM disputes WHERE transaction_id = '<transaction_id>'
)
ORDER BY le.created_at;
```

3. Verificar se margem da plataforma foi revertida (`ReversePlatformMarginAsync`):
```sql
SELECT le.operation, le.direction, le.amount, le.created_at
FROM ledger_entries le
WHERE le.operation = 'MARGIN_REVERSAL'
  AND le.created_at > NOW() - INTERVAL '7 days'
  AND le.reference_id IN (
    SELECT d.id::text FROM disputes d WHERE d.transaction_id = '<transaction_id>'
  );
```

4. Para transacoes com split — verificar reversao de todos os SplitTransfers:
```sql
SELECT st.id, st.recipient_seller_id, st.amount, st.reversed_amount, st.status
FROM split_transfers st
WHERE st.transaction_id = '<transaction_id>'
ORDER BY st.is_primary_share DESC;
```

## Correcao definitiva

### Reversao de margem nao aplicada

Se `ReversePlatformMarginAsync` nao foi executado:

```sql
-- Verificar saldo atual da conta de margem
SELECT la.type, la.balance FROM ledger_accounts la
WHERE la.type = 'PLATFORM_MARGIN' AND la.tenant_id = '<tenant_id>';

-- Inserir entry de reversao manual (ForceDebit — pode ficar negativo)
INSERT INTO ledger_entries (id, ledger_account_id, amount, direction, operation, reference_id, description, created_at)
SELECT gen_random_uuid(), la.id, t.platform_margin_amount, 'DEBIT', 'MARGIN_REVERSAL',
       d.id::text, 'Correcao manual: margem nao revertida apos chargeback perdido', NOW()
FROM ledger_accounts la, transactions t, disputes d
WHERE la.type = 'PLATFORM_MARGIN' AND la.tenant_id = t.tenant_id
  AND t.id = '<transaction_id>' AND d.transaction_id = t.id;
```

### Split nao revertido

Se `SplitTransfer` com status diferente de `REVERSED`:

```csharp
// Via API admin
POST /api/v1/reconciliation/issues/{issueId}/resolve
{
  "resolution": "RESOLVED",
  "note": "Split reversal aplicado manualmente via ReverseSplitsProportionallyAsync"
}
```

Ou diretamente chamar o servico via endpoint interno de admin.

## Como validar

```sql
-- Confirmar que PLATFORM_MARGIN foi debitado
SELECT le.operation, le.direction, le.amount FROM ledger_entries le
WHERE le.operation = 'MARGIN_REVERSAL'
  AND le.reference_id = '<dispute_id>';

-- Confirmar saldo do seller zerou corretamente
SELECT la.type, la.balance FROM ledger_accounts la
WHERE la.seller_id = '<seller_id>' AND la.tenant_id = '<tenant_id>';

-- Confirmar splits revertidos
SELECT st.status, st.reversed_amount FROM split_transfers st
WHERE st.transaction_id = '<transaction_id>';
```

## Quando escalar

- Valor do chargeback > R$5.000 — notificar Finance e avaliar contestacao junto ao provider
- Mais de 5 chargebacks perdidos do mesmo seller em 30 dias — revisar risco e possivel cancelamento
- Reversao de split falhou para algum destinatario — escalar para engenharia, risco de saldo incorreto
