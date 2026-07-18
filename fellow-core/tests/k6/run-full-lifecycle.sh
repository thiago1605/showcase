#!/bin/bash
# =============================================================================
# Full Money Lifecycle Integrity Test — Setup & Execution
# =============================================================================
# 1. Creates a Stripe Connected Account (sandbox)
# 2. Links it to the seeded seller in PostgreSQL
# 3. Starts the API with webhook secret configured
# 4. Runs the k6 full lifecycle integrity test
# 5. Cleans up
# =============================================================================

set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
DOCKER_PG_CONTAINER="fellowcore_db"
DB_USER="admin"
DB_NAME="fellowcore"

# Stripe sandbox keys — set via environment or .env.development
STRIPE_SK="${STRIPE_SK:?Set STRIPE_SK env var}"
STRIPE_PK="${STRIPE_PK:?Set STRIPE_PK env var}"
WEBHOOK_SECRET="${WEBHOOK_SECRET:-whsec_k6_integrity_test_secret_2024}"
API_KEY="${API_KEY:?Set API_KEY env var}"
BASE_URL="http://localhost:5195"

API_PID=""

cleanup() {
  echo ""
  echo "=== Cleanup ==="
  if [ -n "$API_PID" ] && kill -0 "$API_PID" 2>/dev/null; then
    echo "Stopping API (PID $API_PID)..."
    kill "$API_PID" 2>/dev/null || true
    wait "$API_PID" 2>/dev/null || true
  fi
  # Kill any remaining dotnet process on port 5195
  lsof -ti:5195 2>/dev/null | xargs kill 2>/dev/null || true
}
trap cleanup EXIT

run_psql() {
  docker exec "$DOCKER_PG_CONTAINER" psql -U "$DB_USER" -d "$DB_NAME" -t -A -c "$1"
}

echo "============================================================"
echo "  FULL MONEY LIFECYCLE INTEGRITY TEST"
echo "============================================================"

