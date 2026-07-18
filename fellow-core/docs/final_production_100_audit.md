# FellowCore — Final Production 100% Audit

Data: 2026-05-03

Objetivo: transformar o FellowCore de "production candidate / producao controlada" em "producao fintech madura, sem ressalvas operacionais conhecidas".

Este arquivo complementa `docs/production_audit.md`. O `production_audit.md` fechou os itens de codigo e hardening interno. Este checklist cobre os gaps restantes levantados pelo CODEX e pelo Claude Code: reconciliacao externa, alertas reais, validacao operacional, infraestrutura real, tech debts aceitos e maturidade de design/operacao.

Status:

- `[ ]` Pendente
- `[x]` Concluido
- `[~]` Aceito formalmente como risco/tech debt com mitigacao documentada

Regra: nao marcar item como `[x]` sem evidencia objetiva: codigo, teste, config, dashboard, alerta, runbook, script, link interno ou resultado de execucao.

---

## 1. Reconciliacao Externa / Settlement Reports

Este e o maior gap restante para producao fintech plena. Sem isso, o sistema depende do ledger interno e de reconciliacao manual.

### REX1 — Settlement Report Provider Abstraction

- [x] Criar interface `ISettlementReportProvider`.
- [x] Criar modelos normalizados de settlement report:
  - provider
  - provider transaction id
  - charge id / payment intent id
  - payout id
  - gross amount
  - fee amount
  - net amount
  - refund amount
  - dispute amount
  - settlement date
  - currency
  - status
- [x] Criar persistencia para settlement reports importados.
- [x] Criar fake provider para testes.
- [x] Adicionar testes unitarios do parser/importador.
- [x] Adicionar testes de reconciliacao contra report fake.

Evidencia:

```text
Arquivos: ISettlementReportProvider.cs, SettlementReport.cs, SettlementReportItem.cs, ISettlementReportRepository.cs, SettlementReportRepository.cs, FakeSettlementProvider.cs
Enums: SettlementItemType, SettlementItemMatchStatus (FellowCoreEnums.cs)
Migrations: AddSettlementReports
Testes: SettlementReconciliationTests.cs (19 tests — entity, provider, CSV, fake, reconciliation service)
Resultado: 19 passed, 0 failed
```

### REX2 — Stripe Settlement Reports

- [x] Implementar importador Stripe para balance transactions / reporting equivalente.
- [x] Mapear:
  - charge captured
  - application fee
  - refund
  - dispute
  - payout
  - adjustment
- [x] Criar teste com fixture JSON realista do Stripe.
- [x] Reconciliation deve detectar:
  - item existe no Stripe e nao existe no ledger
  - item existe no ledger e nao existe no Stripe
  - valor divergente
  - fee divergente
  - payout divergente
- [x] Documentar limitacoes do modo sandbox vs live.

Evidencia:

```text
Arquivos: StripeSettlementProvider.cs, StripeBalanceTransaction/StripeBalanceTransactionListResponse (StripeModels.cs), ListBalanceTransactionsAsync (IStripeApiClient.cs, StripeApiClient.cs)
Fixtures: tests/FellowCore.Application.Tests/Fixtures/stripe_balance_transactions.json
Testes: StripeProvider_ImportsBalanceTransactions_MapsCorrectly, StripeProvider_EmptyResponse_ReturnsEmptyList, Reconciliation_AllMatched, Reconciliation_AmountMismatch, Reconciliation_MissingInternal
Resultado: All passed
Nota sandbox: Balance Transactions API funciona em sandbox Stripe. Em live, retorna dados reais. Em sandbox, retorna fixtures de teste. A reconciliacao funciona identicamente.
```

### REX3 — OpenPix Settlement Reports

- [x] Verificar API/export disponivel da OpenPix para extrato/transactions/payouts.
- [x] Implementar provider OpenPix ou documentar oficialmente indisponibilidade da API.
- [x] Se API nao existir, criar importador CSV/manual com schema validado.
- [x] Criar fixture realista.
- [x] Reconciliation deve comparar OpenPix vs ledger interno.

