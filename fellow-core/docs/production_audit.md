# FellowCore — Production Readiness Audit

**Data**: 2026-05-03
**Baseline**: 612 testes passando (107 Domain + 245 Application + 260 Integration), 16 skipped (sandbox)
**Objetivo**: Identificar e rastrear todos os itens necessários para atingir 100% em cada área

---

## Legenda de Status

- `[ ]` Pendente
- `[x]` Concluído
- `[~]` Aceito como tech debt / não aplicável agora

---

## 1. FUNCIONALIDADE CORE — 100% (Nenhum gap encontrado)

Todas as features de negócio estão implementadas:
- CRUD completo para todas entidades
- State machine de transações completa
- Refund (parcial + total), dispute, payout, subscription, payment link, PIX, relatórios agendados
- Multi-rail orchestration (Stripe Card, Stripe Boleto, OpenPix PIX)
- Outbox, dunning, splits, reconciliação — tudo funcional

---

## 2. SEGURANÇA APLICAÇÃO/API

### Já Corrigidos (Sessão Atual)

- [x] **S1**: WebhookEndpoint.Secret criptografado at rest (ISecurityService.EncryptAsync)
- [x] **S2**: Tenant.ApiSecret nunca persiste no entity (named param `ownerEmail:`)
- [x] **S3**: ReconcilePayoutAsync filtra por `payout.Id` (event-driven path)
- [x] **S4**: CI security scan como gate robusto (falha em erro de execução)
- [x] **S5**: IdempotencyMiddleware habilitado em Testing env + FakeIdempotencyService
- [x] **S6**: OpenPix webhook token comparison timing-safe (CryptographicOperations.FixedTimeEquals)
- [x] **S7**: Tenant.ApiSecret ignorado pelo EF Core (`Ignore(t => t.ApiSecret)`)
- [x] **S8**: Cache-Control: no-store em todas as respostas API
- [x] **S9**: Root endpoint não expõe environment name
- [x] **S10**: Email provider não loga PII (endereços de email removidos dos logs)
- [x] **S11**: Seeder reduz prefixo de API key logado (15 → 8 chars)

### Corrigidos pelo Agente de Segurança (Sessão Atual)

- [x] **S12**: Validadores FluentValidation para 9+ DTOs sem validação server-side
  - Criado `src/FellowCore.Api/Validators/ControllerDtoValidators.cs` (5 validators)
  - Adicionado 4 validators em `src/FellowCore.Application/Modules/Sellers/Validators/SellerValidators.cs`
  - Registrado assembly de validators da API no DI

### Corrigidos (Phase 1 Checklist)

- [x] **S13**: Backup code HMAC pepper agora lido de `Configuration["Security:BackupCodePepper"]`
  - User.HashCode/GenerateBackupCodes/UseBackupCode aceitam pepper como parâmetro
  - AuthService injeta pepper de IConfiguration; validação em produção no Program.cs
- [x] **S14**: UserService.DeleteAsync agora desativa usuário (User.IsActive + Deactivate())
  - Login e RefreshToken rejeitam usuário inativo
  - Migration `AddUserIsActive` com default true para usuários existentes
- [x] **S15**: Tenant.ApiSecret coluna dropada na migration `AddUserIsActive`
  - EF Core já ignorava via `tenant.Ignore(t => t.ApiSecret)`

---

## 3. LEDGER / CONSISTÊNCIA FINANCEIRA

### Pendentes

- [x] **L1**: Global balance check implementado na reconciliação Phase 1
  - Verifica `sum(all entries) == 0` por tenant. Issue type `LEDGER_GLOBAL_IMBALANCE`.

- [x] **L2**: Refund agora reverte PLATFORM_FEE proporcionalmente para Direct Charge
  - `ReversePlatformFeeAsync` debita PLATFORM_FEE e credita PLATFORM_PAYOUT

- [x] **L3**: Dispute hold usa NetAmount — correto por design
  - Seller só tem net no ledger. Fee é tratado separadamente em L5.

- [x] **L4**: Falso positivo — state machine não permite CAPTURED → VOIDED
  - Apenas CAPTURED → REFUNDED ou CAPTURED → CHARGEBACKERROR

- [x] **L5**: Dispute loss agora debita PLATFORM_FEE para Direct Charge
  - Ao perder disputa em Direct Charge, fee é revertida via `ReversePlatformFeeAsync`

- [x] **L6**: Falso positivo — payout já debita ledger ANTES de PROCESSING
  - Concorrência resolvida por optimistic concurrency (xmin) no LedgerService

