# Runbook: Provider Down (Stripe / OpenPix)

**Alert:** `StripeCircuitBreakerOpen`, `OpenPixCircuitBreakerOpen`, `HighProviderTimeoutRate`
**Severity:** P1
**Owner:** Backend

## Sintoma

O circuit breaker do provider esta aberto ou a taxa de timeout esta acima de 10%.

## Impacto

- Novas transacoes do tipo afetado falharao
- Webhooks podem atrasar (o provider envia quando voltar)
- Payouts podem falhar se o provider estiver indisponivel

## Diagnostico

1. Verificar status page do provider:
   - Stripe: https://status.stripe.com
   - OpenPix: https://status.openpix.com.br

2. Verificar logs de erro:
```
grep "circuit breaker" /var/log/fellowcore/*.log | tail -50
grep "SocketException\|HttpRequestException\|TaskCanceledException" /var/log/fellowcore/*.log | tail -20
```

3. Verificar metricas de latencia:
```
curl -s http://api:8080/metrics | grep fellowcore_provider
```

4. Verificar health check:
```
curl -s http://api:8080/health | jq '.entries'
```

## Mitigacao

1. **Nao desabilitar o circuit breaker** — ele protege contra cascade failure
2. O circuit breaker se auto-recupera (half-open → closed) quando o provider voltar
3. Se o downtime for prolongado (> 30min):
   - Ativar pagina de manutencao no checkout se disponivel
   - Comunicar sellers via email se impacto > 1h
4. Transacoes falhadas podem ser retentadas manualmente apos recuperacao
5. Verificar se webhooks pendentes foram reprocessados apos recuperacao

## Configuracao do Circuit Breaker

- Break duration: 30s (padrao Polly)
- Failure threshold: 5 falhas consecutivas
- Timeout: 30s por request
- Retry: 3x exponential backoff

## Prevencao

- Health checks monitoram providers a cada 30s
- Metricas Prometheus rastreiam latencia e erros por provider
- Graceful degradation: transacoes de outros providers continuam funcionando
