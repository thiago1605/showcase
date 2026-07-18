# FellowCore Production Runbook

## Staging Environment Setup

### Infrastructure Requirements
- PostgreSQL 16+ with SSL enabled (`SSL Mode=Require` in connection string)
- Redis 7+ with TLS and authentication
- MinIO or S3-compatible object storage with HTTPS
- .NET 10 runtime or Docker runtime (production image from GHCR)

### Configuration
All secrets must be injected via environment variables. Never store secrets in config files.

Required environment variables:
- `ConnectionStrings__DefaultConnection` -- PostgreSQL connection string with `SSL Mode=Require`
- `Jwt__SecretKey` -- minimum 32 characters, cryptographically random
- `Security__MasterKey` -- minimum 32 characters, for AES-GCM encryption at rest
- `Security__BackupCodePepper` -- minimum 16 characters
- `Stripe__SecretKey` -- must start with `sk_test_` (staging) or `sk_live_` (production)
- `REDIS_HOST`, `REDIS_PORT`, `REDIS_PASSWORD`
- `Email__ApiKey` -- Resend API key

### Staging-Specific Notes
- Use Stripe test mode keys (`sk_test_*`)
- Seed data is NOT auto-applied (seeder skips in Production/Staging)
- Run migrations explicitly via: `dotnet ef database update --project src/FellowCore.Infrastructure --startup-project src/FellowCore.Api`

---

## Deployment Strategy (Blue-Green)

### Overview
FellowCore uses blue-green deployment to achieve zero-downtime releases.

### Steps
1. **Build**: CI builds and pushes a Docker image tagged with the commit SHA to GHCR.
2. **Deploy to inactive slot**: Pull the new image to the inactive environment (green).
3. **Run migrations**: Execute database migrations against production DB from the green slot before switching traffic.
   ```bash
   dotnet ef database update \
     --project src/FellowCore.Infrastructure \
     --startup-project src/FellowCore.Api \
     --connection "$PRODUCTION_CONNECTION_STRING"
   ```
4. **Health check**: Verify `GET /health` returns `{"status":"Healthy"}` on the green slot.
5. **Switch traffic**: Update the load balancer / reverse proxy to route traffic to the green slot.
6. **Monitor**: Watch logs and metrics for 10-15 minutes.
7. **Decommission blue**: After confirming stability, stop the old (blue) slot.

### Health Check Endpoint
- `GET /health` -- returns aggregate status only (no internal topology leaked)
- Used by Docker HEALTHCHECK, load balancer, and Kubernetes liveness probes

---

## Rollback Procedures

### Application Rollback
1. **Identify the last known-good image tag** (commit SHA) from GHCR.
2. **Re-deploy** the previous image to the inactive slot.
3. **Switch traffic** back to the rolled-back slot.
4. **Verify** via `/health` endpoint and log monitoring.

```bash
# Example: rollback to a specific commit SHA
docker pull ghcr.io/<org>/fellow-pay:<previous-sha>
# Deploy and switch traffic
```

### Database Rollback
EF Core migrations support rollback via `dotnet ef database update <target-migration>`.

```bash
# List applied migrations
dotnet ef migrations list \
  --project src/FellowCore.Infrastructure \
  --startup-project src/FellowCore.Api

# Rollback to a specific migration
dotnet ef database update <MigrationName> \
  --project src/FellowCore.Infrastructure \
  --startup-project src/FellowCore.Api \
  --connection "$PRODUCTION_CONNECTION_STRING"
```

**WARNING**: Destructive migrations (column drops, table drops) cannot be cleanly rolled back. Always review migration SQL before applying.

---

## Migration Rollback Policy

1. **All migrations must be backward-compatible** for at least one release cycle.
   - Add columns as nullable or with defaults.
   - Do NOT drop columns in the same release that stops using them.
2. **Two-phase column removal**:
   - Release N: Stop reading/writing the column in code.
   - Release N+1: Drop the column via migration.
3. **Test migrations** against a staging database clone before production.
4. **Keep migration history** -- never delete migration files from the repository.
5. **Auto-migrate is disabled in Production** -- the `DatabaseSeeder` skips `MigrateAsync()` when `ASPNETCORE_ENVIRONMENT=Production`.

---

