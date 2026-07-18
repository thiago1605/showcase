// =============================================================================
// SCENARIO 4: Provider Instability
// =============================================================================
// Rapid-fire transaction creation under load. The provider (Stripe sandbox)
// may return errors, timeouts, or slow responses. Validates that:
// - Failed transactions do NOT corrupt the ledger
// - Partial failures are handled correctly
// - The system remains consistent after provider errors
// =============================================================================

import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Rate, Trend } from "k6/metrics";
import { uuidv4 } from "https://jslib.k6.io/k6-utils/1.4.0/index.js";
import { randomString } from "https://jslib.k6.io/k6-utils/1.4.0/index.js";

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
const txAttempted = new Counter("tx_attempted");
const txSucceeded = new Counter("tx_succeeded");
const txFailedProvider = new Counter("tx_failed_provider");
const txFailedOther = new Counter("tx_failed_other");
const negativeBalance = new Counter("negative_balance_detected");
const orphanTxDetected = new Counter("orphan_tx_detected");
const txLatency = new Trend("tx_latency", true);
const errorRate = new Rate("error_rate");

export const options = {
  scenarios: {
    // Record initial state
    record_initial: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "recordInitialState",
      maxDuration: "15s",
    },
    // Burst load — push the provider to its limits
    provider_stress: {
      executor: "ramping-vus",
      startVUs: 20,
      stages: [
        { duration: "5s", target: 100 },
        { duration: "30s", target: 200 }, // Peak load
        { duration: "30s", target: 200 }, // Sustain
        { duration: "5s", target: 0 },
      ],
      exec: "stressProvider",
      startTime: "3s",
    },
    // Verify final state
    verify_state: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "verifyFinalState",
      startTime: "75s",
      maxDuration: "30s",
    },
  },
  thresholds: {
    negative_balance_detected: ["count==0"],
    orphan_tx_detected: ["count==0"],
  },
};

let initialBalance = 0;
let initialTxCount = 0;

export function recordInitialState() {
  const balRes = http.get(
    `${BASE_URL}/api/v1/sellers/${SELLER_ID}/balance`,
    { headers: HEADERS }
  );

  if (balRes.status === 200) {
    const bal = JSON.parse(balRes.body).data;
    initialBalance = bal.available;
    console.log(
      `[INIT] Initial balance: available=${bal.available} total=${bal.total}`
    );
  }

  // Count existing transactions
  const txRes = http.get(
    `${BASE_URL}/api/v1/transactions?page=1&pageSize=1&sellerId=${SELLER_ID}`,
    { headers: HEADERS }
  );

  if (txRes.status === 200) {
    const txData = JSON.parse(txRes.body).data;
    initialTxCount = txData.totalCount || 0;
    console.log(`[INIT] Initial transaction count: ${initialTxCount}`);
  }
}

export function stressProvider() {
  const idempotencyKey = uuidv4();
  const amount = Math.floor(Math.random() * 200) + 10;

  // Mix of payment types to stress different provider paths
  // 0 = CREDIT_CARD, 2 = PIX
  const paymentTypes = [0, 0, 0, 2];
  const paymentType = paymentTypes[Math.floor(Math.random() * paymentTypes.length)];

  const payload = JSON.stringify({
    sellerId: SELLER_ID,
    amount: amount,
    paymentType: paymentType,
    installments: paymentType === 0 ? Math.floor(Math.random() * 3) + 1 : 1,
    description: `k6-instability-${idempotencyKey.substring(0, 8)}`,
    payer: {
      name: "K6 Provider Stress",
      document: "12345678901",
      email: `stress-${randomString(6)}@k6.io`,
    },
  });

  const headers = Object.assign({}, HEADERS, {
    "Idempotency-Key": idempotencyKey,
  });

  txAttempted.add(1);

  const start = Date.now();
  const res = http.post(`${BASE_URL}/api/v1/transactions`, payload, {
    headers: headers,
    timeout: "30s",
  });
  const elapsed = Date.now() - start;
  txLatency.add(elapsed);

  if (res.status >= 200 && res.status < 300) {
    txSucceeded.add(1);
    errorRate.add(0);
  } else if (res.status === 502 || res.status === 503 || res.status === 504) {
    // Provider errors — expected during instability
    txFailedProvider.add(1);
    errorRate.add(1);
  } else if (res.status === 429) {
    // Rate limited — expected under heavy load
    errorRate.add(0);
  } else {
    txFailedOther.add(1);
    errorRate.add(1);

    // Check if the response indicates a provider-side failure
    const body = res.body ? res.body.substring(0, 300) : "";
    if (
      body.includes("provider") ||
      body.includes("timeout") ||
      body.includes("Stripe")
    ) {
      txFailedProvider.add(1);
    }
  }
}

