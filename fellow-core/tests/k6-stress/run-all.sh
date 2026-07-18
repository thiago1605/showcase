#!/usr/bin/env bash
# =============================================================================
# FellowPay Stress Test Runner
# =============================================================================
# Usage: ./run-all.sh [BASE_URL] [API_KEY] [SELLER_ID]
# =============================================================================
set -euo pipefail

BASE_URL="${1:-http://localhost:5195}"
API_KEY="${2:?Set API_KEY as second argument}"
SELLER_ID="${3:-}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULTS_DIR="${SCRIPT_DIR}/results"
mkdir -p "$RESULTS_DIR"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

echo -e "${CYAN}============================================================${NC}"
echo -e "${CYAN}  FellowPay Stripe-Level Stress Test Suite${NC}"
echo -e "${CYAN}============================================================${NC}"
echo ""
echo "  Base URL:   $BASE_URL"
echo "  API Key:    ${API_KEY:0:20}..."
echo ""

# --- Step 0: Resolve SELLER_ID if not provided ---
if [ -z "$SELLER_ID" ]; then
    echo -e "${YELLOW}[SETUP] Resolving seller ID from API...${NC}"
    SELLER_RESPONSE=$(curl -s -w "\n%{http_code}" -H "X-Api-Key: $API_KEY" "$BASE_URL/api/v1/sellers?page=1&pageSize=1")
    HTTP_CODE=$(echo "$SELLER_RESPONSE" | tail -1)
    BODY=$(echo "$SELLER_RESPONSE" | head -n -1)

    if [ "$HTTP_CODE" = "200" ]; then
        SELLER_ID=$(echo "$BODY" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['data']['items'][0]['id'])" 2>/dev/null || true)
        if [ -n "$SELLER_ID" ]; then
            echo -e "${GREEN}[SETUP] Resolved seller ID: $SELLER_ID${NC}"
        else
            echo -e "${RED}[ERROR] Could not extract seller ID from response${NC}"
            echo "$BODY"
            exit 1
        fi
    else
        echo -e "${RED}[ERROR] Failed to fetch sellers (HTTP $HTTP_CODE)${NC}"
        echo "$BODY"
        exit 1
    fi
fi

# --- Step 0b: Check initial balance ---
echo ""
echo -e "${YELLOW}[SETUP] Checking initial balance...${NC}"
BALANCE=$(curl -s -H "X-Api-Key: $API_KEY" "$BASE_URL/api/v1/sellers/$SELLER_ID/balance")
echo "  Initial balance: $BALANCE"

# Export env vars for k6
export BASE_URL API_KEY SELLER_ID

PASS_COUNT=0
FAIL_COUNT=0
TOTAL=5

run_scenario() {
    local NUM=$1
    local NAME=$2
    local FILE=$3

    echo ""
    echo -e "${CYAN}============================================================${NC}"
    echo -e "${CYAN}  [$NUM/$TOTAL] $NAME${NC}"
    echo -e "${CYAN}============================================================${NC}"

    if k6 run --quiet "$SCRIPT_DIR/$FILE" 2>&1; then
        echo -e "${GREEN}  [COMPLETED]${NC}"
    else
        echo -e "${YELLOW}  [COMPLETED WITH WARNINGS]${NC}"
    fi

    # Check result file
    local RESULT_FILE="$RESULTS_DIR/$(echo $FILE | sed 's/.js/-result.json/' | sed 's/^[0-9]*-//')"
    local ACTUAL_FILE=$(ls "$RESULTS_DIR/"*"$(echo $FILE | sed 's/.js//' | sed 's/^[0-9]*-//')"* 2>/dev/null | head -1 || true)

    if [ -f "$ACTUAL_FILE" ] 2>/dev/null; then
        local VERDICT=$(python3 -c "import json; d=json.load(open('$ACTUAL_FILE')); print(d.get('verdict','UNKNOWN'))" 2>/dev/null || echo "UNKNOWN")
        if echo "$VERDICT" | grep -qi "PASS"; then
            echo -e "${GREEN}  VERDICT: $VERDICT${NC}"
            PASS_COUNT=$((PASS_COUNT + 1))
        else
            echo -e "${RED}  VERDICT: $VERDICT${NC}"
            FAIL_COUNT=$((FAIL_COUNT + 1))
        fi
    fi

    # Brief pause between scenarios
    sleep 3
}

