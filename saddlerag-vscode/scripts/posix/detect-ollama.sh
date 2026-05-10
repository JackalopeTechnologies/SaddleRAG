#!/usr/bin/env bash
# detect-ollama.sh — exit 0=running, 1=installed-not-running, 2=not-found

if curl -sf http://localhost:11434 >/dev/null 2>&1; then
    echo '{"status":"running","port":11434}'
    exit 0
fi

OLLAMA=$(command -v ollama 2>/dev/null)
if [ -n "$OLLAMA" ]; then
    echo "{\"status\":\"stopped\",\"path\":\"$OLLAMA\"}"
    exit 1
fi

echo '{"status":"not-found"}'
exit 2
