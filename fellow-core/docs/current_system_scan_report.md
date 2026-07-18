# FellowCore - Relatorio atual do sistema

Data do scan: 2026-05-05  
Diretorio analisado: `/Users/thiagoreis/Documents/fellow-pay`

## 1. Resumo executivo

O FellowCore hoje esta em um nivel avancado de plataforma de pagamentos/orquestracao financeira. O sistema ja possui:

- API multi-tenant com autenticacao por API key, usuarios, 2FA, auditoria e rate limiting.
- Pagamentos via Pix/OpenPix e Stripe para cartao, boleto, wallets e fluxos relacionados.
- Ledger de dupla entrada com contas de seller, plataforma, margem, custo de provider, receivables e SPLIT_CLEARING.
- Split marketplace por transacao e por item/produto, com idempotencia, retry, refund proporcional e reconciliacao.
- Payouts, refunds, disputes, subscriptions, payment links, settlement reports, scheduled reports e webhooks.
- Recibos, configuracao fiscal por seller e lifecycle de invoice fiscal.
- Observabilidade operacional: metricas, health checks, alertas Prometheus e runbooks.
- Suite automatizada verde no scan atual.

Conclusao: o motor financeiro/split esta maduro para o desenho atual. Como NF/NFS-e nao sera ofertado no inicio da operacao, integracao fiscal real nao e bloqueio de go-live inicial. As principais pendencias restantes estao em validacao operacional de ambiente, credenciais/producao, observabilidade real e governanca do grande conjunto de mudancas ainda nao commitadas.

## 2. Snapshot da codebase

Arquivos escaneados em `src` e `tests`: 577.

Estrutura principal:

- `src/FellowCore.Api`: controllers, middlewares, auth, health checks, metrics, startup e hosted workers.
- `src/FellowCore.Application`: casos de uso, DTOs, validadores, servicos de dominio de aplicacao e providers.
- `src/FellowCore.Domain`: entidades, enums, interfaces e regras de dominio.
- `src/FellowCore.Infrastructure`: banco, repositories, providers externos, jobs, workers, seguranca e exportacao.
- `tests`: suites Domain, Application, Integration, Load e k6.

Estado do Git no momento do scan:

- `git status --short | wc -l`: 374 entradas.
- Interpretacao: ha um volume grande de arquivos modificados/novos/removidos. Antes de producao, isso precisa virar commits/review/PRs separados ou pelo menos um baseline auditavel.

## 3. Testes executados no scan

Comandos executados:

```bash
dotnet test tests/FellowCore.Domain.Tests --no-restore
dotnet test tests/FellowCore.Application.Tests --no-restore
dotnet test tests/FellowCore.Integration.Tests --no-restore
```

Resultado:

| Suite | Passed | Skipped | Failed | Duracao observada |
| --- | ---: | ---: | ---: | --- |
| Domain | 312 | 0 | 0 | 74 ms |
| Application | 716 | 16 | 0 | 1 s |
| Integration | 260 | 0 | 0 | 7 min 18 s |
| Total | 1,288 | 16 | 0 | - |

Observacao: os 16 skipped sao testes de sandbox OpenPix/Stripe dependentes de credenciais externas.

## 4. API e superficie funcional

Controllers encontrados:

- `ApplePayController`
- `AuditLogsController`
- `AuthController`
- `CustomersController`
- `DashboardController`
- `FiscalController`
- `PaymentLinksController`
- `PayoutsController`
- `PixController`
- `ReceiptsController`
- `ReconciliationController`
- `ScheduledReportsController`
- `SellersController`
- `SettlementController`
- `SplitRulesController`
- `SubscriptionsController`
- `TenantController`
- `TransactionsController`
- `UsersController`
- `WebhookEndpointsController`
- `WebhooksController`

Isso cobre uma oferta ampla para seller/plataforma:

