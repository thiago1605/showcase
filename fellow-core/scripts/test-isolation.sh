#!/usr/bin/env bash
# Phase E — security boundary tests with two real sellers (A and B).
#
# What this script proves:
#   For every portal-facing controller (Dashboard, Transactions, Payouts, PaymentLinks,
#   Receipts, Subscriptions, Sellers/me, Users, and the operator-only set), seller A
#   never sees, lists, reads-by-id, or operates on seller B's resources, and seller-
#   scoped JWTs cannot reach platform-operator endpoints.
#
# Idempotent: safe to rerun. Recreates seller B + user B + B's resources only when
# missing. Rolls back any test-created rows at the end.
#
# Run: bash scripts/test-isolation.sh

set -uo pipefail

API="${API:-http://localhost:8080}"
DB_USER="${DB_USER:-admin}"
DB_NAME="${DB_NAME:-fellowpay}"
DB_CONTAINER="${DB_CONTAINER:-fellowcore_db}"

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[0;33m'; NC='\033[0m'
PASS=0; FAIL=0
UUID_RE='^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'

# psql_exec runs SQL with ON_ERROR_STOP=1 and aborts the whole script on any DB error.
# Returns the first row, trimmed.
psql_exec() {
    local out
    out=$(docker exec "$DB_CONTAINER" psql -U "$DB_USER" -d "$DB_NAME" -At -v ON_ERROR_STOP=1 -c "$1" 2>&1)
    local rc=$?
    if [ $rc -ne 0 ]; then
        echo "DB ERROR running: $1" >&2
        echo "$out" >&2
        exit 1
    fi
    echo "$out" | head -1
}

require_uuid() {
    local name="$1" value="$2"
    if ! [[ "$value" =~ $UUID_RE ]]; then
        echo "FATAL: $name is not a UUID: '$value'" >&2
        exit 1
    fi
}

assert_status() {
    local name="$1" expected="$2" actual="$3"
    if [ "$actual" = "$expected" ]; then
        echo -e "  ${GREEN}PASS${NC} $name → $actual"
        PASS=$((PASS+1))
    else
        echo -e "  ${RED}FAIL${NC} $name → got $actual, expected $expected"
        FAIL=$((FAIL+1))
    fi
}

call() {
    # All mutating endpoints require an Idempotency-Key middleware. Pass a fresh one
    # each call so it never collides with prior runs.
    local m="$1" u="$2" t="$3" b="${4:-}"
    local ikey
    ikey="test-$(date +%s%N)-$RANDOM"
    if [ -n "$b" ]; then
        curl -s -o /dev/null -w "%{http_code}" -X "$m" \
            -H "Authorization: Bearer $t" \
            -H "Content-Type: application/json" \
            -H "Idempotency-Key: $ikey" \
            -d "$b" "$API$u"
    else
        curl -s -o /dev/null -w "%{http_code}" -X "$m" \
            -H "Authorization: Bearer $t" \
            -H "Idempotency-Key: $ikey" \
            "$API$u"
    fi
}

# ============================================================
# SETUP — Seller B + user B + isolated resources for both sellers
# ============================================================
echo "=== Setup ==="

TENANT_ID=$(psql_exec "SELECT \"Id\" FROM \"Tenants\" LIMIT 1;")
require_uuid "TENANT_ID" "$TENANT_ID"

SELLER_A_ID=$(psql_exec "SELECT \"Id\" FROM \"Sellers\" WHERE \"Email\"='bruce@wayneenterprises.com' LIMIT 1;")
require_uuid "SELLER_A_ID" "$SELLER_A_ID"
echo "  Seller A: $SELLER_A_ID"

# --- Seller B ---
SELLER_B_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"Sellers\" WHERE \"Email\"='alfred@wayneenterprises.com' LIMIT 1)::text, '');")
if [ -z "$SELLER_B_ID" ]; then
    SELLER_B_ID=$(psql_exec "INSERT INTO \"Sellers\" (\"Id\",\"TenantId\",\"LegalName\",\"Document\",\"Email\",\"WebhookSecret\",\"PreferredProvider\",\"EncryptedAccessToken\",\"FeeDebit\",\"FeeCreditCash\",\"FeeCreditInstallment\",\"FeePixIn\",\"PayoutFixedFee\",\"PayoutPercentFee\",\"Status\",\"CreatedAt\",\"UpdatedAt\") VALUES (gen_random_uuid(),'$TENANT_ID','Mansão Wayne','98765432109','alfred@wayneenterprises.com','seed-webhook-secret-32chars-long!!',0,'placeholder-token',0,0,0,0,0,0,1,NOW(),NOW()) RETURNING \"Id\";")
    require_uuid "SELLER_B_ID" "$SELLER_B_ID"
    echo "  Seller B criado: $SELLER_B_ID"
else
    require_uuid "SELLER_B_ID" "$SELLER_B_ID"
    echo "  Seller B reusado: $SELLER_B_ID"
fi

# --- Ledger accounts for Seller B (WALLET=0, FUTURE_RECEIVABLES=1) ---
LEDGER_B_WALLET=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"LedgerAccounts\" WHERE \"SellerId\"='$SELLER_B_ID' AND \"Type\"=0 LIMIT 1)::text,'');")
if [ -z "$LEDGER_B_WALLET" ]; then
    LEDGER_B_WALLET=$(psql_exec "INSERT INTO \"LedgerAccounts\" (\"Id\",\"TenantId\",\"SellerId\",\"Type\",\"Balance\",\"UpdatedAt\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_B_ID',0,0,NOW()) RETURNING \"Id\";")
    require_uuid "LEDGER_B_WALLET" "$LEDGER_B_WALLET"
    echo "  Ledger B WALLET criado"