- [x] **L7**: Payout fee registrada como entry separada no ledger
  - `DebitPayoutFeeAsync`: WALLET → PLATFORM_FEE. Reversão: `ReversePayoutFeeAsync`

- [x] **L8**: Split fees cobertos por L7 (SplitProcessor usa PayoutService.CreateAsync)

- [x] **L9**: Contra-entry correto por construção
  - LedgerService sempre cria pares debit/credit simétricos. Reconciliação Phase 1 valida.

- [~] **L10**: Rounding tolerance hardcoded (1 cent) sem escala
  - **Ação**: Definir política de arredondamento (ex: 0.05% do valor, mínimo 1 cent)
  - **Severidade**: MEDIUM (aceito como tech debt — 1 cent funcional para BRL)

- [x] **L11**: Direct Charge fee reservada em dispute — RESOLVIDO
  - Fee é congelada (PLATFORM_FEE→DISPUTE_FEE) na criação da disputa, liberada na vitória (DISPUTE_FEE→PLATFORM_FEE), e liquidada na perda (DISPUTE_FEE→PLATFORM_PAYOUT). Métodos: HoldDisputeFeeAsync, ReleaseDisputeFeeAsync, SettleDisputeFeeLossAsync. 13 testes em DisputeFlowTests.

- [x] **L12**: Reconciliação contra settlement reports de Stripe/OpenPix
  - **Implementado**: ISettlementReportProvider, StripeSettlementProvider (Balance Transactions API), OpenPixSettlementProvider (Statement API), CsvSettlementProvider (fallback manual), SettlementReconciliationService, Hangfire job diário (04:30 UTC), 19 testes, migration AddSettlementReports
  - **Severidade**: RESOLVIDO

- [x] **L13**: Subscription billing sem ledger entries intermediárias — RESOLVIDO (by design)
  - **Decisão formal**: Não precisa de ledger intermediário. Billing cria TX via TransactionService → webhook registra ledger no CAPTURE. PIX é instant-capture. Idempotency key: sub-{id}-cycle-{n}. Teste: BillingProcessor_LedgerEntriesViaTransaction_NotIntermediate.

---

## 4. TESTES AUTOMATIZADOS

### Serviços — unit tests

- [x] **T1**: AuthService — 30+ unit tests
  - Login, 2FA, token refresh, password reset, account lockout, backup codes, deactivated user
  - Arquivo: `tests/FellowCore.Application.Tests/Services/AuthServiceTests.cs`

- [x] **T2**: SellerService — 11 unit tests
  - Criação, duplicate doc, balance fallback, listing, update, not-found
  - Arquivo: `tests/FellowCore.Application.Tests/Services/SellerServiceTests.cs`

- [x] **T3**: TenantService — 10 unit tests
  - Criação com hashed key, duplicate slug, rotation, provider config
  - Arquivo: `tests/FellowCore.Application.Tests/Services/TenantServiceTests.cs`

- [x] **T4**: PayoutService — 9 unit tests
  - Criação, fee calculation, provider failure reversal, insufficient balance
  - Arquivo: `tests/FellowCore.Application.Tests/Services/PayoutServiceTests.cs`

- [x] **T5**: SubscriptionService — 22 unit tests
  - Lifecycle (create, activate, pause, resume, cancel), billing cycle, max cycles
  - Arquivo: `tests/FellowCore.Application.Tests/Services/SubscriptionServiceTests.cs`

- [x] **T6**: UserService — 8 unit tests
  - Criação, deactivation, tenant isolation, listing
  - Arquivo: `tests/FellowCore.Application.Tests/Services/UserServiceTests.cs`

- [x] **T7**: CustomerService — 11 unit tests
  - CRUD completo, duplicate email, tenant isolation
  - Arquivo: `tests/FellowCore.Application.Tests/Services/CustomerServiceTests.cs`

- [x] **T8**: PixPaymentService — 11 unit tests
  - Create, get, list, tenant isolation, provider exception
  - Arquivo: `tests/FellowCore.Application.Tests/Services/PixPaymentServiceTests.cs`

- [x] **T9**: DashboardService — 10 unit tests
  - Summary aggregation, financial health, tenant isolation
  - Arquivo: `tests/FellowCore.Application.Tests/Services/DashboardServiceTests.cs`

- [x] **T10**: SettlementService — 8 unit tests
  - Multi-seller, rollback on failure, resilience, date handling
  - Arquivo: `tests/FellowCore.Application.Tests/Services/SettlementServiceTests.cs`

- [x] **T11**: AuditLogService — 9 unit tests
  - Log, list, filter, tenant isolation
  - Arquivo: `tests/FellowCore.Application.Tests/Services/AuditLogServiceTests.cs`

### Entidades de domínio

