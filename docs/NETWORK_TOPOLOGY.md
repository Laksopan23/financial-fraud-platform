# Network topology

## Local deployment

Compose defines `edge`, `backend` and `observability` networks. Backend and observability networks are internal. Only the UI, ingress, audit, identity and management surfaces publish loopback ports. PostgreSQL, Redis, RabbitMQ AMQP, detection and telemetry ingestion have no published host ports.

| Caller | Destination | Container port/protocol | Purpose |
| --- | --- | --- | --- |
| Merchant/client | Ingestion | 8080 HTTP/1.1; 8081 HTTP/2 | REST/binary and gRPC transaction ingestion |
| Browser | Dashboard | 8080 HTTP/WebSocket | OIDC login and Blazor/SignalR |
| Browser and service backchannels | Keycloak | 8080 HTTP in development | Discovery, authorization, JWKS and tokens |
| Ingestion, detection, audit, dashboard | RabbitMQ | 5672 AMQP | Durable events and acknowledgements |
| Ingestion | PostgreSQL ingestion DB | 5432 PostgreSQL | Receipt, vault and outbox |
| Detection | PostgreSQL detection DB | 5432 PostgreSQL | Scoring decision and outbox |
| Audit | PostgreSQL audit DB | 5432 PostgreSQL | Ledger, head, receipt and outbox |
| Dashboard | PostgreSQL dashboard DB | 5432 PostgreSQL | Read models and evidence checkpoints |
| Keycloak | PostgreSQL keycloak DB | 5432 PostgreSQL | Identity persistence |
| Detection | Redis | 6379 RESP | Atomic feature computation and snapshots |
| Application services | OpenTelemetry collector | 4317 OTLP/gRPC | Custom traces and metrics |
| Collector | Tempo | 4317 OTLP/gRPC | Trace storage |
| Prometheus | Collector | 8889 HTTP | Metrics scrape |
| Grafana | Prometheus / Tempo | 9090 / 3200 HTTP | Operations dashboards and traces |

The local shared broker principal and broad backend network do not constitute a production zero-trust mesh. Provision distinct identities/ACLs and workload network policies for each permitted flow before production. Broker access is a privileged boundary: a publisher who can fabricate an internal event can influence decision processing.

## Startup order

PostgreSQL and RabbitMQ become healthy first. Keycloak imports the generated realm; the one-shot model initializer trains independently. Dashboard declares its queues before audit becomes healthy. Audit becomes healthy before detection starts. Detection requires Redis and the completed model initialization. Ingestion waits for detection readiness before accepting traffic. This prevents the normal first-start race where an event is published before a required subscription exists.

A production rollout should declare durable subscriptions and verify consumer readiness before enabling producers. Restarting already declared consumers does not remove durable queues.

## Transport and availability boundaries

The supplied HTTP/h2c endpoints are a local development profile. Production ingress must terminate TLS and preserve authenticated context; use validated TLS/mTLS for service, database, Redis and broker links. The RabbitMQ client has a TLS configuration switch; PostgreSQL and Redis connection strings must include their validated TLS settings. Do not disable certificate verification to make deployment pass.

Use externally reachable HTTPS identity discovery with correct issuer, audience and callback configuration. Restrict forwarded-header trust to known proxies. An OIDC issuer is an identity identifier, not an arbitrary endpoint that can be rewritten in production.

Compose is single-node development infrastructure. It does not implement a PostgreSQL HA cluster, Redis Sentinel/Cluster, RabbitMQ quorum replication, regional failover, certificate rotation or Kubernetes policies. Those deployment choices require an actual capacity and availability target.

## Backpressure, retries and failed delivery

| Mechanism | Current setting | Meaning |
| --- | --- | --- |
| REST/body limit | 128 KiB | Bounded buffering; binary batches still have per-frame limits |
| Binary frame | 256 bytes maximum | Length checked before waiting for a whole frame |
| gRPC input | 4 KiB per message; 500 observations per stream | Limits messages and stream workload |
| Tenant write budget | 200 new observations/second per ingestion process | Existing accepted retries do not consume new transaction budget |
| RabbitMQ prefetch / consumer concurrency | 64 / 32 | Bounded in-flight consumer work |
| Redis bulkhead | 64 permits, 128 queued calls | Limits feature dependency concurrency |
| Redis resilience | Two jittered exponential retries; circuit breaker | Transient connection/timeout handling |
| Outbox | 60-second lease, 15-second publish budget, 10 ms idle poll | At-least-once delivery with reclaim after a crash |
| Consumer retries | Five exponential retries, excluding validation/conflict errors | Exhausted messages remain in MassTransit error queues |
| UI feed | 100 buffered events per circuit plus 15-second reload | Bounded memory with durable read-model recovery |

No consumer creates a Low score because a dependency is unavailable. Monitor error queues and outbox backlog, and apply the recovery procedure before redelivery. Messages older than the six-hour live-feature horizon must not be blindly replayed into today's velocity windows.

Local Prometheus rules cover inference and pipeline latency. Notification routing to an on-call system is deliberately not configured without an identified destination and authorization.
