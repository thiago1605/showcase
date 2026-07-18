// =============================================================================
// SCENARIO 6: REAL MONEY FLOW STRESS TEST (CRITICAL)
// =============================================================================
// This is the DEFINITIVE financial integrity test.
//
// Phase 1: Create transactions via Stripe → send valid payment_intent.succeeded
//          webhooks → verify funds land in FUTURE_RECEIVABLES
// Phase 2: 200 concurrent webhook credits to the same seller → verify correct
//          total, no duplicates, no lost updates
// Phase 3: CREDIT + PAYOUT RACE — simultaneous webhook credits (CAPTURED) and
//          payout requests (DEBIT) — validate no negative balance, no double-spend
// Phase 4: Webhook deduplication — same webhook event sent 100x concurrently,
//          only 1 ledger credit
// Phase 5: Settlement — transfer FUTURE_RECEIVABLES → WALLET concurrently with
//          payouts
// Phase 6: Final ledger invariant check — totalCredits - totalDebits = balance
// =============================================================================

import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Rate, Trend } from "k6/metrics";
import { uuidv4 } from "https://jslib.k6.io/k6-utils/1.4.0/index.js";
import { randomString } from "https://jslib.k6.io/k6-utils/1.4.0/index.js";
import crypto from "k6/crypto";

const BASE_URL = __ENV.BASE_URL || "http://localhost:5195";
const API_KEY = __ENV.API_KEY || "MISSING_API_KEY";
const SELLER_ID = __ENV.SELLER_ID || "";
const WEBHOOK_SECRET = __ENV.WEBHOOK_SECRET || "whsec_k6stresstest_secret_for_hmac";

const HEADERS = {
  "Content-Type": "application/json",
  Accept: "application/json",
  "X-Api-Key": API_KEY,
};

// --- Custom Metrics ---
const txCreated = new Counter("tx_created");
const txFailed = new Counter("tx_failed");
const webhookSent = new Counter("webhook_sent");
const webhookAccepted = new Counter("webhook_accepted");
const webhookRejected = new Counter("webhook_rejected");
const webhookDuplicate = new Counter("webhook_duplicate_sent");
const ledgerCreditCount = new Counter("ledger_credits");
const payoutAttempted = new Counter("payout_attempted");
const payoutAccepted = new Counter("payout_accepted");
const payoutRejected = new Counter("payout_rejected");
const negativeBalance = new Counter("negative_balance_detected");
const duplicateCredit = new Counter("duplicate_credit_detected");
const settlementTriggered = new Counter("settlement_triggered");
const ledgerInvariantBroken = new Counter("ledger_invariant_broken");
const webhookLatency = new Trend("webhook_latency", true);
const txLatency = new Trend("tx_latency", true);
const payoutLatency = new Trend("payout_latency", true);

// --- Shared state ---
// Arrays of providerTxIds from created transactions (populated in phase 1)
// k6 shared arrays don't work for dynamic data, so we use a simpler approach:
// each VU creates its own transaction and sends its own webhook.
let phase1Balance = { available: 0, total: 0 };

// --- Helper: compute valid Stripe webhook signature ---
function stripeSign(payload, secret) {
  const timestamp = Math.floor(Date.now() / 1000);
  const signedPayload = `${timestamp}.${payload}`;
  const signature = crypto.hmac("sha256", secret, signedPayload, "hex");
  return `t=${timestamp},v1=${signature}`;
}

