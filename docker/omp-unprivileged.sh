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
# Do not let two retained workflows share that UID: owner-only permissions would otherwise fail
# to isolate their worktrees and attachments.
workflow_uid="$((200000 + $(printf '%s' "$workflow_id" | cksum | cut -d ' ' -f 1) % 2000000000))"
owner_uid="$(stat -c '%u' "$workflow_root")"
if [ "$owner_uid" -ne 10001 ] && [ "$owner_uid" -ne "$workflow_uid" ]; then
    echo "Workflow principal collision for '$workflow_id'." >&2
    exit 65
fi

for retained_workflow in "$(dirname "$workflow_root")"/*; do
    [ -d "$retained_workflow" ] || continue
    [ "$retained_workflow" = "$workflow_root" ] && continue
    retained_id="$(basename "$retained_workflow")"
    case "$retained_id" in
        *[!0123456789abcdef-]*|'')
            continue
            ;;
    esac
    if [ "$(stat -c '%u' "$retained_workflow")" -eq "$workflow_uid" ]; then
        echo "Workflow principal collision for '$workflow_id'." >&2
        exit 65
    fi
done

# OMP receives only its workflow's retained worktree and attachments. Remove the host group
# access granted while Git prepared the worktree before dropping all privilege.
chown --recursive "$workflow_uid:10001" "$workflow_root"
chmod --recursive u+rwX,go-rwx "$workflow_root"
umask 0077

exec /usr/bin/setpriv --reuid="$workflow_uid" --regid=10001 --clear-groups \
    --ambient-caps -chown,-dac_override,-fowner,-setuid,-setgid --no-new-privs -- /usr/local/bin/omp "$@"