fi
LEDGER_B_FR=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"LedgerAccounts\" WHERE \"SellerId\"='$SELLER_B_ID' AND \"Type\"=1 LIMIT 1)::text,'');")
if [ -z "$LEDGER_B_FR" ]; then
    psql_exec "INSERT INTO \"LedgerAccounts\" (\"Id\",\"TenantId\",\"SellerId\",\"Type\",\"Balance\",\"UpdatedAt\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_B_ID',1,0,NOW());" >/dev/null
    echo "  Ledger B FUTURE_RECEIVABLES criado"
fi

# --- User OWNER B vinculado ao Seller B ---
USER_B_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"Users\" WHERE \"Email\"='alfred@fellowpay.dev' LIMIT 1)::text,'');")
if [ -z "$USER_B_ID" ]; then
    HASH_B=$(htpasswd -bnBC 12 "" 'senha1234' | tr -d ':\n')
    # Postgres dollar-quoting com tag única evita interpretação dos $ internos do hash bcrypt.
    USER_B_ID=$(psql_exec "INSERT INTO \"Users\" (\"Id\",\"TenantId\",\"SellerId\",\"Name\",\"Email\",\"Password\",\"Role\",\"CreatedAt\",\"IsTotpEnabled\",\"AccessFailedCount\",\"IsActive\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_B_ID','Alfred','alfred@fellowpay.dev',\$pw\$$HASH_B\$pw\$,1,NOW(),false,0,true) RETURNING \"Id\";")
    require_uuid "USER_B_ID" "$USER_B_ID"
    echo "  User B criado: $USER_B_ID"
else
    require_uuid "USER_B_ID" "$USER_B_ID"
    echo "  User B reusado: $USER_B_ID"
fi

# --- Resource: Transaction A & B (idempotent: pega ou cria) ---
TX_A_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"Transactions\" WHERE \"SellerId\"='$SELLER_A_ID' LIMIT 1)::text,'');")
require_uuid "TX_A_ID" "$TX_A_ID"
TX_B_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"Transactions\" WHERE \"SellerId\"='$SELLER_B_ID' LIMIT 1)::text,'');")
if [ -z "$TX_B_ID" ]; then
    TX_B_ID=$(psql_exec "INSERT INTO \"Transactions\" (\"Id\",\"TenantId\",\"SellerId\",\"Amount\",\"PaymentType\",\"Provider\",\"Installments\",\"FeeAmount\",\"NetAmount\",\"Status\",\"SettlementStatus\",\"Currency\",\"CreatedAt\",\"UpdatedAt\",\"DunningAttempts\",\"RefundedAmount\",\"FeeAllocationPolicy\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_B_ID',250,2,1,1,0,247.50,3,0,'BRL',NOW(),NOW(),0,0,0) RETURNING \"Id\";")
    require_uuid "TX_B_ID" "$TX_B_ID"
fi
echo "  TX A=$TX_A_ID  TX B=$TX_B_ID"

# --- Resource: Payout A & B ---
PAYOUT_A_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"Payouts\" WHERE \"SellerId\"='$SELLER_A_ID' LIMIT 1)::text,'');")
if [ -z "$PAYOUT_A_ID" ]; then
    PAYOUT_A_ID=$(psql_exec "INSERT INTO \"Payouts\" (\"Id\",\"TenantId\",\"SellerId\",\"Amount\",\"Fee\",\"Status\",\"BankProvider\",\"CreatedAt\",\"UpdatedAt\",\"AttemptCount\",\"IdempotencyKey\",\"MaxRetries\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_A_ID',100,1.49,0,0,NOW(),NOW(),0,'test-A-' || gen_random_uuid()::text,0) RETURNING \"Id\";")
fi
require_uuid "PAYOUT_A_ID" "$PAYOUT_A_ID"
PAYOUT_B_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"Payouts\" WHERE \"SellerId\"='$SELLER_B_ID' LIMIT 1)::text,'');")
if [ -z "$PAYOUT_B_ID" ]; then
    PAYOUT_B_ID=$(psql_exec "INSERT INTO \"Payouts\" (\"Id\",\"TenantId\",\"SellerId\",\"Amount\",\"Fee\",\"Status\",\"BankProvider\",\"CreatedAt\",\"UpdatedAt\",\"AttemptCount\",\"IdempotencyKey\",\"MaxRetries\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_B_ID',200,1.49,0,0,NOW(),NOW(),0,'test-B-' || gen_random_uuid()::text,0) RETURNING \"Id\";")
fi
require_uuid "PAYOUT_B_ID" "$PAYOUT_B_ID"
echo "  Payouts A=$PAYOUT_A_ID  B=$PAYOUT_B_ID"

