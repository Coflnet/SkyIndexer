# SkyIndexer startup anonymization regression

Date: 2026-07-31 to 2026-08-01

Impact: SkyIndexer was unavailable for approximately 10 hours without a notification.

## Summary

Commit `2dcf5de` added a synchronous historical anonymization pass to the fatal startup path even though the protected UUID's stored data had already been anonymized. The query combined indexed numeric player IDs with unindexed raw UUID columns across the auction and bid tables. MySQL timed out after 60 seconds, the exception escaped `Main`, and Kubernetes repeatedly restarted the process.

Commit `d504b38` changed the query to indexed columns, but the historical pass was still unnecessary startup work. Commit `29670a1` removed it. The required permanent behavior remains at ingestion and name-update boundaries, preventing new auction, bid, or name data for the protected UUID from being stored.

## Detection gap

The EU cluster's `KubernetesPodCrashLooping` rule used `increase(kube_pod_container_status_restarts_total[1m]) > 3` with `for: 2m`. Kubernetes restart backoff makes that threshold unlikely to remain true for two consecutive minutes. A CrashLoopBackOff pod can also retain phase `Running`, so `KubernetesPodNotHealthy` does not cover this state.

On 2026-08-01, the live talos-eu Alertmanager routing tree was also verified to contain only the `null` receiver. Even a firing rule therefore had no notification destination.

During verification on 2026-08-01, the replacement expression directly matched the live `sky/sky-indexer` pod in `CrashLoopBackOff`. This proved the metric and expression covered the incident state; notification delivery was still an unresolved configuration gap on that date.

## Corrective actions

- Removed the historical database scrub from application startup.
- Kept anonymization at the Kafka/import ingestion and name-update boundaries.
- Replaced the restart-rate alert with a rule based on the persistent `CrashLoopBackOff` waiting reason.
- Configure and test a real talos-eu Alertmanager notification receiver; a firing rule alone is not sufficient detection.
- Do not add unbounded data repair or migration work to the fatal startup path. Run one-time repairs as explicit, observable operations. If nonessential startup work is unavoidable, isolate failures with structured error logging and allow the service to start.