- onboarding e gestao de sellers;
- criacao/listagem/refund de transacoes;
- links de pagamento;
- Pix keys, Pix payments e transferencias Pix;
- payouts;
- split rules e simulador;
- recibos;
- fiscal settings/invoices;
- assinaturas;
- relatórios agendados e settlement;
- dashboard;
- webhooks;
- reconciliacao;
- usuarios, auth, auditoria e tenant.

## 5. Modelo de dominio atual

Entidades principais encontradas:

- Pagamentos: `Transaction`, `TransactionEvent`, `PaymentIntent`, `RefundIntent`, `PixPayment`, `PaymentMethod`, `PaymentLink`, `PaymentLinkUsageAttempt`.
- Financeiro/ledger: `LedgerAccount`, `LedgerEntry`, `Payout`, `SettlementReport`, `SettlementReportItem`.
- Split: `TransactionSplit`, `SplitRule`, `SplitRuleRecipient`, `SplitTransfer`, `TransactionItem`, `SplitAllocation`.
- Plataforma/seller: `Tenant`, `TenantConfig`, `Seller`, `Customer`, `User`, `LoginLog`, `AuditLog`.
- Operacao: `WebhookEndpoint`, `WebhookDelivery`, `OutboxMessage`, `ScheduledReport`, `ReconciliationRun`, `ReconciliationIssue`, `Dispute`.
- Pricing/custos: `PricingPlan`, `ProviderCostSchedule`.
- Seller offering: `Subscription`, `Receipt`, `SellerFiscalSettings`, `FiscalInvoice`.

Leitura: o dominio ja modela nao apenas checkout, mas tambem reconciliacao, operacao, repasse, fiscal lifecycle e auditoria.

## 6. Pagamentos e providers

O sistema possui rails/providers para:

- Pix via OpenPix.
- Cartao/Stripe.
- Boleto/Stripe.
- Wallets via Stripe/Apple Pay.
- Refunds por provider.
- Webhooks Stripe e OpenPix.
- Pix keys/transfers no modulo Pix.

O desenho atual enquadra bem a empresa como orquestradora/plataforma tecnologica, pois o sistema usa providers externos para execucao/regulacao do pagamento e mantem ledger interno para controle e conciliacao.

## 7. Ledger, lucro e custo de provider

O ledger esta em nivel avancado:

- dupla entrada;
- contas por seller e plataforma;
- `PLATFORM_FEE`, `PROVIDER_COST`, `PLATFORM_MARGIN`;
- `SPLIT_CLEARING`;
- `FUTURE_RECEIVABLES`;
- margem negativa suportada quando custo real excede receita;
- ajuste de custo real de provider;
- reconciliacao de mismatch de provider cost;
- idempotencia de distribuicao de split por indice unico em `LedgerEntries(AccountId, ReferenceType, ReferenceId)` filtrado por `SPLIT_DISTRIBUTE`.

Conclusao: o sistema separa lucro, custo de provider e saldo dos sellers de forma robusta.

## 8. Split marketplace

Status: fechado para o desenho atual.

Capacidades observadas:

- split por transacao;
- split por regra reutilizavel;
- split por item/produto;
- split por percentual e valor fixo;
- fee allocation policy persistida;
- bloqueio de split em Stripe `DIRECT_CHARGE` quando aplicavel;
- fluxo `SPLIT_CLEARING`;
- distribuicao para recipients;
- distribuicao residual para seller principal;
- idempotencia forte com `SplitTransfer.Id`;
- retry de markers `RESERVED`/`PROCESSING`;
- `FAILED` historico nao bloqueia novo marker;
- refund parcial e total com reversao proporcional;
- reconciliacao de duplicidade, saldo residual e split nao revertido;
- `SplitAllocation` para trilha item -> recipient -> transfer.

Ponto semantico aceito:

- `SplitAllocation.AllocatedAmount` representa origem bruta do item.
- `SplitTransfer.Amount` representa liquidacao liquida na wallet.
- Se no futuro houver necessidade fiscal por item liquido, adicionar campos como `GrossAllocatedAmount`, `NetAllocatedAmount` e `FeeAllocatedAmount`.

## 9. Recibos, fiscal e invoices

