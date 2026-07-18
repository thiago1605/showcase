// =============================================================================
// Stripe Webhook Signature Helper
// =============================================================================
// Generates valid Stripe-Signature headers for webhook testing.
// Signature format: t={unix_timestamp},v1={hmac_sha256_hex}
// Signed payload:   {timestamp}.{raw_json_body}
// =============================================================================

import crypto from "k6/crypto";

/**
 * Generate a valid Stripe webhook signature.
 * @param {string} payload  - Raw JSON body string
 * @param {string} secret   - Webhook secret (e.g. whsec_...)
 * @returns {{header: string, timestamp: number}}
 */
export function generateStripeSignature(payload, secret) {
  const timestamp = Math.floor(Date.now() / 1000);
  const signedPayload = `${timestamp}.${payload}`;
  const signature = crypto.hmac("sha256", secret, signedPayload, "hex");
  return {
    header: `t=${timestamp},v1=${signature}`,
    timestamp,
  };
}

/**
 * Build a Stripe payment_intent.succeeded webhook event payload.
 * @param {string} paymentIntentId - e.g. "pi_3Abc..."
 * @param {number} amountCents     - Amount in cents
 * @param {Object} [metadata]      - Optional metadata dict
 * @param {string} [walletType]    - Optional wallet type (e.g. "apple_pay", "google_pay")
 * @returns {string} JSON string
 */
export function buildPaymentSucceededEvent(
  paymentIntentId,
  amountCents,
  metadata,
  walletType
) {
  return JSON.stringify({
    id: `evt_k6_${Date.now()}_${Math.random().toString(36).substring(2, 10)}`,
    type: "payment_intent.succeeded",
    data: {
      object: {
        id: paymentIntentId,
        object: "payment_intent",
        status: "succeeded",
        amount: amountCents,
        currency: "brl",
        metadata: metadata || {},
        charges: {
          data: [
            {
              id: `ch_k6_${Date.now()}_${Math.random().toString(36).substring(2, 8)}`,
              payment_method_details: {
                card: {
                  brand: "visa",
                  last4: "4242",
                  wallet: walletType ? { type: walletType } : null,
                },
              },
            },
          ],
        },
      },
    },
  });
}

/**
 * Build a Stripe charge.refunded webhook event payload.
 * @param {string} chargeId          - e.g. "ch_..."
 * @param {string} paymentIntentId   - e.g. "pi_..."
 * @param {number} amountRefundedCents - Total refunded amount in cents
 * @returns {string} JSON string
 */
export function buildChargeRefundedEvent(
  chargeId,
  paymentIntentId,
  amountRefundedCents
) {
  return JSON.stringify({
    id: `evt_k6_refund_${Date.now()}_${Math.random().toString(36).substring(2, 10)}`,
    type: "charge.refunded",
    data: {
      object: {
        id: chargeId,
        object: "charge",
        payment_intent: paymentIntentId,
        amount_refunded: amountRefundedCents,
        status: "succeeded",
      },
    },
  });
}

/**
 * Build a Stripe charge.dispute.created webhook event payload.
 * @param {string} disputeId        - e.g. "dp_..."
 * @param {string} chargeId         - e.g. "ch_..."
 * @param {string} paymentIntentId  - e.g. "pi_..."
 * @param {number} amountCents      - Disputed amount in cents
 * @returns {string} JSON string
 */
export function buildDisputeCreatedEvent(
  disputeId,
  chargeId,
  paymentIntentId,
  amountCents
) {
  return JSON.stringify({
    id: `evt_k6_dispute_${Date.now()}_${Math.random().toString(36).substring(2, 10)}`,
    type: "charge.dispute.created",
    data: {
      object: {
        id: disputeId,
        object: "dispute",
        charge: chargeId,
        payment_intent: paymentIntentId,
        amount: amountCents,
        currency: "brl",
        status: "needs_response",
        reason: "fraudulent",
      },
    },
  });
}

/**
 * Build a Stripe charge.dispute.closed webhook event payload.
 * @param {string} disputeId        - e.g. "dp_..."
 * @param {string} chargeId         - e.g. "ch_..."
 * @param {string} paymentIntentId  - e.g. "pi_..."
 * @param {number} amountCents      - Disputed amount in cents
 * @param {string} status           - "won" or "lost"
 * @returns {string} JSON string
 */
export function buildDisputeClosedEvent(
  disputeId,
  chargeId,
  paymentIntentId,
  amountCents,
  status
) {
  return JSON.stringify({
    id: `evt_k6_dispute_close_${Date.now()}_${Math.random().toString(36).substring(2, 10)}`,
    type: "charge.dispute.closed",
    data: {
      object: {
        id: disputeId,
        object: "dispute",
        charge: chargeId,
        payment_intent: paymentIntentId,
        amount: amountCents,
        currency: "brl",
        status: status,
        reason: "fraudulent",
      },
    },
  });
}