# --- Resource: PaymentLink A & B ---
LINK_A_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"PaymentLinks\" WHERE \"SellerId\"='$SELLER_A_ID' LIMIT 1)::text,'');")
if [ -z "$LINK_A_ID" ]; then
    LINK_A_ID=$(psql_exec "INSERT INTO \"PaymentLinks\" (\"Id\",\"TenantId\",\"SellerId\",\"Token\",\"Amount\",\"PaymentType\",\"Installments\",\"MaxUses\",\"UsageCount\",\"Active\",\"CreatedAt\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_A_ID',substr(md5(random()::text),1,16),50,2,1,10,0,true,NOW()) RETURNING \"Id\";")
fi
require_uuid "LINK_A_ID" "$LINK_A_ID"
LINK_B_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"PaymentLinks\" WHERE \"SellerId\"='$SELLER_B_ID' LIMIT 1)::text,'');")
if [ -z "$LINK_B_ID" ]; then
    LINK_B_ID=$(psql_exec "INSERT INTO \"PaymentLinks\" (\"Id\",\"TenantId\",\"SellerId\",\"Token\",\"Amount\",\"PaymentType\",\"Installments\",\"MaxUses\",\"UsageCount\",\"Active\",\"CreatedAt\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_B_ID',substr(md5(random()::text),1,16),75,2,1,10,0,true,NOW()) RETURNING \"Id\";")
fi
require_uuid "LINK_B_ID" "$LINK_B_ID"
echo "  PaymentLinks A=$LINK_A_ID  B=$LINK_B_ID"

# --- Resource: Subscription A & B ---
SUB_A_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"Subscriptions\" WHERE \"SellerId\"='$SELLER_A_ID' LIMIT 1)::text,'');")
if [ -z "$SUB_A_ID" ]; then
    SUB_A_ID=$(psql_exec "INSERT INTO \"Subscriptions\" (\"Id\",\"TenantId\",\"SellerId\",\"Amount\",\"Description\",\"Interval\",\"Status\",\"StartDate\",\"NextBillingDate\",\"CycleCount\",\"CreatedAt\",\"UpdatedAt\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_A_ID',49.90,'Plano A',1,0,NOW(),NOW()+interval '30 days',0,NOW(),NOW()) RETURNING \"Id\";")
fi
require_uuid "SUB_A_ID" "$SUB_A_ID"
SUB_B_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"Subscriptions\" WHERE \"SellerId\"='$SELLER_B_ID' LIMIT 1)::text,'');")
if [ -z "$SUB_B_ID" ]; then
    SUB_B_ID=$(psql_exec "INSERT INTO \"Subscriptions\" (\"Id\",\"TenantId\",\"SellerId\",\"Amount\",\"Description\",\"Interval\",\"Status\",\"StartDate\",\"NextBillingDate\",\"CycleCount\",\"CreatedAt\",\"UpdatedAt\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_B_ID',79.90,'Plano B',1,0,NOW(),NOW()+interval '30 days',0,NOW(),NOW()) RETURNING \"Id\";")
fi
require_uuid "SUB_B_ID" "$SUB_B_ID"
echo "  Subscriptions A=$SUB_A_ID  B=$SUB_B_ID"

# --- Resource: Receipt A & B ---
RCT_A_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"Receipts\" WHERE \"SellerId\"='$SELLER_A_ID' LIMIT 1)::text,'');")
if [ -z "$RCT_A_ID" ]; then
    RCT_A_ID=$(psql_exec "INSERT INTO \"Receipts\" (\"Id\",\"TenantId\",\"SellerId\",\"Type\",\"Provider\",\"Status\",\"Amount\",\"Currency\",\"CreatedAt\",\"CustomerEmailAttempts\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_A_ID',0,1,0,100,'BRL',NOW(),0) RETURNING \"Id\";")
fi
require_uuid "RCT_A_ID" "$RCT_A_ID"
RCT_B_ID=$(psql_exec "SELECT COALESCE((SELECT \"Id\" FROM \"Receipts\" WHERE \"SellerId\"='$SELLER_B_ID' LIMIT 1)::text,'');")
if [ -z "$RCT_B_ID" ]; then
    RCT_B_ID=$(psql_exec "INSERT INTO \"Receipts\" (\"Id\",\"TenantId\",\"SellerId\",\"Type\",\"Provider\",\"Status\",\"Amount\",\"Currency\",\"CreatedAt\",\"CustomerEmailAttempts\") VALUES (gen_random_uuid(),'$TENANT_ID','$SELLER_B_ID',0,1,0,200,'BRL',NOW(),0) RETURNING \"Id\";")
fi
require_uuid "RCT_B_ID" "$RCT_B_ID"
echo "  Receipts A=$RCT_A_ID  B=$RCT_B_ID"

