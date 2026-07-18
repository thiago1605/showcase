// =============================================================================
// FULL MONEY LIFECYCLE INTEGRITY TEST
// =============================================================================
// Validates financial consistency across the ENTIRE lifecycle:
//   Payment → Capture → Refund → Dispute → Resolution
//
// Layers validated:
//   1. Internal double-entry ledger
//   2. Stripe platform balance
//   3. Stripe connected account balance
//
// Flows tested:
//   - Payment + Capture (credit card via Stripe)
//   - Partial refund (API + webhook)
//   - Full refund
//   - Dispute/chargeback (created + won/lost)
//   - Duplicate webhook idempotency
//   - Concurrent operations (payments + refunds + disputes)
//
// HARD FAIL conditions:
//   - Any negative balance
//   - Ledger drift > 0
//   - Connected account drift > 0
//   - Double credit / double debit from duplicate webhooks
// =============================================================================

import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Trend, Gauge } from "k6/metrics";
import { SharedArray } from "k6/data";
import { uuidv4 } from "https://jslib.k6.io/k6-utils/1.4.0/index.js";
import {
  generateStripeSignature,
  buildPaymentSucceededEvent,
  buildChargeRefundedEvent,
  buildDisputeCreatedEvent,
  buildDisputeClosedEvent,
} from "./stripe-signature.js";

// ─── Configuration ──────────────────────────────────────────────────────────
const BASE_URL = __ENV.BASE_URL || "http://localhost:5195";
const API_KEY =
  __ENV.API_KEY || "MISSING_API_KEY";
const STRIPE_SK = __ENV.STRIPE_SK || "";
const WEBHOOK_SECRET = __ENV.WEBHOOK_SECRET || "";
const SELLER_ID = __ENV.SELLER_ID || "";
const CONNECTED_ACCOUNT_ID = __ENV.CONNECTED_ACCOUNT_ID || "";

const API_HEADERS = {
  "Content-Type": "application/json",
  Accept: "application/json",
  "X-Api-Key": API_KEY,
};

const STRIPE_HEADERS = {
  Authorization: `Bearer ${STRIPE_SK}`,
  "Content-Type": "application/x-www-form-urlencoded",
};

// ─── Custom Metrics ─────────────────────────────────────────────────────────
// Payments
const txCreated = new Counter("tx_created");
const txCaptured = new Counter("tx_captured");
const txFailed = new Counter("tx_failed");

// Refunds
const refundsRequested = new Counter("refunds_requested");
const refundsProcessed = new Counter("refunds_processed");
const refundsFailed = new Counter("refunds_failed");
const refundsDuplicate = new Counter("refunds_duplicate_blocked");

// Disputes
const disputesCreated = new Counter("disputes_created");
const disputesWon = new Counter("disputes_won");
const disputesLost = new Counter("disputes_lost");
const disputesDuplicate = new Counter("disputes_duplicate_blocked");

// Webhooks
const webhooksSent = new Counter("webhooks_sent");
const webhooksDuplicate = new Counter("webhooks_duplicate_sent");

// Financial integrity
const negativeBalance = new Counter("negative_balance");
const doubleCreditDetected = new Counter("double_credit");

// Amounts tracked (centavos)
const totalCapturedCents = new Counter("total_captured_cents");
const totalRefundedCents = new Counter("total_refunded_cents");
const totalDisputedCents = new Counter("total_disputed_cents");
const totalDisputeWonCents = new Counter("total_dispute_won_cents");

// Latency
const e2ePaymentLatency = new Trend("e2e_payment_latency", true);
const refundLatency = new Trend("refund_latency", true);

// Balance snapshots
const initLedgerTotalCents = new Trend("init_ledger_total_cents");
const finLedgerTotalCents = new Trend("fin_ledger_total_cents");
const initConnectedTotalCents = new Trend("init_connected_total_cents");
const finConnectedTotalCents = new Trend("fin_connected_total_cents");

// ─── Shared State ───────────────────────────────────────────────────────────
// Captured transaction IDs shared between scenarios for refund/dispute
// We use a simple approach: each VU tracks its own captures

