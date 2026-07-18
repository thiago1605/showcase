// Shared configuration for all stress test scenarios
export const BASE_URL = __ENV.BASE_URL || "http://localhost:5195";
export const API_KEY = __ENV.API_KEY || "MISSING_API_KEY";

// These are resolved after seed/setup. Override via env vars.
export const SELLER_ID = __ENV.SELLER_ID || "";
export const TENANT_ID = __ENV.TENANT_ID || "";

export const HEADERS = {
  "Content-Type": "application/json",
  Accept: "application/json",
  "X-Api-Key": API_KEY,
};

export function authHeaders(idempotencyKey) {
  return Object.assign({}, HEADERS, {
    "Idempotency-Key": idempotencyKey,
  });
}

// Financial validation: call after each scenario
export function validateLedger(sellerId) {
  const res = http.get(`${BASE_URL}/api/v1/sellers/${sellerId}/balance`, {
    headers: HEADERS,
  });

  if (res.status !== 200) {
    console.error(
      `LEDGER CHECK FAILED: status=${res.status} body=${res.body}`
    );
    return null;
  }

  const balance = JSON.parse(res.body).data;
  return balance;
}

import http from "k6/http";