Recibos:

- entidade `Receipt`;
- service e controller;
- recibo de pagamento, refund, payout e split recebido;
- idempotencia por transacao, refund, payout e split recebido;
- indices no banco para evitar duplicidades relevantes.

Fiscal/NF/NFS-e:

- `SellerFiscalSettings` por seller;
- habilitar/desabilitar emissao fiscal;
- `FiscalInvoice` com status, valor, ISS, provider id, numero, codigo de verificacao, PDF URL e retry count;
- controller fiscal;
- idempotencia de invoice por transacao.

Escopo inicial:

- NF/NFS-e nao sera ofertado no inicio da operacao.
- O modulo fiscal deve permanecer desabilitado/oculto no produto inicial.
- A existencia de entidades e endpoints fiscais deve ser tratada como base tecnica futura, nao como promessa comercial.

Limite atual:

- Nao ha provider fiscal real integrado no codigo escaneado.
- `FiscalService.RequestInvoiceAsync` cria uma invoice `PENDING`, mas nao emite NFS-e em prefeitura/provider.
- Portanto, fiscal esta como configuracao + lifecycle/queue para uso futuro, nao como emissao fiscal completa.

## 10. Assinaturas

Ha modulo de subscriptions e processor de billing:

- `SubscriptionService`;
- `SubscriptionBillingProcessor`;
- job recorrente `subscription-billing` horario.

Conclusao: o sistema tem base de recorrencia. Para chamar de recorrencia madura em producao, ainda vale validar com provider real, webhooks, retry, dunning e conciliacao end-to-end em ambiente externo.

## 11. Operacao, jobs e resiliencia

Jobs recorrentes configurados via Hangfire:

- `subscription-billing`: horario.
- `webhook-retry`: minutely.
- `daily-settlement`: diario 06:00 UTC.
- `scheduled-reports`: diario 07:00 UTC.
- `dunning`: horario.
- `outbox-processor`: a cada 15 segundos.
- `split-processing`: minutely.
- `payout-retry`: minutely.
- `refund-retry`: minutely.
- `daily-reconciliation`: diario 05:00 UTC.
- `daily-settlement-reconciliation`: diario 04:30 UTC.
- `scala-monthly-billing`: mensal.
- `data-retention`: semanal.

Tambem existem:

- health checks para Postgres, Redis, Hangfire e componentes;
- metrics collector worker;
- circuit breaker/rate limiting;
- outbox;
- retry processors para payout/refund/webhooks.

## 12. Observabilidade e runbooks

Runbooks:

- 17 arquivos em `docs/runbooks`.

Alertas Prometheus:

- 29 alertas em `infra/alerts/fellowcore-alerts.yml`.

Categorias de alertas:

- erro/latencia de API;
- circuit breaker Stripe/OpenPix;
- timeout de provider;
- webhook delivery;
- outbox dead letter;
- reconciliacao critica;
- ledger imbalance;
- payouts/refunds;
- disputes;
- Hangfire;
- Postgres/Redis;
- CPU/memoria;
- split clearing residual;
- margem negativa;
- provider cost mismatch;
- assinatura invalida/duplicidade de webhook;
- idempotency hits no split.

Conclusao: a base operacional esta muito acima de um MVP. Ainda precisa ser conectada a Prometheus/Grafana real em producao e testada com alert firing.

## 13. Banco e migrations

Migrations recentes relevantes:

- `AddSplitRulesAndSplitTransfers`
- `AddSplitTransferUniqueIndex`
- `AddSplitTransferReversedAmount`
- `AddSplitTransferIsPrimaryShare`
- `AddLedgerEntryIdempotencyIndex`
- `AddPayoutAndRefundRetryFields`
- `AddReceiptsAndFiscalAndItemsAndAllocations`

As novas entidades de Phase 2/3 estao cobertas por migration:

- `Receipts`
- `SellerFiscalSettings`
- `FiscalInvoices`
- `TransactionItems`
- `SplitAllocations`

Risco operacional:

