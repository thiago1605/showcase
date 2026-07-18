# FellowCore — Ledger Model

## Account Types

| Account | Scope | Purpose |
|---------|-------|---------|
| WALLET | Per-seller | Available funds for payout |
| FUTURE_RECEIVABLES | Per-seller | Funds awaiting D+N settlement |
| DISPUTE | Per-seller | Frozen funds in dispute |
| PLATFORM_RECEIVABLE | Platform (SellerId=null) | Gross incoming funds |
| PLATFORM_PAYOUT | Platform | Outgoing funds (payouts, refunds, chargebacks) |
| PLATFORM_FEE | Platform | Accumulated fees (direct charge only) |
| EXTERNAL_FUNDS | Per-seller | Funds in seller's connected account (direct charge) |

## Double-Entry Invariant

For every tenant: `sum(all entries) == 0`. Every credit has a corresponding debit linked via `ContraEntryId`.

## Entry Flows by Event

### Capture — Destination Charge
```
+PLATFORM_RECEIVABLE   (gross)    "Recebimento"
-PLATFORM_RECEIVABLE   (net)      "Repasse seller"     ←contra→
+SELLER(WALLET|FUTURE)  (net)      "Recebimento TX"
```

### Capture — Direct Charge
```
+EXTERNAL_FUNDS        (gross)    "Recebimento"
-EXTERNAL_FUNDS        (net)      "Repasse seller"     ←contra→
+SELLER(WALLET|FUTURE)  (net)      "Recebimento TX"
-EXTERNAL_FUNDS        (fee)      "Application fee"    ←contra→
+PLATFORM_FEE          (fee)      "Application fee"
```

### Refund (Partial or Total)
Proportional: `refundDelta × (netAmount / grossAmount)`
```
-SELLER(WALLET|FUTURE)  (proportional net)  "Estorno"  ←contra→
+PLATFORM_PAYOUT       (proportional net)  "Payout seller"
```
Platform fee reversal (proportional):
```
-PLATFORM_FEE          (proportional fee)  "Estorno fee"  ←contra→
+PLATFORM_PAYOUT       (proportional fee)  "Estorno fee"
```

### Dispute Created (Hold)
```
-SELLER(WALLET|FUTURE)  (gross)    "Disputa"            ←contra→
+DISPUTE               (gross)    "Disputa"
```
Note: Hold uses gross amount (Amount, not NetAmount) because the entire charge is contested.

### Dispute Won (Release)
```
-DISPUTE               (gross)    "Disputa ganha"       ←contra→
+WALLET                (gross)    "Disputa ganha"
```

### Dispute Lost (Settlement)
```
-DISPUTE               (net)      "Disputa perdida"     ←contra→
+PLATFORM_PAYOUT       (net)      "Disputa perdida"
```
Platform fee on lost dispute:
```
-DISPUTE               (fee)      "Fee disputa perdida" ←contra→
+PLATFORM_PAYOUT       (fee)      "Fee disputa perdida"
```

### Payout
```
-WALLET                (amount)   "Saque"               ←contra→
+PLATFORM_PAYOUT       (amount)   "Saque"
```
Fee entry:
```
-WALLET                (fee)      "Taxa saque"          ←contra→
+PLATFORM_FEE          (fee)      "Taxa saque"
```

### Payout Failure Reversal
```
+WALLET                (amount)   "Reversão saque"      ←contra→
-PLATFORM_PAYOUT       (amount)   "Reversão saque"
```

### Settlement Transfer (FUTURE_RECEIVABLES → WALLET)
```
-FUTURE_RECEIVABLES    (amount)   "Liquidação"          ←contra→
+WALLET                (amount)   "Liquidação"
```

### VOIDED after CAPTURED
State machine does NOT allow CAPTURED → VOIDED. No reversal entries needed.

## Reconciliation Phases

1. **Ledger Balance Check**: `account.Balance == sum(entries)` per account + `sum(all debits) == sum(all credits)` per tenant
2. **Transaction 1:1**: Internal TX ↔ Stripe charge (amount, status, currency, refund)
3. **Payout**: Internal Payout ↔ ledger debit entry + provider transfer
4. **Platform Balance Drift**: Stripe balance vs internal ledger (tolerance R$1.00)
5. **Cross-Rail Invariants**: Double-capture, dispute orphan, refund total mismatch
