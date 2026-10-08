#!/usr/bin/env bash
# Only an owned, disposable localhost database is used; no application credentials.
set -euo pipefail
cd "$(dirname "$0")/.."
python3 -m unittest discover -s scripts -p 'test_player_export.py'
docker_local=(docker --host unix:///var/run/docker.sock)
image=bitnami/mariadb@sha256:25cedf501ccff9b99af390105e5316f4b40c6a51d80737d419bfd04559aa2c05
container=$("${docker_local[@]}" run --detach --pull=never --publish 127.0.0.1::3306 \
  --env MARIADB_ROOT_PASSWORD=local-privacy-test-only --env MARIADB_DATABASE=privacy_test \
  --env MARIADB_USER=privacy_test --env MARIADB_PASSWORD=local-privacy-test-only "$image")
trap '"${docker_local[@]}" rm --force --volumes "$container" >/dev/null' EXIT
ready=false
for attempt in {1..60}; do
  if "${docker_local[@]}" exec "$container" /opt/bitnami/mariadb/bin/mysql \
    --host=127.0.0.1 --user=privacy_test --password=local-privacy-test-only \
    --database=privacy_test --execute="SELECT 1" --batch --skip-column-names >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 1
done
if [[ "$ready" != true ]]; then
  echo 'Disposable MariaDB did not become ready within 60 seconds.' >&2
  "${docker_local[@]}" logs --tail 30 "$container" >&2
  exit 1
fi
port=$("${docker_local[@]}" inspect --format '{{(index (index .NetworkSettings.Ports "3306/tcp") 0).HostPort}}' "$container")
SKY_PRIVACY_TEST_DB="server=127.0.0.1;port=$port;user=privacy_test;password=local-privacy-test-only;database=privacy_test" \
  dotnet test --filter 'FullyQualifiedName~PrivacyExportTests|FullyQualifiedName~OptOutRequestsTests|FullyQualifiedName~PlayerOptOutRefresherTests|FullyQualifiedName~PermanentAnonymizationTests' --verbosity quiet