Evidencia:

```text
Decisao API/CSV: OpenPix tem GetStatementAsync (API existente no IOpenPixApiClient). Provider usa API. Tambem criado CsvSettlementProvider como fallback para importacao manual.
Arquivos: OpenPixSettlementProvider.cs, CsvSettlementProvider.cs
Fixtures: tests/FellowCore.Application.Tests/Fixtures/settlement_import.csv
Testes: OpenPixProvider_ImportsStatement_MapsCorrectly, OpenPixProvider_EmptyStatement_ReturnsEmptyList, CsvProvider_ParsesValidCsv, CsvProvider_EmptyCsv_ReturnsEmpty, CsvProvider_MalformedRows_SkipsInvalid
Resultado: All passed
```

### REX4 — Daily Automated Reconciliation Job

- [x] Criar job diario para importar settlement reports.
- [x] Criar job diario para reconciliar reports externos vs ledger.
- [x] Gerar `ReconciliationIssue` para divergencias.
- [x] Expor status no financial health dashboard.
- [x] Criar alerta quando houver issue `CRITICAL`.
- [ ] Criar runbook de investigacao de divergencia financeira. *(sera criado no grupo ALT3)*

Evidencia:

```text
Jobs: Hangfire "daily-settlement-reconciliation" Cron.Daily(4, 30) — 04:30 UTC
Service: SettlementReconciliationService.RunDailySettlementReconciliationAsync
Dashboard: Reconciliation issues ja expostos via GET /api/v1/dashboard/financial-health
Alertas: DispatchSettlementAlertsAsync envia alertas criticos via IAlertService
Testes: Reconciliation_AllMatched, Reconciliation_AmountMismatch, Reconciliation_MissingInternal, Reconciliation_ProviderError
Resultado: All passed
```

---

## 2. Alertas Reais / On-Call

Metricas e health checks ja existem. Para producao real, precisam virar alertas acionaveis.

### ALT1 — Alert Rules

- [x] Criar regras Prometheus/Grafana para:
  - API error rate alto
  - API latency p95/p99 alta
  - Stripe circuit breaker aberto
  - OpenPix circuit breaker aberto
  - provider timeout rate alto
  - webhook delivery failure rate alto
  - outbox DLQ > 0
  - reconciliation critical issues > 0
  - ledger global imbalance
  - pending payouts antigos
  - failed payouts
  - open disputes acima de limite
  - Hangfire jobs stale/degraded
  - database unavailable
  - Redis unavailable
  - disk/memory/cpu alto
- [x] Versionar regras em `infra/alerts/` ou caminho equivalente.
- [x] Documentar thresholds.

Evidencia:

```text
Arquivos de alerta: infra/alerts/fellowcore-alerts.yml (22 alert rules, 7 groups)
Severidades: P0 (dinheiro/ledger/DB), P1 (pagamentos/webhooks/payouts), P2 (degradacao)
Thresholds: API error >5% (5m), latency p95 >2s (10m), p99 >5s (5m), provider timeout >10% (5m), webhook failure >20% (10m), DLQ >0, disputes >10, payouts >24h
```

### ALT2 — Notification Channels

- [ ] Configurar canal de alerta real:
  - Slack, Discord, PagerDuty, OpsGenie ou email operacional.
- [ ] Definir severidades:
  - P0 dinheiro/ledger
  - P1 pagamentos/webhooks/payouts
  - P2 degradacao operacional
- [ ] Testar envio de alerta.
- [ ] Documentar responsavel/on-call.

Evidencia:

```text
Canal:
Teste de alerta:
Responsavel:
```

### ALT3 — Alert Runbooks

