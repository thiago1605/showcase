# Deep Technical Audit — FellowCore Payment System

**Date:** 2026-04-30
**Auditor:** Staff Engineer (Payment Systems & Distributed Systems)

---

## 1. Capabilities & Strengths

**What works well:**

- **Clean Architecture** with proper separation: Domain has no infrastructure dependencies, Application orchestrates, Infrastructure implements.
- **Domain-driven entities** with factory methods (`Transaction.Create`, `Payout.Create`), Result pattern for error handling, and domain events.
- **Idempotency middleware** with Redis-backed atomic locking (`SETNX`) — well-implemented pattern that covers all POST `/api/v1/*` endpoints.
- **Optimistic concurrency** on `Transaction` via `RowVersion` (Npgsql xmin).
- **Webhook signature verification** for both Stripe (HMAC SHA256 with timing-safe comparison) and OpenPix.
- **Webhook retry system** with exponential backoff (10s->30s->2m->10m->1h), delivery recording, and manual retry.
- **Multi-tenant isolation** via `TenantId` on all entities, enforced at repository level.
- **API key rotation** with SHA256 hashing, timing-safe comparison, and Redis cache invalidation.
- **Rate limiting** (100/min general, 500/min webhooks, 5/min auth-sensitive).
- **Security headers**, HSTS, CORS lockdown, account lockout, structured logging with correlation IDs.
- **382 tests** across Domain, Application, and Integration layers.

**Core flows implemented:** Transaction creation -> provider processing -> webhook status update -> ledger recording -> settlement (D+30 transfer) -> payout via BaaS.

---

## 2. Critical Gaps

### 2.1 — Ledger is NOT double-entry

`LedgerAccount.cs:35-48` — The ledger uses single-entry bookkeeping. A `Credit()` increments `Balance` and creates one `LedgerEntry`. There is no corresponding debit to a counterpart account.

**In a real payment system**, every credit to a seller's wallet must be a debit from the platform's settlement account. Without double-entry:
- You cannot prove the total of all accounts equals zero (fundamental accounting invariant).
- There is no audit trail for where money came from — only that it appeared.
- Financial reconciliation with bank statements is impossible.

**What's needed**: A `PlatformAccount` per tenant (revenue, settlement holding, fee income), and every operation creates paired entries (debit one account, credit another, same amount).

### 2.2 — No reconciliation process

There is zero reconciliation logic. The system trusts webhooks as truth for transaction status, but never cross-checks against:
- Provider settlement reports (Stripe/OpenPix daily summaries)
- Actual bank account movements
- Ledger totals vs. provider-reported totals

A single missed webhook or duplicate webhook silently causes financial drift.

### 2.3 — No dispute/chargeback handling

`TransactionStatus` has `CHARGEBACKERROR` but it is never used anywhere in the codebase. Stripe `charge.dispute.*` webhooks are not handled. When a chargeback occurs:
- The ledger balance is never adjusted (seller keeps funds)
- No hold is placed on the disputed amount
- The `LedgerAccountType.DISPUTE` enum exists but is never used

### 2.4 — Payout lacks atomicity with ledger

`PayoutService.cs:52-97` — The payout flow is:
1. Save payout as PROCESSING (line 54-55)
2. Call OpenPix withdraw (line 65)
3. If success: debit ledger (line 72)
4. Save payout update (line 96-97)

**Problem**: Steps 1, 3, and 4 are separate `SaveChangesAsync()` calls with no DB transaction. If step 3 succeeds but step 4 fails, the ledger is debited but the payout status isn't updated. Also, the balance check (line 42-45) has a TOCTOU race: two concurrent payouts can both pass the balance check before either debits.

---

## 3. System Design Review

### 3.1 — UnitOfWork inconsistency

The `UnitOfWork` is used in some places (`WebhooksService`, `TransactionService.CreateAsync`, `SettlementService`) but not in others (`PayoutService`, `LedgerService`). Most `LedgerService` methods call `SaveChangesAsync()` directly without a transaction, which means partial writes are possible during failures.

**Critical example** — `TransactionService.CreateAsync:91-103`:
```csharp
await unitOfWork.BeginAsync();
transactionRepository.Add(transaction);
await unitOfWork.CommitAsync();      // commits the DB transaction
await transactionRepository.SaveChangesAsync();  // this is AFTER commit
```