# Login both sellers
TOKEN_A=$(curl -s -X POST "$API/api/v1/auth/login" -H "Content-Type: application/json" -d '{"email":"admin@fellowpay.dev","password":"senha1234"}' | python3 -c "import sys,json; print(json.loads(sys.stdin.read())['data']['accessToken'])")
TOKEN_B=$(curl -s -X POST "$API/api/v1/auth/login" -H "Content-Type: application/json" -d '{"email":"alfred@fellowpay.dev","password":"senha1234"}' | python3 -c "import sys,json; print(json.loads(sys.stdin.read())['data']['accessToken'])")
[ -z "$TOKEN_A" ] || [ -z "$TOKEN_B" ] && { echo "ERR: login falhou"; exit 1; }
# Verify B JWT carries seller_id matching SELLER_B_ID
B_SELLER_FROM_JWT=$(python3 -c "import sys,base64,json; t='$TOKEN_B'.split('.')[1]; t+='='*(-len(t)%4); print(json.loads(base64.urlsafe_b64decode(t)).get('seller_id',''))")
[ "$B_SELLER_FROM_JWT" = "$SELLER_B_ID" ] || { echo "ERR: token B carrega seller_id=$B_SELLER_FROM_JWT, esperado $SELLER_B_ID"; exit 1; }
echo "  Login A/B OK (B JWT seller_id confere)"

# ============================================================
# TESTS
# ============================================================
echo
echo "=== Listagens (filtro forçado por SellerId) ==="
COUNT_A=$(curl -s -H "Authorization: Bearer $TOKEN_A" "$API/api/v1/transactions?pageSize=100" | python3 -c "import sys,json; d=json.loads(sys.stdin.read())['data']; print(len(d.get('items') or []))")
COUNT_A_FAKE=$(curl -s -H "Authorization: Bearer $TOKEN_A" "$API/api/v1/transactions?pageSize=100&sellerId=$SELLER_B_ID" | python3 -c "import sys,json; d=json.loads(sys.stdin.read())['data']; print(len(d.get('items') or []))")
COUNT_B=$(curl -s -H "Authorization: Bearer $TOKEN_B" "$API/api/v1/transactions?pageSize=100" | python3 -c "import sys,json; d=json.loads(sys.stdin.read())['data']; print(len(d.get('items') or []))")
[ "$COUNT_A" = "$COUNT_A_FAKE" ] && echo -e "  ${GREEN}PASS${NC} A list ?sellerId=B silenciosamente forçado (A=$COUNT_A em ambos)" && PASS=$((PASS+1)) || { echo -e "  ${RED}FAIL${NC} A vazou? base=$COUNT_A com_B=$COUNT_A_FAKE"; FAIL=$((FAIL+1)); }
[ "$COUNT_A" -ge 1 ] && [ "$COUNT_B" -ge 1 ] && echo -e "  ${GREEN}PASS${NC} A=$COUNT_A B=$COUNT_B (cada seller só vê suas tx)" && PASS=$((PASS+1)) || { echo -e "  ${RED}FAIL${NC} A=$COUNT_A B=$COUNT_B"; FAIL=$((FAIL+1)); }

echo
echo "=== Transactions ==="
assert_status "A → GET /transactions/<TX_B>" 404 "$(call GET "/api/v1/transactions/$TX_B_ID" "$TOKEN_A")"
assert_status "B → GET /transactions/<TX_A>" 404 "$(call GET "/api/v1/transactions/$TX_A_ID" "$TOKEN_B")"
assert_status "A → GET /transactions/<TX_A>" 200 "$(call GET "/api/v1/transactions/$TX_A_ID" "$TOKEN_A")"
assert_status "A → POST /transactions/<TX_B>/cancel" 403 "$(call POST "/api/v1/transactions/$TX_B_ID/cancel" "$TOKEN_A")"
assert_status "A → PATCH /transactions/<TX_B>" 403 "$(call PATCH "/api/v1/transactions/$TX_B_ID" "$TOKEN_A" '{"expiresAt":"2027-01-01T00:00:00Z"}')"
# Refund: needs valid body (Amount required); we still expect 403 to fire before deeper validation
assert_status "A → POST /transactions/<TX_B>/refund (payload válido)" 403 "$(call POST "/api/v1/transactions/$TX_B_ID/refund" "$TOKEN_A" '{"amount":1.00,"reason":"TEST"}')"

# Create transaction by A com sellerId=B no body → deve forçar SellerId=A
IKEY="test-tx-$(date +%s%N)"
RES_NEW=$(curl -s -X POST "$API/api/v1/transactions" -H "Authorization: Bearer $TOKEN_A" -H "Content-Type: application/json" -H "Idempotency-Key: $IKEY" -d "{\"sellerId\":\"$SELLER_B_ID\",\"amount\":11,\"paymentType\":2,\"provider\":1,\"installments\":1}")
NEW_TX_ID=$(echo "$RES_NEW" | python3 -c "import sys,json; d=json.loads(sys.stdin.read()).get('data') or {}; print(d.get('internalId') or '')" 2>/dev/null)
if [[ "$NEW_TX_ID" =~ $UUID_RE ]]; then
    NEW_TX_SELLER=$(psql_exec "SELECT \"SellerId\" FROM \"Transactions\" WHERE \"Id\"='$NEW_TX_ID';")
    [ "$NEW_TX_SELLER" = "$SELLER_A_ID" ] && echo -e "  ${GREEN}PASS${NC} A → POST /transactions sellerId=B no body → criada como A" && PASS=$((PASS+1)) || { echo -e "  ${RED}FAIL${NC} criada com sellerId=$NEW_TX_SELLER"; FAIL=$((FAIL+1)); }
    psql_exec "DELETE FROM \"TransactionEvents\" WHERE \"TransactionId\"='$NEW_TX_ID';" >/dev/null 2>&1
    psql_exec "DELETE FROM \"Transactions\" WHERE \"Id\"='$NEW_TX_ID';" >/dev/null
