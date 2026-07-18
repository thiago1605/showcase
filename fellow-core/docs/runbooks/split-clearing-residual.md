# Split Clearing Residual Balance

## Symptom
The SPLIT_CLEARING ledger account has a non-zero balance but there are no pending, reserved, or processing split transfers that would justify the balance. Funds are stuck in the clearing account and not being distributed to sellers.

## Related Alert
- Prometheus metric: `fellowcore_split_clearing_balance_cents` non-zero with no active transfers
- Reconciliation issue type: `SPLIT_CLEARING_NON_ZERO_NO_PENDING` (Phase 6)

## Impact
- Seller funds are trapped in the clearing account and not reaching their wallets.
- If not addressed, sellers will have incorrect available balances and payouts will be short.
- Revenue recognition may be delayed for affected sellers.

## Immediate Action
1. Identify affected tenants with non-zero SPLIT_CLEARING and no active split transfers.
2. Verify the clearing balance matches a known transaction that failed mid-distribution.
3. If a SplitProcessor job failed, check Hangfire dashboard for failed jobs related to the transaction.
4. Re-enqueue the SplitProcessor job for the stuck transaction if it was a transient failure.
5. If the issue is due to a bug (e.g., partial distribution), escalate to engineering.

## Investigation Queries

Find SPLIT_CLEARING accounts with non-zero balance:
```sql
SELECT la."Id", la."TenantId", la."SellerId", la."Balance", la."UpdatedAt"
FROM "LedgerAccounts" la
WHERE la."Type" = 10  -- SPLIT_CLEARING
  AND la."Balance" != 0
ORDER BY ABS(la."Balance") DESC;
```

Check if there are any active split transfers for those tenants:
```sql
SELECT st."Id", st."TenantId", st."TransactionId", st."RecipientSellerId",
       st."Amount", st."Status", st."CreatedAt", st."UpdatedAt"
FROM "SplitTransfers" st
WHERE st."TenantId" IN (
    SELECT la."TenantId"
    FROM "LedgerAccounts" la
    WHERE la."Type" = 10 AND la."Balance" != 0
)
AND st."Status" IN (0, 1, 2)  -- PENDING, RESERVED, PROCESSING
ORDER BY st."CreatedAt" DESC;
```

Find transactions that credited SPLIT_CLEARING but have no corresponding distribution:
```sql
SELECT le."ReferenceId" AS "TransactionId", SUM(le."Amount") AS "ClearingNet"
FROM "LedgerEntries" le
JOIN "LedgerAccounts" la ON le."AccountId" = la."Id"
WHERE la."Type" = 10  -- SPLIT_CLEARING
  AND la."Balance" != 0
GROUP BY le."ReferenceId"
HAVING SUM(le."Amount") != 0
ORDER BY ABS(SUM(le."Amount")) DESC;
```

Check the last split transfers for the stuck transaction:
```sql
SELECT st."Id", st."TransactionId", st."RecipientSellerId", st."Amount",
       st."Status", st."IsPrimaryShare", st."ReversedAmount",
       st."CreatedAt", st."UpdatedAt"
FROM "SplitTransfers" st
WHERE st."TransactionId" = '<transaction_id>'
ORDER BY st."CreatedAt";
```

## Root Cause Resolution
1. **SplitProcessor job failure**: Re-enqueue the Hangfire job. The processor uses `DistributeFromClearingAsync` to move funds from SPLIT_CLEARING to SELLER_WALLET accounts.
2. **Partial distribution crash**: If some recipients were credited but not all, manually trigger `DistributeFromClearingAsync` for remaining recipients.
3. **Refund mid-distribution**: If a refund was processed while splits were distributing, verify the refund reversal path (`ReturnToClearingAsync` + `DrainClearingForRefundAsync`) completed fully.
4. **Code bug**: If the clearing account balance does not match any expected pattern, create a manual ledger adjustment entry (double-entry: debit SPLIT_CLEARING, credit appropriate destination).

## Validation
```sql
-- Verify SPLIT_CLEARING balance is now zero for the affected tenant
SELECT la."Id", la."TenantId", la."Balance"
FROM "LedgerAccounts" la
WHERE la."Type" = 10
  AND la."TenantId" = '<tenant_id>';

-- Verify seller wallets received the expected amounts
SELECT la."TenantId", la."SellerId", la."Balance"
FROM "LedgerAccounts" la
WHERE la."Type" = 0  -- WALLET
  AND la."TenantId" = '<tenant_id>'
  AND la."SellerId" IN (
    SELECT st."RecipientSellerId"
    FROM "SplitTransfers" st
    WHERE st."TransactionId" = '<transaction_id>'
  );

-- Run reconciliation to confirm no remaining issues
-- Trigger via API: POST /api/v1/reconciliation/run
```

## Escalation
- If the residual amount exceeds R$1,000 or affects more than 5 tenants, escalate to the engineering lead immediately.
- If manual ledger adjustments are required, escalate to the finance team for approval before executing.
- If the root cause is a code bug in SplitProcessor/LedgerService, escalate to backend engineering with the transaction IDs and clearing account state.