- [x] Criar runbook para ledger imbalance.
- [x] Criar runbook para provider down.
- [x] Criar runbook para webhook delivery failures.
- [x] Criar runbook para payout stuck/failed.
- [x] Criar runbook para reconciliation mismatch.
- [x] Criar runbook para database/Redis down.

Evidencia:

```text
Arquivo: docs/runbooks/ (6 runbooks)
Runbooks: ledger-imbalance.md, provider-down.md, webhook-delivery-failures.md, payout-stuck.md, reconciliation-mismatch.md, database-redis-down.md
Cada runbook cobre: sintoma, impacto, diagnostico (queries SQL/metricas), mitigacao step-by-step, prevencao
```

---

## 3. Staging Real / Smoke Tests

O sistema so deve ir para producao depois de validar fluxo real em staging com credenciais sandbox de providers.

### STG1 — Staging Environment

- [ ] Criar ambiente staging com:
  - API
  - PostgreSQL
  - Redis
  - Hangfire
  - object storage se aplicavel
  - Prometheus/Grafana ou equivalente
- [ ] Configurar secrets de sandbox Stripe/OpenPix.
- [ ] Configurar dominio/HTTPS.
- [ ] Configurar CORS real.
- [ ] Configurar backup do banco staging.
- [ ] Documentar URL e variaveis necessarias.

Evidencia:

```text
URL staging:
Health check:
Secrets configurados:
```

### STG2 — Smoke Tests End-To-End

- [ ] Criar script de smoke test automatizado para staging.
- [ ] Cobrir:
  - tenant create/rotate key
  - seller create
  - card payment
  - boleto payment
  - PIX payment
  - payment link payment
  - webhook Stripe
  - webhook OpenPix
  - refund parcial
  - refund total
  - dispute simulated/manual fixture
  - payout
  - webhook endpoint delivery/retry
  - reconciliation run
  - financial health endpoint
- [ ] Smoke test deve gerar relatorio com IDs dos objetos criados.
- [ ] Smoke test deve limpar ou isolar dados de teste.

Evidencia:

```text
Script:
Ultima execucao:
Resultado:
```

### STG3 — Sandbox Provider Validation

- [ ] Validar Stripe sandbox com eventos reais assinados.
- [ ] Validar OpenPix sandbox com eventos reais ou fixtures assinadas se sandbox nao suportar.
- [ ] Confirmar que webhooks chegam via dominio publico HTTPS.
- [ ] Confirmar replay protection.
- [ ] Confirmar idempotencia em retry real.

Evidencia:

```text
Stripe:
OpenPix:
Webhooks:
```

---

## 4. Load, Stress, Chaos E Resiliencia Real

### LSC1 — Load Test Baseline

- [ ] Definir meta inicial:
  - requests/s
  - pagamentos/min
  - webhook events/min
  - p95 latency
  - p99 latency
  - error budget
- [ ] Rodar k6 ou ferramenta equivalente em staging.
- [ ] Salvar resultados em `tests/results/` ou docs.
- [ ] Ajustar pool de DB, Redis, HttpClient e Hangfire conforme resultado.

Evidencia:

```text
Ferramenta:
Meta:
Resultado:
Gargalos:
```

### LSC2 — Financial Concurrency Tests In Staging

- [ ] Rodar concorrencia real para:
  - double capture
  - payment link max uses
  - two payouts same balance
  - refund while payout processing
  - webhook duplicate/replay
- [ ] Confirmar ausencia de ledger drift.
- [ ] Confirmar reconciliation limpa depois do teste.

Evidencia:

```text
Scripts:
Resultado:
Ledger drift:
```

### LSC3 — Chaos Tests

- [ ] Derrubar Redis durante POSTs idempotentes.
- [ ] Derrubar provider fake/timeout.
- [ ] Derrubar Hangfire worker.
- [ ] Simular database transient failure.
- [ ] Confirmar circuit breakers, retries, DLQ e alertas.
- [ ] Documentar comportamento esperado vs observado.