# ─── Step 0: Pre-checks ────────────────────────────────────────────
echo ""
echo "--- Pre-checks ---"
command -v k6 >/dev/null 2>&1 || { echo "ERROR: k6 not found. Install with: brew install k6"; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq not found. Install with: brew install jq"; exit 1; }
command -v curl >/dev/null 2>&1 || { echo "ERROR: curl not found."; exit 1; }
docker ps --format '{{.Names}}' | grep -q "$DOCKER_PG_CONTAINER" || { echo "ERROR: PostgreSQL container '$DOCKER_PG_CONTAINER' not running. Start with docker-compose up -d"; exit 1; }
echo "All pre-checks passed."

# ─── Step 1: Kill existing API ─────────────────────────────────────
echo ""
echo "--- Stopping any existing API on port 5195 ---"
lsof -ti:5195 2>/dev/null | xargs kill 2>/dev/null || true
sleep 1

# ─── Step 2: Get Seller ID ─────────────────────────────────────────
echo ""
echo "--- Getting Seller ID ---"
SELLER_ID=$(run_psql "SELECT \"Id\" FROM \"Sellers\" LIMIT 1;" | tr -d ' \n')
if [ -z "$SELLER_ID" ]; then
  echo "ERROR: No seller found in database. Run the API once to seed data."
  exit 1
fi
echo "Seller ID: $SELLER_ID"

# ─── Step 3: Create Stripe Connected Account ──────────────────────
echo ""
echo "--- Creating Stripe Connected Account (sandbox) ---"
CONNECTED_ACCT_RESPONSE=$(curl -s -X POST https://api.stripe.com/v1/accounts \
  -u "${STRIPE_SK}:" \
  -d "type=custom" \
  -d "country=BR" \
  -d "email=k6-lifecycle-test@fellowcore.io" \
  -d "business_type=individual" \
  -d "capabilities[card_payments][requested]=true" \
  -d "capabilities[transfers][requested]=true" \
  -d "tos_acceptance[date]=$(date +%s)" \
  -d "tos_acceptance[ip]=127.0.0.1" \
  -d "individual[first_name]=K6" \
  -d "individual[last_name]=Lifecycle" \
  -d "individual[email]=k6-lifecycle@fellowcore.io" \
  -d "individual[dob][day]=1" \
  -d "individual[dob][month]=1" \
  -d "individual[dob][year]=1990" \
  -d "individual[address][line1]=Rua Teste 123" \
  -d "individual[address][city]=Sao Paulo" \
  -d "individual[address][state]=SP" \
  -d "individual[address][postal_code]=01001000" \
  -d "individual[address][country]=BR" \
  --data-urlencode "individual[phone]=+5511999999999" \
  -d "individual[id_number]=12345678909" \
  -d "individual[political_exposure]=none" \
  -d "individual[verification][document][front]=file_identity_document_success" \
  -d "business_profile[url]=https://fellowcore.io" \
  -d "business_profile[mcc]=5734" 2>&1)

CONNECTED_ACCT=$(echo "$CONNECTED_ACCT_RESPONSE" | jq -r '.id // empty')
if [ -z "$CONNECTED_ACCT" ]; then
  echo "WARNING: Failed to create connected account. Response:"
  echo "$CONNECTED_ACCT_RESPONSE" | jq . 2>/dev/null || echo "$CONNECTED_ACCT_RESPONSE"
  echo "Continuing WITHOUT connected account..."
  CONNECTED_ACCT=""
else
  echo "Connected Account: $CONNECTED_ACCT"

  # Add test bank account for payouts
  echo "Adding test bank account..."
  curl -s -X POST "https://api.stripe.com/v1/accounts/${CONNECTED_ACCT}/external_accounts" \
    -u "${STRIPE_SK}:" \
    -d "external_account=btok_br" > /dev/null 2>&1

  # Wait for capabilities to activate in test mode
  echo "Waiting for capabilities to activate..."
  for i in $(seq 1 15); do
    CAPS=$(curl -s -u "${STRIPE_SK}:" "https://api.stripe.com/v1/accounts/${CONNECTED_ACCT}" | jq -r '.capabilities.transfers // "inactive"')
    if [ "$CAPS" = "active" ]; then
      echo "Capabilities active after ${i}s"
      break
    fi
    sleep 1
  done

  if [ "$CAPS" != "active" ]; then
    echo "WARNING: Capabilities not yet active (status: $CAPS). Payments with transfer_data may fail."
  fi

  # ─── Step 4: Link Connected Account to Seller ──────────────────
  echo ""
  echo "--- Linking Connected Account to Seller ---"
  run_psql "UPDATE \"Sellers\" SET \"ExternalAccountId\" = '${CONNECTED_ACCT}' WHERE \"Id\" = '${SELLER_ID}';"
  echo "Seller updated with ExternalAccountId = $CONNECTED_ACCT"
fi

# ─── Step 5: Start API ─────────────────────────────────────────────
echo ""
echo "--- Starting API ---"

export ASPNETCORE_ENVIRONMENT="Development"
export ASPNETCORE_URLS="http://localhost:5195"
export RateLimiting__FixedPermitLimit="9999999"
export RateLimiting__WebhooksPermitLimit="9999999"
export ConnectionStrings__DefaultConnection="${DB_CONNECTION:-Host=localhost;Port=5454;Database=fellowcore;Username=admin;Password=changeme;Maximum Pool Size=200;Timeout=30}"
export Stripe__SecretKey="$STRIPE_SK"
export Stripe__PublishableKey="$STRIPE_PK"
export Stripe__WebhookSecret="$WEBHOOK_SECRET"
export Security__MasterKey="${SECURITY_MASTER_KEY:?Set SECURITY_MASTER_KEY env var}"
export REDIS_HOST="localhost"
export REDIS_PORT="6380"
export REDIS_PASSWORD="fellowcore_redis_dev"
export Storage__Endpoint="http://localhost:9000"
export Storage__AccessKey="minioadmin"
export Storage__SecretKey="minioadmin123"
export Storage__BucketName="documents"
export Storage__PublicUrl="http://localhost:9000"

cd "$PROJECT_DIR"
dotnet build src/FellowPay.Api -c Release -v q 2>&1 | tail -3
dotnet run --project src/FellowPay.Api --no-launch-profile --no-build -c Release > /tmp/fellowcore-api-lifecycle.log 2>&1 &
API_PID=$!

echo "API starting (PID $API_PID)..."
echo "Waiting for API to be ready..."

for i in $(seq 1 60); do
  if curl -s "$BASE_URL/" > /dev/null 2>&1; then
    echo "API ready after ${i}s"
    break
  fi
  if ! kill -0 "$API_PID" 2>/dev/null; then
    echo "ERROR: API process died. Last 30 lines:"
    tail -30 /tmp/fellowcore-api-lifecycle.log
    exit 1
  fi
  sleep 1
done

# Verify the API is actually responding
HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" "$BASE_URL/")
if [ "$HTTP_STATUS" != "200" ]; then
  echo "ERROR: API not responding (HTTP $HTTP_STATUS). Logs:"
  tail -30 /tmp/fellowcore-api-lifecycle.log
  exit 1
fi

echo "API is live."

# ─── Step 6: Run k6 Test ───────────────────────────────────────────
echo ""
echo "============================================================"
echo "  RUNNING FULL LIFECYCLE INTEGRITY TEST"
echo "============================================================"
echo ""

mkdir -p "$PROJECT_DIR/tests/results"

k6 run "$PROJECT_DIR/tests/k6/full-lifecycle-integrity.js" \
  -e BASE_URL="$BASE_URL" \
  -e API_KEY="$API_KEY" \
  -e STRIPE_SK="$STRIPE_SK" \
  -e WEBHOOK_SECRET="$WEBHOOK_SECRET" \
  -e SELLER_ID="$SELLER_ID" \
  -e CONNECTED_ACCOUNT_ID="$CONNECTED_ACCT"

# ─── Step 7: Restore Seller ────────────────────────────────────────
if [ -n "$CONNECTED_ACCT" ]; then
  echo ""
  echo "--- Restoring seller (removing test connected account) ---"
  run_psql "UPDATE \"Sellers\" SET \"ExternalAccountId\" = NULL WHERE \"Id\" = '${SELLER_ID}';" || true
fi

# ─── Step 8: Show Report ───────────────────────────────────────────
echo ""
REPORT_FILE="$PROJECT_DIR/tests/results/full-lifecycle-report.json"
if [ -f "$REPORT_FILE" ]; then
  echo "============================================================"
  echo "  FINAL REPORT"
  echo "============================================================"
  VERDICT=$(jq -r '.verdict' "$REPORT_FILE")
  echo "Verdict: $VERDICT"
  echo ""
  jq '.' "$REPORT_FILE"
  echo ""
  echo "Full report: $REPORT_FILE"
fi
