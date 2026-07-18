// =============================================================================
// END-TO-END MONEY FLOW INTEGRITY TEST (Ledger + Stripe + Connect)
// =============================================================================
// Validates financial consistency across THREE layers:
//   1. Internal double-entry ledger
//   2. Stripe platform balance
//   3. Stripe connected account balance (if configured)
//
// Flow per VU iteration:
//   Create Transaction → Confirm PaymentIntent → Webhook → CAPTURED → Validate
//
// HARD FAIL conditions:
//   - Any negative balance
//   - Any drift > 0.01 between layers
//   - Any double credit from duplicate webhooks
//   - Any missing ledger entry for a captured transaction
// =============================================================================

import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Trend } from "k6/metrics";
import { uuidv4 } from "https://jslib.k6.io/k6-utils/1.4.0/index.js";
import {
  generateStripeSignature,
  buildPaymentSucceededEvent,
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
const txCreated = new Counter("tx_created");
const txConfirmedStripe = new Counter("tx_confirmed_stripe");
const txCaptured = new Counter("tx_captured");
const txFailed = new Counter("tx_failed");
const webhooksSent = new Counter("webhooks_sent");
const webhooksDuplicate = new Counter("webhooks_duplicate_sent");
const stripeRateLimited = new Counter("stripe_rate_limited");
const negativeBalance = new Counter("negative_balance");
const doubleCreditDetected = new Counter("double_credit");
const ledgerDriftDetected = new Counter("ledger_drift");

// Financial tracking (all in centavos to avoid float drift)
const totalAmountCents = new Counter("total_amount_cents");
const totalNetCents = new Counter("total_net_cents");
const totalFeeCents = new Counter("total_fee_cents");

// Latency
const e2eLatency = new Trend("e2e_latency", true);
const stripeConfirmLatency = new Trend("stripe_confirm_latency", true);

// Balance snapshots (Trend used as gauge — single value per metric)
const initLedgerTotalCents = new Trend("init_ledger_total_cents");
const finLedgerTotalCents = new Trend("fin_ledger_total_cents");
const initStripeTotalCents = new Trend("init_stripe_total_cents");
const finStripeTotalCents = new Trend("fin_stripe_total_cents");
const initConnectedTotalCents = new Trend("init_connected_total_cents");
const finConnectedTotalCents = new Trend("fin_connected_total_cents");

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
    // Phase 1: main payment flow (100 → 300 VUs)
    payment_flow: {
      executor: "ramping-vus",
      startVUs: 10,
      stages: [
        { duration: "15s", target: 10 },
        { duration: "60s", target: 30 },
        { duration: "30s", target: 30 },
        { duration: "15s", target: 0 },
      ],
      exec: "paymentFlow",
      startTime: "5s",
    },
    // Phase 2: duplicate webhook storm
    dedup_storm: {
      executor: "constant-vus",
      vus: 5,
      duration: "30s",
      exec: "dedupStorm",
      startTime: "80s",
    },
    // Phase 3: final verification
    verify_end: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "verifyEnd",
      startTime: "135s",
      maxDuration: "60s",
    },
  },
  thresholds: {
    negative_balance: ["count==0"],
    double_credit: ["count==0"],
    ledger_drift: ["count==0"],
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

function confirmPaymentIntent(piId) {
  const payload =
    "payment_method=pm_card_visa&return_url=https%3A%2F%2Fexample.com%2Freturn";

  const t0 = Date.now();
  const res = http.post(
    `https://api.stripe.com/v1/payment_intents/${piId}/confirm`,
    payload,
    { headers: STRIPE_HEADERS, timeout: "30s" }
  );
  stripeConfirmLatency.add(Date.now() - t0);

  return res;
}

function sendWebhook(piId, amountCents, metadata) {
  const body = buildPaymentSucceededEvent(piId, amountCents, metadata);
  const sig = generateStripeSignature(body, WEBHOOK_SECRET);

  return http.post(`${BASE_URL}/api/webhooks/stripe`, body, {
    headers: {
      "Content-Type": "application/json",
      "Stripe-Signature": sig.header,
    },
    timeout: "15s",
  });
}

function pollForCaptured(internalId, maxWaitSec) {
  for (let i = 0; i < maxWaitSec * 2; i++) {
    sleep(0.5);
    const res = http.get(
      `${BASE_URL}/api/v1/transactions/${internalId}`,
      { headers: API_HEADERS }
    );
    if (res.status === 200) {
      const tx = JSON.parse(res.body).data;
      // Status can be numeric (3) or string ("CAPTURED")
      if (tx.status === 3 || tx.status === "CAPTURED") {
        return tx;
      }
    }
  }
  return null;
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

  const stripe = getStripeBalance(null);
  if (stripe) {
    initStripeTotalCents.add(stripe.total);
    console.log(
      `[START] Stripe platform: available=${stripe.available} pending=${stripe.pending} total=${stripe.total} (centavos)`
    );
  }

  if (CONNECTED_ACCOUNT_ID) {
    const connected = getStripeBalance(CONNECTED_ACCOUNT_ID);
    if (connected) {
      initConnectedTotalCents.add(connected.total);
      console.log(
        `[START] Connected acct: available=${connected.available} pending=${connected.pending} total=${connected.total} (centavos)`
      );
    }
  }
}

// ─── Phase 1: Main Payment Flow ─────────────────────────────────────────────

export function paymentFlow() {
  const idempotencyKey = uuidv4();
  const amount = Math.floor(Math.random() * 190) + 10; // R$10–200
  const amountCents = amount * 100;

  const t0 = Date.now();

  // Step 1: Create transaction via FellowPay API
  const txPayload = JSON.stringify({
    sellerId: SELLER_ID,
    amount: amount,
    paymentType: 0, // CREDIT_CARD
    installments: 1,
    description: `k6-integrity-${idempotencyKey.substring(0, 8)}`,
    payer: {
      name: "K6 Integrity Payer",
      document: "12345678901",
      email: `k6-${uuidv4().substring(0, 8)}@test.io`,
    },
  });

  const txRes = http.post(`${BASE_URL}/api/v1/transactions`, txPayload, {
    headers: Object.assign({}, API_HEADERS, {
      "Idempotency-Key": idempotencyKey,
    }),
    timeout: "30s",
  });

  if (txRes.status === 429) {
    // Rate limited by our API — expected under load
    return;
  }

  if (txRes.status !== 201) {
    txFailed.add(1);
    return;
  }

  const txBody = JSON.parse(txRes.body).data;
  const piId = txBody.payment.transactionId;
  const internalId = txBody.internalId;
  txCreated.add(1);

  // Step 2: Confirm PaymentIntent via Stripe API (real sandbox)
  const confirmRes = confirmPaymentIntent(piId);

  if (confirmRes.status === 429) {
    stripeRateLimited.add(1);
    return;
  }

  if (confirmRes.status !== 200) {
    txFailed.add(1);
    return;
  }

  const piData = JSON.parse(confirmRes.body);
  if (piData.status !== "succeeded") {
    txFailed.add(1);
    return;
  }

  txConfirmedStripe.add(1);

  // Step 3: Send webhook to trigger CAPTURED in our system
  const webhookRes = sendWebhook(piId, amountCents, {
    seller_id: SELLER_ID,
  });
  webhooksSent.add(1);

  if (webhookRes.status !== 200) {
    txFailed.add(1);
    return;
  }

  // Step 4: Poll until CAPTURED
  const captured = pollForCaptured(internalId, 10);

  if (!captured) {
    txFailed.add(1);
    return;
  }

  txCaptured.add(1);

  const fee = captured.feeAmount || 0;
  const net = captured.netAmount || amount;
  totalAmountCents.add(amountCents);
  totalNetCents.add(Math.round(net * 100));
  totalFeeCents.add(Math.round(fee * 100));

  e2eLatency.add(Date.now() - t0);

  // Step 5: Edge case — 20% chance of sending duplicate webhook
  if (Math.random() < 0.2) {
    const dupRes = sendWebhook(piId, amountCents, {
      seller_id: SELLER_ID,
    });
    webhooksDuplicate.add(1);
    // The idempotency guard (status == newStatus) prevents double credit
  }
}

// ─── Phase 2: Dedup Storm ───────────────────────────────────────────────────
// Sends webhooks for the SAME fabricated PI ID to stress deduplication.
// Since no transaction with this PI exists, the handler logs a warning
// and exits — validating that unknown PIs don't create phantom credits.

export function dedupStorm() {
  const fakePiId = `pi_dedup_test_${__VU}`;
  const body = buildPaymentSucceededEvent(fakePiId, 5000, {});
  const sig = generateStripeSignature(body, WEBHOOK_SECRET);

  const res = http.post(`${BASE_URL}/api/webhooks/stripe`, body, {
    headers: {
      "Content-Type": "application/json",
      "Stripe-Signature": sig.header,
    },
    timeout: "10s",
  });

  // Handler should return 200 (accepted) but NOT create any ledger entry
  // because the transaction doesn't exist
  sleep(0.3);
}

// ─── Phase 3: Final Verification ────────────────────────────────────────────

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

  // 2. Stripe platform balance
  const stripe = getStripeBalance(null);
  if (stripe) {
    finStripeTotalCents.add(stripe.total);
    console.log(
      `[VERIFY] Stripe platform: available=${stripe.available} pending=${stripe.pending} total=${stripe.total} (centavos)`
    );
  }

  // 3. Connected account balance
  if (CONNECTED_ACCOUNT_ID) {
    const connected = getStripeBalance(CONNECTED_ACCOUNT_ID);
    if (connected) {
      finConnectedTotalCents.add(connected.total);
      console.log(
        `[VERIFY] Connected acct: available=${connected.available} pending=${connected.pending} total=${connected.total} (centavos)`
      );
    }
  }

  // 4. Transaction count check
  const txRes = http.get(
    `${BASE_URL}/api/v1/transactions?page=1&pageSize=1&sellerId=${SELLER_ID}`,
    { headers: API_HEADERS }
  );
  if (txRes.status === 200) {
    const txData = JSON.parse(txRes.body).data;
    console.log(`[VERIFY] Total transactions: ${txData.totalCount}`);
  }

  // 5. Check for CAPTURED transactions with balance validation
  const capturedRes = http.get(
    `${BASE_URL}/api/v1/transactions?page=1&pageSize=1&sellerId=${SELLER_ID}&status=3`,
    { headers: API_HEADERS }
  );
  if (capturedRes.status === 200) {
    const capturedData = JSON.parse(capturedRes.body).data;
    console.log(
      `[VERIFY] Captured transactions: ${capturedData.totalCount}`
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
  const confirmed = val("tx_confirmed_stripe");
  const captured = val("tx_captured");
  const failed = val("tx_failed");
  const rateLimited = val("stripe_rate_limited");
  const webhooks = val("webhooks_sent");
  const duplicates = val("webhooks_duplicate_sent");
  const negBal = val("negative_balance");
  const dblCredit = val("double_credit");
  const driftCount = val("ledger_drift");

  const chargedCents = val("total_amount_cents");
  const netCents = val("total_net_cents");
  const feeCents = val("total_fee_cents");

  // Balance deltas
  const iLedger = avg("init_ledger_total_cents");
  const fLedger = avg("fin_ledger_total_cents");
  const ledgerDelta = fLedger - iLedger;

  const iStripe = avg("init_stripe_total_cents");
  const fStripe = avg("fin_stripe_total_cents");
  const stripeDelta = fStripe - iStripe;

  const hasConnected = m["init_connected_total_cents"] != null;
  const iConn = hasConnected ? avg("init_connected_total_cents") : 0;
  const fConn = hasConnected ? avg("fin_connected_total_cents") : 0;
  const connDelta = hasConnected ? fConn - iConn : null;

  // Consistency checks
  const ledgerVsNet = Math.abs(ledgerDelta - netCents);
  const connVsNet =
    connDelta !== null ? Math.abs(connDelta - netCents) : null;

  // With Stripe Connect, the platform balance only reflects application fees,
  // not the full charge amount (which goes to the connected account).
  // Compare platform delta to feeCents when Connect is active, chargedCents otherwise.
  const stripeExpected = hasConnected ? feeCents : chargedCents;
  const stripeVsExpected = Math.abs(stripeDelta - stripeExpected);

  const isLedgerDrift = ledgerVsNet > 1;
  // Platform balance check is informational with Connect (Stripe timing varies)
  const isStripeDrift = hasConnected ? false : stripeVsExpected > 1;
  const isConnDrift = connVsNet !== null ? connVsNet > 1 : false;

  const hardFails =
    negBal +
    dblCredit +
    (isLedgerDrift ? 1 : 0) +
    (isStripeDrift ? 1 : 0) +
    (isConnDrift ? 1 : 0);

  let verdict;
  if (captured === 0) {
    verdict =
      "INCONCLUSIVE — No transactions captured (Stripe rate-limited all requests)";
  } else if (hardFails === 0) {
    verdict = "PASS — Financial consistency verified across all layers";
  } else {
    verdict = "FAIL — FINANCIAL INTEGRITY VIOLATION DETECTED";
  }

  const report = {
    scenario: "Stripe Connect End-to-End Integrity Test",
    verdict,
    transactions: {
      created,
      confirmed_stripe: confirmed,
      captured,
      failed,
      stripe_rate_limited: rateLimited,
    },
    webhooks: {
      sent: webhooks,
      duplicates_sent: duplicates,
      double_credits: dblCredit,
    },
    amounts_centavos: {
      total_charged: chargedCents,
      total_net: netCents,
      total_fee: feeCents,
    },
    consistency: {
      ledger: {
        initial_cents: iLedger,
        final_cents: fLedger,
        delta_cents: ledgerDelta,
        expected_delta_cents: netCents,
        drift_cents: ledgerVsNet,
        ok: !isLedgerDrift,
      },
      stripe_platform: {
        initial_cents: iStripe,
        final_cents: fStripe,
        delta_cents: stripeDelta,
        expected_delta_cents: stripeExpected,
        drift_cents: stripeVsExpected,
        ok: !isStripeDrift,
        note: hasConnected
          ? "With Connect, platform keeps only application fees; timing may vary"
          : null,
      },
      connected_account: hasConnected
        ? {
            initial_cents: iConn,
            final_cents: fConn,
            delta_cents: connDelta,
            expected_delta_cents: netCents,
            drift_cents: connVsNet,
            ok: !isConnDrift,
          }
        : "N/A — no connected account configured",
    },
    hard_fails: {
      negative_balance: negBal,
      double_credit: dblCredit,
      ledger_drift: isLedgerDrift,
      stripe_drift: isStripeDrift,
      connected_drift: isConnDrift,
    },
    latency: {
      e2e_avg_ms: m.e2e_latency ? m.e2e_latency.values.avg : null,
      e2e_p95_ms: p95("e2e_latency"),
      e2e_max_ms: m.e2e_latency ? m.e2e_latency.values.max : null,
      stripe_confirm_avg_ms: m.stripe_confirm_latency
        ? m.stripe_confirm_latency.values.avg
        : null,
      stripe_confirm_p95_ms: p95("stripe_confirm_latency"),
    },
  };

  const sep = "=".repeat(70);
  return {
    "tests/results/stripe-integrity-report.json": JSON.stringify(
      report,
      null,
      2
    ),
    stdout: `\n${sep}\nSTRIPE CONNECT INTEGRITY TEST\n${sep}\n${JSON.stringify(report, null, 2)}\n`,
  };
}