## Monitoring and Alerting

### Key Metrics to Monitor
- **API response times** (p50, p95, p99) -- alert if p99 > 2s
- **Error rate** (5xx responses) -- alert if > 1% over 5 minutes
- **Health check failures** -- alert immediately on 2 consecutive failures
- **Database connection pool utilization** -- alert if > 80%
- **Redis memory usage** -- alert if > 80% of limit (256M in compose)
- **Hangfire job failures** -- alert on any failed background job
- **Ledger balance drift** -- reconciliation runs daily at 05:00 UTC

### Log Aggregation
- Structured logs via Serilog with `CorrelationId` for request tracing
- Production log level: `Warning` (default), `Information` (FellowCore namespace)
- All logs include `Application=FellowCore` property for filtering

### Recommended Alerting Stack
- **Prometheus + Grafana** for metrics and dashboards
- **Sentry or Seq** for structured log aggregation and error tracking
- **PagerDuty or Opsgenie** for on-call alerting

### Reconciliation
- Daily reconciliation runs at 05:00 UTC via Hangfire
- Event-driven reconciliation triggers on: capture, refund, payout
- Check `ReconciliationRun` and `ReconciliationIssue` tables for discrepancies
- Dashboard endpoint: `GET /api/reconciliation/runs` (admin role required)

## Modelo Híbrido (Antecipação) — Runbook operacional

Detalhes completos: `docs/advance_cash_flow_design.md`.

### Habilitar antecipação pra um tenant + seller (do zero)

```bash
# 1. Plataforma faz top-up da reserve (R$ 50.000 aporte inicial)
curl -X POST $API/api/v1/advance-reserve/topup \
  -H "X-Api-Key: $TENANT_KEY" -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"amountCents": 5000000}'

# 2. Define limit de exposure pro seller (R$ 10.000)
curl -X PATCH $API/api/v1/sellers/$SELLER_ID/advance-limit \
  -H "X-Api-Key: $TENANT_KEY" -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"advanceCreditLimit": 10000.00}'

# 3. (Opcional) threshold custom de alerta operacional (default = AdvanceAlert:DefaultSellerExposureThresholdCents)
curl -X PATCH $API/api/v1/sellers/$SELLER_ID/advance-alert-threshold \
  -H "X-Api-Key: $TENANT_KEY" -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"thresholdCents": 5000000}'   # R$ 50.000 — pra sellers high-volume
```

### Alertas críticos

| Alert | Significa | Ação |
|-------|-----------|------|
| `AdvanceReserveLow` | reserve > 80% consumida | top-up urgente (loop de captura sem reserve → fallback INSTALLMENT silencioso) |
| `AdvanceReserveNegative` | reserve < 0 (bug, não deveria acontecer) | parar captura ADVANCE imediatamente, investigar bug no `HasReserveFor` |
| `AdvanceSellerExposureHigh` | seller exposure > threshold por 1h | confirmar que reconciler está rodando; se reserve está sangrando, considerar pausar `AutoAdvanceSettlement` do seller |
| `AdvanceReconcilerStale` | sem reconciler bem-sucedido > 4h | checar Hangfire dashboard `/hangfire` — provavelmente Stripe API fora ou cursor preso |

### Toggle entre reconcilers

```jsonc
// appsettings.json
"AdvanceReconciler": {
  "UseStripe": false   // true = StripeAdvanceReconciler (preciso, depende API)
                       // false = AdvanceSettlementReconciler (time-proxy D+30, conservador)
}
```

Mudou pra `true`? Confirmar via `/metrics`:
- `fellowcore_advance_recovered_installments_total` deve incrementar
- `LastStripeAdvanceReconcileAt` deve avançar a cada hora

### Reverter um ADVANCE no caso de problema

1. **TX específica**: refund via webhook/API — automaticamente reverte advance fee + libera reserve/exposure.
2. **Seller suspeito**: `PATCH /api/v1/sellers/{id}/advance-limit` com `{"advanceCreditLimit": 0}` — bloqueia novas TXs ADVANCE sem afetar as já capturadas.
3. **Tenant em emergência**: setar `AutoAdvanceSettlement=false` em todos sellers (script SQL direto se necessário). Captura existente continua, futuras viram INSTALLMENT.
