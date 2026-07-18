#!/usr/bin/env bash
# =============================================================================
# Multi-Rail Chaos Test Runner
# =============================================================================
# Runs the K6 multi-rail chaos test against a running API instance.
#
# Usage:
#   ./tests/k6/run-multi-rail-chaos.sh [api_url] [api_key] [webhook_secret]
#
# Defaults:
#   api_url       = http://localhost:8080
#   api_key       = pk_test_xxxx
#   webhook_secret = whsec_test_secret
# =============================================================================

set -euo pipefail

BASE_URL="${1:-http://localhost:8080}"
API_KEY="${2:-pk_test_xxxx}"
WEBHOOK_SECRET="${3:-whsec_test_secret}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "================================================"
echo " Multi-Rail Chaos Test"
echo "================================================"
echo " API:            $BASE_URL"
echo " API Key:        ${API_KEY:0:8}..."
echo " Webhook Secret: ${WEBHOOK_SECRET:0:10}..."
echo "================================================"

# Health check
echo "Checking API health..."
curl -sf "${BASE_URL}/health" > /dev/null 2>&1 || {
  echo "ERROR: API not reachable at ${BASE_URL}/health"
  exit 1
}
echo "API is healthy."

# Run k6
k6 run \
  -e BASE_URL="$BASE_URL" \
  -e API_KEY="$API_KEY" \
  -e WEBHOOK_SECRET="$WEBHOOK_SECRET" \
  "$SCRIPT_DIR/multi-rail-chaos.js"