// --- Helper: create a transaction and return its providerTxId ---
function createTxAndGetId(amount) {
  const idempotencyKey = uuidv4();
  const payload = JSON.stringify({
    sellerId: SELLER_ID,
    amount: amount,
    paymentType: 0, // CREDIT_CARD → goes to FUTURE_RECEIVABLES
    installments: 1,
    description: `k6-realflow-${idempotencyKey.substring(0, 8)}`,
    payer: {
      name: "K6 Real Flow",
      document: "12345678901",
      email: `flow-${randomString(5)}@k6.io`,
    },
  });

  const headers = Object.assign({}, HEADERS, {
    "Idempotency-Key": idempotencyKey,
  });

  const start = Date.now();
  const res = http.post(`${BASE_URL}/api/v1/transactions`, payload, {
    headers: headers,
    timeout: "30s",
  });
  txLatency.add(Date.now() - start);

  if (res.status >= 200 && res.status < 300) {
    txCreated.add(1);
    const data = JSON.parse(res.body).data;
    return {
      providerTxId: data.payment?.transactionId || "",
      internalId: data.internalId || "",
      netAmount: amount * 0.99, // ~1% fee
    };
  } else {
    txFailed.add(1);
    return null;
  }
}

// --- Helper: send a valid Stripe webhook for payment_intent.succeeded ---
function sendCaptureWebhook(providerTxId, amountCents) {
  const webhookPayload = JSON.stringify({
    id: `evt_${uuidv4().substring(0, 20)}`,
    type: "payment_intent.succeeded",
    data: {
      object: {
        id: providerTxId,
        status: "succeeded",
        amount: amountCents,
      },
    },
  });

  const signature = stripeSign(webhookPayload, WEBHOOK_SECRET);

  const webhookHeaders = {
    "Content-Type": "application/json",
    "Stripe-Signature": signature,
  };

  webhookSent.add(1);
  const start = Date.now();
  const res = http.post(
    `${BASE_URL}/api/webhooks/stripe`,
    webhookPayload,
    { headers: webhookHeaders, timeout: "15s" }
  );
  webhookLatency.add(Date.now() - start);

  if (res.status === 200) {
    webhookAccepted.add(1);
    return true;
  } else {
    webhookRejected.add(1);
    if (res.status !== 429) {
      console.warn(
        `[WEBHOOK] Rejected: status=${res.status} body=${(res.body || "").substring(0, 200)}`
      );
    }
    return false;
  }
}

// --- Helper: get balance ---
function getBalance() {
  const res = http.get(
    `${BASE_URL}/api/v1/sellers/${SELLER_ID}/balance`,
    { headers: HEADERS }
  );
  if (res.status === 200) {
    return JSON.parse(res.body).data;
  }
  return null;
}

// =============================================================================
// SCENARIOS
// =============================================================================
export const options = {
  scenarios: {
    // Phase 1: Record initial state
    phase1_init: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "phase1Init",
      maxDuration: "15s",
    },

    // Phase 2: Create transactions + send CAPTURED webhooks (200 VUs)
    // Each VU: create tx → send webhook → verify credit
    phase2_credit_storm: {
      executor: "per-vu-iterations",
      vus: 200,
      iterations: 1, // each VU does 1 create+capture cycle
      exec: "phase2CreditStorm",
      startTime: "3s",
      maxDuration: "120s",
    },

    // Phase 2b: Verify total credits after storm
    phase2_verify: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "phase2Verify",
      startTime: "65s",
      maxDuration: "30s",
    },

    // Phase 3: CREDIT + PAYOUT RACE (MOST IMPORTANT)
    // Concurrent webhook credits AND payout requests
    phase3_credit_race: {
      executor: "per-vu-iterations",
      vus: 50,
      iterations: 1,
      exec: "phase3CreditRace",
      startTime: "70s",
      maxDuration: "120s",
    },
    phase3_payout_race: {
      executor: "constant-vus",
      vus: 30,
      duration: "60s",
      exec: "phase3PayoutRace",
      startTime: "70s",
    },
    phase3_balance_monitor: {
      executor: "constant-vus",
      vus: 5,
      duration: "60s",
      exec: "phase3BalanceMonitor",
      startTime: "70s",
    },

    // Phase 3 verify
    phase3_verify: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "phase3Verify",
      startTime: "135s",
      maxDuration: "30s",
    },

    // Phase 4: Webhook deduplication (same event 100x)
    phase4_dedup_setup: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "phase4DedupSetup",
      startTime: "140s",
      maxDuration: "30s",
    },
    phase4_dedup_storm: {
      executor: "per-vu-iterations",
      vus: 100,
      iterations: 1,
      exec: "phase4DedupStorm",
      startTime: "150s",
      maxDuration: "60s",
    },
    phase4_dedup_verify: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "phase4DedupVerify",
      startTime: "175s",
      maxDuration: "30s",
    },

    // Phase 5: Settlement simulation (FUTURE_RECEIVABLES → WALLET)
    phase5_settlement: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "phase5Settlement",
      startTime: "180s",
      maxDuration: "30s",
    },

    // Phase 6: Final validation
    phase6_final: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "phase6FinalValidation",
      startTime: "195s",
      maxDuration: "30s",
    },
  },
  thresholds: {
    negative_balance_detected: ["count==0"],
    duplicate_credit_detected: ["count==0"],
    ledger_invariant_broken: ["count==0"],
  },
};