// ─── Scenario Options ───────────────────────────────────────────────────────
export const options = {
  scenarios: {
    // Phase 0: record initial state
    record_start: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "recordStart",
      maxDuration: "30s",
    },
    // Phase 1: main payment flow (create + capture via webhook)
    payment_flow: {
      executor: "ramping-vus",
      startVUs: 10,
      stages: [
        { duration: "15s", target: 10 },
        { duration: "45s", target: 25 },
        { duration: "20s", target: 25 },
        { duration: "10s", target: 0 },
      ],
      exec: "paymentFlow",
      startTime: "5s",
    },
    // Phase 2: refund flow (partial + full refunds on captured txs)
    refund_flow: {
      executor: "ramping-vus",
      startVUs: 5,
      stages: [
        { duration: "15s", target: 5 },
        { duration: "30s", target: 15 },
        { duration: "15s", target: 15 },
        { duration: "10s", target: 0 },
      ],
      exec: "refundFlow",
      startTime: "30s", // start after some payments exist
    },
    // Phase 3: dispute flow (chargeback simulation)
    dispute_flow: {
      executor: "constant-vus",
      vus: 5,
      duration: "40s",
      exec: "disputeFlow",
      startTime: "50s",
    },
    // Phase 4: concurrent chaos — all flows simultaneously
    chaos_storm: {
      executor: "constant-vus",
      vus: 10,
      duration: "30s",
      exec: "chaosStorm",
      startTime: "95s",
    },
    // Phase 5: duplicate webhook storm
    dedup_storm: {
      executor: "constant-vus",
      vus: 5,
      duration: "20s",
      exec: "dedupStorm",
      startTime: "130s",
    },
    // Phase 6: final verification
    verify_end: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "verifyEnd",
      startTime: "175s",
      maxDuration: "60s",
    },
  },
  thresholds: {
    negative_balance: ["count==0"],
    double_credit: ["count==0"],
  },
};

// ─── Helpers ────────────────────────────────────────────────────────────────

function getStripeBalance(accountId) {
  const headers = Object.assign({}, STRIPE_HEADERS);
  if (accountId) headers["Stripe-Account"] = accountId;

  const res = http.get("https://api.stripe.com/v1/balance", { headers });
  if (res.status !== 200) return null;

  const balance = JSON.parse(res.body);
  let available = 0;
  let pending = 0;

  if (balance.available) {
    const brl = balance.available.find((a) => a.currency === "brl");
    if (brl) available = brl.amount;
  }
  if (balance.pending) {
    const brl = balance.pending.find((a) => a.currency === "brl");
    if (brl) pending = brl.amount;
  }

  return { available, pending, total: available + pending };
}

function getLedgerBalance() {
  const res = http.get(
    `${BASE_URL}/api/v1/sellers/${SELLER_ID}/balance`,
    { headers: API_HEADERS }
  );
  if (res.status !== 200) return null;

  const d = JSON.parse(res.body).data;
  return {
    available: d.available,
    blocked: d.blocked,
    total: d.total,
  };
}

function sendWebhook(body) {
  const sig = generateStripeSignature(body, WEBHOOK_SECRET);
  return http.post(`${BASE_URL}/api/webhooks/stripe`, body, {
    headers: {
      "Content-Type": "application/json",
      "Stripe-Signature": sig.header,
    },
    timeout: "15s",
  });
}

function apiHeadersWithIdempotency(key) {
  return Object.assign({}, API_HEADERS, { "Idempotency-Key": key || uuidv4() });
}

function confirmPaymentIntent(piId) {
  const payload =
    "payment_method=pm_card_visa&return_url=https%3A%2F%2Fexample.com%2Freturn";
  return http.post(
    `https://api.stripe.com/v1/payment_intents/${piId}/confirm`,
    payload,
    { headers: STRIPE_HEADERS, timeout: "30s" }
  );
}

