#!/bin/sh
set -eu

if [ -d /run/secrets-source ]; then
    mkdir -p /run/issue-agent-secrets
    for source in /run/secrets-source/*; do
        [ -f "$source" ] || continue
        install --mode=0400 "$source" "/run/issue-agent-secrets/$(basename "$source")"
    done
fi

# The host retains the provider credentials while OMP runs under a distinct UID.
exec /usr/bin/setpriv --regid=10001 --clear-groups --no-new-privs -- "$@"
