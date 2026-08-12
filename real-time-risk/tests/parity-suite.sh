#!/usr/bin/env bash
# Shared, language-agnostic black-box parity suite for the Real-Time Risk slice (Trade Ledger + Risk
# Read Models). It speaks ONLY the HTTP contract - POST /trades on the ledger, GET
# /books/{book}/positions on the read model - and asserts the exact same observable behaviour, so it
# runs unchanged against ANY language's stack (or a deployed API). This is what turns "the same system
# in every language" from a claim into a check (root README / PARITY-FINDINGS roadmap item 5).
#
# Usage:
#   LEDGER_URL=http://localhost:8081 READMODEL_URL=http://localhost:8082 ./parity-suite.sh
# Defaults are the compose slice's mapped ports (8081 ledger, 8082 read model). Against a single
# deployed API Gateway, point both at the same base URL.
#
# Exit 0 = every language MUST behave this way; non-zero on the first divergence, with a diagnostic.
set -uo pipefail

LEDGER_URL="${LEDGER_URL:-http://localhost:8081}"
READMODEL_URL="${READMODEL_URL:-http://localhost:8082}"
# The read model is eventually consistent (CDC stream -> projection), so reads poll until caught up.
POLL_ATTEMPTS="${POLL_ATTEMPTS:-90}"
POLL_INTERVAL="${POLL_INTERVAL:-2}"

pass=0
fail() { echo "::error::PARITY FAIL: $*"; exit 1; }
ok()   { pass=$((pass + 1)); echo "  ✓ $*"; }

# book_trade <book> <symbol> <side> <qty> <price> -> echoes response JSON; asserts HTTP 2xx.
book_trade() {
  local book=$1 symbol=$2 side=$3 qty=$4 price=$5 body http status
  body=$(printf '{"book":"%s","symbol":"%s","side":"%s","quantity":%s,"price":%s}' \
    "$book" "$symbol" "$side" "$qty" "$price")
  http=$(curl -sS -w $'\n%{http_code}' -X POST "$LEDGER_URL/trades" \
    -H 'content-type: application/json' -d "$body")
  status=${http##*$'\n'}
  local json=${http%$'\n'*}
  [ "$status" -ge 200 ] && [ "$status" -lt 300 ] || fail "POST /trades ($side $qty $symbol) -> HTTP $status, body: $json"
  echo "$json"
}

# expect_version <json> <want> - assert the ledger response's version.
expect_version() {
  local got; got=$(echo "$1" | jq -r '.version')
  [ "$got" = "$2" ] || fail "expected ledger version $2, got $got (response: $1)"
}

# wait_projected <book> <version> - poll GET positions until projectedThroughVersion >= version.
# Echoes the final positions JSON.
wait_projected() {
  local book=$1 want=$2 i projected json
  for ((i = 1; i <= POLL_ATTEMPTS; i++)); do
    json=$(curl -fsS "$READMODEL_URL/books/$book/positions" 2>/dev/null) || { sleep "$POLL_INTERVAL"; continue; }
    projected=$(echo "$json" | jq -r '.projectedThroughVersion // 0')
    if [ "$projected" != "null" ] && [ "$projected" -ge "$want" ] 2>/dev/null; then
      echo "$json"; return 0
    fi
    sleep "$POLL_INTERVAL"
  done
  fail "read model never projected through version $want for book '$book' (last: ${json:-<none>})"
}

# assert_position <json> <symbol> <netQuantity> <realizedCash>
assert_position() {
  local json=$1 symbol=$2 wantQty=$3 wantCash=$4
  local row; row=$(echo "$json" | jq -c --arg s "$symbol" '.positions[] | select(.symbol == $s)')
  [ -n "$row" ] || fail "no position for $symbol in $json"
  echo "$row" | jq -e --argjson q "$wantQty" '.netQuantity == $q' >/dev/null \
    || fail "$symbol netQuantity != $wantQty (row: $row)"
  echo "$row" | jq -e --argjson c "$wantCash" '.realizedCash == $c' >/dev/null \
    || fail "$symbol realizedCash != $wantCash (row: $row)"
}

echo "Parity suite -> ledger=$LEDGER_URL readmodel=$READMODEL_URL"
# A unique book per run so repeated runs against a persistent deployment don't accumulate state.
BOOK="parity-${RANDOM}${RANDOM}"
OTHER="parity-other-${RANDOM}"

# 1. A fresh, never-traded book projects to an empty, zero-version view.
fresh=$(curl -fsS "$READMODEL_URL/books/$BOOK/positions") || fail "GET positions (fresh book) failed"
echo "$fresh" | jq -e '.projectedThroughVersion == 0 and (.positions | length) == 0' >/dev/null \
  || fail "fresh book not empty/zero: $fresh"
ok "fresh book is empty at version 0"

# 2. First trade on a fresh book is version 1 (event sourcing: one stream per book, 1-based).
r=$(book_trade "$BOOK" AAPL Buy 100 150.25); expect_version "$r" 1
ok "first trade -> version 1"

# 3. Projection reflects the buy: +100 shares, cash out 100*150.25 = -15025.
p=$(wait_projected "$BOOK" 1)
assert_position "$p" AAPL 100 -15025
ok "buy projected: AAPL +100, realizedCash -15025"

# 4. A sell on the same symbol nets the quantity down and brings cash in (proceeds add).
r=$(book_trade "$BOOK" AAPL Sell 40 160); expect_version "$r" 2
p=$(wait_projected "$BOOK" 2)
# netQuantity 100-40 = 60; realizedCash -15025 + 40*160(6400) = -8625.
assert_position "$p" AAPL 60 -8625
ok "sell projected: AAPL 60, realizedCash -8625"

# 5. A second symbol appears as its own row; rows are sorted by symbol (AAPL before MSFT).
r=$(book_trade "$BOOK" MSFT Buy 10 300); expect_version "$r" 3
p=$(wait_projected "$BOOK" 3)
assert_position "$p" MSFT 10 -3000
symbols=$(echo "$p" | jq -c '[.positions[].symbol]')
[ "$symbols" = '["AAPL","MSFT"]' ] || fail "positions not sorted by symbol: $symbols"
ok "second symbol MSFT +10 / -3000; rows sorted [AAPL,MSFT]"

# 6. Book isolation: a different book is unaffected by the first book's trades.
other=$(curl -fsS "$READMODEL_URL/books/$OTHER/positions")
echo "$other" | jq -e '(.positions | length) == 0' >/dev/null || fail "book isolation broken: $OTHER shows positions: $other"
ok "book isolation holds (other book still empty)"

# 7. Validation: a non-positive quantity is rejected (not booked). The exact 4xx varies by transport,
#    so assert "not 2xx" rather than a specific code.
status=$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$LEDGER_URL/trades" \
  -H 'content-type: application/json' -d "{\"book\":\"$BOOK\",\"symbol\":\"AAPL\",\"side\":\"Buy\",\"quantity\":0,\"price\":10}")
{ [ "$status" -ge 400 ] && [ "$status" -lt 500 ]; } || fail "invalid trade (quantity 0) not rejected: HTTP $status"
ok "invalid trade rejected (HTTP $status)"

# 8. The rejected trade did not advance the ledger: version stays 3.
p=$(curl -fsS "$READMODEL_URL/books/$BOOK/positions")
echo "$p" | jq -e '.projectedThroughVersion == 3' >/dev/null \
  || fail "rejected trade advanced the ledger (expected still 3): $p"
ok "rejected trade did not advance the ledger"

echo "PARITY OK - $pass checks passed against ledger=$LEDGER_URL readmodel=$READMODEL_URL"
