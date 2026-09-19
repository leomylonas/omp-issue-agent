#!/bin/sh
set -eu

umask 0002

exec /usr/bin/setpriv --reuid=10002 --regid=10001 --clear-groups --no-new-privs -- /usr/local/bin/omp "$@"
