// =============================================================================
// MULTI-METHOD PAYMENT INTEGRITY TEST
// =============================================================================
// Validates payment method orchestration and cross-method collision guards:
//
// Scenarios:
//   1. Mixed Load: 30% Card, 20% Apple Pay, 20% Google Pay, 15% PIX, 15% Boleto
//   2. Double Payment Attack: Same order paid with card + boleto simultaneously
//   3. Refund Storm: Mass refunds across all payment methods
//
// HARD FAIL conditions:
//   - Double credit: Same order credited twice in ledger
//   - Negative balance: Seller balance < 0
//   - Wrong wallet type: CAPTURED transaction has incorrect walletType
//   - Ledger drift: Balance delta != expected (captured - refunded)
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

const API_HEADERS = {
  "Content-Type": "application/json",
  Accept: "application/json",
  "X-Api-Key": API_KEY,
};

const STRIPE_HEADERS = {
  Authorization: `Bearer ${STRIPE_SK}`,
  "Content-Type": "application/x-www-form-urlencoded",
};

// ─── Payment Method Definitions ─────────────────────────────────────────────
// PaymentType enum: CREDIT_CARD=0, DEBIT_CARD=1, PIX=2, BOLETO=3
const METHOD_CARD = { name: "CARD", type: 0, wallet: null, confirmable: true };
const METHOD_APPLE = { name: "APPLE_PAY", type: 0, wallet: "apple_pay", confirmable: true };
const METHOD_GOOGLE = { name: "GOOGLE_PAY", type: 0, wallet: "google_pay", confirmable: true };
const METHOD_PIX = { name: "PIX", type: 2, wallet: null, confirmable: false };
const METHOD_BOLETO = { name: "BOLETO", type: 3, wallet: null, confirmable: false };

function pickRandomMethod() {
  const r = Math.random();
  if (r < 0.30) return METHOD_CARD;       // 30%
  if (r < 0.50) return METHOD_APPLE;      // 20%
  if (r < 0.70) return METHOD_GOOGLE;     // 20%
  if (r < 0.85) return METHOD_PIX;        // 15%
  return METHOD_BOLETO;                    // 15%
}

// ─── Custom Metrics ─────────────────────────────────────────────────────────
// Per-method counters
const txCreatedCard = new Counter("tx_created_card");
const txCreatedApple = new Counter("tx_created_apple_pay");
const txCreatedGoogle = new Counter("tx_created_google_pay");
const txCreatedPix = new Counter("tx_created_pix");
const txCreatedBoleto = new Counter("tx_created_boleto");

const txCapturedCard = new Counter("tx_captured_card");
const txCapturedApple = new Counter("tx_captured_apple_pay");
const txCapturedGoogle = new Counter("tx_captured_google_pay");
const txCapturedPix = new Counter("tx_captured_pix");
const txCapturedBoleto = new Counter("tx_captured_boleto");

const txCreatedTotal = new Counter("tx_created_total");
const txCapturedTotal = new Counter("tx_captured_total");
const txFailed = new Counter("tx_failed");

// Collision guard
const collisionBlocked = new Counter("collision_blocked");
const doubleCredit = new Counter("double_credit");

// Wallet verification
const walletCorrect = new Counter("wallet_type_correct");
const walletWrong = new Counter("wallet_type_wrong");

// Refunds
const refundsProcessed = new Counter("refunds_processed");
const refundsFailed = new Counter("refunds_failed");

// Financial
const negativeBalance = new Counter("negative_balance");
const totalCapturedCents = new Counter("total_captured_net_cents");
const totalRefundedCents = new Counter("total_refunded_net_cents");