# --- Run all scenarios ---
run_scenario 1 "Mixed Concurrency Storm" "01-mixed-concurrency-storm.js"
run_scenario 2 "Double-Spend Attack" "02-double-spend-attack.js"
run_scenario 3 "Webhook Duplication Storm" "03-webhook-duplication-storm.js"
run_scenario 4 "Provider Instability" "04-provider-instability.js"
run_scenario 5 "Database Contention" "05-database-contention.js"

# --- Final financial validation ---
echo ""
echo -e "${CYAN}============================================================${NC}"
echo -e "${CYAN}  FINAL FINANCIAL VALIDATION${NC}"
echo -e "${CYAN}============================================================${NC}"
echo ""

FINAL_BALANCE=$(curl -s -H "X-Api-Key: $API_KEY" "$BASE_URL/api/v1/sellers/$SELLER_ID/balance")
echo "  Final balance: $FINAL_BALANCE"

# Check for negative balances
AVAILABLE=$(echo "$FINAL_BALANCE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('data',{}).get('available',0))" 2>/dev/null || echo "ERROR")
TOTAL_BAL=$(echo "$FINAL_BALANCE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('data',{}).get('total',0))" 2>/dev/null || echo "ERROR")

echo ""
if [ "$AVAILABLE" != "ERROR" ] && [ "$(python3 -c "print(1 if float('$AVAILABLE') >= 0 else 0)")" = "1" ]; then
    echo -e "${GREEN}  Available balance: $AVAILABLE (non-negative)${NC}"
else
    echo -e "${RED}  Available balance: $AVAILABLE (NEGATIVE — CRITICAL FAILURE)${NC}"
    FAIL_COUNT=$((FAIL_COUNT + 1))
fi

if [ "$TOTAL_BAL" != "ERROR" ] && [ "$(python3 -c "print(1 if float('$TOTAL_BAL') >= 0 else 0)")" = "1" ]; then
    echo -e "${GREEN}  Total balance: $TOTAL_BAL (non-negative)${NC}"
else
    echo -e "${RED}  Total balance: $TOTAL_BAL (NEGATIVE — CRITICAL FAILURE)${NC}"
    FAIL_COUNT=$((FAIL_COUNT + 1))
fi

# --- Final Verdict ---
echo ""
echo -e "${CYAN}============================================================${NC}"
echo -e "${CYAN}  FINAL VERDICT${NC}"
echo -e "${CYAN}============================================================${NC}"
echo ""
echo "  Scenarios passed: $PASS_COUNT / $TOTAL"
echo "  Scenarios failed: $FAIL_COUNT"
echo ""

if [ "$FAIL_COUNT" -eq 0 ]; then
    echo -e "${GREEN}  ██████████████████████████████████████████████████${NC}"
    echo -e "${GREEN}  ██                                              ██${NC}"
    echo -e "${GREEN}  ██        SAFE FOR PRODUCTION                   ██${NC}"
    echo -e "${GREEN}  ██                                              ██${NC}"
    echo -e "${GREEN}  ██████████████████████████████████████████████████${NC}"
else
    echo -e "${RED}  ██████████████████████████████████████████████████${NC}"
    echo -e "${RED}  ██                                              ██${NC}"
    echo -e "${RED}  ██        NOT SAFE FOR PRODUCTION               ██${NC}"
    echo -e "${RED}  ██                                              ██${NC}"
    echo -e "${RED}  ██████████████████████████████████████████████████${NC}"
fi
echo ""