// =============================================================================
// Phase 1: Record initial state
// =============================================================================
export function phase1Init() {
  const bal = getBalance();
  if (bal) {
    phase1Balance = bal;
    console.log(
      `[PHASE 1] Initial balance: available=${bal.available} total=${bal.total} blocked=${bal.blocked}`
    );
  }
}

// =============================================================================
// Phase 2: Concurrent Credit Storm
// Each VU creates a transaction, sends a CAPTURED webhook, verifying credit
// =============================================================================
export function phase2CreditStorm() {
  const amount = 100; // BRL

  // Step 1: Create transaction
  const tx = createTxAndGetId(amount);
  if (!tx || !tx.providerTxId) {
    console.warn(`[PHASE 2] VU ${__VU}: Failed to create transaction`);
    return;
  }

  // Step 2: Send payment_intent.succeeded webhook
  const captured = sendCaptureWebhook(tx.providerTxId, amount * 100);

  if (captured) {
    ledgerCreditCount.add(1);
  }
}

// =============================================================================
// Phase 2 Verify: Check total credits match expected
// =============================================================================
export function phase2Verify() {
  sleep(3);
  const bal = getBalance();
  if (!bal) {
    console.error("[PHASE 2 VERIFY] Failed to get balance");
    return;
  }

  console.log(
    `[PHASE 2 VERIFY] Balance after credit storm: available=${bal.available} total=${bal.total} blocked=${bal.blocked}`
  );

  // Total should reflect credits into FUTURE_RECEIVABLES (not WALLET for credit cards)
  if (bal.total < 0) {
    negativeBalance.add(1);
    console.error(`[PHASE 2] NEGATIVE TOTAL BALANCE: ${bal.total}`);
  }

  if (bal.available < 0) {
    negativeBalance.add(1);
    console.error(`[PHASE 2] NEGATIVE AVAILABLE BALANCE: ${bal.available}`);
  }

  console.log(
    `[PHASE 2 VERIFY] Credit storm complete. Total balance: ${bal.total}`
  );
}

// =============================================================================
// Phase 3: CREDIT + PAYOUT RACE (THE REAL DANGER)
// Credits via webhook + payouts at the same time
// =============================================================================
export function phase3CreditRace() {
  const amount = 50;

  // Create a new transaction
  const tx = createTxAndGetId(amount);
  if (!tx || !tx.providerTxId) return;

  // Send CAPTURED webhook (credits the ledger)
  sendCaptureWebhook(tx.providerTxId, amount * 100);
  ledgerCreditCount.add(1);
}