The commit happens before `SaveChangesAsync()`. If `SaveChangesAsync` does the actual EF Core persistence, then the `CommitAsync` committed an empty transaction. This is architecturally confused.

### 3.2 — Domain events dispatched AFTER commit

`UnitOfWork.cs:27` — `DispatchDomainEventsAsync()` runs after `CommitAsync()`. This means the transaction is already committed, but if event dispatching fails, the event is silently lost. Events like `TransactionStatusChangedEvent` trigger notification webhooks to sellers — losing these is a data integrity issue.

### 3.3 — Sync webhook delivery in Hangfire

Seller notification webhooks are dispatched via Hangfire (`NotificationsService.cs:16-17`). This is good (async), but the `NotificationsProcessor` has no retry mechanism — if the seller endpoint is down, the notification is lost. Only `TenantWebhookProcessor` records deliveries for retry.

---

## 4. Resilience & Fault Tolerance

### 4.1 — Zero circuit breakers

No Polly, no resilience pipelines, no circuit breakers anywhere. `StripeApiClient`, `OpenPixApiClient`, and `ResendEmailProvider` all make HTTP calls with no:
- Retry with backoff for transient failures
- Circuit breaking when a provider is down
- Timeout beyond the default HttpClient timeout

If Stripe is down for 5 minutes, every payment attempt will timeout sequentially, exhausting thread pool and database connections.

### 4.2 — Transaction creation is not idempotent at the provider level

`TransactionService.CreateAsync:55` calls `gateway.ProcessPaymentAsync()` *before* persisting the transaction. If the process crashes after the provider charges the card but before the DB write, the charge exists at Stripe but FellowCore has no record of it. On retry (via idempotency middleware), a new idempotency key creates a second charge.

The `IdempotencyKey` on `Transaction` (unique index) prevents duplicate DB records, but does not prevent duplicate provider charges because the provider call happens first.

### 4.3 — Webhook replay attack window

`WebhookAuthFilter.cs:75-94` — Stripe signature verification parses the `t=timestamp` but never validates that the timestamp is recent. Stripe recommends rejecting events older than 5 minutes. An attacker who obtains a valid signed payload can replay it indefinitely.

### 4.4 — No dead-letter queue

Webhook retry maxes out at 5 attempts (`WebhookDelivery.MaxRetryAttempts`). After that, the delivery is marked FAILED and forgotten. There's no DLQ, no alerting, no manual review mechanism beyond the retry endpoint.

### 4.5 — Provider-side idempotency not guaranteed

The idempotency key from the client is stored on the Transaction, but it's not passed to the payment provider. Stripe supports idempotency keys natively — not using them means retries at the provider level can create duplicates.

---

## 5. Performance

### 5.1 — `GetByProviderTxIdAsync` has no tenant scoping

`TransactionRepository.cs:62-63` — Lookup by `ProviderTxId` scans all tenants. This is the hot path for every webhook. The index exists but is non-unique and doesn't include `TenantId`.

### 5.2 — Settlement query loads all matching transactions into memory

`GetPendingSettlementsAsync` and `MarkAsSettledAsync` work correctly at the DB level (GROUP BY, ExecuteUpdate), but the `SettlementService` loops over each seller sequentially.

### 5.3 — Export loads up to 10,000 transactions into memory

`TransactionRepository.GetForExportAsync` with `limit=10000` materializes all transactions before generating CSV/PDF.

### 5.4 — Redis connection is not lazy

`ServiceCollectionExtensions.cs:394-395` — `ConnectionMultiplexer.Connect()` runs synchronously during app startup. If Redis is down, the application fails to start entirely.

---

## 6. Security

### 6.1 — API key auth uses plain comparison on cache miss

`Tenant.ApiKey` stores the raw `pk_live_*` value. Only `ApiSecretHash` is hashed. A database breach exposes all API keys immediately.

### 6.2 — Webhook SSRF protection is documented but not visible in code

No SSRF protection in `TenantWebhookProcessor` or `WebhookRetryProcessor`. The webhook URL from `WebhookEndpoint` is used directly without URL validation or private IP blocking.

### 6.3 — OpenPix webhook auth uses a single global AppId

OpenPix webhooks are validated by comparing the `Authorization` header against a single global `OpenPix:AppId`. Any valid OpenPix webhook for any sub-account is accepted.

### 6.4 — Idempotency key is not scoped to tenant

The idempotency key from the header is used as-is, without tenant scoping. Tenant A and Tenant B using the same idempotency key will collide.

