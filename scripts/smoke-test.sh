#!/usr/bin/env bash
# smoke-test.sh — validates a published SaddleRAG.Mcp binary
# Usage: ./scripts/smoke-test.sh [binary-path] [port]
set -euo pipefail

BINARY="${1:-./SaddleRAG.Mcp}"
PORT="${2:-6100}"
TIMEOUT_SECS=60

echo "Starting $BINARY on port $PORT..."
"$BINARY" &
SERVER_PID=$!

cleanup() { kill "$SERVER_PID" 2>/dev/null || true; }
trap cleanup EXIT

echo "Waiting for /health (timeout ${TIMEOUT_SECS}s)..."
for i in $(seq 1 "$TIMEOUT_SECS"); do
    if curl -sf "http://localhost:$PORT/health" >/dev/null 2>&1; then
        echo "  /health OK (after ${i}s)"
        break
    fi
    sleep 1
    if [ "$i" -eq "$TIMEOUT_SECS" ]; then
        echo "FAIL: /health timed out after ${TIMEOUT_SECS}s"
        exit 1
    fi
done

echo "Checking /mcp..."
curl -sf "http://localhost:$PORT/mcp" >/dev/null || { echo "FAIL: /mcp not responding"; exit 1; }
echo "  /mcp OK"

echo "Checking /api/status..."
STATUS=$(curl -sf "http://localhost:$PORT/api/status")
echo "$STATUS" | grep -q '"libraries"' || { echo "FAIL: /api/status missing 'libraries' key"; exit 1; }
echo "  /api/status OK"

echo "PASS: smoke test completed"