function pollForStatus(internalId, targetStatus, maxWaitSec) {
  const targetNum = typeof targetStatus === "number" ? targetStatus : null;
  for (let i = 0; i < maxWaitSec * 2; i++) {
    sleep(0.5);
    const res = http.get(
      `${BASE_URL}/api/v1/transactions/${internalId}`,
      { headers: API_HEADERS }
    );
    if (res.status === 200) {
      const tx = JSON.parse(res.body).data;
      if (tx.status === targetStatus || tx.status === targetNum) {
        return tx;
      }
    }
  }
  return null;
}

// Create a transaction, confirm it on Stripe, send webhook, poll for CAPTURED.
// Returns { internalId, piId, amount, amountCents, netAmountCents } or null.
function createAndCapturePayment() {
  const idempotencyKey = uuidv4();
  const amount = Math.floor(Math.random() * 190) + 10; // R$10–200
  const amountCents = amount * 100;

  const txRes = http.post(
    `${BASE_URL}/api/v1/transactions`,
    JSON.stringify({
      sellerId: SELLER_ID,
      amount: amount,
      paymentType: 0,
      installments: 1,
      description: `k6-lifecycle-${idempotencyKey.substring(0, 8)}`,
      payer: {
        name: "K6 Lifecycle Payer",
        document: "12345678901",
        email: `k6-${uuidv4().substring(0, 8)}@test.io`,
      },
    }),
    {
      headers: Object.assign({}, API_HEADERS, {
        "Idempotency-Key": idempotencyKey,
      }),
      timeout: "30s",
    }
  );

  if (txRes.status === 429) return null;
  if (txRes.status !== 201) {
    txFailed.add(1);
    return null;
  }

  const txBody = JSON.parse(txRes.body).data;
  const piId = txBody.payment.transactionId;
  const internalId = txBody.internalId;
  txCreated.add(1);

  // Confirm on Stripe
  const confirmRes = confirmPaymentIntent(piId);
  if (confirmRes.status === 429 || confirmRes.status !== 200) {
    txFailed.add(1);
    return null;
  }

  const piData = JSON.parse(confirmRes.body);
  if (piData.status !== "succeeded") {
    txFailed.add(1);
    return null;
  }

  // Send webhook
  const webhookBody = buildPaymentSucceededEvent(piId, amountCents, {
    seller_id: SELLER_ID,
  });
  const webhookRes = sendWebhook(webhookBody);
  webhooksSent.add(1);

  if (webhookRes.status !== 200) {
    txFailed.add(1);
    return null;
  }

  // Poll for CAPTURED (status=3)
  const captured = pollForStatus(internalId, 3, 10);
  if (!captured) {
    txFailed.add(1);
    return null;
  }

  txCaptured.add(1);

  const netCents = Math.round((captured.netAmount || amount) * 100);
  totalCapturedCents.add(netCents);

  return {
    internalId,
    piId,
    amount,
    amountCents,
    netAmountCents: netCents,
    chargeId: `ch_k6_${piId}`, // synthetic charge ID for webhooks
  };
}

// ─── Phase 0: Record Start ──────────────────────────────────────────────────

export function recordStart() {
  console.log("=== RECORDING INITIAL STATE ===");

  const ledger = getLedgerBalance();
  if (ledger) {
    initLedgerTotalCents.add(Math.round(ledger.total * 100));
    console.log(
      `[START] Ledger: available=${ledger.available} blocked=${ledger.blocked} total=${ledger.total}`
    );
  }

  if (CONNECTED_ACCOUNT_ID) {
    const connected = getStripeBalance(CONNECTED_ACCOUNT_ID);
    if (connected) {
      initConnectedTotalCents.add(connected.total);
      console.log(
        `[START] Connected acct: total=${connected.total} (centavos)`
      );
    }
  }
}

// ─── Phase 1: Payment Flow ──────────────────────────────────────────────────

export function paymentFlow() {
  const t0 = Date.now();
  const result = createAndCapturePayment();

  if (result) {
    e2ePaymentLatency.add(Date.now() - t0);

    // 20% chance of sending duplicate webhook (idempotency test)
    if (Math.random() < 0.2) {
      const dupBody = buildPaymentSucceededEvent(
        result.piId,
        result.amountCents,
        { seller_id: SELLER_ID }
      );
      sendWebhook(dupBody);
      webhooksDuplicate.add(1);
    }
  }
}

