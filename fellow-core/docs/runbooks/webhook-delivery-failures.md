# Runbook: Webhook Delivery Failures

**Alert:** `HighWebhookDeliveryFailureRate`
**Severity:** P1
**Owner:** Backend

## Sintoma

Mais de 20% das entregas de webhook estao falhando nos ultimos 10 minutos.

## Impacto

- Tenants nao recebem notificacoes de eventos (payment captured, refund, dispute, etc.)
- Integracao de sellers pode ficar desatualizada
- Retry automatico reenvia com backoff, mas pode atrasar

## Diagnostico

1. Verificar deliveries falhadas recentes:
```sql
SELECT endpoint_id, url, status_code, error_message, created_at
FROM webhook_deliveries
WHERE status = 'FAILED'
  AND created_at > NOW() - INTERVAL '1 hour'
ORDER BY created_at DESC
LIMIT 50;
```

2. Agrupar por endpoint para identificar endpoints problematicos:
```sql
SELECT we.url, COUNT(*) as failures,
       MAX(wd.status_code) as last_status
FROM webhook_deliveries wd
JOIN webhook_endpoints we ON wd.endpoint_id = we.id
WHERE wd.status = 'FAILED'
  AND wd.created_at > NOW() - INTERVAL '1 hour'
GROUP BY we.url
ORDER BY failures DESC;
```

3. Verificar se e um problema de rede ou do destino:
   - Status 0 / timeout → rede ou endpoint offline
   - Status 4xx → endpoint rejeitando payload
   - Status 5xx → endpoint com erro interno

## Mitigacao

1. Se for um endpoint especifico falhando:
   - Verificar se o endpoint do tenant esta online
   - Contatar tenant se necessario
   - Webhook retry automatico cobre ate 3 tentativas com backoff
2. Se for falha generalizada:
   - Verificar firewall/rede de saida
   - Verificar se DNS resolver esta funcionando
   - Verificar se `SocketsHttpHandler.ConnectCallback` nao esta bloqueando IPs validos
3. Retry manual via API:
   ```
   POST /api/v1/webhooks/{deliveryId}/retry
   ```

## Prevencao

- Webhook client tem timeout de 10s
- Retry automatico: 3 tentativas com exponential backoff
- SSRF protection: `ConnectCallback` bloqueia IPs privados no runtime
- Metricas: `fellowcore_webhook_deliveries_total`, `fellowcore_webhook_delivery_failures_total`