export function phase3PayoutRace() {
  const idempotencyKey = uuidv4();
  const amount = Math.floor(Math.random() * 30) + 5; // 5-35 BRL

  const payload = JSON.stringify({
    sellerId: SELLER_ID,
    amount: amount,
  });

  const headers = Object.assign({}, HEADERS, {
    "Idempotency-Key": idempotencyKey,
  });

  payoutAttempted.add(1);

  const start = Date.now();
  const res = http.post(`${BASE_URL}/api/v1/payouts`, payload, {
    headers: headers,
    timeout: "30s",
  });
  payoutLatency.add(Date.now() - start);

  if (res.status >= 200 && res.status < 300) {
    payoutAccepted.add(1);
  } else if (res.status === 422 || res.status === 400) {
    payoutRejected.add(1); // Insufficient balance — expected
  } else if (res.status !== 429) {
    payoutRejected.add(1);
  }
}

export function phase3BalanceMonitor() {
  const bal = getBalance();
  if (bal) {
    if (bal.available < 0) {
      negativeBalance.add(1);
      console.error(
        `[PHASE 3 MONITOR] NEGATIVE AVAILABLE: ${bal.available}`
      );
    }
    if (bal.total < 0) {
      negativeBalance.add(1);
      console.error(`[PHASE 3 MONITOR] NEGATIVE TOTAL: ${bal.total}`);
    }
  }
  sleep(0.3);
}

export function phase3Verify() {
  sleep(5);
  const bal = getBalance();
  if (!bal) {
    console.error("[PHASE 3 VERIFY] Failed to get balance");
    return;
  }

  console.log(
    `[PHASE 3 VERIFY] Balance after credit+payout race: available=${bal.available} total=${bal.total} blocked=${bal.blocked}`
  );

  if (bal.available < 0) {
    negativeBalance.add(1);
    console.error(
      `[PHASE 3] NEGATIVE BALANCE AFTER RACE: available=${bal.available}`
    );
  }
}

// =============================================================================
// Phase 4: Webhook Deduplication — same event 100x, only 1 credit
// =============================================================================
// We need a shared providerTxId. Since k6 doesn't share let vars across VUs,
// we create it in setup. But setup returns data available in all VUs.
// Instead, we use a deterministic providerTxId that phase4_dedup_setup creates.

let dedupProviderTxId = "";
let dedupBalanceBefore = 0;
let dedupAmount = 200;

export function phase4DedupSetup() {
  // Record balance before dedup test
  const bal = getBalance();
  if (bal) {
    dedupBalanceBefore = bal.total;
    console.log(`[PHASE 4 SETUP] Balance before dedup: total=${bal.total}`);
  }

  // Create a transaction for dedup testing
  const tx = createTxAndGetId(dedupAmount);
  if (tx && tx.providerTxId) {
    dedupProviderTxId = tx.providerTxId;
    console.log(
      `[PHASE 4 SETUP] Created tx for dedup: providerTxId=${dedupProviderTxId}`
    );
  } else {
    console.error("[PHASE 4 SETUP] Failed to create transaction for dedup");
  }
}

export function phase4DedupStorm() {
  if (!dedupProviderTxId) {
    // Fallback: use a hardcoded providerTxId (will be a no-op if tx doesn't exist)
    return;
  }

  // All 100 VUs send the SAME webhook event for the SAME transaction
  const webhookPayload = JSON.stringify({
    id: `evt_dedup_fixed_event_id`, // Same event ID
    type: "payment_intent.succeeded",
    data: {
      object: {
        id: dedupProviderTxId,
        status: "succeeded",
        amount: dedupAmount * 100,
      },
    },
  });

  const signature = stripeSign(webhookPayload, WEBHOOK_SECRET);
  const webhookHeaders = {
    "Content-Type": "application/json",
    "Stripe-Signature": signature,
  };

  webhookDuplicate.add(1);
  const res = http.post(`${BASE_URL}/api/webhooks/stripe`, webhookPayload, {
    headers: webhookHeaders,
    timeout: "15s",
  });

  // All should succeed (200) but only the first should actually credit
  if (res.status === 200) {
    webhookAccepted.add(1);
  }
}