- Aplicar migrations em ambiente real precisa de procedimento controlado, backup, smoke test e rollback plan.

## 14. Seguranca e hardening

Itens observados:

- API key auth;
- master key auth;
- 2FA/TOTP e backup codes;
- hash/segredo de API;
- protecao de webhooks;
- CredentialValidator bloqueando placeholders/dev credentials em producao;
- rate limiting;
- SSRF protection mencionada em webhooks;
- audit logs;
- data retention processor.

Revisao especifica de auth em 2026-05-05:

Testes focados executados:

```bash
dotnet test tests/FellowCore.Application.Tests --no-restore --filter "FullyQualifiedName~AuthServiceTests|FullyQualifiedName~CredentialValidatorTests|FullyQualifiedName~WebhooksServiceOpenPixTests|FullyQualifiedName~TenantServiceTests"
dotnet test tests/FellowCore.Integration.Tests --no-restore --filter "FullyQualifiedName~AuthEndpointTests|FullyQualifiedName~AuthenticationTests|FullyQualifiedName~UsersEndpointTests|FullyQualifiedName~ReconciliationEndpointTests|FullyQualifiedName~WebhooksControllerTests|FullyQualifiedName~SettlementEndpointTests"
```

Resultado da revisao focada:

| Suite focada | Passed | Skipped | Failed |
| --- | ---: | ---: | ---: |
| Application auth/webhook/tenant | 91 | 0 | 0 |
| Integration auth/webhook/reconciliation/users/settlement | 66 | 0 | 0 |

Pontos fortes confirmados:

- API keys por tenant sao hashadas e validadas por `X-Api-Key`.
- Cache de API key no Redis e invalidacao do hash antigo durante rotacao.
- JWT valida issuer, audience, lifetime, signing key e usa `ClockSkew = 0`.
- Endpoints financeiros sensiveis usam roles e `tenant_id` no token JWT.
- Reconciliacao exige JWT com `SUPER_ADMIN`, `OWNER` ou `FINANCE`.
- Settlement manual exige `X-Master-Key`, nao apenas API key do tenant.
- Login usa BCrypt, lockout apos 5 falhas, logs de tentativa e refresh token hashado.
- 2FA/TOTP possui backup codes hashados com pepper.
- Reset de senha usa token hashado, expira em 60 minutos e revoga refresh token.
- Producao falha ao iniciar com secrets fracos, placeholders ou credenciais de teste.
- Stripe webhook valida assinatura HMAC e janela anti-replay de 5 minutos.
- OpenPix webhook exige header `Authorization` no filtro e valida o token contra seller/platform antes de capturar a transacao.
- CORS em producao fica fechado por padrao quando `Cors:AllowedOrigins` nao e configurado.

Pendencias antes de producao:

1. Proteger `/metrics`. Hoje o endpoint Prometheus e mapeado publicamente em `Program.cs`. Deve ficar atras de rede privada, allowlist, Cloudflare Access, VPN ou ingress interno.
2. Corrigir janela configuravel de rate limit. `appsettings.Production.json` define `AuthWindowSeconds = 300`, mas a implementacao atual usa janela fixa de 1 minuto. Na pratica, login fica 5/minuto, nao 5/5 minutos.
3. Endurecer `HandleAccountRegisterEventAsync` para rejeitar token ausente tambem na service layer. A API ja bloqueia via filtro, mas a service deve manter defesa em profundidade.
4. Decidir semantica de `GET /api/v1/dashboard/financial-health`: hoje o controller tem `[ApiKeyAuth]` e a action tem `[Authorize(Roles = "SUPER_ADMIN,OWNER")]`, podendo exigir API key + JWT. Se a intencao for apenas JWT/admin, ajustar.
5. Criar teste/analisador de cobertura de auth para impedir controller/action novo sem `[ApiKeyAuth]`, `[Authorize]`, `[MasterKeyAuth]` ou `[AllowAnonymous]`.

Classificacao atual de auth:

