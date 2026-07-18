# Auditoria Completa — FellowCore

**Data:** 2026-05-03 | **Escopo:** Todo o código-fonte, infra, testes
**3 auditorias:** Segurança, Arquitetura Financeira, Design/Tech Debt

---

## Resumo Executivo

| Severidade | Segurança | Financeiro | Design/Debt | **Total** |
|------------|-----------|------------|-------------|-----------|
| CRITICAL   | 0         | 2          | 1           | **3**     |
| HIGH       | 2         | 4          | 13          | **16**    |
| MEDIUM     | 6         | 7          | 18          | **26**    |
| LOW        | 5         | 3          | 9           | **13**    |
| INFO       | 4         | —          | —           | **4**     |

---

## CRITICAL

### C1. `RecordIncomingFundsAsync` — double-entry violado
- [x] Corrigido
- **Arquivo:** `LedgerService.cs:160-187`
- **Descrição:** Cria 2 créditos (PLATFORM_RECEIVABLE + seller account) sem débito correspondente. Toda transação capturada em Destination Charge viola double-entry. O total de créditos do ledger supera o de débitos indefinidamente.
- **Impacto:** Auditoria contábil impossível. Reconciliação Phase 1 (LEDGER_BALANCE_MISMATCH) não funciona ao nível global.
- **Fix:** Credit PLATFORM_RECEIVABLE, depois debit para transferir ao seller — ou criar conta "External Funds In" como contra-entrada.

### C2. `RecordDirectChargeFundsAsync` — single-entry
- [x] Corrigido
- **Arquivo:** `LedgerService.cs:189-221`
- **Descrição:** Para Direct Charge, seller recebe crédito e PLATFORM_FEE recebe crédito, ambos sem débito. Comentário line 213: "No contra-entry linkage".
- **Impacto:** Livros desbalanceados. Reguladores/auditores encontrarão material finding.
- **Fix:** Introduzir conta de liability "Connected Account Funds" como contra-entrada.

### C3. `GetByProviderTxIdAsync` sem filtro de TenantId
- [x] Corrigido
- **Arquivo:** `TransactionRepository.cs:71-72`
- **Descrição:** Webhook handlers (Stripe e OpenPix) buscam transação por ProviderTxId sem escopo de tenant. Em sandbox/test com colisão de ID, webhook pode atualizar transação de outro tenant.
- **Impacto:** Cross-tenant data contamination. Webhook pode creditar seller errado no ledger errado.
- **Fix:** Documentar explicitamente o risco aceito (webhook não sabe TenantId upfront) OU fazer lookup em 2 passos.

---

## HIGH

### H1. `charge.refunded` webhook NÃO debita o ledger do seller
- [x] Corrigido
- **Arquivo:** `WebhooksService.cs:233-298`
- **Descrição:** Refunds originados no Stripe Dashboard ou auto-refunds atualizam status da transação mas o seller mantém o saldo no ledger. `TransactionService.RefundAsync()` debita corretamente, mas o webhook handler não.
- **Impacto:** **Perda financeira direta.** Seller pode sacar fundos já devolvidos ao cliente.
- **Fix:** Adicionar `DebitSellerAsync` proporcional ao net amount, espelhando `TransactionService.RefundAsync`.

### H2. Collision guard não executa auto-refund
- [x] Corrigido
- **Arquivo:** `WebhooksService.cs:119-131, 537-549`
- **Descrição:** Loga `"Auto-refund required"` mas não dispara refund. Cliente cobrado, dinheiro no provider, ninguém rastreia a obrigação de devolver.
- **Impacto:** Dinheiro real cobrado ao cliente sem tracking de liability.
- **Fix:** Enqueue job via `IBackgroundJobs` para refund, ou criar `ReconciliationIssue`.

