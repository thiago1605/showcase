# Runbook: Database / Redis Down

**Alert:** `DatabaseUnavailable`, `RedisUnavailable`
**Severity:** P0
**Owner:** Infra

## Sintoma

Health check de PostgreSQL ou Redis esta falhando.

## Impacto

### PostgreSQL Down
- TODAS as operacoes de escrita falham
- API retorna 503
- Hangfire jobs param
- Ledger nao registra entries

### Redis Down
- Cache indisponivel (degradacao de performance, nao falha total)
- Idempotency middleware nao funciona (risco de duplicacao)
- Rate limiting nao funciona
- API continua funcionando mas sem protecoes de cache

## Diagnostico

1. Verificar health endpoint:
```
curl -s http://api:8080/health | jq '.'
```

2. Verificar conectividade PostgreSQL:
```
PGPASSWORD=<pwd> psql -h <host> -U <user> -d <db> -c "SELECT 1;"
```

3. Verificar conectividade Redis:
```
redis-cli -h <host> -a <pwd> PING
```

4. Verificar logs do container:
```
docker logs fellowcore-db --tail 50
docker logs fellowcore-redis --tail 50
```

5. Verificar disco/memoria do host:
```
df -h
free -m
```

## Mitigacao

### PostgreSQL Down
1. **Prioridade maxima** — sem banco, nenhuma operacao financeira funciona
2. Verificar se o processo do PostgreSQL esta rodando
3. Verificar se disco esta cheio (causa mais comum de crash)
4. Se WAL corrompido: restaurar do ultimo backup
5. Se OOM killed: aumentar limites de memoria
6. **NAO deletar WAL files** sem entender o impacto

### Redis Down
1. Prioridade alta mas nao critica (API continua sem cache)
2. Reiniciar Redis: `docker restart fellowcore-redis`
3. Se persistencia RDB corrompida: `redis-cli FLUSHALL` e reiniciar
4. Cache se rehydrata automaticamente (cold start ok)
5. **Atentar para idempotency**: requests duplicados podem ser processados durante downtime

### Pos-Recuperacao
1. Verificar se Hangfire jobs atrasados estao sendo processados
2. Rodar reconciliacao manual para o periodo de downtime:
   ```
   POST /api/v1/reconciliation/run
   ```
3. Verificar se webhooks pendentes foram reprocessados
4. Monitorar metricas por 30min apos recuperacao

## Prevencao

- Health checks executam a cada 30s
- Backup automatico do PostgreSQL (pg_dump diario)
- Redis persistence com RDB snapshots
- Alertas de disco e memoria devem disparar antes de ficar critico
- Connection pool resiliente com retry automatico