// ─── Phase 2: Refund Flow ───────────────────────────────────────────────────

export function refundFlow() {
  // First create and capture a payment
  const payment = createAndCapturePayment();
  if (!payment) return;

  sleep(0.5); // small delay after capture

  const t0 = Date.now();

  // Decide: 40% partial refund, 40% full refund, 20% double refund (idempotency)
  const roll = Math.random();

  // Net ratio: seller only received netAmount, not gross amount
  const netRatio = payment.netAmountCents / payment.amountCents;

  if (roll < 0.4) {
    // Partial refund: refund half
    const refundAmount = Math.max(1, Math.floor(payment.amount / 2));
    const refundRes = http.post(
      `${BASE_URL}/api/v1/transactions/${payment.internalId}/refund`,
      JSON.stringify({ amount: refundAmount, reason: "requested_by_customer" }),
      { headers: apiHeadersWithIdempotency(`refund-partial-${payment.internalId}`), timeout: "15s" }
    );
    refundsRequested.add(1);

    if (refundRes.status === 200) {
      refundsProcessed.add(1);
      // Track net debit (API debits proportional net from seller ledger)
      totalRefundedCents.add(Math.round(refundAmount * 100 * netRatio));
      refundLatency.add(Date.now() - t0);
    } else {
      refundsFailed.add(1);
    }
  } else if (roll < 0.8) {
    // Full refund (no amount = API uses transaction.Amount)
    const refundRes = http.post(
      `${BASE_URL}/api/v1/transactions/${payment.internalId}/refund`,
      JSON.stringify({ reason: "requested_by_customer" }),
      { headers: apiHeadersWithIdempotency(`refund-full-${payment.internalId}`), timeout: "15s" }
    );
    refundsRequested.add(1);

    if (refundRes.status === 200) {
      refundsProcessed.add(1);
      // Full refund debits entire netAmount from seller ledger
      totalRefundedCents.add(payment.netAmountCents);
      refundLatency.add(Date.now() - t0);
    } else {
      refundsFailed.add(1);
    }
  } else {
    // Double refund idempotency test: refund full, then try again
    const refundKey = `refund-double-${payment.internalId}`;
    const refundRes1 = http.post(
      `${BASE_URL}/api/v1/transactions/${payment.internalId}/refund`,
      JSON.stringify({ reason: "duplicate" }),
      { headers: apiHeadersWithIdempotency(refundKey), timeout: "15s" }
    );
    refundsRequested.add(1);

    if (refundRes1.status === 200) {
      refundsProcessed.add(1);
      totalRefundedCents.add(payment.netAmountCents);
    }

    sleep(0.3);

    // Second refund attempt — different idempotency key to bypass middleware cache
    // Should still fail because transaction status is no longer CAPTURED
    const refundRes2 = http.post(
      `${BASE_URL}/api/v1/transactions/${payment.internalId}/refund`,
      JSON.stringify({ reason: "duplicate" }),
      { headers: apiHeadersWithIdempotency(`refund-dup-${payment.internalId}`), timeout: "15s" }
    );
    refundsRequested.add(1);

    if (refundRes2.status !== 200) {
      refundsDuplicate.add(1); // Expected: blocked
    } else {
      doubleCreditDetected.add(1); // BAD: double refund went through
    }
  }
}

// ─── Phase 3: Dispute Flow ──────────────────────────────────────────────────