- [x] **T12**: Tenant, TenantConfig, Payout, PaymentLink, LedgerEntry, OutboxMessage, User — testes completos
  - TenantTests, TenantConfigTests, PayoutTests, PaymentLinkTests, LedgerEntryTests, OutboxMessageTests, UserTests
  - +99 testes de domínio (107 → 206)

### Fluxos críticos

- [x] **T13**: Dispute flows — 9 testes (DisputeFlowTests.cs)
  - Dispute created (hold, idempotency), won (release), lost (settle, fee reversal)

- [x] **T14**: Payout failure & reversal + multi-seller split — 9 testes (PayoutFlowTests.cs)
  - Failure reversal (net + fee), exception path, retry on reversal failure, SplitProcessor

- [x] **T15**: Reconciliation Phase 5 — 8 testes (ReconciliationPhase5Tests.cs)
  - DOUBLE_CAPTURE, DISPUTE_ORPHAN, REFUND_TOTAL_MISMATCH, LEDGER_GLOBAL_IMBALANCE

- [x] **T16**: Concurrent double-capture + race conditions — 8 testes (ConcurrencyTests.cs)
  - PaymentIntent collision, ExternalReferenceId fallback, DirectCharge ledger

- [x] **T17**: Payment link flows — 10 testes (PaymentLinkFlowTests.cs)
  - Reserve + complete, fail rollback, expiration, max usage

- [x] **T18**: Subscription flows — 22 testes (SubscriptionFlowTests.cs)
  - Pause/resume, billing, dunning, max cycles, DunningProcessor

### Background processors

- [x] **T19**: OutboxProcessor, DunningProcessor, SplitProcessor, ScheduledReportProcessor — unit tests
  - Arquivos: `tests/FellowCore.Application.Tests/Processors/`

---

## 5. RESILIÊNCIA OPERACIONAL

### Concluídos

- [x] **R1**: Circuit breaker para Stripe e OpenPix HttpClients
  - 5 falhas consecutivas em 30s → circuito abre por 30s. LogCritical na abertura.
  - Arquivo: `src/FellowCore.Api/Extensions/ServiceCollectionExtensions.cs`

- [x] **R2**: Timeout explícito (30s) + retry com exponential backoff (3 retries, 1s base + jitter)
  - Apenas erros transientes (5xx, timeouts, HttpRequestException)

- [x] **R3**: Bulkhead/concurrency limiter (max 10 concurrent, queue 20) por provider
  - Outermost policy no resilience pipeline

- [x] **R4**: OutboxProcessor DLQ — LogCritical quando max retries excedido
  - `OutboxMessage.MarkDeadLetter()` marca como processado com prefixo `[DLQ]`
  - Arquivo: `src/FellowCore.Infrastructure/Workers/Processors/OutboxProcessor.cs`

- [x] **R5**: Graceful shutdown — configurable drain delay (GracefulShutdown:DrainDelaySeconds, default 10s)
  - `ApplicationStopping` callback com Thread.Sleep para in-flight requests
  - Arquivo: `src/FellowCore.Api/Program.cs`

- [x] **R6**: Worker health checks — HangfireHealthCheck verifica recurring jobs (stale > 2h = Degraded)
  - Arquivo: `src/FellowCore.Api/HealthChecks/HangfireHealthCheck.cs`

- [x] **R7**: Email retry — 3 retries com exponential backoff (1s, 2s, 4s)
  - 4xx (exceto 429) não faz retry. 5xx e 429 fazem retry.
  - Arquivo: `src/FellowCore.Infrastructure/Email/ResendEmailProvider.cs`

---

## 6. OBSERVABILIDADE / AUDITORIA PRODUÇÃO

### Concluídos

- [x] **O1**: OpenTelemetry + Prometheus + métricas de negócio
  - 5 custom instruments: transactions_total, refunds_total, payouts_total, provider_request_duration, webhook_deliveries
  - Endpoint `/metrics` para scraping Prometheus
  - Pacotes: OpenTelemetry.Extensions.Hosting, Exporter.Prometheus.AspNetCore, Instrumentation.AspNetCore/Http
  - Arquivo: `src/FellowCore.Api/Metrics/FellowCoreMetrics.cs`

- [x] **O2**: Health checks para Stripe e OpenPix
  - StripeHealthCheck: verifica API via balance endpoint
  - OpenPixHealthCheck: verifica API via static QR list
  - Arquivos: `src/FellowCore.Api/HealthChecks/`

- [x] **O3**: Health check para Hangfire recurring jobs
  - Verifica se jobs existem e não estão stale (> 2h = Degraded)
  - Arquivo: `src/FellowCore.Api/HealthChecks/HangfireHealthCheck.cs`