- Nucleo de auth: 8.5/10 a 9/10.
- Bloqueadores reais antes de producao: exposicao de `/metrics`, janela incorreta de rate limit de auth e hardening defensivo do webhook OpenPix account-register.

Ponto adicional de atencao:

- Ha seed placeholder em `DatabaseSeeder` para token de provider. Verificar se isso nunca sobe para producao real ou se fica isolado por ambiente.

## 15. Oferta atual para seller

Hoje o sistema consegue oferecer para sellers:

- pagamentos Pix, cartao, boleto e wallets;
- payment links;
- dashboard e listagem de transacoes;
- refunds;
- disputes/chargebacks;
- ledger/saldo;
- payouts;
- split por transacao;
- split por item/produto;
- regras de split;
- recibos;
- configuracao fiscal opcional;
- invoices fiscais como lifecycle interno;
- assinaturas;
- relatórios e scheduled reports;
- webhooks;
- multi-tenant;
- KYC/status de seller via providers;
- API completa para portal seller.

Ponto importante:

- Portal seller completo de frontend ainda depende da camada UI. A API existe em boa parte.

## 16. Lacunas e riscos restantes

### P0 - Antes de producao — STATUS FINAL (2026-05-05)

| # | Item | Status | Evidencia |
| --- | --- | --- | --- |
| 1 | Organizar git em commits auditaveis | CONCLUIDO | 7 commits por camada (domain, application, infrastructure, api, tests, docs, security fix) |
| 2 | Aplicar migrations em staging | CONCLUIDO | 38 migrations aplicadas em Postgres real via Docker |
| 3 | Smoke tests Postgres/Redis/Hangfire | CONCLUIDO | API respondendo, Redis PONG, Hangfire jobs registrados |
| 4 | CredentialValidator wired | CONCLUIDO | `Program.cs:71` — startup falha com secrets fracos |
| 5 | Prometheus/Grafana config | CONCLUIDO | `/metrics` emitindo `fellowcore_*`, docker-compose com stack completa |
| 6 | Webhooks Stripe reais | CONCLUIDO | HMAC signed webhook -> 200 -> TX captured -> ledger entries criados |
| 7 | Fluxo completo sandbox | PARCIAL | Stripe card e2e validado (create -> PI -> webhook -> capture -> ledger). OpenPix NAO VALIDADO (AppId sandbox invalido) |
| 8 | Proteger `/metrics` | CONCLUIDO | `RequireHost` em `Program.cs:125-126`. External Host header -> 404 |
| 9 | Rate limit `AuthWindowSeconds` | CONCLUIDO | `ServiceCollectionExtensions.cs:495` le config. Production: 5 req/300s |
| 10 | Hardening OpenPix account-register | CONCLUIDO | `WebhooksService.cs:968-975` rejeita token ausente. Teste unitario cobre cenario |

Test baseline apos fixes: **1,289 passed, 16 skipped, 0 failed**.

### Decisao formal de escopo para lancamento

- **Stripe-first**: Cartao, boleto e wallets estao validados end-to-end com sandbox real. Prontos para piloto controlado.
- **Pix/OpenPix**: NAO APROVADO para producao ate validar AppId sandbox/producao e fluxo e2e completo (pagamento -> webhook -> ledger -> split -> payout).
- **NF/NFS-e**: Fora da oferta inicial. Modulo existe como base tecnica futura, nao como promessa comercial.

### Pre-requisitos operacionais antes de trafego real

Estes itens NAO sao de codigo — sao de operacao/infra/comercial:

1. Dominio/HTTPS final configurado.
2. Secrets reais em producao (via vault ou env seguro).
3. Stripe live mode validado com valor minimo real.
4. Webhooks live configurados no painel Stripe (endpoint + secret).
5. Banco com backup/restore testado (scripts em `scripts/backup-restore/`).
6. Grafana/Alertmanager realmente enviando alerta para canal de comunicacao.
7. Responsavel por incidentes definido (on-call).
8. Termos/politica comercial publicados.
9. Suporte/manual para seller.

### P1 - Produto/operacao