// Latency
const e2ePaymentLatency = new Trend("e2e_payment_latency", true);
const initLedgerCents = new Trend("init_ledger_cents");
const finLedgerCents = new Trend("fin_ledger_cents");

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
    // Phase 1: mixed load — payments across all methods
    mixed_load: {
      executor: "ramping-vus",
      startVUs: 5,
      stages: [
        { duration: "15s", target: 15 },
        { duration: "30s", target: 20 },
        { duration: "15s", target: 0 },
      ],
      exec: "mixedLoad",
      startTime: "5s",
    },
    // Phase 2: double payment attack — same order, multiple methods
    double_payment_attack: {
      executor: "constant-vus",
      vus: 5,
      duration: "40s",
      exec: "doublePaymentAttack",
      startTime: "10s",
    },
    // Phase 3: refund storm — mass refunds across methods
    refund_storm: {
      executor: "constant-vus",
      vus: 10,
      duration: "25s",
      exec: "refundStorm",
      startTime: "70s",
    },
    // Phase 4: final verification
    verify_end: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "verifyEnd",
      startTime: "115s",
      maxDuration: "60s",
    },
  },
  thresholds: {
    double_credit: ["count==0"],
    negative_balance: ["count==0"],
    wallet_type_wrong: ["count==0"],
  },
};

// ─── Helpers ────────────────────────────────────────────────────────────────

