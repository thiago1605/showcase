#!/usr/bin/env bash
#
# Smoke test E2E da integração Woovi/OpenPix (sandbox).
#
# Valida a cadeia inteira:
#   1. Auth — o AppID é aceito
#   2. Feature Subconta — POST /api/v1/subaccount cria subconta nominal
#   3. Listagem de subcontas
#   4. Charge PIX simples (sem split)
#   5. Charge PIX com split pra subconta criada
#   6. Listagem de charges
#
# Os passos 5/6 só passam se a feature Split estiver ativada na conta.
# Quando não estiver, o script reporta claramente (gate comercial Woovi).
#
# Uso:
#   OPENPIX_APPID="<seu-appid-sandbox>" ./scripts/woovi-smoke-test.sh
#
# Opcional:
#   OPENPIX_BASE_URL  (default https://api.woovi-sandbox.com)
#   SELLER_PIX_KEY    (default gera um email fake determinístico)

set -uo pipefail

BASE_URL="${OPENPIX_BASE_URL:-https://api.woovi-sandbox.com}"
APPID="${OPENPIX_APPID:-}"
SELLER_PIX_KEY="${SELLER_PIX_KEY:-seller-smoke-$(date +%s)@fellow.test}"

if [[ -z "$APPID" ]]; then
  echo "❌ OPENPIX_APPID não setado. Uso: OPENPIX_APPID=<chave> $0"
  exit 1
fi

pass() { echo "✅ $1"; }
fail() { echo "❌ $1"; }
info() { echo "ℹ️  $1"; }

BODY_FILE=/tmp/woovi_resp.json

# req METHOD PATH [JSON_BODY]
# Escreve o corpo da resposta em $BODY_FILE e ecoa o HTTP code no stdout.
# (Não usa variável global — o caller captura via `CODE=$(req ...)` e lê $BODY_FILE.)
req() {
  local method="$1" path="$2" body="${3:-}"
  local args=(-s -o "$BODY_FILE" -w "%{http_code}"
    -X "$method" "${BASE_URL}${path}"
    -H "Authorization: ${APPID}"
    -H "Content-Type: application/json")
  [[ -n "$body" ]] && args+=(-d "$body")
  curl "${args[@]}"
}

ok_code() { [[ "$1" == "200" || "$1" == "201" ]]; }

echo "════════════════════════════════════════════════"
echo " Woovi smoke test — base=$BASE_URL"
echo "════════════════════════════════════════════════"

# ── 1. Auth ─────────────────────────────────────────────────────────
info "1) Auth check — GET /api/v1/charge?limit=1"
CODE=$(req GET "/api/v1/charge?limit=1")
if [[ "$CODE" == "200" ]]; then
  pass "Auth OK (AppID aceito)"
else
  fail "Auth FALHOU (HTTP $CODE) — $(cat "$BODY_FILE")"
  exit 1
fi
echo ""

# ── 2. Criar subconta ───────────────────────────────────────────────
info "2) Criar subconta — POST /api/v1/subaccount (pixKey=$SELLER_PIX_KEY)"
CODE=$(req POST "/api/v1/subaccount" "{\"name\":\"Seller Smoke\",\"pixKey\":\"${SELLER_PIX_KEY}\"}")
SUBACCOUNT_OK=0
if ok_code "$CODE"; then
  pass "Subconta criada — feature Subconta ATIVADA"
  SUBACCOUNT_OK=1
else
  fail "Subconta FALHOU (HTTP $CODE) — $(cat "$BODY_FILE")"
  info "Provável: feature 'Subconta' não ativada. Solicitar ao suporte (Morgana/Iago)."
fi
echo ""

# ── 3. Listar subcontas ─────────────────────────────────────────────
if [[ "$SUBACCOUNT_OK" == "1" ]]; then
  info "3) Listar subcontas — GET /api/v1/subaccount"
  CODE=$(req GET "/api/v1/subaccount")
  ok_code "$CODE" && pass "Listagem OK" || fail "Listagem FALHOU (HTTP $CODE)"
  echo ""
fi

# ── 4. Charge PIX simples ───────────────────────────────────────────
CORR="smoke-simple-$(date +%s)"
info "4) Charge PIX simples — POST /api/v1/charge (R\$ 5,00)"
CODE=$(req POST "/api/v1/charge" "{\"correlationID\":\"${CORR}\",\"value\":500,\"comment\":\"Smoke simples\"}")
if ok_code "$CODE"; then
  pass "Charge simples criada"
  python3 -c "import json; d=json.load(open('$BODY_FILE')); c=d.get('charge',{}); print('   brCode:', (c.get('brCode') or '')[:50], '...'); print('   status:', c.get('status'))" 2>/dev/null || true
else
  fail "Charge simples FALHOU (HTTP $CODE) — $(cat "$BODY_FILE")"
fi
echo ""

# ── 5. Charge PIX com split ─────────────────────────────────────────
if [[ "$SUBACCOUNT_OK" == "1" ]]; then
  CORR_SPLIT="smoke-split-$(date +%s)"
  info "5) Charge PIX com split — R\$ 10,00, split R\$ 8,00 pra subconta"
  CODE=$(req POST "/api/v1/charge" "{\"correlationID\":\"${CORR_SPLIT}\",\"value\":1000,\"comment\":\"Smoke split\",\"splits\":[{\"pixKey\":\"${SELLER_PIX_KEY}\",\"value\":800,\"splitType\":\"SPLIT_SUB_ACCOUNT\"}]}")
  if ok_code "$CODE"; then
    pass "Charge com split criada — feature Split ATIVADA"
  else
    fail "Charge com split FALHOU (HTTP $CODE) — $(cat "$BODY_FILE")"
    info "Provável: feature 'Split' não ativada. Solicitar ao suporte."
  fi
  echo ""
fi

# ── 6. Listar charges ───────────────────────────────────────────────
info "6) Listar charges — GET /api/v1/charge?limit=5"
CODE=$(req GET "/api/v1/charge?limit=5")
if [[ "$CODE" == "200" ]]; then
  CNT=$(python3 -c "import json; print(len(json.load(open('$BODY_FILE')).get('charges',[])))" 2>/dev/null || echo "?")
  pass "Listagem OK — $CNT charge(s)"
else
  fail "Listagem FALHOU (HTTP $CODE)"
fi

echo ""
echo "════════════════════════════════════════════════"
echo " Smoke test concluído. Veja ✅/❌ acima."
echo "════════════════════════════════════════════════"