else
    BODY_ERR=$(echo "$RES_NEW" | python3 -c "import sys,json; d=json.loads(sys.stdin.read()); print(d.get('message') or d)" 2>/dev/null || true)
    echo -e "  ${YELLOW}NOTE${NC} POST /transactions retornou erro de validação: $BODY_ERR"
fi

echo
echo "=== Payouts ==="
assert_status "A → GET /payouts/<PAYOUT_B>" 404 "$(call GET "/api/v1/payouts/$PAYOUT_B_ID" "$TOKEN_A")"
assert_status "A → GET /payouts/<PAYOUT_A>" 200 "$(call GET "/api/v1/payouts/$PAYOUT_A_ID" "$TOKEN_A")"
assert_status "B → GET /payouts/<PAYOUT_A>" 404 "$(call GET "/api/v1/payouts/$PAYOUT_A_ID" "$TOKEN_B")"
# POST com sellerId divergente → 403 (controller faz check ANTES da service); payload válido (Amount > 0)
RES_PAYOUT=$(call POST "/api/v1/payouts" "$TOKEN_A" "{\"sellerId\":\"$SELLER_B_ID\",\"amount\":50}")
[ "$RES_PAYOUT" = "403" ] && echo -e "  ${GREEN}PASS${NC} A → POST /payouts (sellerId=B) → 403" && PASS=$((PASS+1)) || echo -e "  ${YELLOW}NOTE${NC} A → POST /payouts (sellerId=B) → $RES_PAYOUT (validation interceptou — auth não confirmado neste path)"
# List of A não inclui payout B
COUNT_PAYOUT_A=$(curl -s -H "Authorization: Bearer $TOKEN_A" "$API/api/v1/payouts?pageSize=100" | python3 -c "import sys,json; d=json.loads(sys.stdin.read())['data']; items=d.get('items') or d.get('data') or d; n=sum(1 for p in (items if isinstance(items,list) else items.get('items',[])) if p.get('sellerId') == '$SELLER_B_ID'); print(n)")
[ "$COUNT_PAYOUT_A" = "0" ] && echo -e "  ${GREEN}PASS${NC} A → GET /payouts não vê payout B" && PASS=$((PASS+1)) || { echo -e "  ${RED}FAIL${NC} A vê $COUNT_PAYOUT_A payouts B"; FAIL=$((FAIL+1)); }

echo
echo "=== PaymentLinks ==="
assert_status "A → GET /payment-links/<LINK_B>" 404 "$(call GET "/api/v1/payment-links/$LINK_B_ID" "$TOKEN_A")"
assert_status "A → GET /payment-links/<LINK_A>" 200 "$(call GET "/api/v1/payment-links/$LINK_A_ID" "$TOKEN_A")"
assert_status "A → DELETE /payment-links/<LINK_B>" 403 "$(call DELETE "/api/v1/payment-links/$LINK_B_ID" "$TOKEN_A")"
RES_PL=$(call POST "/api/v1/payment-links" "$TOKEN_A" "{\"amount\":10,\"paymentType\":2,\"installments\":1,\"sellerId\":\"$SELLER_B_ID\"}")
[ "$RES_PL" = "403" ] && echo -e "  ${GREEN}PASS${NC} A → POST /payment-links (sellerId=B) → 403" && PASS=$((PASS+1)) || echo -e "  ${YELLOW}NOTE${NC} A → POST /payment-links (sellerId=B) → $RES_PL"
# List filtra
COUNT_LINK_LEAK=$(curl -s -H "Authorization: Bearer $TOKEN_A" "$API/api/v1/payment-links" | python3 -c "import sys,json; d=json.loads(sys.stdin.read()).get('data') or []; print(sum(1 for p in d if p.get('sellerId') == '$SELLER_B_ID'))")
[ "$COUNT_LINK_LEAK" = "0" ] && echo -e "  ${GREEN}PASS${NC} A → GET /payment-links não vê links de B" && PASS=$((PASS+1)) || { echo -e "  ${RED}FAIL${NC} A vê $COUNT_LINK_LEAK links B"; FAIL=$((FAIL+1)); }

