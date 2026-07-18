# Audit Execution Backlog

**Created:** 2026-04-30
**Status:** Complete (25/25)

---

## Critical (Blockers for Production)

- [x] Ledger concurrency — RowVersion + optimistic concurrency retry (ConcurrencyException in Application layer, retry 3x in LedgerService, migration applied)
- [x] Payout race condition — Debit-first pattern: ledger debit before provider call, compensating ReversalCreditAsync on failure. Eliminates TOCTOU race. Also fixed TestAppDbContext for LedgerAccount.RowVersion on SQLite.
- [x] Refund does not reverse ledger — RefundAsync now debits seller wallet via LedgerService.DebitSellerAsync. Critical log on ledger failure for manual intervention.
- [x] No circuit breakers — Added Microsoft.Extensions.Http.Resilience (Polly 8.x) with AddStandardResilienceHandler() on Stripe and OpenPix HTTP clients (retry + circuit breaker + timeout).
- [x] Provider call before DB persistence — Persist-first pattern: Transaction saved with null ProviderTxId, then provider called. On failure, transaction marked FAILED. Added TransactionStatus.FAILED and Transaction.SetProviderTxId().
- [x] Ledger is single-entry — Double-entry implemented: ContraEntryId on LedgerEntry, platform accounts (PLATFORM_RECEIVABLE, PLATFORM_FEE, PLATFORM_PAYOUT), all operations create paired contra-entries. Removed redundant UpdateAccount calls (EF Core change tracking handles it).

---

## High Priority

- [x] Webhook replay protection — Added 5-minute tolerance window check on Stripe-Signature t= timestamp. Rejects replayed webhooks with `Math.Abs(age) > 300s`.
- [x] Idempotency not scoped per tenant — Key now prefixed with SHA256(X-Api-Key)[0:16] to prevent cross-tenant collisions.
- [x] No SSRF protection — Already implemented in CreateWebhookEndpointDtoValidator: HTTPS required, private/reserved IPs blocked (10.x, 127.x, 172.16-31.x, 192.168.x, 169.254.x, localhost, .local, .internal, cloud metadata).
- [x] API keys stored in plaintext — SHA256 hash stored in `Tenant.ApiKeyHash`, lookup by hash. Added `ApiKeyPrefix` for display. `ApiKeyAuthAttribute` hashes incoming key before lookup/cache. Plaintext returned only once at creation/rotation.
- [x] UnitOfWork inconsistency — `CommitAsync()` now calls `SaveChangesAsync()` before DB commit, guaranteeing changes are flushed within the transaction. Removed redundant `SaveChangesAsync()` calls from all services. Fixed TransactionService.CreateAsync which had CommitAsync before SaveChanges.
- [x] Missing domain event reliability — Transactional outbox pattern: domain events persisted to `OutboxMessages` table within same DB transaction. Best-effort immediate dispatch after commit; `OutboxProcessor` Hangfire job retries unprocessed messages (max 5 retries, 15s interval).
- [x] Split processing not executed — Added `ProcessAllPendingSplitsAsync()` to `ISplitProcessor`, queries captured transactions with pending splits, wired as Hangfire recurring job (`split-processing`, minutely). Also added `GetTransactionIdsWithPendingSplitsAsync` to repository.
- [x] Provider idempotency not used — Stripe: Idempotency-Key header passed to all mutating API calls (PaymentIntent, Refund, ConnectedAccount, Person, BankAccount, BoletoPaymentMethod, ConfirmIntent). OpenPix: deterministic correlationId used in charge, refund, and account-register flows (replaced Guid.NewGuid with tenant+document key). Note: OpenPix withdraw API has no idempotency support (provider limitation).
- [x] OpenPix webhook per-seller validation — WebhookAuthFilter stores authToken in HttpContext.Items. WebhooksService validates token against seller's decrypted EncryptedAccessToken (via ISecurityService). Falls back to platform OpenPix:AppId. Account-register events validated against platform AppId only.
- [x] ProviderTxId not indexed uniquely — Added unique composite index `(TenantId, ProviderTxId)` with `HasFilter("ProviderTxId IS NOT NULL")` in AppDbContext. Migration `AddProviderTxIdUniqueIndex` created. Prevents duplicate provider transaction references per tenant.

---

## Improvements

- [x] No DLQ — Dead-letter tracking implemented: `GetDeadLettersAsync`/`GetDeadLetterCountAsync` in repository, `GET /api/v1/webhook-endpoints/dead-letters` endpoint, `LogCritical` with structured context (EventType, EventId, LastError) when delivery exhausts all retries.
- [x] Export memory inefficiency — CSV exports now use `IAsyncEnumerable` streaming via `StreamForExportAsync` (no full List materialization). Both CSV and PDF queries use `AsNoTracking()` for reduced memory pressure. PDF still materializes for QuestPDF rendering.
- [x] Sequential settlement — `SettlementService` now uses `Parallel.ForEachAsync` with `MaxDegreeOfParallelism=5`. Each seller gets its own DI scope (`IServiceScopeFactory`) to avoid DbContext thread-safety issues.
- [x] Missing reconciliation job — `ReconciliationService` cross-checks ledger account balances vs sum of entries. Runs daily at 05:00 UTC via Hangfire (`daily-reconciliation`), before settlement (06:00). Logs `Critical` on discrepancies.
- [x] DateTime usage in domain — Financial-critical entities (`Transaction`, `LedgerAccount`, `LedgerEntry`, `Payout`, `WebhookDelivery`) now accept `DateTime? now = null` on factory and mutation methods. Defaults to `DateTime.UtcNow` for backwards compatibility, but allows injection for deterministic testing.
- [x] Seller notifications retry — NotificationsProcessor enhanced with `[AutomaticRetry(Attempts=5, DelaysInSeconds=[10,30,60,300,900])]` for explicit Hangfire retry policy. Added `Stopwatch` timing, structured logging with all delivery context (SellerId, Url, EventType, TransactionId, StatusCode, DurationMs).
