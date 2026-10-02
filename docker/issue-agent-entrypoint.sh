#!/bin/sh
set -eu

umask 0077

# A mounted volume replaces the image-owned /data. Only a root-started container
# can repair an existing volume and copy root-owned secret sources.
if [ "$(id -u)" -eq 0 ]; then
    mkdir -p /data/omp/agent
    # OMP owns 0600 native state beneath /data/omp; repair only shared directories.
    chown 10001:10001 /data /data/omp /data/omp/agent
    chmod 2770 /data /data/omp /data/omp/agent

    if [ -d /run/secrets-source ]; then
        mkdir -p /run/issue-agent-secrets
        for source in /run/secrets-source/*; do
            [ -f "$source" ] || continue
            install --owner=10001 --group=10001 --mode=0400 "$source" "/run/issue-agent-secrets/$(basename "$source")"
        done
    fi
fi

# The host retains provider credentials. Each OMP process derives a distinct workflow UID in its
# wrapper; retain only the capabilities needed to assign that principal and its filesystem owner.
umask 0002

exec /usr/bin/setpriv --reuid=10001 --regid=10001 --clear-groups \
    --inh-caps +chown,+dac_override,+fowner,+setuid,+setgid \
    --ambient-caps +chown,+dac_override,+fowner,+setuid,+setgid --no-new-privs -- "$@"