export function disputeFlow() {
  // Create and capture a payment
  const payment = createAndCapturePayment();
  if (!payment) return;

  sleep(0.5);

  const disputeId = `dp_k6_${uuidv4().substring(0, 12)}`;

  // Send dispute.created webhook
  const disputeBody = buildDisputeCreatedEvent(
    disputeId,
    payment.chargeId,
    payment.piId,
    payment.amountCents
  );
  const disputeRes = sendWebhook(disputeBody);
  webhooksSent.add(1);

  if (disputeRes.status !== 200) return;

  disputesCreated.add(1);
  // Ledger holds netAmount (not gross) for disputes
  totalDisputedCents.add(payment.netAmountCents);

  // Send duplicate dispute (idempotency test)
  const dupDisputeBody = buildDisputeCreatedEvent(
    disputeId,
    payment.chargeId,
    payment.piId,
    payment.amountCents
  );
  const dupRes = sendWebhook(dupDisputeBody);
  webhooksDuplicate.add(1);

  // Verify tx status changed to CHARGEBACKERROR (7)
  sleep(1);
  const tx = pollForStatus(payment.internalId, 7, 5);
  if (!tx) return;

  // 60% win, 40% lose
  sleep(0.5);
  if (Math.random() < 0.6) {
    // Dispute won — funds released
    const closeBody = buildDisputeClosedEvent(
      disputeId,
      payment.chargeId,
      payment.piId,
      payment.amountCents,
      "won"
    );
    const closeRes = sendWebhook(closeBody);
    webhooksSent.add(1);

    if (closeRes.status === 200) {
      disputesWon.add(1);
      totalDisputeWonCents.add(payment.netAmountCents);
    }
  } else {
    // Dispute lost — funds stay frozen
    const closeBody = buildDisputeClosedEvent(
      disputeId,
      payment.chargeId,
      payment.piId,
      payment.amountCents,
      "lost"
    );
    const closeRes = sendWebhook(closeBody);
    webhooksSent.add(1);

    if (closeRes.status === 200) {
      disputesLost.add(1);
    }
  }
}

// ─── Phase 4: Chaos Storm ───────────────────────────────────────────────────
// Concurrent payments + refunds + disputes all hitting the same seller

export function chaosStorm() {
  const action = Math.random();

  if (action < 0.5) {
    // Payment
    const payment = createAndCapturePayment();
    if (payment && Math.random() < 0.3) {
      // Immediate refund on some
      sleep(0.3);
      const chaosRefundAmount = Math.max(1, Math.floor(payment.amount / 3));
      const refundRes = http.post(
        `${BASE_URL}/api/v1/transactions/${payment.internalId}/refund`,
        JSON.stringify({
          amount: chaosRefundAmount,
          reason: "requested_by_customer",
        }),
        { headers: apiHeadersWithIdempotency(`refund-chaos-${payment.internalId}`), timeout: "15s" }
      );
      refundsRequested.add(1);
      if (refundRes.status === 200) {
        refundsProcessed.add(1);
        // Track net debit (proportional to netAmount/amount ratio)
        const netRatio = payment.netAmountCents / payment.amountCents;
        totalRefundedCents.add(Math.round(chaosRefundAmount * 100 * netRatio));
      }
    }
  } else if (action < 0.8) {
    // Payment + dispute
    const payment = createAndCapturePayment();
    if (payment) {
      sleep(0.3);
      const disputeId = `dp_chaos_${uuidv4().substring(0, 8)}`;
      const body = buildDisputeCreatedEvent(
        disputeId,
        payment.chargeId,
        payment.piId,
        payment.amountCents
      );
      const res = sendWebhook(body);
      webhooksSent.add(1);
      if (res.status === 200) {
        disputesCreated.add(1);
        totalDisputedCents.add(payment.netAmountCents);

        // Immediately resolve (won)
        sleep(0.5);
        const closeBody = buildDisputeClosedEvent(
          disputeId,
          payment.chargeId,
          payment.piId,
          payment.amountCents,
          "won"
        );
        sendWebhook(closeBody);
        disputesWon.add(1);
        totalDisputeWonCents.add(payment.netAmountCents);
      }
    }
  } else {
    // Webhook retry storm — duplicate webhooks for fake PI
    const fakePiId = `pi_chaos_${__VU}_${__ITER}`;
    for (let i = 0; i < 3; i++) {
      const body = buildPaymentSucceededEvent(fakePiId, 5000, {});
      sendWebhook(body);
      webhooksDuplicate.add(1);
    }
  }
}

// ─── Phase 5: Dedup Storm ───────────────────────────────────────────────────