### H3. `Transaction.UpdateStatus` sem state machine
- [x] Corrigido
- **Arquivo:** `Transaction.cs:128-144`
- **Descrição:** Aceita qualquer transição. Nada impede `CAPTURED → PROCESSING` ou `CAPTURED → DECLINED` via webhook tardio.
- **Impacto:** Dinheiro creditado no ledger para transação que o sistema não considera mais como bem-sucedida.
- **Fix:** Mapa de transições permitidas (ex: CAPTURED → {REFUNDED, CHARGEBACKERROR}).

### H4. Split amounts validados contra gross, não net
- [x] Corrigido
- **Arquivo:** `TransactionValidators.cs:42`, `TransactionService.cs:115-125`
- **Descrição:** Validator checa `totalAmt > request.Amount` (gross), mas splits executam contra `netAmount`. Split pode pedir 100% do gross quando net é menor.
- **Impacto:** Payout tenta debitar mais do que foi creditado → falha sem remediation path.
- **Fix:** Validar sum(splits) ≤ netAmount na criação da transação.

### H5. Dead letter endpoints vazam dados cross-tenant (IDOR)
- [x] Corrigido
- **Arquivo:** `WebhookEndpointsController.cs:59-72`, `WebhookDeliveryRepository.cs:36-46`
- **Descrição:** `GetDeadLetters` e `RetryAllDeadLetters` retornam dead letters de TODOS os tenants. Sem filtro TenantId.
- **Impacto:** Qualquer tenant vê payloads, URLs e erros de webhook de outros tenants.
- **Fix:** Filtrar por `Endpoint.TenantId`.

### H6. `SettlementController` acessível por qualquer tenant
- [x] Corrigido
- **Arquivo:** `SettlementController.cs:12-18`
- **Descrição:** `ProcessDailySettlements` com `[ApiKeyAuth]` apenas. Processa settlements de TODOS os tenants.
- **Impacto:** Qualquer tenant com API key dispara settlement global.
- **Fix:** Trocar para `[MasterKeyAuth]` ou role-based.

### H7. `GetAccountsWithEntryTotalsAsync` carrega ALL ledger accounts cross-tenant
- [x] Corrigido
- **Arquivo:** `LedgerRepository.cs:42-54`
- **Descrição:** Reconciliação vê dados de todos os tenants + O(total_entries) em memória.
- **Impacto:** Multi-tenancy leak + performance bomb.
- **Fix:** Adicionar filtro TenantId.

### H8. Redis down = SPOF para todos os POST requests
- [x] Corrigido
- **Arquivo:** `IdempotencyMiddleware.cs:39`
- **Descrição:** Se Redis cair, `TryAcquireLockAsync` lança exceção. Catch no line 82-86 faz `throw`, retornando 500 para todo POST.
- **Impacto:** Indisponibilidade total de POST para `/api/v1/*`.
- **Fix:** Catch Redis exceptions e fall-through com warning log.

### H9. `charge.refunded` handler usa `Update()` — risco xmin corruption
- [x] Corrigido
- **Arquivo:** `WebhooksService.cs:282`
- **Descrição:** Após `transaction.Refund()` (mutação in-memory), chama `Update(transaction)`. `ChangeTracker.Clear()` no retry do ledger pode perder a mudança silenciosamente.
- **Impacto:** Refund registrado no provider mas não persistido no DB.
- **Fix:** Usar `ExecuteUpdateAsync` para `RefundedAmount` e `Status`.

### H10. `GetByExternalAccountIdAsync` sem TenantId
- [x] Corrigido
- **Arquivo:** `SellerRepository.cs:36-39`
- **Descrição:** Webhook de account register pode afetar seller de outro tenant em teste com Stripe Connect compartilhado.
- **Fix:** Adicionar TenantId ou unique constraint.

### H11. `GetByIdWithSplitsAsync` sem TenantId
- [x] Corrigido
- **Arquivo:** `TransactionRepository.cs:52-55`
- **Descrição:** Background job carrega transação sem escopo de tenant.
- **Fix:** Documentar ou adicionar filtro.

