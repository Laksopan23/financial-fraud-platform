# Change log

## 13 September 2026 — retry and feature integrity revision

This revision retains the service layout, public routes and model feature schema. It changes retry fingerprinting and cached snapshot storage. Read the [upgrade procedure](docs/RUNBOOK.md#upgrading-from-the-initial-generated-package) before updating a stack that already contains data.

| Problem found in source review | Change | Regression coverage supplied |
| --- | --- | --- |
| Numerically equal amounts with different decimal scales could conflict on retry | Version 2 fingerprints use integer minor units, uppercase currency, UTC time and normalized signed zero; version 1 receipt comparison remains available | Canonicalization unit cases, concurrent PostgreSQL acceptance with mixed decimal scales, legacy receipt replay |
| The tenant and merchant regexes could accept a final newline | Whole-string anchors plus empty transaction ID rejection | Identifier/claim boundary tests |
| A same-card Redis snapshot could be reused for an altered event | Snapshot envelopes contain a version and the event digest; mismatches stop processing before mutation | Lua logic tests and Testcontainers changed-payload test |
| A wrong Redis key type could fail after an earlier command had already mutated state | All four key types are checked before writes | Lua logic tests for each key and a Testcontainers recovery test |
| Replayed bus input could carry inconsistent event and receipt times | Feature-store boundary validates currency, minor precision, merchant and the accepted event-time interval | Testcontainers invalid-time cases |
| A stale outbox candidate could bypass a newly scheduled retry delay | The claim step rechecks NextAttemptAt after loading current state | Source review; expired lease coverage expanded in the broker integration test |
| A missing audit head could be reported as an empty valid ledger despite remaining events | Verification checks for orphaned events and rereads the head to handle a concurrent first append | PostgreSQL empty/valid/missing-head regression case |
| Request error handling converted body-limit HTTP 413 into HTTP 400 | Preserve the framework status and return a bounded request_too_large code | Middleware unit cases for 400/413 and sensitive error text |

The regular-expression fix follows Microsoft's documented distinction between `$` and the strict end-of-string `\z` anchor. [Microsoft anchor reference](https://learn.microsoft.com/en-us/dotnet/standard/base-types/anchors-in-regular-expressions).

Redis script isolation does not provide transaction rollback for an error after a write. The key-type preflight addresses that identified failure path; resource exhaustion and data recovery still need operational handling. [Redis transaction and rollback explanation](https://redis.io/blog/you-dont-need-transaction-rollbacks-in-redis/).

Eight tests execute the actual feature script through an available native Lua library, using deterministic Redis and CJSON doubles. Together with the six development-tool tests, fourteen local tests passed. The added C# unit and integration tests have not run; .NET and Docker remain unavailable. See [VERIFICATION.md](docs/VERIFICATION.md) for the precise validation boundary.