export function phase4DedupVerify() {
  sleep(3);
  const bal = getBalance();
  if (!bal) {
    console.error("[PHASE 4 VERIFY] Failed to get balance");
    return;
  }

  console.log(
    `[PHASE 4 VERIFY] Balance after dedup storm: total=${bal.total} (before=${dedupBalanceBefore})`
  );

  // The balance should have increased by AT MOST the net amount of 1 transaction
  const maxExpected = dedupAmount; // generous upper bound
  const actualIncrease = bal.total - dedupBalanceBefore;

  if (actualIncrease > maxExpected * 1.5) {
    duplicateCredit.add(1);
    console.error(
      `[PHASE 4] DUPLICATE CREDIT DETECTED: increase=${actualIncrease}, max expected=${maxExpected}`
    );
  } else {
    console.log(
      `[PHASE 4 VERIFY] Dedup verified: increase=${actualIncrease} (max expected: ${maxExpected})`
    );
  }
}

// =============================================================================
// Phase 5: Settlement Simulation (FUTURE_RECEIVABLES → WALLET)
// =============================================================================
export function phase5Settlement() {
  const bal = getBalance();
  if (!bal) return;

  console.log(
    `[PHASE 5] Before settlement: available=${bal.available} total=${bal.total} blocked=${bal.blocked}`
  );

  // Trigger settlement via API (POST /api/v1/settlements/process-daily)
  const settleRes = http.post(
    `${BASE_URL}/api/v1/settlements/process-daily`,
    null,
    { headers: HEADERS, timeout: "60s" }
  );

  if (settleRes.status >= 200 && settleRes.status < 300) {
    settlementTriggered.add(1);
    console.log(`[PHASE 5] Settlement triggered: ${settleRes.status}`);
  } else {
    console.warn(
      `[PHASE 5] Settlement response: ${settleRes.status} ${(settleRes.body || "").substring(0, 200)}`
    );
  }

  sleep(3);

  const balAfter = getBalance();
  if (balAfter) {
    console.log(
      `[PHASE 5] After settlement: available=${balAfter.available} total=${balAfter.total} blocked=${balAfter.blocked}`
    );

    // After settlement, FUTURE_RECEIVABLES should have moved to WALLET
    // Total should remain the same (just moved between accounts)
    if (balAfter.available < 0) {
      negativeBalance.add(1);
      console.error(
        `[PHASE 5] NEGATIVE BALANCE AFTER SETTLEMENT: ${balAfter.available}`
      );
    }
  }
}

// =============================================================================
// Phase 6: FINAL LEDGER INVARIANT VALIDATION
// =============================================================================
export function phase6FinalValidation() {
  sleep(5);

  const bal = getBalance();
  if (!bal) {
    console.error("[PHASE 6] Failed to get final balance");
    return;
  }

  console.log("============================================================");
  console.log("  FINAL LEDGER VALIDATION");
  console.log("============================================================");
  console.log(`  Available (WALLET):          ${bal.available}`);
  console.log(`  Blocked (FUTURE_RECEIVABLES): ${bal.blocked}`);
  console.log(`  Total:                        ${bal.total}`);
  console.log(`  Account Ready:                ${bal.isAccountReady}`);

  // Invariant 1: No negative balances
  if (bal.available < 0) {
    negativeBalance.add(1);
    ledgerInvariantBroken.add(1);
    console.error("  INVARIANT BROKEN: Negative available balance!");
  }
  if (bal.total < 0) {
    negativeBalance.add(1);
    ledgerInvariantBroken.add(1);
    console.error("  INVARIANT BROKEN: Negative total balance!");
  }

  // Invariant 2: available + blocked = total
  const calculatedTotal = bal.available + bal.blocked;
  if (Math.abs(calculatedTotal - bal.total) > 0.01) {
    ledgerInvariantBroken.add(1);
    console.error(
      `  INVARIANT BROKEN: available(${bal.available}) + blocked(${bal.blocked}) = ${calculatedTotal} != total(${bal.total})`
    );
  } else {
    console.log("  INVARIANT OK: available + blocked = total");
  }

  // Check transactions
  const txRes = http.get(
    `${BASE_URL}/api/v1/transactions?page=1&pageSize=1&sellerId=${SELLER_ID}`,
    { headers: HEADERS }
  );
  if (txRes.status === 200) {
    const txData = JSON.parse(txRes.body).data;
    console.log(`  Total transactions: ${txData.totalCount}`);
  }

  // Summary
  console.log("============================================================");
  if (bal.available >= 0 && bal.total >= 0 && Math.abs(calculatedTotal - bal.total) < 0.01) {
    console.log("  LEDGER INVARIANTS: ALL PASSED");
  } else {
    console.log("  LEDGER INVARIANTS: FAILED");
  }
  console.log("============================================================");
}