### H12. Dispute perdida não debita conta DISPUTE
- [x] Corrigido
- **Arquivo:** `WebhooksService.cs:400-404`
- **Descrição:** Dispute lost → loga warning, não debita DISPUTE account. Fundos ficam congelados para sempre.
- **Impacto:** DISPUTE balance cresce monotonicamente = balanço fantasma.
- **Fix:** Debitar DISPUTE, creditar `PLATFORM_LOSS`.

### H13. Split SCHEDULED sem mecanismo de retry
- [x] Corrigido
- **Arquivo:** `SplitProcessor.cs:45-48`
- **Descrição:** Se processor crashar após `MarkAsScheduled()` mas antes do payout, split fica preso em SCHEDULED eternamente.
- **Fix:** Recovery job para splits SCHEDULED há mais de X minutos.

### H14. `PayoutPercentFee` definido no Seller mas nunca usado
- [x] Corrigido
- **Arquivo:** `Seller.cs:44`, `PayoutService.cs:36`
- **Descrição:** Plataforma cobra apenas fee fixo, ignorando o percentual configurado silenciosamente.
- **Impacto:** Receita perdida.
- **Fix:** Incluir no cálculo ou remover o campo.

### H15. Payment Links anônimos colidem idempotência globalmente
- [x] Corrigido
- **Arquivo:** `IdempotencyMiddleware.cs:37`
- **Descrição:** `POST /api/v1/payment-links/pay/{token}` é `[AllowAnonymous]`, sem `X-Api-Key`. O middleware usa `tenantPrefix = "unknown"` para todas as requests anônimas. Dois clientes diferentes pagando links diferentes com o mesmo `Idempotency-Key` colidem e um recebe a resposta cacheada do outro.
- **Impacto:** Cliente recebe dados de transação de outro pagamento. Potencial data leak + pagamento perdido.
- **Fix:** Para endpoints anônimos com token no path, incluir o `{token}` no escopo da idempotency key. Alternativa: usar IP + User-Agent hash como prefixo para requests sem API key.

### H16. Webhook secrets do Seller em plaintext no banco
- [x] Corrigido
- **Arquivo:** `Seller.cs:36` (`WebhookSecret` property)
- **Descrição:** `Seller.WebhookSecret` é armazenado em texto plano no banco de dados. Diferente de `EncryptedAccessToken` (que usa `ISecurityService.EncryptAsync`), o webhook secret não é criptografado em repouso.
- **Impacto:** Database breach expõe todos os webhook secrets dos sellers. Atacante pode forjar webhooks legítimos para qualquer seller.
- **Fix:** Criptografar com `ISecurityService.EncryptAsync` (mesmo padrão de `EncryptedAccessToken`). Descriptografar apenas no momento de validação da assinatura.

---

## MEDIUM

### M1. `Tenant.ApiSecret` plaintext no DB para tenants não-rotacionados
- [x] Corrigido
- **Arquivo:** `Tenant.cs:55`, `TenantService.cs:30`
- **Descrição:** Plaintext `apiSecret` persistido. Só é nullado em `RotateApiKey`.
- **Fix:** Null out após salvar, ou criptografar com `ISecurityService`.

### M2. Login/verify-mfa rate limit = 100/min (deveria ser 5/min)
- [x] Corrigido
- **Arquivo:** `AuthController.cs:13`
- **Descrição:** Class-level `[EnableRateLimiting("fixed")]` (100/min). `auth-sensitive` (5/min) só em forgot/reset-password. Login e MFA ficam em 100/min.
- **Impacto:** TOTP de 6 dígitos (1M combinações) vulnerável a brute-force a 100/min.
- **Fix:** `[EnableRateLimiting("auth-sensitive")]` em Login, VerifyMfa, Refresh.

