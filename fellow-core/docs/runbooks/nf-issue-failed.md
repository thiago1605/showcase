# Runbook: Falha na Emissao de Nota Fiscal

**Alert:** `FiscalInvoiceEmissionFailed`
**Severity:** P2
**Owner:** Finance + Backend

## Sintoma

Emissao de Nota Fiscal (NF-e/NFS-e) falhou para uma ou mais transacoes. Status da nota fiscal permanece `FAILED_RETRYABLE` ou `FAILED_FINAL`. Seller pode reclamar de nao ter recebido a nota ou de impedimento em processar receita.

## Alerta relacionado

- Job de emissao de NF no Hangfire com status `Failed`
- Logs com `"FiscalInvoice emission failed"` ou `"NF provider error"`
- Alerta de monitoramento de emissao em atraso (SLA tipico: 24h apos transacao)

## Impacto

- Seller sem nota fiscal para transacoes capturadas — risco fiscal e compliance
- Possivel multa por emissao em atraso dependendo da legislacao municipal/estadual
- Clientes finais sem comprovante fiscal

## Acao imediata

1. Identificar notas fiscais falhadas:
```sql
SELECT fi.id, fi.transaction_id, fi.seller_id, fi.tenant_id,
       fi.status, fi.failure_reason, fi.retry_count,
       fi.last_attempt_at, fi.created_at
FROM fiscal_invoices fi
WHERE fi.status IN ('FAILED_RETRYABLE', 'FAILED_FINAL')
  AND fi.created_at > NOW() - INTERVAL '7 days'
ORDER BY fi.created_at DESC;
```

2. Verificar transacoes sem NF associada (gap de emissao):
```sql
SELECT t.id, t.amount, t.status, t.created_at, t.tenant_id, t.seller_id
FROM transactions t
WHERE t.status = 'CAPTURED'
  AND t.created_at > NOW() - INTERVAL '48 hours'
  AND NOT EXISTS (
    SELECT 1 FROM fiscal_invoices fi WHERE fi.transaction_id = t.id
  )
ORDER BY t.created_at DESC;
```

3. Verificar configuracao do provider de NF do seller:
```sql
SELECT tc.fiscal_provider, tc.fiscal_api_key_encrypted IS NOT NULL AS has_api_key,
       tc.fiscal_company_cnpj, tc.fiscal_municipal_code,
       s.name AS seller_name
FROM tenant_configs tc
JOIN sellers s ON s.tenant_id = tc.tenant_id
WHERE s.id = '<seller_id>';
```

4. Testar conectividade com o provider de NF:
```bash
curl -s http://api:8080/health | jq '.entries.fiscal_provider'

# Verificar logs de erro especificos
grep "FiscalInvoice\|NF.*error\|fiscal.*exception" /var/log/fellowcore/*.log | tail -30
```

## Correcao definitiva

### Causa: chave de API do provider expirada ou invalida

```sql
-- Identificar tenant afetado
SELECT tc.tenant_id, tc.fiscal_provider FROM tenant_configs tc
WHERE tc.tenant_id = '<tenant_id>';
```

1. Obter nova chave de API com o seller/contador
2. Atualizar via endpoint admin:
```
PATCH /api/v1/tenants/{tenantId}/config
{
  "fiscalApiKey": "<nova_chave>"
}
```

### Causa: dados cadastrais invalidos (CNPJ, inscricao municipal)

Corrigir dados no `TenantConfig` e solicitar re-emissao manual no portal do provider.

### Retentativa para FAILED_RETRYABLE

```sql
-- Resetar contador de retries para forcar nova tentativa no proximo ciclo do Hangfire
UPDATE fiscal_invoices
SET status = 'PENDING', retry_count = 0, last_attempt_at = NULL, updated_at = NOW()
WHERE id IN ('<id1>', '<id2>')
  AND status = 'FAILED_RETRYABLE';
```

### FAILED_FINAL — emissao manual obrigatoria

Para notas em `FAILED_FINAL`:
1. Emitir manualmente no portal do provider de NF (ex: Nota Carioca, NFSe.io, Prefeitura)
2. Registrar numero da NF emitida:
```sql
UPDATE fiscal_invoices
SET status = 'ISSUED', external_invoice_number = '<numero_nf>',
    issued_at = NOW(), updated_at = NOW()
WHERE id = '<fiscal_invoice_id>';
```

## Como validar

```sql
-- Confirmar que notas retentativas foram processadas
SELECT fi.id, fi.status, fi.external_invoice_number, fi.issued_at
FROM fiscal_invoices fi
WHERE fi.id IN ('<id1>', '<id2>');
```

Verificar que o job do Hangfire processou sem erros:
```
GET /hangfire  -> Jobs -> Succeeded -> filtrar por "FiscalInvoice"
```

## Quando escalar

- `FAILED_FINAL` para mais de 10 notas do mesmo seller — possivel problema cadastral sistematico, acionar contador do seller
- Provider de NF com downtime confirmado > 4h — abrir chamado com o provider e registrar incidente
- Transacao > R$10.000 sem NF por mais de 24h — prioridade fiscal alta, escalar para compliance