---

## 7. Financial Integrity (CRITICAL)

### 7.1 — LedgerAccount has no concurrency control

`LedgerAccount.cs` — No `RowVersion`, no `[Timestamp]`, no optimistic concurrency. Two concurrent webhook events for the same seller can both read `Balance = 100`, both credit `+50`, and both write `Balance = 150` — losing `50`.

### 7.2 — Payout balance check is racy

`PayoutService.cs:42-45` — Between the balance check and the debit, another payout or webhook could modify the balance. No database lock, no transaction wrapping both operations.

### 7.3 — Refund at provider without ledger reversal

`TransactionService.RefundAsync:131-152` — Calls `gateway.RefundAsync()` and updates `RefundedAmount`, but never reverses the ledger entry. The seller keeps the money after the refund.

### 7.4 — Split processing is informational only

`TransactionSplit` entries are created but never processed for money movement. `SplitProcessor` is registered in DI but no recurring job triggers it.

### 7.5 — Settlement marks as settled without confirming ledger success

If `TransferFundsAsync` partially fails, the entire batch for that seller is still marked as settled.

---

## 8. Code Quality

### 8.1 — UnitOfWork usage is inconsistent

Some services use `unitOfWork.BeginAsync()` + `CommitAsync()` while also calling `repository.SaveChangesAsync()` separately.

### 8.2 — `DateTime.UtcNow` used directly in entities

Domain entities use `DateTime.UtcNow` directly, making them untestable for time-dependent behavior.

### 8.3 — Webhook event types are hardcoded strings

No constants, no enum mapping — prone to typos and drift.

### 8.4 — Test coverage gaps

- No tests for concurrent ledger operations
- No tests for payout race conditions
- No tests for refund -> ledger reversal
- No tests for settlement with partial failures

---

## 9. Prioritized Issues

### Critical (Blockers for Production)

| # | Issue | Location | Risk |
|---|-------|----------|------|
| 1 | LedgerAccount has no concurrency control | `LedgerAccount.cs` | Lost updates = incorrect seller balances |
| 2 | Payout balance check is racy | `PayoutService.cs:42-72` | Double-spend |
| 3 | Refund doesn't reverse ledger | `TransactionService.RefundAsync`, `WebhooksService` | Seller keeps money after refund |
| 4 | No circuit breakers on provider HTTP calls | `StripeApiClient`, `OpenPixApiClient` | Provider outage cascades |
| 5 | Transaction created at provider before DB persist | `TransactionService.CreateAsync:55` | Phantom charges |
| 6 | Ledger is single-entry | `LedgerAccount`, `LedgerService` | Cannot reconcile or audit |

### High Priority

| # | Issue | Location | Risk |
|---|-------|----------|------|
| 7 | Stripe webhook has no timestamp tolerance | `WebhookAuthFilter.cs:75-94` | Replay attacks |
| 8 | Idempotency key not scoped to tenant | `IdempotencyMiddleware.cs:30` | Cross-tenant collision |
| 9 | No SSRF protection on webhook URLs | `TenantWebhookProcessor.cs:64` | Internal network scanning |
| 10 | API keys stored in plaintext | `Tenant.ApiKey` | DB breach = full compromise |
| 11 | UnitOfWork commit/save order is wrong | `TransactionService.CreateAsync:91-96` | Empty transactions |
| 12 | Domain events lost if dispatch fails | `UnitOfWork.cs:27` | Missing notifications |
| 13 | Split processing never executes | `SplitProcessor` | Splits are decorative |
| 14 | OpenPix webhook uses single global AppId | `WebhookAuthFilter.cs:54-73` | Cross-seller spoofing |
| 15 | Provider idempotency keys not passed | `TransactionService.CreateAsync:55` | Duplicate charges |

### Improvements

| # | Issue | Location |
|---|-------|----------|
| 16 | `DateTime.UtcNow` in domain entities | Various entities |
| 17 | No DLQ for exhausted webhook retries | `WebhookRetryProcessor` |
| 18 | Export loads 10k rows into memory | `TransactionRepository.GetForExportAsync` |
| 19 | Seller notifications lack retry | `NotificationsProcessor` |
| 20 | `ProviderTxId` index is not unique | `AppDbContext` |
| 21 | Sequential settlement processing | `SettlementService` |
| 22 | No reconciliation job | Missing entirely |