- [x] **O4/O5**: Structured logs com TenantId/SellerId context
  - CorrelationIdMiddleware push TenantId (JWT claim ou HttpContext.Items) e SellerId
  - Todos os logs downstream incluem tenant context automaticamente
  - Arquivo: `src/FellowCore.Api/Middlewares/CorrelationIdMiddleware.cs`

- [x] **O6**: Financial health dashboard endpoint
  - `GET /api/v1/dashboard/financial-health` (SUPER_ADMIN, OWNER)
  - Agrega: reconciliation issues, pending payouts, failed webhooks, open disputes
  - Arquivo: `src/FellowCore.Api/Controllers/DashboardController.cs`

---

## 7. DEPLOY / INFRA

### Concluídos

- [x] **D1**: CI/CD com docker build + push para GHCR
  - Job `docker-build-push` roda em push para main
  - Push para `ghcr.io/<owner>/fellowcore:<sha>` e `:latest`
  - Arquivo: `.github/workflows/ci.yml`

- [x] **D2**: Container registry GHCR configurado
  - Usa `GITHUB_TOKEN` para autenticação, sem secrets extras

- [x] **D3**: docker-compose com resource limits
  - API: 2 CPU, limites de memória. Redis: 0.5 CPU. PostgreSQL: 1 CPU. MinIO: 0.5 CPU
  - Arquivo: `docker-compose.yml`

- [x] **D4**: `.dockerignore` criado
  - Exclui `.git`, `.github`, `docs`, `tests`, `node_modules`, `bin`, `obj`, `.env*`, `.claude`
  - Arquivo: `.dockerignore`

- [x] **D5**: HEALTHCHECK no Dockerfile
  - `HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD curl -f http://localhost:8080/health || exit 1`
  - Arquivo: `Dockerfile`

- [x] **D6**: `appsettings.Production.json` criado
  - Log levels, timeouts, rate limits, pool sizes, SSL Mode documentation
  - Secrets via env vars (Jwt__SecretKey, Security__MasterKey, etc.)
  - Arquivo: `src/FellowCore.Api/appsettings.Production.json`

- [x] **D7**: Auto-migrate desabilitado em Production
  - `DatabaseSeeder` verifica `environment.IsProduction()` e pula migração
  - Arquivo: `src/FellowCore.Infrastructure/Database/Seeding/DatabaseSeeder.cs`

- [x] **D8**: SSL/TLS obrigatório documentado
  - `appsettings.Production.json` documenta `SSL Mode=Require` na connection string

- [x] **D9**: Staging + rollback strategy documentados
  - `docs/production_runbook.md` criado com blue-green deploy, rollback app e DB
  - Arquivo: `docs/production_runbook.md`

- [x] **D10**: Trivy container scan no CI
  - `aquasecurity/trivy-action@master` com output SARIF, upload para GitHub Security
  - Arquivo: `.github/workflows/ci.yml`

- [x] **D11**: docker-compose credenciais default (MinIO, Redis, PG) — RESOLVIDO
  - **Implementado**: CredentialValidator no startup bloqueia Production com credenciais dev/default. Valida JWT secret (>32 chars, nao dev placeholder), Stripe sk_test_, OpenPix sandbox, localhost DB. Testes: CredentialValidatorTests.cs (6 tests).

---

## RESUMO POR ÁREA

| Área | Total | Concluídos | Pendentes | Tech Debt | % Concluído |
|------|-------|------------|-----------|-----------|-------------|
| Funcionalidade Core | 0 | 0 | 0 | 0 | **100%** |
| Segurança | 15 | 15 | 0 | 0 | **100%** |
| Ledger/Consistência | 13 | 9 | 0 | 4 | **100%** |
| Testes | 19 | 19 | 0 | 0 | **100%** |
| Resiliência | 7 | 7 | 0 | 0 | **100%** |
| Observabilidade | 6 | 6 | 0 | 0 | **100%** |
| Deploy/Infra | 11 | 10 | 0 | 1 | **100%** |

**Total geral**: 71 itens | 66 concluídos | 0 pendentes | 5 tech debt

**Baseline de testes**: 948 passed, 16 skipped (sandbox), 0 failures
- Domain: 206 (was 107, +99)
- Application: 482 (was 245, +237)
- Integration: 260 (unchanged)

### Todas as áreas em 100%

Segurança, Ledger, Testes, Resiliência, Observabilidade e Deploy/Infra �� todos concluídos.
1 item aceito como tech debt (L10 — tolerancia de arredondamento) com justificativa formal. L11, L12, L13 e D11 foram resolvidos.
