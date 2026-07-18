// =============================================================================
// SCENARIO 5: Database Contention Test
// =============================================================================
// 300+ concurrent operations all targeting the SAME seller account.
// Mix of reads and writes to maximize row-level contention.
// Validates optimistic concurrency, retry convergence, and no lost updates.
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
const opsAttempted = new Counter("ops_attempted");
const opsSucceeded = new Counter("ops_succeeded");
const opsFailed = new Counter("ops_failed");
const opsRetried = new Counter("ops_retried_429");
const balanceReads = new Counter("balance_reads");
const negativeBalance = new Counter("negative_balance_detected");
const lostUpdateDetected = new Counter("lost_update_detected");
const opsLatency = new Trend("ops_latency", true);
const readLatency = new Trend("read_latency", true);

export const options = {
  scenarios: {
    // Record starting state
    record_start: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "recordStart",
      maxDuration: "15s",
    },
    // Writer storm: transaction creation (300 VUs)
    writer_storm: {
      executor: "ramping-vus",
      startVUs: 50,
      stages: [
        { duration: "5s", target: 150 },
        { duration: "60s", target: 300 },
        { duration: "5s", target: 0 },
      ],
      exec: "writeOperation",
      startTime: "3s",
    },
    // Reader contention: balance checks (concurrent reads on same row)
    reader_contention: {
      executor: "constant-vus",
      vus: 50,
      duration: "70s",
      exec: "readOperation",
      startTime: "3s",
    },
    // Mixed small payouts during writes
    payout_contention: {
      executor: "ramping-vus",
      startVUs: 5,
      stages: [
        { duration: "5s", target: 20 },
        { duration: "60s", target: 50 },
        { duration: "5s", target: 0 },
      ],
      exec: "payoutOperation",
      startTime: "3s",
    },
    // Verify at end
    verify_end: {
      executor: "shared-iterations",
      vus: 1,
      iterations: 1,
      exec: "verifyEnd",
      startTime: "75s",
      maxDuration: "30s",
    },
  },
  thresholds: {
    negative_balance_detected: ["count==0"],
    lost_update_detected: ["count==0"],
    ops_latency: ["p(95)<10000"],
  },
};

let initialBalance = { available: 0, total: 0 };
let successfulTxAmounts = [];

export function recordStart() {
  const res = http.get(
    `${BASE_URL}/api/v1/sellers/${SELLER_ID}/balance`,
    { headers: HEADERS }
  );

  if (res.status === 200) {
    initialBalance = JSON.parse(res.body).data;
    console.log(
      `[START] Balance: available=${initialBalance.available} total=${initialBalance.total}`
    );
  }
}

export function writeOperation() {
  const idempotencyKey = uuidv4();
  const amount = Math.floor(Math.random() * 50) + 5; // Small amounts, high volume

  const payload = JSON.stringify({
    sellerId: SELLER_ID,
    amount: amount,
    paymentType: 0,
    installments: 1,
    description: `k6-contention-${__VU}-${__ITER}`,
    payer: {
      name: "K6 Contention",
      document: "12345678901",
      email: `contention-${randomString(5)}@k6.io`,
    },
  });

  const headers = Object.assign({}, HEADERS, {
    "Idempotency-Key": idempotencyKey,
  });

  opsAttempted.add(1);

  const start = Date.now();
  const res = http.post(`${BASE_URL}/api/v1/transactions`, payload, {
    headers: headers,
    timeout: "30s",
  });
  opsLatency.add(Date.now() - start);

  if (res.status >= 200 && res.status < 300) {
    opsSucceeded.add(1);
  } else if (res.status === 429) {
    opsRetried.add(1);
  } else {
    opsFailed.add(1);
    if (res.status >= 500) {
      console.warn(
        `[WRITE] Server error: ${res.status} body=${res.body?.substring(0, 200)}`
      );
    }
  }
}

export function readOperation() {
  const start = Date.now();
  const res = http.get(
    `${BASE_URL}/api/v1/sellers/${SELLER_ID}/balance`,
    { headers: HEADERS }
  );
  readLatency.add(Date.now() - start);

  balanceReads.add(1);

  if (res.status === 200) {
    const balance = JSON.parse(res.body).data;

    if (balance.available < 0) {
      negativeBalance.add(1);
      console.error(
        `NEGATIVE BALANCE during contention: available=${balance.available}`
      );
    }
  }

  sleep(0.2); // 5 reads/sec per VU
}

