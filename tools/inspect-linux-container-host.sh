#!/usr/bin/env bash
# Read-only OCI capability census for TrueNAS SCALE Apps or ordinary Linux.
# Does not create containers, mutate daemon, or inspect existing workload names.
set -Eeuo pipefail
present=false
available=false
daemon_os=unknown
daemon_arch=unknown
version=unknown
cpu_count=0
memory_gib=0
tmp_free_gib=0
if command -v docker >/dev/null 2>&1; then
  present=true
  if info="$(docker info --format '{{.OSType}} {{.Architecture}} {{.ServerVersion}} {{.NCPU}} {{.MemTotal}}' 2>/dev/null)"; then
    read -r daemon_os daemon_arch version cpu_count memory_bytes <<< "$info"
    available=true
    memory_gib="$(awk -v bytes="$memory_bytes" 'BEGIN{printf "%.2f", bytes/1073741824}')"
  fi
fi
free_kib="$(df -Pk /tmp | awk 'END{print $4}')"
if [[ "$free_kib" =~ ^[0-9]+$ ]]; then
  tmp_free_gib="$(awk -v kb="$free_kib" 'BEGIN{printf "%.2f", kb/1048576}')"
fi
[[ "$cpu_count" =~ ^[0-9]+$ ]] || cpu_count=0
printf '{\n'
printf '  "schema":"suprachat-linux-container-host-census/v1",\n'
printf '  "observed_read_only":true,\n'
printf '  "docker_cli_present":%s,\n' "$present"
printf '  "daemon_reachable":%s,\n' "$available"
printf '  "daemon_os":"%s",\n' "$daemon_os"
printf '  "daemon_arch":"%s",\n' "$daemon_arch"
printf '  "docker_version":"%s",\n' "$version"
printf '  "logical_cpus":%d,\n' "$cpu_count"
printf '  "daemon_memory_gib":%s,\n' "$memory_gib"
printf '  "tmp_free_gib":%s,\n' "$tmp_free_gib"
printf '  "private_source_accepted":false,\n'
printf '  "heavyweight_execution_admitted":false\n'
printf '}\n'