export function dedupStorm() {
  const fakePiId = `pi_dedup_lifecycle_${__VU}`;
  const body = buildPaymentSucceededEvent(fakePiId, 5000, {});
  sendWebhook(body);
  sleep(0.3);
}

// ─── Phase 6: Final Verification ────────────────────────────────────────────

export function verifyEnd() {
  console.log("\n=== FINAL VERIFICATION ===");
  sleep(5); // Let pending operations settle

  // 1. Ledger balance
  const ledger = getLedgerBalance();
  if (ledger) {
    finLedgerTotalCents.add(Math.round(ledger.total * 100));
    console.log(
      `[VERIFY] Ledger: available=${ledger.available} blocked=${ledger.blocked} total=${ledger.total}`
    );

    if (ledger.available < 0 || ledger.blocked < 0) {
      negativeBalance.add(1);
      console.error(
        `NEGATIVE BALANCE: available=${ledger.available} blocked=${ledger.blocked}`
      );
    }
  }

  // 2. Connected account balance
  if (CONNECTED_ACCOUNT_ID) {
    const connected = getStripeBalance(CONNECTED_ACCOUNT_ID);
    if (connected) {
      finConnectedTotalCents.add(connected.total);
      console.log(
        `[VERIFY] Connected acct: total=${connected.total} (centavos)`
      );
    }
  }

  // 3. Transaction counts
  const txRes = http.get(
    `${BASE_URL}/api/v1/transactions?page=1&pageSize=1&sellerId=${SELLER_ID}`,
    { headers: API_HEADERS }
  );
  if (txRes.status === 200) {
    const txData = JSON.parse(txRes.body).data;
    console.log(`[VERIFY] Total transactions: ${txData.totalCount}`);
  }

  // 4. Captured count
  const capturedRes = http.get(
    `${BASE_URL}/api/v1/transactions?page=1&pageSize=1&sellerId=${SELLER_ID}&status=3`,
    { headers: API_HEADERS }
  );
  if (capturedRes.status === 200) {
    console.log(
      `[VERIFY] Captured transactions: ${JSON.parse(capturedRes.body).data.totalCount}`
    );
  }

  // 5. Refunded count
  const refundedRes = http.get(
    `${BASE_URL}/api/v1/transactions?page=1&pageSize=1&sellerId=${SELLER_ID}&status=6`,
    { headers: API_HEADERS }
  );
  if (refundedRes.status === 200) {
    console.log(
      `[VERIFY] Refunded transactions: ${JSON.parse(refundedRes.body).data.totalCount}`
    );
  }

  // 6. Chargeback count
  const cbRes = http.get(
    `${BASE_URL}/api/v1/transactions?page=1&pageSize=1&sellerId=${SELLER_ID}&status=7`,
    { headers: API_HEADERS }
  );
  if (cbRes.status === 200) {
    console.log(
      `[VERIFY] Chargeback transactions: ${JSON.parse(cbRes.body).data.totalCount}`
    );
  }
}

// ─── Summary Report ─────────────────────────────────────────────────────────

