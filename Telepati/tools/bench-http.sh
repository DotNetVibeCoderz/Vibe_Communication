#!/usr/bin/env bash
# End-to-end HTTP timings against a running Telepati server.
# Run the server first, then: bash tools/bench-http.sh
set -u

BASE="${TELEPATI_URL:-https://localhost:7180}"
RUNS="${RUNS:-20}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "Telepati — HTTP benchmark"
echo "Target: $BASE - $RUNS runs per endpoint"
echo "=============================================================================="

TOKEN=$(curl -sk -m 20 -X POST "$BASE/api/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"usernameOrEmail":"kangfadhil","password":"Telepati123!","deviceName":"bench","deviceType":"cli"}' \
  | python -c "import sys,json; print(json.load(sys.stdin)['accessToken'])")

CHAT=$(curl -sk -m 20 "$BASE/api/chats?pageSize=30" -H "Authorization: Bearer $TOKEN" \
  | python -c "import sys,json; d=json.load(sys.stdin); print(next(c['id'] for c in d['items'] if c['type'] in (0,1)))")

# Measures total wall time for one request, repeated, and reports mean/p50/p95.
measure() {
  local label="$1"; shift
  local samples="$TMP/samples.txt"
  : > "$samples"

  for _ in $(seq 1 "$RUNS"); do
    curl -sk -o /dev/null -w "%{time_total}\n" "$@" >> "$samples"
  done

  python - "$label" "$samples" <<'PY'
import sys
label, path = sys.argv[1], sys.argv[2]
values = sorted(float(line) * 1000 for line in open(path) if line.strip())
mean = sum(values) / len(values)
p50 = values[len(values) // 2]
p95 = values[int(len(values) * 0.95) - 1]
print(f"  {label:<44} mean {mean:7.2f} ms | p50 {p50:7.2f} ms | p95 {p95:7.2f} ms")
PY
}

# Reports the transferred size with and without compression negotiated.
compare_size() {
  local label="$1"; local url="$2"
  local plain compressed
  plain=$(curl -sk -o /dev/null -w "%{size_download}" -H "Accept-Encoding: identity" -H "Authorization: Bearer $TOKEN" "$url")
  compressed=$(curl -sk -o /dev/null -w "%{size_download}" -H "Accept-Encoding: gzip, br" -H "Authorization: Bearer $TOKEN" "$url")

  python - "$label" "$plain" "$compressed" <<'PY'
import sys
label, plain, compressed = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
saving = 0 if plain == 0 else (1 - compressed / plain) * 100
print(f"  {label:<44} {plain:8,d} B -> {compressed:8,d} B  ({saving:.1f}% smaller)")
PY
}

echo
echo "Latency"
echo "------------------------------------------------------------------------------"
measure "GET /api/health" "$BASE/api/health"
measure "GET /api/theme/active (output cached)" "$BASE/api/theme/active"
measure "GET /api/chats?pageSize=30" -H "Authorization: Bearer $TOKEN" "$BASE/api/chats?pageSize=30"
measure "GET /api/messages/{chat}?pageSize=50" -H "Authorization: Bearer $TOKEN" "$BASE/api/messages/$CHAT?pageSize=50"
measure "GET /api/admin/dashboard" -H "Authorization: Bearer $TOKEN" "$BASE/api/admin/dashboard"

echo
echo "Response compression"
echo "------------------------------------------------------------------------------"
compare_size "GET /api/chats?pageSize=30" "$BASE/api/chats?pageSize=30"
compare_size "GET /api/messages/{chat}?pageSize=50" "$BASE/api/messages/$CHAT?pageSize=50"
compare_size "GET /swagger/v1/swagger.json" "$BASE/swagger/v1/swagger.json"

echo "=============================================================================="
