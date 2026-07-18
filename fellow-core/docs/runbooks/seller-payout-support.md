# Runbook: Suporte — Seller Relata Payout Nao Recebido

**Alert:** N/A (ticket de suporte)
**Severity:** P2 (P1 se valor > R$10.000)
**Owner:** Suporte + Finance

## Sintoma

Seller abre chamado relatando que um payout agendado ou automatico nao foi creditado na conta bancaria dele. Pode ter recebido email de confirmacao mas o saldo bancario nao reflete.

## Alerta relacionado

- Ticket de suporte com tag `payout` ou `nao-recebi`
- Seller consultando `GET /api/v1/sellers/{id}/payouts` e vendo status inconsistente

## Impacto

- Seller sem acesso a seus fundos
- Risco de churn e reputacao da plataforma
- Se FAILED sem compensacao, saldo do ledger incorreto

## Acao imediata

### Passo 1 — Localizar o payout

```sql
SELECT p.id, p.seller_id, p.amount, p.fee,
       p.net_amount, p.status, p.bank_transaction_id,
       p.failure_reason, p.idempotency_key,
       p.created_at, p.processed_at, p.updated_at
FROM payouts p
WHERE p.seller_id = '<seller_id>'
  AND p.created_at > NOW() - INTERVAL '30 days'
ORDER BY p.created_at DESC;
```

### Passo 2 — Verificar dados bancarios do seller

```sql
SELECT s.name, s.bank_code, s.bank_agency, s.bank_account,
       s.bank_account_type, s.external_account_id
FROM sellers s
WHERE s.id = '<seller_id>';
```

### Passo 3 — Diagnostico por status

**Se Status = PAID**

Payout foi processado com sucesso pelo provider. `BankTransactionId` deve estar preenchido.

```sql
SELECT p.bank_transaction_id, p.processed_at FROM payouts p
WHERE p.id = '<payout_id>';
```

- Informar ao seller: fundos enviados em `processed_at`, ID bancario `bank_transaction_id`
- Prazo de compensacao bancaria: TED = D+0 ate 17h, D+1 apos; PIX = instantaneo
- Se passaram mais de 2 dias uteis: pedir ao seller que abra reclamacao no banco com o `bank_transaction_id`
- Verificar se dados bancarios do seller estao corretos (banco, agencia, conta, tipo)

**Se Status = PROCESSING**

```sql
-- Verificar ha quanto tempo esta em PROCESSING
SELECT p.id, p.status, p.created_at, p.updated_at,
       NOW() - p.updated_at AS time_in_processing
FROM payouts p WHERE p.id = '<payout_id>';
```

- Se < 2h: aguardar processamento normal
- Se > 2h: consultar status no painel do provider (OpenPix) usando `idempotency_key`
- Se provider confirmou PAID: seguir runbook `payout-provider-success-ledger-failed.md`
- Se provider nao encontra: verificar se Hangfire `PayoutRetryProcessor` esta rodando

```bash
GET /hangfire -> Jobs -> Scheduled -> filtrar por "Payout"
```

**Se Status = FAILED**

```sql
SELECT p.failure_reason, p.retry_count, p.updated_at FROM payouts p
WHERE p.id = '<payout_id>';
```

- Ler `failure_reason` para entender causa (dados bancarios invalidos, limite diario, etc.)
- Verificar se compensacao foi aplicada (saldo devolvido ao seller):
```sql
SELECT le.operation, le.direction, le.amount, le.created_at
FROM ledger_entries le
WHERE le.reference_id = '<payout_id>'
  AND le.operation IN ('REVERSAL_CREDIT', 'PAYOUT_FEE_REVERSAL')
ORDER BY le.created_at;
```
- Se compensacao OK: informar seller que saldo foi devolvido e ele pode solicitar novo payout
- Se compensacao ausente: seguir runbook `payout-stuck.md`

**Se Status = PENDING**

```sql
-- Verificar se o job de payout esta na fila do Hangfire
SELECT p.id, p.idempotency_key, p.created_at FROM payouts p
WHERE p.id = '<payout_id>';
```

- Verificar fila do Hangfire: se job nao foi enfileirado, pode indicar falha no worker
- Verificar se saldo do seller e suficiente:
```sql
SELECT la.type, la.balance FROM ledger_accounts la
WHERE la.seller_id = '<seller_id>' AND la.type = 'SELLER_WALLET';
```

## Correcao definitiva

### Dados bancarios incorretos

1. Solicitar dados corretos ao seller
2. Atualizar via API admin:
```
PATCH /api/v1/sellers/{sellerId}
{ "bankCode": "...", "bankAgency": "...", "bankAccount": "...", "bankAccountType": "..." }
```
3. Criar novo payout (o anterior FAILED ja compensou o saldo)

### Payout travado em PROCESSING sem resposta do provider

```sql
-- Forcar status para FAILED e acionar compensacao
UPDATE payouts SET status = 'FAILED',
  failure_reason = 'Timeout de provider — compensacao manual aplicada',
  updated_at = NOW()
WHERE id = '<payout_id>' AND status = 'PROCESSING';
```

Executar compensacao manual conforme runbook `payout-stuck.md`.

## Como validar

```sql
-- Estado final do payout
SELECT p.status, p.bank_transaction_id, p.failure_reason, p.updated_at
FROM payouts p WHERE p.id = '<payout_id>';

-- Saldo do seller apos resolucao
SELECT la.type, la.balance FROM ledger_accounts la
WHERE la.seller_id = '<seller_id>' AND la.tenant_id = '<tenant_id>';
```

## Quando escalar

- PAID mas seller nao recebeu apos 3 dias uteis — abrir disputa bancaria com o provider
- Valor > R$10.000 — notificar Finance imediatamente
- Dados bancarios atualizados mas payout continua falhando — possivel restricao bancaria, escalar para compliance
- Seller alega fraude ou uso indevido da conta — escalar para seguranca
