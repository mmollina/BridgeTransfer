#!/usr/bin/env bash
# Stops the isolated test sshd instances started by start-sftp-fixture.sh.
set -uo pipefail
RUN="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/.run"
for pid in "$RUN"/sshd-*.pid; do
  [[ -f "$pid" ]] && kill "$(cat "$pid")" 2>/dev/null && echo "stopped $(cat "$pid")"
done
exit 0