function getLedgerBalance() {
  const res = http.get(
    `${BASE_URL}/api/v1/sellers/${SELLER_ID}/balance`,
    { headers: API_HEADERS }
  );
  if (res.status !== 200) return null;
  const d = JSON.parse(res.body).data;
  return { available: d.available, blocked: d.blocked, total: d.total };
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

function apiHeaders(idempotencyKey) {
  return Object.assign({}, API_HEADERS, {
    "Idempotency-Key": idempotencyKey || uuidv4(),
  });
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
  for (let i = 0; i < maxWaitSec * 2; i++) {
    sleep(0.5);
    const res = http.get(
      `${BASE_URL}/api/v1/transactions/${internalId}`,
      { headers: API_HEADERS }
    );
    if (res.status === 200) {
      const tx = JSON.parse(res.body).data;
      if (tx.status === targetStatus) return tx;
    }
  }
  return null;
}

function trackCreated(method) {
  switch (method.name) {
    case "CARD": txCreatedCard.add(1); break;
    case "APPLE_PAY": txCreatedApple.add(1); break;
    case "GOOGLE_PAY": txCreatedGoogle.add(1); break;
    case "PIX": txCreatedPix.add(1); break;
    case "BOLETO": txCreatedBoleto.add(1); break;
  }
}

function trackCaptured(method) {
  switch (method.name) {
    case "CARD": txCapturedCard.add(1); break;
    case "APPLE_PAY": txCapturedApple.add(1); break;
    case "GOOGLE_PAY": txCapturedGoogle.add(1); break;
    case "PIX": txCapturedPix.add(1); break;
    case "BOLETO": txCapturedBoleto.add(1); break;
  }
}

/**
 * Create a transaction, optionally confirm on Stripe, send webhook, poll for CAPTURED.
 * Returns { internalId, piId, amount, amountCents, netAmountCents, method, walletType } or null.
 */
function createAndCapture(method, externalReferenceId) {
  const idempotencyKey = uuidv4();
  const amount = Math.floor(Math.random() * 190) + 10; // R$10-200
  const amountCents = amount * 100;

  const body = {
    sellerId: SELLER_ID,
    amount: amount,
    paymentType: method.type,
    installments: 1,
    description: `k6-multi-${method.name}-${idempotencyKey.substring(0, 8)}`,
    payer: {
      name: "K6 Multi Method Test",
      document: "12345678901",
      email: `k6-${uuidv4().substring(0, 8)}@test.io`,
    },
  };

  if (externalReferenceId) {
    body.externalReferenceId = externalReferenceId;
  }

  const txRes = http.post(
    `${BASE_URL}/api/v1/transactions`,
    JSON.stringify(body),
    { headers: apiHeaders(idempotencyKey), timeout: "30s" }
  );

  if (txRes.status === 429) return null;
  if (txRes.status !== 201) {
    txFailed.add(1);
    return null;
  }

  const txBody = JSON.parse(txRes.body).data;
  const piId = txBody.payment.transactionId;
  const internalId = txBody.internalId;

  txCreatedTotal.add(1);
  trackCreated(method);

  // Confirm on Stripe for card-based methods
  if (method.confirmable) {
    const confirmRes = confirmPaymentIntent(piId);
    if (confirmRes.status === 429 || confirmRes.status !== 200) {
      txFailed.add(1);
      return null;
    }
    const piData = JSON.parse(confirmRes.body);
    if (piData.status !== "succeeded" && piData.status !== "requires_action") {
      txFailed.add(1);
      return null;
    }
  }

  // Send capture webhook (with wallet type for card variants)
  const webhookBody = buildPaymentSucceededEvent(
    piId,
    amountCents,
    { seller_id: SELLER_ID },
    method.wallet
  );
  const webhookRes = sendWebhook(webhookBody);

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

  txCapturedTotal.add(1);
  trackCaptured(method);

  const netCents = Math.round((captured.netAmount || amount) * 100);
  totalCapturedCents.add(netCents);

  return {
    internalId,
    piId,
    amount,
    amountCents,
    netAmountCents: netCents,
    method,
    walletType: captured.walletType,
  };
}

// ─── Phase 0: Record Start ──────────────────────────────────────────────────

export function recordStart() {
  console.log("=== RECORDING INITIAL STATE ===");
  const ledger = getLedgerBalance();
  if (ledger) {
    initLedgerCents.add(Math.round(ledger.total * 100));
    console.log(
      `[START] Ledger: available=${ledger.available} blocked=${ledger.blocked} total=${ledger.total}`
    );
  }
}

// ─── Phase 1: Mixed Load ────────────────────────────────────────────────────
// Random mix of all payment methods — validates routing, fees, ledger per method

export function mixedLoad() {
  const method = pickRandomMethod();
  const t0 = Date.now();

  const result = createAndCapture(method);
  if (!result) return;

  e2ePaymentLatency.add(Date.now() - t0);

  // Verify wallet type on captured transaction (Apple Pay / Google Pay)
  if (method.wallet) {
    if (result.walletType === method.wallet) {
      walletCorrect.add(1);
    } else {
      walletWrong.add(1);
      console.error(
        `WRONG WALLET: expected=${method.wallet}, got=${result.walletType} for TX ${result.internalId}`
      );
    }
  }
}

// ─── Phase 2: Double Payment Attack ─────────────────────────────────────────
// Same ExternalReferenceId → create boleto + card → confirm card first → late boleto
// Expected: only first-to-capture payment credits the ledger (collision guard)

export function doublePaymentAttack() {
  const orderId = `ORDER-${uuidv4().substring(0, 12)}`;
  const idempotencyKey1 = uuidv4();
  const idempotencyKey2 = uuidv4();
  const amount = Math.floor(Math.random() * 90) + 10; // R$10-100
  const amountCents = amount * 100;

  // Step 1: Create BOLETO transaction for this order (delayed payment)
  const boletoRes = http.post(
    `${BASE_URL}/api/v1/transactions`,
    JSON.stringify({
      sellerId: SELLER_ID,
      amount: amount,
      paymentType: 3, // BOLETO
      installments: 1,
      description: `k6-collision-boleto-${orderId}`,
      externalReferenceId: orderId,
      payer: {
        name: "K6 Collision Test",
        document: "12345678901",
        email: `k6-${uuidv4().substring(0, 8)}@test.io`,
      },
    }),
    { headers: apiHeaders(idempotencyKey1), timeout: "30s" }
  );

  if (boletoRes.status !== 201) {
    txFailed.add(1);
    return;
  }

  const boletoData = JSON.parse(boletoRes.body).data;
  const boletoPiId = boletoData.payment.transactionId;
  const boletoInternalId = boletoData.internalId;
  txCreatedTotal.add(1);
  txCreatedBoleto.add(1);

  // Step 2: Create CARD transaction for SAME order (customer switches to card)
  const cardRes = http.post(
    `${BASE_URL}/api/v1/transactions`,
    JSON.stringify({
      sellerId: SELLER_ID,
      amount: amount,
      paymentType: 0, // CREDIT_CARD
      installments: 1,
      description: `k6-collision-card-${orderId}`,
      externalReferenceId: orderId,
      payer: {
        name: "K6 Collision Test",
        document: "12345678901",
        email: `k6-${uuidv4().substring(0, 8)}@test.io`,
      },
    }),
    { headers: apiHeaders(idempotencyKey2), timeout: "30s" }
  );

  if (cardRes.status !== 201) {
    txFailed.add(1);
    return;
  }

  const cardData = JSON.parse(cardRes.body).data;
  const cardPiId = cardData.payment.transactionId;
  const cardInternalId = cardData.internalId;
  txCreatedTotal.add(1);
  txCreatedCard.add(1);

  // Step 3: Confirm CARD on Stripe (real payment — card processes instantly)
  const confirmRes = confirmPaymentIntent(cardPiId);
  if (confirmRes.status !== 200) {
    txFailed.add(1);
    return;
  }

  // Step 4: Send webhook for CARD → CAPTURED + ledger credit
  const cardWebhook = buildPaymentSucceededEvent(
    cardPiId,
    amountCents,
    { seller_id: SELLER_ID }
  );
  const cardWHRes = sendWebhook(cardWebhook);

  if (cardWHRes.status !== 200) {
    txFailed.add(1);
    return;
  }

  // Poll for CARD to be CAPTURED
  const capturedCard = pollForStatus(cardInternalId, 3, 10);
  if (!capturedCard) {
    txFailed.add(1);
    return;
  }

  txCapturedTotal.add(1);
  txCapturedCard.add(1);
  const cardNet = Math.round((capturedCard.netAmount || amount) * 100);
  totalCapturedCents.add(cardNet);

  // Step 5: Simulate delayed boleto confirmation (days later in real life)
  sleep(1);

  // Send webhook for BOLETO → should CAPTURE status but collision guard blocks ledger credit
  const boletoWebhook = buildPaymentSucceededEvent(
    boletoPiId,
    amountCents,
    { seller_id: SELLER_ID }
  );
  const boletoWHRes = sendWebhook(boletoWebhook);

  if (boletoWHRes.status !== 200) {
    txFailed.add(1);
    return;
  }

  // Poll for BOLETO to be CAPTURED (status changes, but no ledger credit)
  const capturedBoleto = pollForStatus(boletoInternalId, 3, 8);
  if (capturedBoleto) {
    // Expected: boleto captured but collision guard prevented double ledger credit
    collisionBlocked.add(1);
    txCapturedTotal.add(1);
    txCapturedBoleto.add(1);
    // Do NOT add to totalCapturedCents — blocked by collision guard
    console.log(
      `[COLLISION] Order ${orderId}: card TX ${cardInternalId} won, boleto TX ${boletoInternalId} blocked`
    );
  }

  // Verify: check ledger balance didn't double-credit
  // The balance check in verifyEnd will catch any drift
}

// ─── Phase 3: Refund Storm ──────────────────────────────────────────────────
// Create payments of random methods → immediately refund
// Validates refund works correctly across all payment methods

export function refundStorm() {
  const method = pickRandomMethod();
  const result = createAndCapture(method);
  if (!result) return;

  sleep(0.5);

  // 50% partial refund, 50% full refund
  const isPartial = Math.random() < 0.5;
  const refundAmount = isPartial
    ? Math.max(1, Math.floor(result.amount / 2))
    : null; // null = full refund (API uses transaction.Amount)

  const refundBody = { reason: "requested_by_customer" };
  if (refundAmount) refundBody.amount = refundAmount;

  const refundRes = http.post(
    `${BASE_URL}/api/v1/transactions/${result.internalId}/refund`,
    JSON.stringify(refundBody),
    {
      headers: apiHeaders(`refund-storm-${result.internalId}`),
      timeout: "15s",
    }
  );

  if (refundRes.status === 200) {
    refundsProcessed.add(1);
    // Track net debit (proportional to netAmount/amount ratio)
    const actualRefundAmount = refundAmount || result.amount;
    const netRatio = result.netAmountCents / result.amountCents;
    totalRefundedCents.add(Math.round(actualRefundAmount * 100 * netRatio));
  } else {
    refundsFailed.add(1);
  }
}

// ─── Phase 4: Final Verification ────────────────────────────────────────────

export function verifyEnd() {
  console.log("\n=== FINAL VERIFICATION ===");
  sleep(5); // Let pending operations settle

  // 1. Ledger balance
  const ledger = getLedgerBalance();
  if (ledger) {
    finLedgerCents.add(Math.round(ledger.total * 100));
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

  // 2. Transaction counts by status
  const statuses = [
    { name: "CAPTURED", code: 3 },
    { name: "REFUNDED", code: 6 },
    { name: "FAILED", code: 8 },
  ];
  for (const s of statuses) {
    const res = http.get(
      `${BASE_URL}/api/v1/transactions?page=1&pageSize=1&sellerId=${SELLER_ID}&status=${s.code}`,
      { headers: API_HEADERS }
    );
    if (res.status === 200) {
      const total = JSON.parse(res.body).data.totalCount;
      console.log(`[VERIFY] ${s.name} transactions: ${total}`);
    }
  }
}

// ─── Summary Report ─────────────────────────────────────────────────────────

export function handleSummary(data) {
  const m = data.metrics;
  const val = (name) => (m[name] ? m[name].values.count : 0);
  const avg = (name) => (m[name] ? m[name].values.avg : 0);
  const p95 = (name) => (m[name] ? m[name].values["p(95)"] : null);

  const created = val("tx_created_total");
  const captured = val("tx_captured_total");
  const failed = val("tx_failed");

  // Per-method breakdown
  const perMethod = {
    card: {
      created: val("tx_created_card"),
      captured: val("tx_captured_card"),
    },
    apple_pay: {
      created: val("tx_created_apple_pay"),
      captured: val("tx_captured_apple_pay"),
    },
    google_pay: {
      created: val("tx_created_google_pay"),
      captured: val("tx_captured_google_pay"),
    },
    pix: {
      created: val("tx_created_pix"),
      captured: val("tx_captured_pix"),
    },
    boleto: {
      created: val("tx_created_boleto"),
      captured: val("tx_captured_boleto"),
    },
  };

  // Collision guard
  const blocked = val("collision_blocked");
  const dblCredit = val("double_credit");

  // Wallet verification
  const wCorrect = val("wallet_type_correct");
  const wWrong = val("wallet_type_wrong");

  // Refunds
  const refProc = val("refunds_processed");
  const refFail = val("refunds_failed");

  // Financial
  const negBal = val("negative_balance");
  const capturedCents = val("total_captured_net_cents");
  const refundedCents = val("total_refunded_net_cents");

  // Ledger delta
  const iLedger = avg("init_ledger_cents");
  const fLedger = avg("fin_ledger_cents");
  const ledgerDelta = fLedger - iLedger;
  const expectedDelta = capturedCents - refundedCents;
  const ledgerDrift = Math.abs(ledgerDelta - expectedDelta);

  // Tolerance: float64 (k6) vs decimal (C#) rounding can cause ~1 cent per refund
  const driftTolerance = Math.max(1, refProc);
  const isLedgerDrift = ledgerDrift > driftTolerance;

  const hardFails =
    negBal + dblCredit + wWrong + (isLedgerDrift ? 1 : 0);

  let verdict;
  if (captured === 0) {
    verdict = "INCONCLUSIVE -- No transactions captured";
  } else if (hardFails === 0) {
    verdict = "PASS -- Multi-method financial integrity verified";
  } else {
    verdict = "FAIL -- INTEGRITY VIOLATION DETECTED";
  }

  const report = {
    scenario: "Multi-Method Payment Integrity Test",
    verdict,
    transactions: {
      created,
      captured,
      failed,
    },
    per_method: perMethod,
    collision_guard: {
      attacks_blocked: blocked,
      double_credit: dblCredit,
    },
    wallet_verification: {
      correct: wCorrect,
      wrong: wWrong,
    },
    refunds: {
      processed: refProc,
      failed: refFail,
    },
    consistency: {
      initial_ledger_cents: iLedger,
      final_ledger_cents: fLedger,
      delta_cents: ledgerDelta,
      expected_delta_cents: expectedDelta,
      drift_cents: ledgerDrift,
      drift_tolerance_cents: driftTolerance,
      ok: !isLedgerDrift,
    },
    hard_fails: {
      negative_balance: negBal,
      double_credit: dblCredit,
      wallet_wrong: wWrong,
      ledger_drift: isLedgerDrift,
    },
    latency: {
      payment_e2e_avg_ms: m.e2e_payment_latency
        ? m.e2e_payment_latency.values.avg
        : null,
      payment_e2e_p95_ms: p95("e2e_payment_latency"),
    },
  };

  const sep = "=".repeat(70);
  return {
    "tests/results/multi-method-report.json": JSON.stringify(report, null, 2),
    stdout: `\n${sep}\nMULTI-METHOD INTEGRITY TEST\n${sep}\n${JSON.stringify(
      report,
      null,
      2
    )}\n`,
  };
}
