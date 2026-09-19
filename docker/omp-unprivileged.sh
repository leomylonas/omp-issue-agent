#!/bin/sh
set -eu

umask 0002

exec /usr/bin/setpriv --reuid=10002 --regid=10001 --clear-groups \
    --ambient-caps -setuid,-setgid --no-new-privs -- /usr/local/bin/omp "$@"
