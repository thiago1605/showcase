// =============================================================================
// SCENARIO 1: Mixed Concurrency Storm (CRITICAL)
// =============================================================================
// Simultaneously execute transaction creation (credits), payout requests
// (debits), and balance checks against the SAME seller.
// 200-500 VUs, no artificial delays, maximum resource collision.
// =============================================================================

import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Rate, Trend } from "k6/metrics";
import { randomString } from "https://jslib.k6.io/k6-utils/1.4.0/index.js";
import { uuidv4 } from "https://jslib.k6.io/k6-utils/1.4.0/index.js";

// --- Config ---
const BASE_URL = __ENV.BASE_URL || "http://localhost:5195";
const API_KEY =
  __ENV.API_KEY || "MISSING_API_KEY";
const SELLER_ID = __ENV.SELLER_ID || "";

const HEADERS = {
  "Content-Type": "application/json",
  Accept: "application/json",
  "X-Api-Key": API_KEY,
};

// --- Custom Metrics ---
const txCreated = new Counter("transactions_created");
const txFailed = new Counter("transactions_failed");
const payoutSuccess = new Counter("payouts_succeeded");
const payoutFailed = new Counter("payouts_failed");
const payoutInsufficientBalance = new Counter("payouts_insufficient_balance");
const balanceChecks = new Counter("balance_checks");
const negativeBalanceDetected = new Counter("negative_balance_detected");
const errorRate = new Rate("error_rate");
const txLatency = new Trend("transaction_latency", true);
const payoutLatency = new Trend("payout_latency", true);

// --- Scenarios ---
export const options = {
  scenarios: {
    // Transaction creators (credits)
    transaction_creators: {
      executor: "ramping-vus",
      startVUs: 10,
      stages: [
        { duration: "10s", target: 100 },
        { duration: "60s", target: 200 },
        { duration: "10s", target: 0 },
      ],
      exec: "createTransaction",
    },
    // Payout requesters (debits) — attack the same balance
    payout_attackers: {
      executor: "ramping-vus",
      startVUs: 5,
      stages: [
        { duration: "10s", target: 50 },
        { duration: "60s", target: 100 },
        { duration: "10s", target: 0 },
      ],
      exec: "requestPayout",
    },
    // Balance monitors — continuously verify financial integrity
    balance_monitors: {
      executor: "constant-vus",
      vus: 10,
      duration: "80s",
      exec: "checkBalance",
    },
  },
  thresholds: {
    error_rate: ["rate<0.50"], // Some errors expected (insufficient balance, etc.)
    negative_balance_detected: ["count==0"], // ZERO tolerance for negative balances
    transaction_latency: ["p(95)<5000"], // p95 under 5s
    payout_latency: ["p(95)<5000"],
  },
};

export function createTransaction() {
  const idempotencyKey = uuidv4();
  const amount = Math.floor(Math.random() * 500) + 10; // 10-510 BRL

  const payload = JSON.stringify({
    sellerId: SELLER_ID,
    amount: amount,
    paymentType: 0,
    installments: 1,
    description: `k6-storm-${idempotencyKey.substring(0, 8)}`,
    payer: {
      name: "K6 Stress Payer",
      document: "12345678901",
      email: `k6-${randomString(6)}@test.com`,
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

  const ok = check(res, {
    "tx created (2xx)": (r) => r.status >= 200 && r.status < 300,
  });

  if (ok) {
    txCreated.add(1);
    errorRate.add(0);
  } else {
    txFailed.add(1);
    errorRate.add(1);
    if (res.status !== 429) {
      // Don't log rate-limit errors
      console.warn(
        `TX FAILED: status=${res.status} body=${res.body?.substring(0, 200)}`
      );
    }
  }
}

export function requestPayout() {
  const idempotencyKey = uuidv4();
  const amount = Math.floor(Math.random() * 100) + 5; // 5-105 BRL

  const payload = JSON.stringify({
    sellerId: SELLER_ID,
    amount: amount,
  });

  const headers = Object.assign({}, HEADERS, {
    "Idempotency-Key": idempotencyKey,
  });

  const start = Date.now();
  const res = http.post(`${BASE_URL}/api/v1/payouts`, payload, {
    headers: headers,
    timeout: "30s",
  });
  payoutLatency.add(Date.now() - start);

  if (res.status >= 200 && res.status < 300) {
    payoutSuccess.add(1);
    errorRate.add(0);
  } else if (res.status === 422 || res.status === 400) {
    // Business errors: insufficient balance, etc.
    payoutInsufficientBalance.add(1);
    errorRate.add(0); // Expected failures
  } else {
    payoutFailed.add(1);
    errorRate.add(1);
    if (res.status !== 429) {
      console.warn(
        `PAYOUT FAILED: status=${res.status} body=${res.body?.substring(0, 200)}`
      );
    }
  }
}

export function checkBalance() {
  const res = http.get(
    `${BASE_URL}/api/v1/sellers/${SELLER_ID}/balance`,
    { headers: HEADERS }
  );

  balanceChecks.add(1);

  if (res.status === 200) {
    const balance = JSON.parse(res.body).data;

    check(balance, {
      "available >= 0": (b) => b.available >= 0,
      "total >= 0": (b) => b.total >= 0,
    });

    if (balance.available < 0 || balance.total < 0) {
      negativeBalanceDetected.add(1);
      console.error(
        `NEGATIVE BALANCE DETECTED: available=${balance.available} total=${balance.total} blocked=${balance.blocked}`
      );
    }
  }

  sleep(0.5); // Check every 500ms
}

export function handleSummary(data) {
  const negBal =
    data.metrics.negative_balance_detected
      ? data.metrics.negative_balance_detected.values.count
      : 0;
  const txCount = data.metrics.transactions_created
    ? data.metrics.transactions_created.values.count
    : 0;
  const payoutOk = data.metrics.payouts_succeeded
    ? data.metrics.payouts_succeeded.values.count
    : 0;
  const payoutInsuf = data.metrics.payouts_insufficient_balance
    ? data.metrics.payouts_insufficient_balance.values.count
    : 0;

  const verdict = negBal === 0 ? "PASS" : "FAIL — NEGATIVE BALANCE DETECTED";

  const report = {
    scenario: "Mixed Concurrency Storm",
    verdict: verdict,
    transactions_created: txCount,
    payouts_succeeded: payoutOk,
    payouts_rejected_insufficient: payoutInsuf,
    negative_balances: negBal,
    p95_tx_latency_ms: data.metrics.transaction_latency
      ? data.metrics.transaction_latency.values["p(95)"]
      : null,
    p95_payout_latency_ms: data.metrics.payout_latency
      ? data.metrics.payout_latency.values["p(95)"]
      : null,
  };

  return {
    "tests/k6-stress/results/01-storm-result.json": JSON.stringify(
      report,
      null,
      2
    ),
    stdout: `\n${"=".repeat(60)}\nMIXED CONCURRENCY STORM RESULT\n${"=".repeat(60)}\n${JSON.stringify(report, null, 2)}\n`,
  };
}