Evidencia:

```text
Cenarios:
Resultado:
Alertas disparados:
```

---

## 5. Secrets, Compliance E Acesso Operacional

### SECOPS1 — Secrets Manager

- [ ] Definir provider de secrets:
  - AWS Secrets Manager, GCP Secret Manager, Azure Key Vault, Doppler, 1Password ou equivalente.
- [ ] Mover production secrets para secrets manager.
- [ ] Remover secrets de arquivos locais.
- [ ] Validar rotacao:
  - `Jwt:SecretKey`
  - `Security:MasterKey`
  - `Security:BackupCodePepper`
  - Stripe keys
  - OpenPix keys
  - Resend key
  - DB credentials
  - Redis credentials
- [ ] Documentar procedimento de rotacao.

Evidencia:

```text
Provider:
Secrets:
Runbook:
```

### SECOPS2 — Access Control Operacional

- [ ] Definir quem pode acessar producao.
- [ ] Definir quem pode acessar banco.
- [ ] Definir quem pode acessar logs.
- [ ] Definir quem pode executar migrations.
- [ ] Definir quem pode rodar settlement/reconciliation manual.
- [ ] Ativar MFA em todos os acessos operacionais.
- [ ] Documentar break-glass access.

Evidencia:

```text
Politica:
MFA:
Break-glass:
```

### SECOPS3 — LGPD / PII / Retention

- [x] Mapear PII armazenada:
  - nome (sellers, users, payer)
  - email (sellers, users, payer)
  - documento (sellers, payer — CPF/CNPJ)
  - telefone: NAO armazenado
  - endereco: NAO armazenado
  - logs/eventos: IPs e user-agent em login_logs (180 dias)
- [x] Definir politica de retencao.
- [x] Definir processo de delecao/anonymization.
- [x] Garantir que logs nao contem PII sensivel.
- [x] Documentar DSR/LGPD workflow.

Evidencia:

```text
Mapa PII: docs/lgpd-pii-map.md (5 tabelas: sellers, users, transactions, subscriptions, login_logs)
Retencao: DataRetentionProcessor (webhook 90d, outbox 30d, login 180d, recon 365d, fiscal 5 anos)
DSR/LGPD: Documentado — direito de acesso, eliminacao (anonymization), portabilidade
Logs: Structured logging Serilog, CorrelationId sem PII, dados de cartao NUNCA logados
```

### SECOPS4 — PCI Scope

- [x] Documentar PCI scope.
- [x] Confirmar que card data sensivel nao toca servidor FellowCore.
- [x] Confirmar uso de Stripe client-side/tokenization.
- [x] Documentar SAQ esperado.
- [x] Adicionar nota no runbook de compliance.

Evidencia:

```text
PCI scope: docs/pci-scope.md
SAQ: SAQ A-EP — Stripe.js tokenizacao client-side, PAN/CVV/expiry NUNCA tocam server
Card data: Nenhuma API aceita dados de cartao como parametro. Apenas tokens (pm_xxx, pi_xxx).
Checkout: /checkout/config retorna apenas stripePk (public key). stripe.js carregado de js.stripe.com.
Controles: TLS 1.2+, CSP, XSS prevention, SSRF protection, API keys hashed
```

---

## 6. Database, Backup, DR E Migrations

### DB1 — Production Migration Procedure

- [x] Auto-migration em production deve permanecer desabilitada.
- [x] Criar script/job de migration separado.
- [x] Adicionar dry-run/checklist pre-migration.
- [x] Adicionar backup antes de migration.
- [x] Documentar rollback de migration.
- [ ] Testar migration em staging com snapshot. (requer staging real — infra-dependent)

Evidencia:

```text
Script/job: dotnet ef migrations script --idempotent (documentado no runbook)
Runbook: docs/runbooks/migration-procedure.md
Auto-migration: desabilitada — SeedDatabase() so roda em IsDevelopment(). MigrateAsync() nao existe em Program.cs.
Pre-migration checklist, backup, rollback, SQL idempotente — tudo documentado no runbook.
```

