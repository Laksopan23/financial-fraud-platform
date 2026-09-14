# Operations and release runbook

## Development startup

Run the three commands in README in order. Credentials and realm data are generated once. The realm import only applies to a new realm; editing the JSON later does not mutate an existing Keycloak realm. Database initialization scripts similarly run only on a fresh PostgreSQL data volume. Never replace `.env` while keeping old encrypted data unless performing an intentional, tested key/credential migration.

Useful read-only diagnostics:

```bash
docker compose ps
docker compose logs --tail 100 ingestion detection audit dashboard
docker compose logs --tail 100 model-init keycloak
```

The platform's logger intentionally omits exception messages. Numeric event codes 1101/1102 identify request completion/failure, 1201 vault cleanup failure and 2101/2102 outbox cycle/delivery failure. Check dependency health and authenticated operator tooling; do not enable raw payload logging to investigate card-data failures.

## Failure recovery

| Failure | Expected behavior | Recovery |
| --- | --- | --- |
| Broker temporarily unavailable | Accepted observations retain pending outbox envelopes | Restore the broker; dispatchers retry |
| Outbox process dies after publish | Lease expires; a duplicate may be published | Consumer receipt prevents duplicate durable effect |
| Redis unavailable | No score is emitted; retries/circuit breaker apply | Restore known-good feature state before resuming |
| Detection/model fails | No Low fallback; message eventually goes to the error queue | Repair or roll back the model/dependency, then replay under the correct feature horizon |
| Audit is unavailable | Scores remain pending in broker/outbox; analyst alert waits for audit | Restore audit before enabling ordinary redelivery |
| Analyst disconnects | Some live notifications may be missed | Reconnect; load the persisted read model |
| PostgreSQL cannot commit | Request is not acknowledged as accepted; consumer is not successfully acknowledged | Retry with original IDs after recovery |
| Integrity verification fails | Verifier reports false | Preserve evidence and isolate writers while investigating |

Inspect MassTransit `_error` queues in RabbitMQ management. Validation/conflict messages require review, not blind retries. After a dependency is restored, replay the original messages with their original IDs and original payloads. Back up the queue before administrative movement and use an acknowledged broker-native transfer; do not implement a lossy read-delete-publish script. Replays older than six hours require an offline reconstruction, not current Redis windows.

The source has one score per tenant/transaction. A future rescore must be modeled as a new decision version rather than silently reusing the current transaction identity and overwriting evidence.

## Retention and backup

The private card vault deletes rows after its 24-hour development lifetime, on a five-minute timer. This does not remove copies in backups or WAL. Redis TTLs are described in the ML spec. Idempotency receipts, decisions, ledger entries, checkpoints and delivered outbox envelopes are retained in the reference. Establish reviewed archive/retention jobs for real deployment; monitor storage rather than silently deleting financial evidence.

Back up each service database, the model plus manifest, and required key versions. Test restoration with the original keys and verify ledger chains against an independently held checkpoint. Restoring only a database or only Redis can change the meaning of future feature calculations. Treat feature-store loss as a controlled scoring outage until history is reconciled.

## Model promotion

Train into a new directory. Preserve dataset provenance and evaluation artifacts. Verify parity, fraud operating point and target-hardware latency. Configure the expected manifest digest and read-only mount, then deploy a new worker instance. Do not treat a synthetic label generator as a validated financial dataset. No model is promoted or retrained automatically by this repository.

## Production release gates

Before any real financial data is introduced:

1. Complete package restore, compilation, unit/integration tests, model export/parity and the end-to-end smoke test. Resolve every failure; the authoring environment could not execute those gates.
2. Run package and container vulnerability scans, review licenses, record an SBOM, pin deployable images by digest and commit the generated NuGet lockfiles. Direct package versions are centralized; transitive restore results are not pre-established in this archive. Run subsequent release restores in locked mode.
3. Migrate from .NET 9 before its support ends. Central package versions and Docker base versions must move together after compatibility tests.
4. Provision production OIDC clients, HTTPS issuer/callbacks, service secrets, short-lived broker credentials, certificate validation, network policy and key custody. Use managed database credentials that cannot perform schema changes or disable integrity guards.
5. Create/review schema changes in a controlled migration job. The development profile permits Marten schema creation; production uses AutoCreate.None. Install event mutation guards using the migration identity, then revoke app DDL permissions. Do not switch a production service back to Development to bypass this gate.
6. Establish independently controlled audit checkpoints and immutable retention; define legal retention, data-subject handling, incident response, access reviews and restore exercises with the appropriate owners.
7. Approve real training data, thresholds, calibration and human-review procedures. Validate false-positive cost, label delay, feature completeness and model changes.
8. Establish traffic, p99, throughput, recovery-time and recovery-point requirements, then test on intended hardware with injected broker/Redis/SQL/process failures. The reference is not capacity qualified.

No external service is deployed or published by delivering these source files. CI is supplied as code and has not been run or enabled in an external repository.

## Performance and scale limits

The outbox implementation claims and publishes entries sequentially in batches of up to 32 per dispatcher. Leases allow multiple replicas, but per-message database work and lease contention must be measured. A 10 ms idle poll is a responsiveness choice, not proof of a sub-50 ms end-to-end SLA. Scale only after identifying the bottleneck.

The per-process tenant limiter, shared local broker account, single database server, single Redis instance, transient Tempo storage and single dashboard replica are explicit development limits. Sticky sessions, a SignalR scale-out design, broker quorum queues and replicated stores require an actual production topology. Do not claim high availability from a single-node Compose file.

## Upgrading from the initial generated package

New installations need no data conversion. Existing stacks require a coordinated update because older ingestion instances do not understand version 2 fingerprints.

1. Pause ingestion traffic and let the earlier workers finish accepted transactions, pending outbox deliveries and audit work. Resolve uncertain feature updates using the earlier version and the original message within the live replay horizon.
2. Back up the service databases, Redis data, active model, key versions and deployment configuration. Keep the signing/encryption/tokenization keys unchanged for this code update.
3. Stop the earlier service instances before starting the revised ones. Do not serve a mixture of old and new ingestion code: an old instance would compare a new fingerprint using the legacy rule.
4. Preserve acceptance receipts. New receipts set FingerprintVersion=2; an older document without that property defaults to version 1 and can still resolve its original exact retry. Existing version 1 receipts do not acquire the new amount-scale equivalence automatically.
5. Preserve Redis history and snapshots. New snapshots have a version and event digest. Completed transactions are resolved by the durable detection receipt before Redis is called. An unfinished transaction with a legacy snapshot is rejected as feature_snapshot_conflict because its payload cannot be verified from that snapshot. Reconcile it using the earlier version or a documented offline reconstruction; deleting the snapshot and retrying would risk counting it twice.
6. Run the source checks, .NET tests and Compose smoke test, then resume traffic. Read the verification record before treating supplied test code as executed evidence.

A snapshot conflict is a non-retryable business conflict and goes to the consumer error path. A wrong feature key type is reported as invalid_feature_key_type before the script changes another key; investigate or restore the affected state. Memory exhaustion, process loss and inconsistent backups remain recovery scenarios that this preflight cannot solve.