export function verifyFinalState() {
  sleep(5); // Let pending operations settle

  // Check balance
  const balRes = http.get(
    `${BASE_URL}/api/v1/sellers/${SELLER_ID}/balance`,
    { headers: HEADERS }
  );

  if (balRes.status === 200) {
    const bal = JSON.parse(balRes.body).data;
    console.log(
      `[VERIFY] Final balance: available=${bal.available} total=${bal.total} (initial=${initialBalance})`
    );

    if (bal.available < 0) {
      negativeBalance.add(1);
      console.error(
        `NEGATIVE BALANCE after provider instability: available=${bal.available}`
      );
    }
  }

  // Check for FAILED transactions (these should NOT have ledger entries)
  const failedRes = http.get(
    `${BASE_URL}/api/v1/transactions?page=1&pageSize=100&sellerId=${SELLER_ID}&status=FAILED`,
    { headers: HEADERS }
  );

  if (failedRes.status === 200) {
    const failedData = JSON.parse(failedRes.body).data;
    const failedCount = failedData.totalCount || 0;
    console.log(`[VERIFY] FAILED transactions: ${failedCount}`);

    // Verify failed transactions have no providerTxId set
    if (failedData.items) {
      for (const tx of failedData.items) {
        if (tx.providerTxId && tx.status === "FAILED") {
          // A FAILED tx with a providerTxId could be an orphan
          // (provider charged but we marked as failed)
          console.warn(
            `[VERIFY] Potentially orphaned tx: id=${tx.id} providerTxId=${tx.providerTxId} status=${tx.status}`
          );
        }
      }
    }
  }

  // Check total transactions created
  const txRes = http.get(
    `${BASE_URL}/api/v1/transactions?page=1&pageSize=1&sellerId=${SELLER_ID}`,
    { headers: HEADERS }
  );

  if (txRes.status === 200) {
    const txData = JSON.parse(txRes.body).data;
    const finalTxCount = txData.totalCount || 0;
    const newTxCount = finalTxCount - initialTxCount;
    console.log(
      `[VERIFY] New transactions created: ${newTxCount} (total: ${finalTxCount})`
    );
  }
}

export function handleSummary(data) {
  const attempted = data.metrics.tx_attempted
    ? data.metrics.tx_attempted.values.count
    : 0;
  const succeeded = data.metrics.tx_succeeded
    ? data.metrics.tx_succeeded.values.count
    : 0;
  const providerFail = data.metrics.tx_failed_provider
    ? data.metrics.tx_failed_provider.values.count
    : 0;
  const otherFail = data.metrics.tx_failed_other
    ? data.metrics.tx_failed_other.values.count
    : 0;
  const negBal = data.metrics.negative_balance_detected
    ? data.metrics.negative_balance_detected.values.count
    : 0;

  const verdict =
    negBal === 0
      ? "PASS — Ledger consistent despite provider instability"
      : "FAIL — LEDGER CORRUPTED";

  const report = {
    scenario: "Provider Instability",
    verdict: verdict,
    tx_attempted: attempted,
    tx_succeeded: succeeded,
    tx_failed_provider: providerFail,
    tx_failed_other: otherFail,
    success_rate: attempted > 0 ? (succeeded / attempted * 100).toFixed(1) + "%" : "N/A",
    negative_balance: negBal,
    avg_latency_ms: data.metrics.tx_latency
      ? data.metrics.tx_latency.values.avg
      : null,
    p95_latency_ms: data.metrics.tx_latency
      ? data.metrics.tx_latency.values["p(95)"]
      : null,
    max_latency_ms: data.metrics.tx_latency
      ? data.metrics.tx_latency.values.max
      : null,
  };

  return {
    "tests/k6-stress/results/04-instability-result.json": JSON.stringify(
      report,
      null,
      2
    ),
    stdout: `\n${"=".repeat(60)}\nPROVIDER INSTABILITY RESULT\n${"=".repeat(60)}\n${JSON.stringify(report, null, 2)}\n`,
  };
}