### M3. SSRF não bloqueia IPv6-mapped IPv4 (`::ffff:10.0.0.1`)
- [x] Corrigido
- **Arquivo:** `ServiceCollectionExtensions.cs:261-283`, `WebhookValidators.cs:65-84`
- **Descrição:** `GetAddressBytes()` para IPv6-mapped retorna 16 bytes, falhando no check `AddressFamily.InterNetwork`.
- **Fix:** `ip.MapToIPv4()` quando `ip.IsIPv4MappedToIPv6`.

### M4. `AddCreditAsync` auto-fund condicional — pode gerar drift
- [x] Corrigido
- **Arquivo:** `LedgerService.cs:33-39`
- **Descrição:** Platform receivable auto-funded condicionalmente (se balance < amount). Retry/concorrência pode gerar double-credit.
- **Fix:** Remover conditional, sempre criar external-in entry.

### M5. `SetStatusAsync` bypassa domain events
- [x] Corrigido
- **Arquivo:** `WebhooksService.cs:112-115`
- **Descrição:** `ExecuteUpdateAsync` não dispara `TransactionStatusChangedEvent`. Sem webhook delivery para merchants, sem timeline, sem dunning.
- **Fix:** Dispatch domain event manualmente após `SetStatusAsync`.

### M6. Reconciliation Phase 5 refund check é no-op
- [x] Corrigido
- **Arquivo:** `ReconciliationService.cs:652-659`
- **Descrição:** `REFUND_TOTAL_MISMATCH` existe no enum mas nunca é raised.
- **Fix:** Implementar comparação sum(RefundIntents COMPLETED) vs transaction.RefundedAmount.

### M7. Payout reconciliation all-time debits, não per-payout
- [x] Corrigido
- **Arquivo:** `ReconciliationService.cs:326-347`
- **Descrição:** Se seller tem qualquer payout debit na história, todos os payouts passam reconciliação.
- **Fix:** Filtrar entries por ReferenceId do payout específico.

### M8. Split percentage usa `double` — imprecisão IEEE 754
- [x] Corrigido
- **Arquivo:** `TransactionDTOs.cs:15`, `TransactionSplit.cs:19`, `TransactionService.cs:120`
- **Descrição:** `33.33 / 100.0` em double = `0.33329999999999999`. Cast para decimal propaga o erro.
- **Impacto:** ~1 centavo por split. Acumula em volume.
- **Fix:** Mudar `Percentage` para `decimal?`. Usar divisão `/ 100M`.

### M9. Payout reversal failure = ledger inconsistente permanente
- [x] Corrigido
- **Arquivo:** `PayoutService.cs:97-107`
- **Descrição:** Se payout E reversal falham, seller é debitado sem compensação. Log CRITICAL mas sem recovery automático.
- **Fix:** Enqueue Hangfire job para retry de compensação, ou flag via reconciliation.

### M10. Money VO existe mas não é usado
- [ ] Aceito como dívida técnica
- **Arquivo:** `ValueObjects/Money.cs`
- **Descrição:** `Money` VO implementado mas todos os campos monetários usam `decimal` + `string Currency` separados. Adoção requer refactoring extensivo em entidades, DTOs, repositórios e migrações. Risco de regressão alto. Moeda fixa (BRL) reduz impacto.
- **Fix:** Adotar Money em Transaction, LedgerAccount, LedgerEntry, Payout.

### M11. OutboxMessage sem TenantId
- [x] Corrigido
- **Arquivo:** `OutboxMessage.cs`
- **Descrição:** Sem escopo por tenant. Processamento global sem partição ou priorização.
- **Fix:** Adicionar TenantId + index `(ProcessedAt, TenantId)`.

### M12. Idempotency retorna 200 hardcoded (ignora 201 original)
- [x] Corrigido
- **Arquivo:** `IdempotencyMiddleware.cs:46-48`
- **Descrição:** Response cacheada é devolvida sempre com status 200, mesmo que original fosse 201 Created.
- **Fix:** Cachear status code junto com o response body.