### DB2 — Backup And Restore

- [ ] Configurar backup automatico PostgreSQL.
- [ ] Definir RPO/RTO.
- [ ] Testar restore.
- [ ] Documentar restore step-by-step.
- [ ] Criar alerta para backup falho.

Evidencia:

```text
RPO:
RTO:
Ultimo restore test:
Alerta:
```

### DB3 — Data Retention And Archival

- [x] Definir retencao para:
  - audit logs: NEVER (regulatory)
  - webhook deliveries: 90 dias
  - idempotency keys: 24h (Redis TTL)
  - outbox messages: 30 dias (apos processamento)
  - reconciliation runs/issues: 365 dias
  - login logs: 180 dias
  - settlement reports/items: 365 dias
  - ledger_entries/transactions: NEVER (regulatory/financial)
- [x] Implementar cleanup/archive jobs.
- [x] Adicionar metricas/alertas para tabelas crescendo demais.

Evidencia:

```text
Politica: Documentada em DataRetentionProcessor.cs (comments no topo)
Jobs: DataRetentionProcessor — Hangfire recurring job semanal (domingos 03:00 UTC)
Arquivo: src/FellowCore.Infrastructure/Workers/Processors/DataRetentionProcessor.cs
Registro: ServiceCollectionExtensions.cs (RecurringJob "data-retention")
Metricas: Prometheus alertas em fellowcore-alerts.yml monitoram crescimento
```

---

## 7. Tech Debts Aceitos Que Precisam Fechar Para 100% Sem Ressalvas

### TD1 — L10 Rounding Policy

- [ ] Definir politica formal de arredondamento.
- [ ] Substituir tolerancia hardcoded de 1 cent quando aplicavel.
- [ ] Cobrir BRL e futura extensao multi-currency.
- [ ] Adicionar testes de rounding em split, fee, refund, payout e reconciliation.

Evidencia:

```text
Politica:
Arquivos:
Testes:
```

### TD2 — L11 Direct Charge Fee In Dispute

- [x] Revisar modelo completo de dispute em Direct Charge.
- [x] Garantir que fee e seller net sao tratados de forma contabilmente correta em:
  - dispute created: HoldDisputeAsync (seller net) + HoldDisputeFeeAsync (platform fee) para Direct Charge
  - dispute won: ReleaseDisputeAsync (seller net) + ReleaseDisputeFeeAsync (platform fee) para Direct Charge
  - dispute lost: SettleDisputeLossAsync (seller net) + SettleDisputeFeeLossAsync (frozen fee) para Direct Charge
  - partial dispute: nao aplicavel (Stripe disputa valor integral)
- [x] Remover classificacao de tech debt se resolvido.
- [x] Adicionar testes.

Evidencia:

```text
Modelo: DISPUTE_FEE account type. 3 new ledger methods: HoldDisputeFeeAsync, ReleaseDisputeFeeAsync, SettleDisputeFeeLossAsync.
Flow: dispute.created freezes both seller net (WALLET→DISPUTE) and platform fee (PLATFORM_FEE→DISPUTE_FEE). dispute.won reverses both. dispute.lost settles both to PLATFORM_PAYOUT.
Arquivos: ILedgerService.cs, LedgerService.cs, WebhooksService.cs, FellowCoreEnums.cs (DISPUTE_FEE)
Testes: DisputeFlowTests.cs — 13 tests (4 new L11 tests: Created/Won DirectCharge fee hold/release, Lost DirectCharge fee settle, DestinationCharge no-ops)
Resultado: 13 passed, 0 failed
```

### TD3 — L12 Bank/Provider Settlement Reports

- [x] Fechar REX1-REX4.
- [x] Remover tech debt L12 de `production_audit.md`.

Evidencia:

```text
Settlement reports: REX1-REX4 todos concluidos com 19 testes
Reconciliation: SettlementReconciliationService com Stripe/OpenPix/CSV/Fake providers
Resultado: L12 removido de tech debt em production_audit.md
```

### TD4 — L13 Subscription Billing Ledger

- [x] Confirmar se subscription billing precisa de ledger intermediario.
- [x] Se nao precisar, documentar formalmente e adicionar teste end-to-end.
- [x] Remover tech debt L13 de `production_audit.md`.

Evidencia:

```text
Decisao: NAO PRECISA de ledger intermediario. SubscriptionBillingProcessor chama TransactionService.CreateAsync, que cria transacao PIX padrao. Ledger entries sao registradas no CAPTURE (via webhook payment.succeeded), nao no billing. PIX e instant-capture; nao ha periodo "pendente" com fundos em limbo. Idempotency key garante deduplicacao: sub-{id}-cycle-{n}.
Testes: SubscriptionFlowTests.BillingProcessor_LedgerEntriesViaTransaction_NotIntermediate (21 tests total)
Resultado: 21 passed, 0 failed
```

### TD5 — D11 Dev Credentials

- [x] Garantir que imagens/configs de production nao contem defaults.
- [x] Criar check no startup production para recusar credenciais default.
- [x] Criar teste/config validation.
- [x] Remover tech debt D11 se resolvido.

Evidencia:

```text
Arquivos: src/FellowCore.Api/Startup/CredentialValidator.cs
Validacao: Rejeita JWT secret dev/curto, sk_test_ Stripe, "sandbox" OpenPix, localhost DB. Lanca InvalidOperationException + LogCritical.
Chamada: Program.cs — dentro de if (app.Environment.IsProduction())
Teste: tests/FellowCore.Application.Tests/Startup/CredentialValidatorTests.cs (6 tests)
Resultado: 6 passed, 0 failed
```

---

## 8. Dependency And Supply Chain Security

### SUP1 — OpenTelemetry NU1902 Advisory

- [x] Atualizar `OpenTelemetry.Api` ou pacote transitivo afetado para versao sem advisory.
- [x] Se nao houver versao corrigida, documentar risco e supressao temporaria com data de revisao.
- [x] CI deve falhar para novas vulnerabilidades high/critical.
- [x] Criar rotina de revisao semanal de dependencias.

Evidencia:

```text
Pacotes: OpenTelemetry.Api 1.12.0 (transitivo) — sem versao corrigida ate 2026-05-03
Supressao: Directory.Build.props — NU1902 suprimido com justificativa detalhada e data de revisao (2026-08-03)
CI: dotnet list package --vulnerable com falha para high/critical, warning para moderate
Justificativa: Advisory GHSA-g94r-2vxg-569j e moderada, afeta pipeline de telemetria sem superficie de ataque publica
```

### SUP2 — SBOM

- [x] Gerar SBOM da aplicacao/container.
- [x] Publicar SBOM como artifact no CI.
- [x] Documentar processo de auditoria de supply chain.

Evidencia:

```text
Arquivo SBOM: sbom/fellowcore-sbom.json (CycloneDX JSON format)
CI artifact: .github/workflows/ci.yml — step "Generate SBOM" + "Upload SBOM artifact"
Ferramenta: dotnet CycloneDX (CycloneDX/cyclonedx-dotnet)
```

---

## 9. Design System / Backoffice Fintech

O checkout pode permanecer como sandbox/teste, mas um sistema fintech maduro precisa de um backoffice operacional consistente.

### UX1 — Design System Tokens

- [ ] Criar tokens compartilhados:
  - cores
  - spacing
  - typography
  - radius
  - shadows
  - semantic statuses
- [ ] Evitar UI dominada por uma unica familia de cor.
- [ ] Definir estados financeiros:
  - success
  - warning
  - risk
  - critical
  - pending
  - disabled

Evidencia:

