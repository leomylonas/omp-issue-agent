#!/bin/sh
set -eu

exec /usr/bin/setpriv --reuid=10002 --regid=10001 --clear-groups --no-new-privs -- /usr/local/bin/omp "$@"