### M13. ApiKeyAuth sem try/catch em chamadas Redis
- [x] Corrigido
- **Arquivo:** `ApiKeyAuthAttribute.cs:32-36`
- **Descrição:** Redis timeout causa 500 em toda request API (sem cache fallback).
- **Fix:** Wrap cache calls em try/catch, graceful degrade para DB-only lookup.

### M14. PixController chama OpenPix API diretamente (bypassa service layer)
- [ ] Aceito como dívida técnica
- **Arquivo:** `PixController.cs:85-219`
- **Descrição:** Controller bypassa service layer para QR codes, Pix keys, transfers. Funcionalidade correta, mas violação de layered architecture. Refactoring para service layer planejado para próxima iteração.
- **Fix:** Criar service layer para operações Pix.

### M15. TenantController chama OpenPix API diretamente
- [ ] Aceito como dívida técnica
- **Arquivo:** `TenantController.cs:62-87`
- **Fix:** Mover para service method. Mesma priorização de M14.

### M16. Response envelope inconsistente (3 formatos)
- [ ] Aceito como dívida técnica
- **Descrição:** `StandardResponseFilter` wraps em `{success, data, errors}`. `IdempotencyMiddleware` escreve JSON raw. `GlobalExceptionHandler` escreve `ProblemDetails`. Webhook retorna `{received: true}`. Funcionalidade correta; inconsistência apenas cosmética para clients.
- **Fix:** Unificar todos pelo envelope `{success, data, errors}`.

### M17-M19. Repositories sem TenantId filter
- [x] Corrigido
- `ReconciliationRepository.GetIssueByIdAsync` (line 23-26)
- `PaymentIntentRepository.GetByIdAsync` (line 15-16)
- `WebhookDeliveryRepository.GetByIdAsync` (line 11-14)

### M20. PaymentProviderFactory é dead infrastructure
- [x] Corrigido (reavaliado)
- **Arquivo:** `PaymentProviderFactory.cs`
- **Descrição:** Avaliação original incorreta. Factory é ativamente usado por Rails (StripeCardRail, StripeBoletoRail, OpenPixRail), SellerService, PixController e DunningProcessor. Não é dead code.
- **Status:** Mantido como está. Finding reclassificado como falso positivo.

### M21. `GetByTenantAndDateRangeAsync` sem limit — OOM em volume
- [x] Corrigido
- **Arquivo:** `TransactionRepository.cs:106-113`
- **Fix:** Agregar database-side ou adicionar limit.

### M22-M23. Missing indexes
- [x] Corrigido
- `Transaction.(TenantId, CreatedAt DESC)` — usado por GetPagedAsync, DateRange, Export
- `Payout.(TenantId, CreatedAt)` — usado por GetPagedAsync

### M24. List endpoints sem paginação
- [x] Corrigido
- `PaymentLinkRepository.GetByTenantAsync` — safety `.Take(200)` limit
- `ScheduledReportRepository.GetByTenantAsync` — safety `.Take(200)` limit

### M25. Backup codes usam SHA256 simples, sem sal/pepper
- [x] Corrigido
- **Arquivo:** `User.cs:170-174`
- **Descrição:** `HashCode(string code)` usa `SHA256.HashData(Encoding.UTF8.GetBytes(code))` direto, sem salt nem pepper. Backup codes são 8 dígitos numéricos (10^8 = 100M combinações). Um atacante com acesso ao DB pode computar rainbow table de todos os 100M hashes possíveis em segundos (~380MB).
- **Impacto:** Database breach expõe todos os backup codes imediatamente via precomputed lookup.
- **Fix:** Usar HMAC-SHA256 com uma chave secreta (pepper) da configuração, OU usar BCrypt/Argon2 (mais lento mas elimina rainbow table). Mínimo: HMAC com pepper.