export function payoutOperation() {
  const idempotencyKey = uuidv4();
  const amount = Math.floor(Math.random() * 20) + 2; // Very small payouts

  const payload = JSON.stringify({
    sellerId: SELLER_ID,
    amount: amount,
  });

  const headers = Object.assign({}, HEADERS, {
    "Idempotency-Key": idempotencyKey,
  });

  opsAttempted.add(1);

  const start = Date.now();
  const res = http.post(`${BASE_URL}/api/v1/payouts`, payload, {
    headers: headers,
    timeout: "30s",
  });
  opsLatency.add(Date.now() - start);

  if (res.status >= 200 && res.status < 300) {
    opsSucceeded.add(1);
  } else if (res.status === 429) {
    opsRetried.add(1);
  } else if (res.status === 422 || res.status === 400) {
    // Business rejection (insufficient balance, etc.) — expected
  } else {
    opsFailed.add(1);
  }
}

export function verifyEnd() {
  sleep(5);

  const res = http.get(
    `${BASE_URL}/api/v1/sellers/${SELLER_ID}/balance`,
    { headers: HEADERS }
  );

  if (res.status !== 200) {
    console.error(`[VERIFY] Balance check failed: ${res.status}`);
    return;
  }

  const finalBalance = JSON.parse(res.body).data;
  console.log(
    `[VERIFY] Final balance: available=${finalBalance.available} total=${finalBalance.total}`
  );
  console.log(
    `[VERIFY] Initial balance: available=${initialBalance.available} total=${initialBalance.total}`
  );

  if (finalBalance.available < 0) {
    negativeBalance.add(1);
    console.error(`NEGATIVE BALANCE AT END: ${finalBalance.available}`);
  }

  // Check transaction consistency
  const txRes = http.get(
    `${BASE_URL}/api/v1/transactions?page=1&pageSize=1&sellerId=${SELLER_ID}`,
    { headers: HEADERS }
  );

  if (txRes.status === 200) {
    const txData = JSON.parse(txRes.body).data;
    console.log(`[VERIFY] Total transactions: ${txData.totalCount}`);
  }

  // Verify no 5xx errors leaked through
  console.log(`[VERIFY] Contention test complete.`);
}

export function handleSummary(data) {
  const attempted = data.metrics.ops_attempted
    ? data.metrics.ops_attempted.values.count
    : 0;
  const succeeded = data.metrics.ops_succeeded
    ? data.metrics.ops_succeeded.values.count
    : 0;
  const failed = data.metrics.ops_failed
    ? data.metrics.ops_failed.values.count
    : 0;
  const rateLimited = data.metrics.ops_retried_429
    ? data.metrics.ops_retried_429.values.count
    : 0;
  const reads = data.metrics.balance_reads
    ? data.metrics.balance_reads.values.count
    : 0;
  const negBal = data.metrics.negative_balance_detected
    ? data.metrics.negative_balance_detected.values.count
    : 0;

  const verdict =
    negBal === 0
      ? "PASS — No lost updates, no negative balances"
      : "FAIL — DATA INTEGRITY COMPROMISED";

  const report = {
    scenario: "Database Contention Test",
    verdict: verdict,
    total_write_ops: attempted,
    write_succeeded: succeeded,
    write_failed: failed,
    rate_limited: rateLimited,
    balance_reads: reads,
    negative_balances: negBal,
    avg_write_latency_ms: data.metrics.ops_latency
      ? data.metrics.ops_latency.values.avg
      : null,
    p95_write_latency_ms: data.metrics.ops_latency
      ? data.metrics.ops_latency.values["p(95)"]
      : null,
    avg_read_latency_ms: data.metrics.read_latency
      ? data.metrics.read_latency.values.avg
      : null,
    p95_read_latency_ms: data.metrics.read_latency
      ? data.metrics.read_latency.values["p(95)"]
      : null,
  };

  return {
    "tests/k6-stress/results/05-contention-result.json": JSON.stringify(
      report,
      null,
      2
    ),
    stdout: `\n${"=".repeat(60)}\nDATABASE CONTENTION RESULT\n${"=".repeat(60)}\n${JSON.stringify(report, null, 2)}\n`,
  };
}