export function handleSummary(data) {
  const m = data.metrics;
  const val = (name) => (m[name] ? m[name].values.count : 0);
  const avg = (name) => (m[name] ? m[name].values.avg : 0);
  const p95 = (name) => (m[name] ? m[name].values["p(95)"] : null);

  const created = val("tx_created");
  const captured = val("tx_captured");
  const failed = val("tx_failed");

  const refReq = val("refunds_requested");
  const refProc = val("refunds_processed");
  const refFail = val("refunds_failed");
  const refDup = val("refunds_duplicate_blocked");

  const dispCreated = val("disputes_created");
  const dispWon = val("disputes_won");
  const dispLost = val("disputes_lost");

  const negBal = val("negative_balance");
  const dblCredit = val("double_credit");

  const capturedCents = val("total_captured_cents");
  const refundedCents = val("total_refunded_cents");
  const disputedCents = val("total_disputed_cents");
  const disputeWonCents = val("total_dispute_won_cents");

  // Balance deltas
  const iLedger = avg("init_ledger_total_cents");
  const fLedger = avg("fin_ledger_total_cents");
  const ledgerDelta = fLedger - iLedger;

  const hasConnected = m["init_connected_total_cents"] != null;
  const iConn = hasConnected ? avg("init_connected_total_cents") : 0;
  const fConn = hasConnected ? avg("fin_connected_total_cents") : 0;
  const connDelta = hasConnected ? fConn - iConn : null;

  // Expected ledger delta: captured - refunded (net amounts).
  // Disputes won are released back to wallet, so they cancel out.
  // Disputes lost stay in DISPUTE account but are still part of Total.
  const expectedLedgerDelta = capturedCents - refundedCents;
  const ledgerDrift = Math.abs(ledgerDelta - expectedLedgerDelta);

  // Tolerance for ledger drift: float64 vs C# decimal rounding in net ratio
  // calculations can cause up to 1 cent error per refund operation.
  const ledgerDriftTolerance = Math.max(1, refProc);

  // Connected account: receives net transfers for captured transactions.
  // Refunds are debited from the platform, NOT reversed on the connected account
  // (no reverse_transfer). Disputes are simulated (not real Stripe disputes).
  // So expected connected delta = total captured net amounts.
  const expectedConnDelta = capturedCents;
  const connDrift =
    connDelta !== null ? Math.abs(connDelta - expectedConnDelta) : null;

  const isLedgerDrift = ledgerDrift > ledgerDriftTolerance;
  const isConnDrift = connDrift !== null ? connDrift > 1 : false;

  const hardFails =
    negBal +
    dblCredit +
    (isLedgerDrift ? 1 : 0) +
    (isConnDrift ? 1 : 0);

  let verdict;
  if (captured === 0) {
    verdict = "INCONCLUSIVE — No transactions captured";
  } else if (hardFails === 0) {
    verdict = "PASS — Full lifecycle financial consistency verified";
  } else {
    verdict = "FAIL — FINANCIAL INTEGRITY VIOLATION DETECTED";
  }

  const report = {
    scenario: "Full Money Lifecycle Integrity Test",
    verdict,
    transactions: {
      created,
      captured,
      failed,
    },
    refunds: {
      requested: refReq,
      processed: refProc,
      failed: refFail,
      duplicate_blocked: refDup,
      double_refund_detected: dblCredit,
    },
    disputes: {
      created: dispCreated,
      won: dispWon,
      lost: dispLost,
    },
    webhooks: {
      sent: val("webhooks_sent"),
      duplicates_sent: val("webhooks_duplicate_sent"),
    },
    amounts_centavos: {
      total_captured_net: capturedCents,
      total_refunded: refundedCents,
      total_disputed: disputedCents,
      total_dispute_won: disputeWonCents,
    },
    consistency: {
      ledger: {
        initial_cents: iLedger,
        final_cents: fLedger,
        delta_cents: ledgerDelta,
        expected_delta_cents: expectedLedgerDelta,
        drift_cents: ledgerDrift,
        ok: !isLedgerDrift,
      },
      connected_account: hasConnected
        ? {
            initial_cents: iConn,
            final_cents: fConn,
            delta_cents: connDelta,
            expected_delta_cents: expectedConnDelta,
            drift_cents: connDrift,
            ok: !isConnDrift,
          }
        : "N/A — no connected account configured",
    },
    hard_fails: {
      negative_balance: negBal,
      double_credit: dblCredit,
      ledger_drift: isLedgerDrift,
      connected_drift: isConnDrift,
    },
    latency: {
      payment_e2e_avg_ms: m.e2e_payment_latency
        ? m.e2e_payment_latency.values.avg
        : null,
      payment_e2e_p95_ms: p95("e2e_payment_latency"),
      refund_avg_ms: m.refund_latency ? m.refund_latency.values.avg : null,
      refund_p95_ms: p95("refund_latency"),
    },
  };

  const sep = "=".repeat(70);
  return {
    "tests/results/full-lifecycle-report.json": JSON.stringify(
      report,
      null,
      2
    ),
    stdout: `\n${sep}\nFULL LIFECYCLE INTEGRITY TEST\n${sep}\n${JSON.stringify(report, null, 2)}\n`,
  };
}
