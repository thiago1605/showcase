# Runbook: EF Core Migration Procedure (Production)

**Owner:** Backend + DBA

## Regra de Ouro

**NUNCA usar auto-migration em production.** `Database.MigrateAsync()` so roda em Development/Staging.

## Pre-Requisitos

- [ ] Migration foi testada em staging com snapshot do banco de production
- [ ] Backup do banco de production foi executado e verificado
- [ ] Janela de manutencao comunicada (se migration envolve ALTER TABLE em tabelas grandes)
- [ ] Rollback script foi preparado (ver secao abaixo)
- [ ] CI passou com a migration aplicada

## Passo a Passo

### 1. Gerar SQL Script da Migration

```bash
# Gerar script SQL da migration pendente
dotnet ef migrations script --idempotent \
  --project src/FellowCore.Infrastructure \
  --startup-project src/FellowCore.Api \
  --output migrations/pending.sql

# Revisar o script gerado
cat migrations/pending.sql
```

### 2. Dry-Run em Staging

```bash
# Aplicar em staging primeiro
PGPASSWORD=$STAGING_DB_PWD psql -h $STAGING_DB_HOST -U $DB_USER -d fellowcore_staging \
  -f migrations/pending.sql

# Verificar resultado
dotnet ef database update --connection "$STAGING_CONNECTION_STRING" \
  --project src/FellowCore.Infrastructure \
  --startup-project src/FellowCore.Api
```

### 3. Backup Pre-Migration (Production)

```bash
# Backup completo
pg_dump -h $PROD_DB_HOST -U $DB_USER -d fellowcore \
  -F c -f backups/pre-migration-$(date +%Y%m%d-%H%M%S).dump

# Verificar backup
pg_restore --list backups/pre-migration-*.dump | head -20
```

### 4. Aplicar Migration (Production)

```bash
# Aplicar script SQL gerado no passo 1
PGPASSWORD=$PROD_DB_PWD psql -h $PROD_DB_HOST -U $DB_USER -d fellowcore \
  -f migrations/pending.sql

# OU via EF CLI (menos recomendado para production)
dotnet ef database update --connection "$PROD_CONNECTION_STRING" \
  --project src/FellowCore.Infrastructure \
  --startup-project src/FellowCore.Api
```

### 5. Verificacao Pos-Migration

```bash
# Verificar tabela __EFMigrationsHistory
PGPASSWORD=$PROD_DB_PWD psql -h $PROD_DB_HOST -U $DB_USER -d fellowcore \
  -c "SELECT * FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 5;"

# Health check
curl -s http://api:8080/health | jq '.status'

# Rodar testes de integridade
curl -s http://api:8080/api/v1/dashboard/financial-health
```

## Rollback

### Opcao 1: Revert Migration (se migration tem Down method)

```bash
dotnet ef database update <PREVIOUS_MIGRATION_NAME> \
  --connection "$PROD_CONNECTION_STRING" \
  --project src/FellowCore.Infrastructure \
  --startup-project src/FellowCore.Api
```

### Opcao 2: Restore do Backup

```bash
# Parar API
docker stop fellowcore-api

# Restore
pg_restore -h $PROD_DB_HOST -U $DB_USER -d fellowcore \
  --clean --if-exists \
  backups/pre-migration-*.dump

# Reiniciar API
docker start fellowcore-api
```

## Checklist Pre-Deploy

- [ ] Migration gera SQL idempotente (IF NOT EXISTS)
- [ ] Nao ha DROP COLUMN em tabelas com dados (usar soft-delete ou phased migration)
- [ ] ALTER TABLE em tabelas grandes usa CONCURRENTLY quando possivel
- [ ] Indices novos sao criados com CREATE INDEX CONCURRENTLY
- [ ] Migration nao requer lock exclusivo em tabelas hot (transactions, ledger_entries)
- [ ] Backup verificado
- [ ] Rollback testado em staging