### M26. `/health` público expõe dependências internas
- [x] Corrigido
- **Arquivo:** `Program.cs:104-122`
- **Descrição:** O endpoint `/health` é público (sem auth) e retorna detalhes de cada dependência: nome, status, e duração. Expõe quais serviços internos existem (PostgreSQL, Redis, MinIO, etc.), seus nomes de conexão, e se estão funcionando.
- **Impacto:** Reconhecimento de infraestrutura. Atacante descobre quais serviços o sistema usa e quais estão degradados (facilitando timing de ataque quando Redis está down, por exemplo).
- **Fix:** Em produção, retornar apenas status agregado (Healthy/Unhealthy) sem detalhes. Detalhes apenas para requests autenticadas ou em path separado `/health/details` com auth.

### M27. `Seller.WebhookUrl` permite HTTP na validação
- [x] Corrigido
- **Arquivo:** `SellerValidators.cs:79-82`
- **Descrição:** Validação aceita `uri.Scheme == "https" || uri.Scheme == "http"`. Webhook URLs com HTTP enviam payloads (incluindo dados de transação, status de pagamento) em texto claro pela rede.
- **Impacto:** Man-in-the-middle pode interceptar dados sensíveis de webhook (valores de transação, status de pagamento, dados de payer).
- **Fix:** Remover `|| uri.Scheme == "http"`. Permitir apenas HTTPS. Se necessário para dev, adicionar flag de configuração `AllowInsecureWebhooks`.

---

## LOW

### L1. DatabaseSeeder loga API key completa
- [x] Corrigido
- **Arquivo:** `DatabaseSeeder.cs:40`
- **Fix:** Logar apenas prefixo (12 chars).

### L2. SettlementController sem rate limiting
- [x] Corrigido
- **Arquivo:** `SettlementController.cs:8-9`
- **Fix:** `[EnableRateLimiting("fixed")]`.

### L3. checkout.html `showResult` usa innerHTML
- [x] Corrigido
- **Arquivo:** `checkout.html:626`
- **Fix:** Usar `textContent` ou DOM API.

### L4. API key via query parameter no checkout
- [x] Corrigido
- **Arquivo:** `checkout.html:979`
- **Fix:** Remover ou documentar como dev-only.

### L5-L7. DTOs inline em controllers
- [ ] Aceito como dívida técnica
- `TransactionsController.cs:150` (UpdateTransactionDto)
- `PixController.cs:221-222` (CreatePixKeyRequest, CreatePixTransferRequest)
- `ReconciliationController.cs:126` (ResolveIssueRequest)
- Funcionalidade correta; questão de organização de código apenas.

### L8. OutboxMessage não herda Entity
- [x] Corrigido
- **Arquivo:** `OutboxMessage.cs:4`

### L9. RefundIntentStatus enum fora do lugar
- [x] Corrigido
- **Arquivo:** `RefundIntent.cs:7` (deveria estar em `FellowCoreEnums.cs`)

### L10. OutboxProcessor sem paralelismo
- [ ] Aceito como dívida técnica
- **Arquivo:** `OutboxProcessor.cs:15-57`
- **Descrição:** Sequential processing preserva ordenação de eventos. Batch de 50 já implementado. Paralelismo pode causar out-of-order events.
- **Fix:** Parallel processing ou per-tenant batching (quando necessário por volume).

### L11. Inconsistência `CreatedAtAction` vs `Created`
- [ ] Aceito como dívida técnica
- TenantController usa `Created(url, result)` com URL hardcoded.
- PixController usa `Created("", ...)` com URL vazia.
- Funcionalidade correta; inconsistência apenas cosmética.

### L12. `DisputeRepository.GetByExternalIdAsync` sem TenantId
- [x] Corrigido
- **Arquivo:** `DisputeRepository.cs:11-13`
- **Fix:** Documentar (Stripe IDs globalmente únicos) ou adicionar filtro.

