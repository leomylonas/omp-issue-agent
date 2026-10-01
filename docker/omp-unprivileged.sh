#!/bin/sh
set -eu

workflow_root="$(dirname "$PWD")"
workflow_id="$(basename "$workflow_root")"
case "$workflow_id" in
    *[!0123456789abcdef-]*|'')
        echo "OMP must start in a UUID-named workflow worktree." >&2
        exit 64
        ;;
esac

# A stable, high-range UID preserves a workflow's durable OMP session access after a restart.
# The ownership check fails closed on a hash collision rather than sharing a principal.
workflow_uid="$((200000 + $(printf '%s' "$workflow_id" | cksum | cut -d ' ' -f 1) % 2000000000))"
owner_uid="$(stat -c '%u' "$workflow_root")"
if [ "$owner_uid" -ne 10001 ] && [ "$owner_uid" -ne "$workflow_uid" ]; then
    echo "Workflow principal collision for '$workflow_id'." >&2
    exit 65
fi

# OMP receives only its workflow's retained worktree and attachments. Remove the host group
# access granted while Git prepared the worktree before dropping all privilege.
chown --recursive "$workflow_uid:10001" "$workflow_root"
chmod --recursive u+rwX,go-rwx "$workflow_root"
umask 0077

exec /usr/bin/setpriv --reuid="$workflow_uid" --regid=10001 --clear-groups \
    --ambient-caps -chown,-fowner,-setuid,-setgid --no-new-privs -- /usr/local/bin/omp "$@"
