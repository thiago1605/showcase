# Runbook: Pico de Webhooks Duplicados

**Alert:** `WebhookDuplicateSpike`
**Severity:** P2
**Owner:** Backend + Infra

## Sintoma

Volume anormal de webhooks com mesmo `EventId` (Stripe) ou `CorrelationId` (OpenPix) sendo recebidos multiplas vezes. `WebhookDeliveries` com `IsDuplicate=true` acima do baseline.

## Alerta relacionado

- Metrica `fellowcore_webhook_duplicates_total` com spike
- Logs com `"Duplicate webhook received"` ou `"Idempotency key already processed"`

## Impacto

- Processamento duplicado pode causar entradas duplicadas no ledger se idempotencia falhar
- Carga extra no banco de dados e Redis
- Possivel DOUBLE_CAPTURE se a guarda de idempotencia do `PaymentIntent` falhar

## Acao imediata

1. Quantificar duplicatas recentes por provider:
```sql
SELECT wd.provider, wd.event_type,
       COUNT(*) AS total_deliveries,
       SUM(CASE WHEN wd.is_duplicate THEN 1 ELSE 0 END) AS duplicates,
       MIN(wd.received_at) AS first_seen,
       MAX(wd.received_at) AS last_seen
FROM webhook_deliveries wd
WHERE wd.received_at > NOW() - INTERVAL '2 hours'
GROUP BY wd.provider, wd.event_type
ORDER BY duplicates DESC;
```

2. Verificar se duplicatas resultaram em processamento duplo:
```sql
SELECT wd.event_id, COUNT(*) AS count
FROM webhook_deliveries wd
WHERE wd.received_at > NOW() - INTERVAL '2 hours'
  AND wd.processed = true
GROUP BY wd.event_id
HAVING COUNT(*) > 1
ORDER BY count DESC;
```

3. Verificar se houve DOUBLE_CAPTURE no periodo:
```sql
SELECT ri.type, COUNT(*) FROM reconciliation_issues ri
WHERE ri.type = 'DOUBLE_CAPTURE'
  AND ri.created_at > NOW() - INTERVAL '2 hours';
```

4. Checar saude do load balancer e do provider:
```bash
# Verificar se ha reintenativas no load balancer (timeout muito baixo)
curl -s http://api:8080/health | jq '.entries'

# Stripe: verificar se o endpoint esta respondendo dentro do timeout (30s)
grep "WebhookAuthFilter\|webhook.*timeout" /var/log/fellowcore/*.log | tail -20
```

5. Verificar se Redis de idempotencia esta operacional:
```bash
redis-cli ping
redis-cli info stats | grep "rejected_connections\|keyspace_misses"
```

## Correcao definitiva

### Se o load balancer esta reenviando (timeout muito baixo)

- Aumentar timeout do health check do LB para > 30s
- Verificar se o endpoint de webhook esta respondendo antes do timeout do provider (Stripe exige < 30s)
- Adicionar retry delay no LB para nao reenviar antes de 60s

### Se o provider esta reenviando por falta de resposta 200

- Investigar se algum deploy recente aumentou latencia do handler de webhook
- Verificar se `WebhookAuthFilter` esta adicionando latencia excessiva
- Garantir que o endpoint retorna 200 imediatamente e processa em background (outbox pattern)

### Se Redis de idempotencia caiu durante o pico

- As entregas durante a janela de outage podem ter sido processadas multiplas vezes
- Executar reconciliacao imediata para detectar DOUBLE_CAPTURE:
```
POST /api/v1/reconciliation/runs  { "tenantId": "<id>" }
```

## Como validar

```sql
-- Confirmar retorno ao baseline de duplicatas
SELECT COUNT(*) FROM webhook_deliveries wd
WHERE wd.received_at > NOW() - INTERVAL '15 minutes'
  AND wd.is_duplicate = true;
```

Monitorar metrica `fellowcore_webhook_duplicates_total` por 30 minutos apos correcao.

## Quando escalar

- Qualquer `DOUBLE_CAPTURE` detectado — P1 imediato, acionar Finance
- Redis de idempotencia indisponivel por > 5 minutos — escalar para infra
- Spike de duplicatas proveniente de IP desconhecido — possivel ataque de replay, escalar para seguranca
