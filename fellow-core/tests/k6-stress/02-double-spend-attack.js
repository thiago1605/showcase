// =============================================================================
// SCENARIO 2: Double-Spend Attack Simulation
// =============================================================================
// Multiple concurrent payout requests against the same seller balance.
// All VUs fire at once — maximum contention on the same row.
// Expected: only valid payouts succeed, no negative balance possible.
// =============================================================================

import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Rate, Trend } from "k6/metrics";
import { uuidv4 } from "https://jslib.k6.io/k6-utils/1.4.0/index.js";

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
const payoutAttempted = new Counter("payout_attempted");
const payoutAccepted = new Counter("payout_accepted");
const payoutRejected = new Counter("payout_rejected");
const negativeBalance = new Counter("negative_balance_detected");
const totalWithdrawn = new Counter("total_withdrawn_amount");
const doubleSpendDetected = new Counter("double_spend_detected");
const attackLatency = new Trend("attack_latency", true);

export const options = {
  scenarios: {
    // Phase 1: Seed the wallet with known balance
    seed_balance: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "seedBalance",
      maxDuration: "30s",
    },
    // Phase 2: All VUs fire payout requests simultaneously
    double_spend_attack: {
      executor: "shared-iterations",
      vus: 50,
      iterations: 50, // 50 concurrent payouts for the same balance
      exec: "attemptDoubleSpend",
      startTime: "5s", // Wait for seed
      maxDuration: "60s",
    },
    // Phase 3: Verify final state
    verify_integrity: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "verifyIntegrity",
      startTime: "35s",
      maxDuration: "30s",
    },
  },
  thresholds: {
    negative_balance_detected: ["count==0"],
    double_spend_detected: ["count==0"],
  },
};

export function seedBalance() {
  // Create a transaction to ensure seller has funds
  // The seeder already gives the seller some balance.
  // Just verify current balance.
  const res = http.get(
    `${BASE_URL}/api/v1/sellers/${SELLER_ID}/balance`,
    { headers: HEADERS }
  );

  if (res.status === 200) {
    const balance = JSON.parse(res.body).data;
    console.log(
      `[SEED] Initial balance: available=${balance.available} total=${balance.total}`
    );
  } else {
    console.error(`[SEED] Failed to get balance: ${res.status} ${res.body}`);
  }
}

export function attemptDoubleSpend() {
  const idempotencyKey = uuidv4();
  // Each VU tries to withdraw a significant amount — more than what
  // the seller can sustain if all succeed
  const amount = 100; // If balance is e.g. 500, only 5 of 50 should succeed

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
  attackLatency.add(Date.now() - start);

  if (res.status >= 200 && res.status < 300) {
    payoutAccepted.add(1);
    totalWithdrawn.add(amount);
    console.log(
      `[ATTACK] Payout ACCEPTED: ${amount} BRL (VU=${__VU} iter=${__ITER})`
    );
  } else {
    payoutRejected.add(1);

    if (res.status !== 429) {
      const body = res.body ? res.body.substring(0, 200) : "";
      if (
        body.includes("insuficiente") ||
        body.includes("Insufficient") ||
        body.includes("Saldo")
      ) {
        // Expected: insufficient balance
      } else {
        console.warn(
          `[ATTACK] Unexpected rejection: status=${res.status} body=${body}`
        );
      }
    }
  }
}

export function verifyIntegrity() {
  sleep(2); // Let all pending operations settle

  const res = http.get(
    `${BASE_URL}/api/v1/sellers/${SELLER_ID}/balance`,
    { headers: HEADERS }
  );

  if (res.status !== 200) {
    console.error(
      `[VERIFY] Balance check failed: ${res.status} ${res.body}`
    );
    return;
  }

  const balance = JSON.parse(res.body).data;
  console.log(
    `[VERIFY] Final balance: available=${balance.available} total=${balance.total} blocked=${balance.blocked}`
  );

  if (balance.available < 0) {
    negativeBalance.add(1);
    doubleSpendDetected.add(1);
    console.error(
      `DOUBLE-SPEND DETECTED: available=${balance.available} — SYSTEM NOT SAFE`
    );
  } else {
    console.log(`[VERIFY] No double-spend detected. Balance is non-negative.`);
  }

  if (balance.total < 0) {
    negativeBalance.add(1);
    console.error(`NEGATIVE TOTAL BALANCE: total=${balance.total}`);
  }
}

export function handleSummary(data) {
  const accepted = data.metrics.payout_accepted
    ? data.metrics.payout_accepted.values.count
    : 0;
  const rejected = data.metrics.payout_rejected
    ? data.metrics.payout_rejected.values.count
    : 0;
  const negBal = data.metrics.negative_balance_detected
    ? data.metrics.negative_balance_detected.values.count
    : 0;
  const dblSpend = data.metrics.double_spend_detected
    ? data.metrics.double_spend_detected.values.count
    : 0;
  const withdrawn = data.metrics.total_withdrawn_amount
    ? data.metrics.total_withdrawn_amount.values.count
    : 0;

  const verdict =
    negBal === 0 && dblSpend === 0
      ? "PASS — No double-spend"
      : "FAIL — DOUBLE-SPEND DETECTED";

  const report = {
    scenario: "Double-Spend Attack Simulation",
    verdict: verdict,
    payout_attempts: accepted + rejected,
    payout_accepted: accepted,
    payout_rejected: rejected,
    total_withdrawn: withdrawn,
    negative_balance: negBal,
    double_spend: dblSpend,
    p95_latency_ms: data.metrics.attack_latency
      ? data.metrics.attack_latency.values["p(95)"]
      : null,
  };

  return {
    "tests/k6-stress/results/02-double-spend-result.json": JSON.stringify(
      report,
      null,
      2
    ),
    stdout: `\n${"=".repeat(60)}\nDOUBLE-SPEND ATTACK RESULT\n${"=".repeat(60)}\n${JSON.stringify(report, null, 2)}\n`,
  };
}