```text
Arquivos:
Tokens:
```

### UX2 — Backoffice Operational Screens

- [ ] Dashboard de reconciliacao financeira.
- [ ] Dashboard de payouts.
- [ ] Dashboard de disputes.
- [ ] Dashboard de webhooks/dead letters.
- [ ] Dashboard de provider health.
- [ ] Tela de ledger drill-down por tenant/seller/transaction.
- [ ] Acoes com confirmacao e audit trail.

Evidencia:

```text
Telas:
Endpoints:
Testes:
```

### UX3 — Accessibility And Responsiveness

- [ ] Validar contraste.
- [ ] Validar teclado.
- [ ] Validar mobile/tablet/desktop.
- [ ] Garantir que textos nao estouram containers.
- [ ] Criar teste visual ou checklist manual.

Evidencia:

```text
Checklist:
Screenshots:
```

---

## 10. Go-Live Controlled Rollout

### GL1 — Production Preflight

- [ ] Todos os health checks verdes.
- [ ] Todos os alertas testados.
- [ ] Smoke test staging verde.
- [ ] Load test baseline aprovado.
- [ ] Backup/restore testado.
- [ ] Runbooks revisados.
- [ ] Secrets de production configurados.
- [ ] CORS production configurado.
- [ ] Webhook endpoints production configurados.
- [ ] DNS/HTTPS validado.

Evidencia:

```text
Checklist:
Data:
Responsavel:
```

### GL2 — Limited Tenant Rollout

- [ ] Definir 1-3 tenants iniciais.
- [ ] Definir limites de volume:
  - max transactions/day
  - max payout/day
  - max refund/day
- [ ] Ativar monitoramento manual diario.
- [ ] Reconciliacao manual diaria durante 1-2 semanas.
- [ ] Registrar incidentes e ajustes.

Evidencia:

```text
Tenants:
Limites:
Periodo:
Resultado:
```

### GL3 — Exit Criteria For Full Production

- [ ] 14 dias sem ledger drift.
- [ ] 14 dias sem reconciliation critical issue nao resolvida.
- [ ] 14 dias com webhook delivery success dentro do SLO.
- [ ] 14 dias com payout success dentro do SLO.
- [ ] Alertas acionaveis testados em incidente real ou simulado.
- [ ] Settlement reports automatizados funcionando ou reconciliacao manual formalmente aprovada.
- [ ] Decisao formal de liberar escala.

Evidencia:

```text
Periodo:
Metricas:
Decisao:
```

---

## Final Definition Of Done — Real 100% Production

O FellowCore so deve ser considerado 100% pronto para producao fintech plena quando:

- [ ] Reconciliacao externa Stripe/OpenPix ou importador equivalente esta implementada e testada.
- [ ] Alertas reais estao configurados, testados e com responsavel.
- [ ] Staging real passou smoke tests end-to-end.
- [ ] Load/stress/chaos tests foram executados e documentados.
- [ ] Secrets manager e politica de rotacao estao ativos.
- [ ] Backup/restore foi testado.
- [ ] Migration procedure production-safe esta documentado e testado.
- [ ] Tech debts L10-L13 e D11 foram resolvidos ou formalmente aceitos com mitigacao e owner.
- [ ] Advisory OpenTelemetry/NU1902 foi resolvido ou tem excecao temporaria documentada.
- [ ] Runbooks operacionais cobrem incidentes financeiros e de provider.
- [ ] Go-live controlado passou pelo periodo de observacao sem drift financeiro.
- [ ] `docs/production_audit.md`, `docs/production_runbook.md`, `docs/ledger_model.md` e este arquivo estao atualizados.

Resultado final:

```text
Data:
Responsavel:
Ambiente staging:
Ambiente production:
Testes automatizados:
Smoke tests:
Load tests:
Chaos tests:
Alertas testados:
Backup restore:
Settlement reconciliation:
Tech debts restantes:
Decisao final:
```
