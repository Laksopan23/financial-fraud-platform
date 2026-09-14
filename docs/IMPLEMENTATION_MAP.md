# Implementation map

The solution contains 17 projects. Domain code references only the shared core; MediatR and infrastructure stay outside it.

| Project | Relative directory | Implementation |
| --- | --- | --- |
| FinancialFraudPlatform.Core | `src/BuildingBlocks/FinancialFraudPlatform.Core` | Shared primitives, tenant identifiers, event contracts, features and risk tiers. |
| FinancialFraudPlatform.EventBus | `src/BuildingBlocks/FinancialFraudPlatform.EventBus` | Marten store setup, persisted leased outbox, MassTransit RabbitMQ and OpenTelemetry. |
| FinancialFraudPlatform.Audit.Ledger | `src/Services/Audit/FinancialFraudPlatform.Audit.Ledger` | Signed sharded audit chain, receipt/head consistency, event mutation guard and verifier API. |
| FinancialFraudPlatform.Detection.FeatureStore | `src/Services/Detection/FinancialFraudPlatform.Detection.FeatureStore` | Redis Lua atomic observation, stable snapshots, geolocation/baseline math and Polly resilience. |
| FinancialFraudPlatform.Detection.ML | `src/Services/Detection/FinancialFraudPlatform.Detection.ML` | Pinned manifest/model verification, ONNX contract check, warm-up and probability scoring. |
| FinancialFraudPlatform.Detection.Worker | `src/Services/Detection/FinancialFraudPlatform.Detection.Worker` | Message consumer with durable duplicate check and atomic scored-event outbox. |
| FinancialFraudPlatform.Ingestion.API | `src/Services/Ingestion/FinancialFraudPlatform.Ingestion.API` | REST, bidirectional gRPC, PipeReader endpoint, encrypted persistence and private-vault retention. |
| FinancialFraudPlatform.Ingestion.Application | `src/Services/Ingestion/FinancialFraudPlatform.Ingestion.Application` | MediatR command/query handlers, persistence interface and bounded binary parser. |
| FinancialFraudPlatform.Ingestion.Domain | `src/Services/Ingestion/FinancialFraudPlatform.Ingestion.Domain` | Pure DDD aggregate, domain event and Money/CardNumber/TransactionId/GeoPoint. |
| FinancialFraudPlatform.Security.Shield | `src/Services/Security/FinancialFraudPlatform.Security.Shield` | AES-GCM, key rings, HMAC token/signature services, JWT/OIDC, roles/tenant rules and safe logging. |
| FinancialFraudPlatform.Dashboard.Blazor | `src/Web/FinancialFraudPlatform.Dashboard.Blazor` | OIDC analyst UI, tenant read model and checkpoints, SignalR notifications, expiration and refresh. |
| FinancialFraudPlatform.IntegrationTests | `tests/FinancialFraudPlatform.IntegrationTests` | Docker-backed PostgreSQL, Redis and RabbitMQ tests for persistence and delivery invariants. |
| FinancialFraudPlatform.UnitTests | `tests/FinancialFraudPlatform.UnitTests` | Domain, binary framing, cryptography, logging, authorization, JWT and model-integrity tests. |
| FinancialFraudPlatform.HealthProbe | `tools/FinancialFraudPlatform.HealthProbe` | Minimal HTTP readiness probe used in container health checks. |
| FinancialFraudPlatform.InferenceBenchmark | `tools/FinancialFraudPlatform.InferenceBenchmark` | Warmed latency/allocation/throughput measurement and p99 budget exit gate. |
| FinancialFraudPlatform.ModelTraining | `tools/FinancialFraudPlatform.ModelTraining` | Synthetic or numeric-CSV FastTree training, export, parity and evaluation reporting. |
| FinancialFraudPlatform.NativeParser | `tools/FinancialFraudPlatform.NativeParser` | Native AOT-capable standalone parser executable using the actual binary parser source. |

## Suggested reading order

1. Core contracts and domain value objects.
2. Application handlers and TransactionRepository, following one accepted command into its outbox.
3. OutboxDispatcher and RedisFeatureStore, focusing on leases, retry snapshots and time semantics.
4. OnnxFraudScorer and ModelTraining, then DetectionConsumer.
5. AuditConsumer, LedgerVerifier and the dashboard read path.
6. Compose, security controls, tests and operational limits.

## Requirement coverage

| Requested discipline | Concrete implementation | Qualification |
| --- | --- | --- |
| DDD/Clean Architecture/CQRS | Domain-only aggregate/value objects; command/query handlers and repository interfaces | No claim that reflection-heavy frameworks support whole-service AOT |
| Pipelines/Span/AOT | ASP.NET PipeReader, segmented binary parser, separately publishable NativeParser | Numeric parsing avoids temporary heap buffers; result objects/text still allocate |
| Messaging/gRPC/SignalR | RabbitMQ consumers/outboxes, bidirectional gRPC, authenticated hubs and Blazor circuits | At-least-once delivery; single-dashboard development profile |
| Resilience | Jittered retry, circuit breaker, concurrency limiter, lease expiry and error queues | Long outages require operator recovery and horizon-aware replay |
| Data science | Atomic rolling counts, Haversine/speed, Welford baseline and stable feature snapshots | Live processing-time windows; history loss is an explicit recovery condition |
| ML | C# FastTree training, ONNX export/runtime, model checksums, parity and latency tools | Real data and measured production performance are not supplied |
| Security | AES-256-GCM, separate HMAC keys, safe logs, OIDC/JWT, role and tenant enforcement | Development transports/credentials need production provisioning |
| Audit | Marten events, signed links, atomic heads, mutation guards and separate checkpoints | Tamper-evident; independent immutable anchors are a production requirement |
| Infrastructure | Thirteen Compose services, credentials/realm generator and monitoring configuration | Single-node development; no HA or external deployment performed |
| Verification | xUnit/FluentAssertions, Testcontainers, smoke script and CI | Execution status is recorded separately; source presence is not test success |
