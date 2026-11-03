#!/usr/bin/env bash
set +e
state="${RUNNER_TEMP:-/tmp}/oec-server/private"
for name in tunnel server;do
 if [[ -s "$state/$name.pid" ]];then
  pid="$(cat "$state/$name.pid")"
  kill "$pid" 2>/dev/null
  wait "$pid" 2>/dev/null
 fi
done
rm -rf "${RUNNER_TEMP:-/tmp}/oec-server"
echo "RENDEZVOUS_SERVER_SHUTDOWN"
