# Split Transfer Stuck in RESERVED/PROCESSING

## Symptom
Split transfers remain in RESERVED or PROCESSING status for more than 1 hour. The SplitProcessor Hangfire job may have failed or the distribution process was interrupted.

## Related Alert
- Prometheus metric: `fellowcore_split_distributions_total` stalled / no new completions
- Reconciliation issue type: `SPLIT_TRANSFER_STUCK_PROCESSING` (Phase 7)

## Impact
- Sellers are not receiving their share of split payments.
- Seller wallet balances are incorrect, preventing payouts.
- If many transfers are stuck, it may indicate a systemic failure in the SplitProcessor background job.
- Customer trust is degraded if sellers report missing funds.

## Immediate Action
1. Check the Hangfire dashboard for failed or stuck SplitProcessor jobs.
2. Verify database connectivity and that optimistic concurrency (xmin) is not causing repeated failures.
3. Check if the LedgerAccount for SPLIT_CLEARING has sufficient balance for pending distributions.
4. If the issue is transient (e.g., DB lock timeout), retry the stuck transfers by re-enqueueing the SplitProcessor.
5. If transfers are in RESERVED but never moved to PROCESSING, the initial ledger debit may have failed silently.

## Investigation Queries

Find split transfers stuck for more than 1 hour:
```sql
SELECT st."Id", st."TenantId", st."TransactionId", st."RecipientSellerId",
       st."Amount", st."Status", st."IsPrimaryShare",
       st."CreatedAt", st."UpdatedAt",
       NOW() - st."UpdatedAt" AS "StuckDuration"
FROM "SplitTransfers" st
WHERE st."Status" IN (1, 2)  -- RESERVED, PROCESSING
  AND st."UpdatedAt" < NOW() - INTERVAL '1 hour'
ORDER BY st."UpdatedAt" ASC;
```

Check the associated transactions:
```sql
SELECT t."Id", t."TenantId", t."SellerId", t."Amount", t."Status",
       t."PlatformFeeAmount", t."ProviderCostAmount"
FROM "Transactions" t
WHERE t."Id" IN (
    SELECT DISTINCT st."TransactionId"
    FROM "SplitTransfers" st
    WHERE st."Status" IN (1, 2)
      AND st."UpdatedAt" < NOW() - INTERVAL '1 hour'
);
```

Check SPLIT_CLEARING balance for affected tenants:
```sql
SELECT la."TenantId", la."Balance"
FROM "LedgerAccounts" la
WHERE la."Type" = 10  -- SPLIT_CLEARING
  AND la."TenantId" IN (
    SELECT DISTINCT st."TenantId"
    FROM "SplitTransfers" st
    WHERE st."Status" IN (1, 2)
      AND st."UpdatedAt" < NOW() - INTERVAL '1 hour'
  );
```

Check for related Hangfire job failures:
```sql
SELECT j."Id", j."StateName", j."InvocationData", j."CreatedAt"
FROM "hangfire"."job" j
WHERE j."InvocationData" LIKE '%SplitProcessor%'
  AND j."StateName" IN ('Failed', 'Processing')
ORDER BY j."CreatedAt" DESC
LIMIT 20;
```

Verify if ledger entries exist for the stuck transfers:
```sql
SELECT le."Id", le."AccountId", le."Amount", le."ReferenceType",
       le."ReferenceId", le."CreatedAt"
FROM "LedgerEntries" le
WHERE le."ReferenceType" IN ('SPLIT_DISTRIBUTE', 'SPLIT_CLEARING')
  AND le."ReferenceId" IN (
    SELECT st."TransactionId"::text
    FROM "SplitTransfers" st
    WHERE st."Status" IN (1, 2)
      AND st."UpdatedAt" < NOW() - INTERVAL '1 hour'
  )
ORDER BY le."CreatedAt" DESC;
```

## Root Cause Resolution
1. **Hangfire job failure**: Re-enqueue the SplitProcessor job for the affected transactions. The processor will pick up transfers in RESERVED/PROCESSING states.
2. **Optimistic concurrency conflict**: If xmin conflicts are causing repeated failures, check if multiple jobs are attempting to process the same transaction. The unique index `IX_SplitTransfers_TenantId_TransactionId_RecipientSellerId` should prevent duplicates.
3. **Insufficient clearing balance**: If SPLIT_CLEARING balance is less than the sum of pending distributions, investigate why the capture did not fully credit the clearing account. This may require a manual ledger correction.
4. **Database deadlock**: If PostgreSQL deadlocks are occurring, review connection pool settings and consider increasing the bulkhead concurrency limit.
5. **Manual resolution**: For transfers stuck in RESERVED that will never be processed (e.g., parent transaction was refunded), mark them as FAILED:
   ```sql
   UPDATE "SplitTransfers"
   SET "Status" = 4,  -- FAILED
       "UpdatedAt" = NOW()
   WHERE "Id" = '<split_transfer_id>'
     AND "Status" IN (1, 2);
   ```

## Validation
```sql
-- Confirm no transfers remain stuck beyond 1 hour
SELECT COUNT(*)
FROM "SplitTransfers" st
WHERE st."Status" IN (1, 2)
  AND st."UpdatedAt" < NOW() - INTERVAL '1 hour';

-- Verify affected transfers moved to PAID or FAILED
SELECT st."Id", st."Status", st."UpdatedAt"
FROM "SplitTransfers" st
WHERE st."Id" IN ('<stuck_transfer_id_1>', '<stuck_transfer_id_2>');

-- Verify seller wallet balances are consistent
SELECT la."SellerId", la."Balance"
FROM "LedgerAccounts" la
WHERE la."Type" = 0  -- WALLET
  AND la."TenantId" = '<tenant_id>';
```

## Escalation
- If more than 10 transfers are stuck across multiple tenants, escalate to the engineering team immediately.
- If the root cause is a code bug in the SplitProcessor (not a transient failure), escalate to backend engineering.
- If manual status updates or ledger adjustments are needed for amounts exceeding R$5,000, get finance team approval first.
