#!/bin/sh
set -eu

umask 0002

if [ -d /run/secrets-source ]; then
    mkdir -p /run/issue-agent-secrets
    for source in /run/secrets-source/*; do
        [ -f "$source" ] || continue
        install --owner=10001 --group=10001 --mode=0400 "$source" "/run/issue-agent-secrets/$(basename "$source")"
    done
fi

# The host retains provider credentials while OMP uses a different UID. Both processes
# share a group and umask so OMP can edit worktrees without exposing owner-only secrets.
exec /usr/bin/setpriv --reuid=10001 --regid=10001 --clear-groups --no-new-privs -- "$@"