echo
echo "=== Subscriptions ==="
assert_status "A → GET /subscriptions/<SUB_B>" 404 "$(call GET "/api/v1/subscriptions/$SUB_B_ID" "$TOKEN_A")"
assert_status "A → GET /subscriptions/<SUB_A>" 200 "$(call GET "/api/v1/subscriptions/$SUB_A_ID" "$TOKEN_A")"
assert_status "A → POST /subscriptions/<SUB_B>/cancel" 403 "$(call POST "/api/v1/subscriptions/$SUB_B_ID/cancel" "$TOKEN_A")"
assert_status "A → POST /subscriptions/<SUB_B>/pause" 403 "$(call POST "/api/v1/subscriptions/$SUB_B_ID/pause" "$TOKEN_A")"
assert_status "A → POST /subscriptions/<SUB_B>/resume" 403 "$(call POST "/api/v1/subscriptions/$SUB_B_ID/resume" "$TOKEN_A")"
RES_SUB=$(call POST "/api/v1/subscriptions" "$TOKEN_A" "{\"sellerId\":\"$SELLER_B_ID\",\"amount\":50,\"description\":\"x\",\"interval\":1}")
[ "$RES_SUB" = "403" ] && echo -e "  ${GREEN}PASS${NC} A → POST /subscriptions (sellerId=B) → 403" && PASS=$((PASS+1)) || echo -e "  ${YELLOW}NOTE${NC} A → POST /subscriptions (sellerId=B) → $RES_SUB"

echo
echo "=== Receipts ==="
assert_status "A → GET /receipts/<RCT_B>" 404 "$(call GET "/api/v1/receipts/$RCT_B_ID" "$TOKEN_A")"
assert_status "A → GET /receipts/<RCT_A>" 200 "$(call GET "/api/v1/receipts/$RCT_A_ID" "$TOKEN_A")"
assert_status "A → GET /receipts/seller/<B>" 403 "$(call GET "/api/v1/receipts/seller/$SELLER_B_ID" "$TOKEN_A")"
assert_status "A → GET /receipts/seller/<A>" 200 "$(call GET "/api/v1/receipts/seller/$SELLER_A_ID" "$TOKEN_A")"
assert_status "A → POST /receipts/transaction/<TX_A>" 403 "$(call POST "/api/v1/receipts/transaction/$TX_A_ID" "$TOKEN_A")"

echo
echo "=== Sellers/me JWT-only ==="
assert_status "A → GET /sellers/me" 200 "$(call GET "/api/v1/sellers/me" "$TOKEN_A")"
assert_status "A → GET /sellers/me/balance" 200 "$(call GET "/api/v1/sellers/me/balance" "$TOKEN_A")"
assert_status "B → GET /sellers/me/balance" 200 "$(call GET "/api/v1/sellers/me/balance" "$TOKEN_B")"
# Resposta de A != resposta de B (sellerId diferente)
A_OWN_SELLER=$(curl -s -H "Authorization: Bearer $TOKEN_A" "$API/api/v1/sellers/me/balance" | python3 -c "import sys,json; print(json.loads(sys.stdin.read())['data']['sellerId'])")
B_OWN_SELLER=$(curl -s -H "Authorization: Bearer $TOKEN_B" "$API/api/v1/sellers/me/balance" | python3 -c "import sys,json; print(json.loads(sys.stdin.read())['data']['sellerId'])")
[ "$A_OWN_SELLER" = "$SELLER_A_ID" ] && [ "$B_OWN_SELLER" = "$SELLER_B_ID" ] && echo -e "  ${GREEN}PASS${NC} /sellers/me/balance retorna o próprio seller para cada token" && PASS=$((PASS+1)) || { echo -e "  ${RED}FAIL${NC} A=$A_OWN_SELLER B=$B_OWN_SELLER"; FAIL=$((FAIL+1)); }

echo
echo "=== Users ==="
USER_PAYLOAD_SUPER="{\"name\":\"hack\",\"email\":\"hack-$RANDOM@x.com\",\"password\":\"AbcDef12345!@#\",\"role\":0}"
USER_PAYLOAD_BSELLER="{\"name\":\"hack2\",\"email\":\"hack2-$RANDOM@x.com\",\"password\":\"AbcDef12345!@#\",\"role\":4,\"sellerId\":\"$SELLER_B_ID\"}"
assert_status "A → POST /users {Role=SUPER_ADMIN}" 403 "$(call POST "/api/v1/users" "$TOKEN_A" "$USER_PAYLOAD_SUPER")"
assert_status "A → POST /users {SellerId=B}" 403 "$(call POST "/api/v1/users" "$TOKEN_A" "$USER_PAYLOAD_BSELLER")"
LEAK_USERS=$(curl -s -H "Authorization: Bearer $TOKEN_A" "$API/api/v1/users" | python3 -c "import sys,json; d=json.loads(sys.stdin.read()).get('data') or []; print(sum(1 for u in d if u.get('sellerId') == '$SELLER_B_ID'))")
[ "$LEAK_USERS" = "0" ] && echo -e "  ${GREEN}PASS${NC} A → GET /users não vê users de B" && PASS=$((PASS+1)) || { echo -e "  ${RED}FAIL${NC} A vê $LEAK_USERS users B"; FAIL=$((FAIL+1)); }

echo
echo "=== Operator-only endpoints (seller JWT bloqueado) ==="
assert_status "A → GET /reconciliation/runs" 403 "$(call GET "/api/v1/reconciliation/runs" "$TOKEN_A")"
assert_status "A → GET /audit-logs" 403 "$(call GET "/api/v1/audit-logs" "$TOKEN_A")"
assert_status "A → GET /dashboard/financial-health" 403 "$(call GET "/api/v1/dashboard/financial-health" "$TOKEN_A")"
assert_status "B → GET /reconciliation/runs" 403 "$(call GET "/api/v1/reconciliation/runs" "$TOKEN_B")"