### L13. `UserRepository.GetByEmailAsync` sem TenantId
- [x] Corrigido
- **Arquivo:** `UserRepository.cs:10-11`
- **Descrição:** Email unique global — um email não pode existir em múltiplos tenants.
- **Fix:** Documentar constraint ou avaliar se multi-tenant email overlap é necessário.

---

## INFO

### I1. Docker roda como root
- [x] Corrigido
- **Arquivo:** `Dockerfile:39-58`
- **Fix:** `USER appuser` em dev e production stages.

### I2. CI sem security scanning
- [x] Corrigido
- **Arquivo:** `.github/workflows/ci.yml`
- **Fix:** `dotnet list package --vulnerable --include-transitive`.

### I3. MinIO com credenciais default
- [x] Corrigido
- **Arquivo:** `docker-compose.yml:78-79`
- **Fix:** `${MINIO_ROOT_PASSWORD:?Set MINIO_ROOT_PASSWORD}`.

### I4. Hangfire dashboard aberto em dev
- [x] Corrigido
- **Arquivo:** `ServiceCollectionExtensions.cs:417-419`
- **Descrição:** Comportamento esperado para development. Produção protegida com API key auth.

---

## TESTING GAPS

### T1. Sem integration test para webhook-to-ledger refund
- [x] Corrigido
- **Descrição:** `charge.refunded` webhook → verificar ledger debitado corretamente.

### T2. Sem integration test para dispute flow (open + close/won + close/lost)
- [x] Corrigido

### T3. Sem test para payout ledger compensation failure (double failure)
- [x] Corrigido

### T4. Sem test para multi-tenant data isolation
- [x] Corrigido
- **Descrição:** Criar 2 tenants, verificar que Tenant A não acessa dados de Tenant B.

### T5. Sem test para Redis failure graceful degradation
- [x] Corrigido

### T6. Sem test para concurrent PaymentIntent capture (race condition)
- [x] Corrigido

### T7. SettlementController sem test de auth elevada
- [x] Corrigido

---

## Áreas que Passaram na Auditoria

- **SQL Injection:** Nenhum raw SQL. Tudo EF Core LINQ.
- **JWT:** Validação rigorosa (issuer, audience, clock skew zero, HMAC-SHA256). Guard em produção rejeita keys < 32 chars.
- **Password hashing:** BCrypt work factor 12. Policy: 12+ chars, upper, lower, digit, special.
- **TOTP:** 20-byte random key, encrypted at rest, window=1, backup codes SHA256-hashed (ver M25 para salt issue).
- **Refresh tokens:** 64-byte random, SHA256-hashed, rotação on use, revogação em logout/reset.
- **Stripe webhook:** HMAC-SHA256 + replay window 5 min + timing-safe comparison.
- **OpenPix webhook:** Per-seller AppId validation + platform AppId fallback.
- **CORS:** Origin allowlist em produção.
- **Error responses:** Sem stack traces em 500.
- **File upload:** Magic bytes + MIME whitelist + 10MB + filename sanitization.
- **SignalR:** `[Authorize]` + group por tenant JWT claim.
- **Retry/circuit-breaker:** `AddStandardResilienceHandler()` no HttpClient factory.
- **SSRF:** Duas camadas (registration-time static check + delivery-time DNS-level blocking via SocketsHttpHandler).
- **PaymentLink atomicity:** `PaymentLinkUsageAttempt` com ciclo RESERVED→COMPLETED/FAILED e transação explícita.

---

## Estatísticas

- **Total findings:** 62 (3 CRITICAL + 16 HIGH + 27 MEDIUM + 13 LOW + 4 INFO) + 7 testing gaps
- **Corrigidos:** 55/62 findings + 7/7 testing gaps
- **Aceitos como dívida técnica:** 7 (M10, M14, M15, M16, L5-L7, L10, L11) — design debt sem impacto em correção ou segurança
- **Suite atual:** 612 passed, 16 skipped, 0 failed (107 Domain + 245 Application + 260 Integration)
