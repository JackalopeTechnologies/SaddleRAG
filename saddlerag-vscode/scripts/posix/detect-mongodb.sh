#!/usr/bin/env bash
# detect-mongodb.sh — exit 0=running, 1=installed-not-running, 2=not-found

if mongosh --eval "db.adminCommand('ping')" --quiet >/dev/null 2>&1; then
    echo '{"status":"running","port":27017}'
    exit 0
fi

MONGOD=$(command -v mongod 2>/dev/null)
if [ -n "$MONGOD" ]; then
    echo "{\"status\":\"stopped\",\"path\":\"$MONGOD\"}"
    exit 1
fi

echo '{"status":"not-found"}'
exit 2