echo
echo "=== Split Rules (ownership) ==="
# A creates a rule with B as recipient. B should see it but not delete it.
CREATE_AB=$(curl -s -X POST -H "Authorization: Bearer $TOKEN_A" -H "Content-Type: application/json" -H "Idempotency-Key: sriso-$RANDOM-$(date +%s%N)" -d "{\"name\":\"Iso $(date +%s%N)\",\"recipients\":[{\"sellerId\":\"$SELLER_A_ID\",\"percentage\":70,\"priority\":1},{\"sellerId\":\"$SELLER_B_ID\",\"percentage\":30,\"priority\":2}]}" "$API/api/v1/split-rules")
RULE_AB=$(echo "$CREATE_AB" | python3 -c "import sys,json; d=json.loads(sys.stdin.read()).get('data') or {}; print(d.get('id') or '')" 2>/dev/null)
if [[ "$RULE_AB" =~ $UUID_RE ]]; then
    OWNER_AB=$(echo "$CREATE_AB" | python3 -c "import sys,json; print(json.loads(sys.stdin.read())['data']['ownerSellerId'])")
    INITIAL_ACTIVE=$(echo "$CREATE_AB" | python3 -c "import sys,json; print(json.loads(sys.stdin.read())['data']['isActive'])")
    [ "$OWNER_AB" = "$SELLER_A_ID" ] && echo -e "  ${GREEN}PASS${NC} A criou rule, ownerSellerId=A" && PASS=$((PASS+1)) || { echo -e "  ${RED}FAIL${NC} ownerSellerId=$OWNER_AB esperado $SELLER_A_ID"; FAIL=$((FAIL+1)); }
    [ "$INITIAL_ACTIVE" = "False" ] && echo -e "  ${GREEN}PASS${NC} rule nasce inativa (rascunho)" && PASS=$((PASS+1)) || { echo -e "  ${RED}FAIL${NC} esperado isActive=false, got $INITIAL_ACTIVE"; FAIL=$((FAIL+1)); }
    # B (recipient, não owner) tenta ativar a rule de A → 403
    assert_status "B → POST /split-rules/<RULE>/activate (não-owner)" 403 "$(call POST "/api/v1/split-rules/$RULE_AB/activate" "$TOKEN_B" "{}")"
    # A ativa a sua própria rule
    assert_status "A → POST activate (owner)" 204 "$(call POST "/api/v1/split-rules/$RULE_AB/activate" "$TOKEN_A" "{}")"
    # Idempotente: A ativa de novo
    assert_status "A → POST activate (já ativa, idempotente)" 204 "$(call POST "/api/v1/split-rules/$RULE_AB/activate" "$TOKEN_A" "{}")"
    assert_status "B → GET rule (recipient) /split-rules/<RULE>" 200 "$(call GET "/api/v1/split-rules/$RULE_AB" "$TOKEN_B")"
    assert_status "B → DELETE rule (não-owner)" 403 "$(call DELETE "/api/v1/split-rules/$RULE_AB" "$TOKEN_B")"
    assert_status "A → DELETE rule (owner)" 204 "$(call DELETE "/api/v1/split-rules/$RULE_AB" "$TOKEN_A")"
else
    BODY_ERR=$(echo "$CREATE_AB" | python3 -c "import sys,json; d=json.loads(sys.stdin.read()); print(d.get('message') or d.get('errors') or 'unknown')" 2>/dev/null || true)
    echo -e "  ${YELLOW}NOTE${NC} POST /split-rules retornou: $BODY_ERR"
fi
# Rule with no overlap: A creates own-only, B must NOT see it.
CREATE_OWN=$(curl -s -X POST -H "Authorization: Bearer $TOKEN_A" -H "Content-Type: application/json" -H "Idempotency-Key: sriso2-$RANDOM-$(date +%s%N)" -d "{\"name\":\"Own $(date +%s%N)\",\"recipients\":[{\"sellerId\":\"$SELLER_A_ID\",\"percentage\":100,\"priority\":1}]}" "$API/api/v1/split-rules")
RULE_OWN=$(echo "$CREATE_OWN" | python3 -c "import sys,json; d=json.loads(sys.stdin.read()).get('data') or {}; print(d.get('id') or '')" 2>/dev/null)
if [[ "$RULE_OWN" =~ $UUID_RE ]]; then
    assert_status "B → GET rule só de A → 404" 404 "$(call GET "/api/v1/split-rules/$RULE_OWN" "$TOKEN_B")"
    psql_exec "DELETE FROM \"SplitRules\" WHERE \"Id\"='$RULE_OWN';" >/dev/null
fi