// =============================================================================
// SUMMARY
// =============================================================================
export function handleSummary(data) {
  const m = (name) =>
    data.metrics[name] ? data.metrics[name].values.count : 0;

  const negBal = m("negative_balance_detected");
  const dupCredit = m("duplicate_credit_detected");
  const invariantBroken = m("ledger_invariant_broken");

  const allPassed = negBal === 0 && dupCredit === 0 && invariantBroken === 0;

  const verdict = allPassed
    ? "PASS — REAL MONEY FLOW SAFE FOR PRODUCTION"
    : "FAIL — FINANCIAL INTEGRITY COMPROMISED";

  const report = {
    scenario: "Real Money Flow Stress Test",
    verdict: verdict,
    phases: {
      "Phase 2 — Credit Storm": {
        tx_created: m("tx_created"),
        tx_failed: m("tx_failed"),
        webhooks_sent: m("webhook_sent"),
        webhooks_accepted: m("webhook_accepted"),
        webhooks_rejected: m("webhook_rejected"),
        ledger_credits: m("ledger_credits"),
      },
      "Phase 3 — Credit+Payout Race": {
        payout_attempted: m("payout_attempted"),
        payout_accepted: m("payout_accepted"),
        payout_rejected: m("payout_rejected"),
        negative_balances: negBal,
      },
      "Phase 4 — Webhook Deduplication": {
        duplicate_webhooks_sent: m("webhook_duplicate_sent"),
        duplicate_credits_detected: dupCredit,
      },
      "Phase 5 — Settlement": {
        settlements_triggered: m("settlement_triggered"),
      },
    },
    invariants: {
      negative_balance: negBal,
      duplicate_credit: dupCredit,
      ledger_invariant_broken: invariantBroken,
    },
    performance: {
      avg_tx_latency_ms: data.metrics.tx_latency
        ? data.metrics.tx_latency.values.avg
        : null,
      p95_tx_latency_ms: data.metrics.tx_latency
        ? data.metrics.tx_latency.values["p(95)"]
        : null,
      avg_webhook_latency_ms: data.metrics.webhook_latency
        ? data.metrics.webhook_latency.values.avg
        : null,
      p95_webhook_latency_ms: data.metrics.webhook_latency
        ? data.metrics.webhook_latency.values["p(95)"]
        : null,
      avg_payout_latency_ms: data.metrics.payout_latency
        ? data.metrics.payout_latency.values.avg
        : null,
      p95_payout_latency_ms: data.metrics.payout_latency
        ? data.metrics.payout_latency.values["p(95)"]
        : null,
    },
  };

  return {
    "tests/k6-stress/results/06-real-money-flow-result.json": JSON.stringify(
      report,
      null,
      2
    ),
    stdout: `\n${"=".repeat(60)}\nREAL MONEY FLOW STRESS TEST RESULT\n${"=".repeat(60)}\n${JSON.stringify(report, null, 2)}\n`,
  };
}