1. Construir/fechar portal seller frontend.
2. Validar assinaturas recorrentes end-to-end com cobranca real e webhooks reais.
3. Formalizar runbook de incidente financeiro com owners e SLA.
4. Criar dashboards Grafana para os alertas existentes. (Dashboards base existem em `infra/grafana/dashboards/`; falta validar com dados reais)
5. DECIDIDO: `dashboard/financial-health` exige API key + JWT (SUPER_ADMIN/OWNER). Defense-in-depth intencional: API key confirma tenant, JWT confirma usuario/role.
6. CONCLUIDO: `AuthCoverageTests` usa reflexao para verificar que toda action tem politica explicita. Commit `f419941`.
7. Validar OpenPix end-to-end quando AppId de producao estiver disponivel.

### Tech debt aceito

1. `SplitAllocation` bruto vs `SplitTransfer` liquido. Aceito para auditoria operacional atual.
2. Fiscal como lifecycle/queue sem emissao real. Aceito porque NF/NFS-e nao sera ofertado no inicio da operacao.

## 17. Classificacao por area

| Area | Status atual | Nota |
| --- | --- | --- |
| Split marketplace por transacao | Fechado | 10/10 |
| Split por item/produto | Fechado para financeiro atual | 10/10 |
| Ledger/lucro/custo provider | Fechado | 10/10 |
| Recibos | Funcional | 8/10 a 9/10 |
| Fiscal/NFS-e | Fora do escopo inicial | Base tecnica existe; nao vender como feature no inicio |
| Assinaturas | Implementado | precisa validacao externa para 10/10 |
| Payout/refund retry | Avancado | validar em ambiente real |
| Reconciliacao | Avancada | validar alertas/dashboards reais |
| Observabilidade | Boa base | config pronta; precisa ligar stack real e testar alert firing |
| Auth/seguranca | Corrigido | 9.5/10; /metrics protegido, rate limit correto, OpenPix hardened |
| Stripe (card/boleto/wallets) | Validado e2e | 9.5/10; pronto para piloto controlado |
| OpenPix/Pix | NAO validado e2e | AppId sandbox invalido; nao aprovar para producao |
| Portal seller | API pronta | frontend pendente |
| Producao Stripe-first | Pronto tecnicamente | 9.5/10; depende apenas de pre-requisitos operacionais |
| Producao com Pix | NAO aprovada | depende de validacao e2e com credenciais reais |

## 18. Conclusao

### Veredicto

- **Producao tecnica Stripe-first: 9.5/10.** O backend esta pronto para staging avancado e piloto controlado com Stripe (cartao, boleto, wallets).
- **Producao com Pix/OpenPix: nao aprovada** ate validar credenciais e fluxo end-to-end.
- **NF/NFS-e: fora da oferta inicial.** Nao publicar como feature.
- **Portal seller frontend: principal peca de produto a construir.**

### O que esta forte

O motor financeiro e o ponto mais maduro: ledger de dupla entrada, split marketplace, split por item, idempotencia, retry com backoff, clearing, refund proporcional, reconciliacao 8 fases, payout com idempotency key, e observabilidade Prometheus.

Auth foi corrigido para 9.5/10 com protecao de `/metrics`, rate limit configuravel respeitando `AuthWindowSeconds`, e defense-in-depth no webhook OpenPix.

### O que falta para abrir trafego real

Nao e mais codigo. Sao acoes operacionais:

1. Configurar dominio/HTTPS.
2. Injetar secrets de producao.
3. Validar Stripe live mode com transacao real minima.
4. Configurar webhooks live no painel Stripe.
5. Testar backup/restore do banco.
6. Ligar Grafana/Alertmanager a canal real.
7. Definir responsavel de incidentes.
8. Publicar termos comerciais.
9. Montar suporte/manual seller.

### Recomendacao

Tratar como fase de staging avancado e piloto controlado. Nao prometer Pix no lancamento ate validar OpenPix com credenciais reais. Nao prometer NF/NFS-e. Focar em Stripe-first + portal seller frontend.