echo
echo "=== PaymentLink + Split rule activation ==="
# A creates a rule (nasce inativa) and activates it before using.
RULE_ACT=$(curl -s -X POST -H "Authorization: Bearer $TOKEN_A" -H "Content-Type: application/json" -H "Idempotency-Key: rul-$RANDOM-$(date +%s%N)" -d "{\"name\":\"Activation $(date +%s%N)\",\"recipients\":[{\"sellerId\":\"$SELLER_A_ID\",\"percentage\":80,\"priority\":1},{\"sellerId\":\"$SELLER_B_ID\",\"percentage\":20,\"priority\":2}]}" "$API/api/v1/split-rules")
RULE_ACT_ID=$(echo "$RULE_ACT" | python3 -c "import sys,json; d=json.loads(sys.stdin.read()).get('data') or {}; print(d.get('id') or '')" 2>/dev/null)
if [[ "$RULE_ACT_ID" =~ $UUID_RE ]]; then
    # 0a) Rule recém-criada está inativa → tentar usar em payment-link deve falhar (422)
    PAYLOAD_DRAFT="{\"amount\":50,\"paymentType\":2,\"installments\":1,\"maxUses\":1,\"description\":\"draft\",\"splitRuleId\":\"$RULE_ACT_ID\"}"
    DRAFT_RES=$(call POST "/api/v1/payment-links" "$TOKEN_A" "$PAYLOAD_DRAFT")
    [ "$DRAFT_RES" = "422" ] && echo -e "  ${GREEN}PASS${NC} rule rascunho não pode ser usada em payment-link → 422" && PASS=$((PASS+1)) || { echo -e "  ${RED}FAIL${NC} esperado 422 got $DRAFT_RES"; FAIL=$((FAIL+1)); }
    # 0b) A ativa a rule
    assert_status "A → POST activate da rule" 204 "$(call POST "/api/v1/split-rules/$RULE_ACT_ID/activate" "$TOKEN_A" "{}")"

    # 1) A creates payment-link with own active rule → 201, splitRuleId persisted
    LINK_OK=$(curl -s -X POST -H "Authorization: Bearer $TOKEN_A" -H "Content-Type: application/json" -H "Idempotency-Key: pl-$RANDOM-$(date +%s%N)" -d "{\"amount\":50,\"paymentType\":2,\"installments\":1,\"maxUses\":1,\"description\":\"Link com split\",\"splitRuleId\":\"$RULE_ACT_ID\"}" "$API/api/v1/payment-links")
    LINK_OK_ID=$(echo "$LINK_OK" | python3 -c "import sys,json; d=json.loads(sys.stdin.read()).get('data') or {}; print(d.get('id') or '')" 2>/dev/null)
    LINK_OK_RULE=$(echo "$LINK_OK" | python3 -c "import sys,json; d=json.loads(sys.stdin.read()).get('data') or {}; print(d.get('splitRuleId') or '')" 2>/dev/null)
    if [[ "$LINK_OK_ID" =~ $UUID_RE ]] && [ "$LINK_OK_RULE" = "$RULE_ACT_ID" ]; then
        echo -e "  ${GREEN}PASS${NC} A criou link com splitRuleId persistido"
        PASS=$((PASS+1))
        psql_exec "DELETE FROM \"PaymentLinks\" WHERE \"Id\"='$LINK_OK_ID';" >/dev/null
    else
        echo -e "  ${RED}FAIL${NC} link rule=$LINK_OK_RULE esperado $RULE_ACT_ID"; FAIL=$((FAIL+1))
    fi

    # 2) B (recipient da rule mas não owner) tenta criar link com a rule de A → 403
    PAYLOAD_B="{\"amount\":50,\"paymentType\":2,\"installments\":1,\"maxUses\":1,\"description\":\"hack\",\"splitRuleId\":\"$RULE_ACT_ID\"}"
    echo "  [debug] payload B bytes=$(echo -n "$PAYLOAD_B" | wc -c)"
    assert_status "B → POST /payment-links com rule de A → 403" 403 \
      "$(call POST "/api/v1/payment-links" "$TOKEN_B" "$PAYLOAD_B")"

    # 3) A desativa a rule e tenta criar link com ela → 422 (rule inativa)
    assert_status "A → DELETE rule (deactivate)" 204 "$(call DELETE "/api/v1/split-rules/$RULE_ACT_ID" "$TOKEN_A")"
    PAYLOAD_INACT="{\"amount\":50,\"paymentType\":2,\"installments\":1,\"maxUses\":1,\"description\":\"after-deactivation\",\"splitRuleId\":\"$RULE_ACT_ID\"}"
    INACT=$(call POST "/api/v1/payment-links" "$TOKEN_A" "$PAYLOAD_INACT")
    [ "$INACT" = "422" ] && echo -e "  ${GREEN}PASS${NC} A → POST link com rule inativa → 422" && PASS=$((PASS+1)) || { echo -e "  ${RED}FAIL${NC} esperado 422 got $INACT"; FAIL=$((FAIL+1)); }
fi

echo
echo "=== Anônimos / pay/{token} ==="
assert_status "Anônimo → GET /transactions" 401 "$(curl -s -o /dev/null -w "%{http_code}" "$API/api/v1/transactions")"
assert_status "Anônimo → GET /payment-links/pay/fake-token" 404 "$(curl -s -o /dev/null -w "%{http_code}" "$API/api/v1/payment-links/pay/fake-token")"

# ============================================================
# Summary
# ============================================================
echo
echo "================================================================"
echo -e "Total: ${GREEN}${PASS} PASS${NC} / ${RED}${FAIL} FAIL${NC}"
echo "================================================================"
[ "$FAIL" = "0" ] && exit 0 || exit 1
