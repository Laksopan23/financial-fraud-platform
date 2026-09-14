# Security and compliance engineering

## Status and intended use

This repository implements technical controls for a fraud investigation workflow. It supplies no PCI DSS validation, SOC 2 report, GDPR determination, APPI assessment or guarantee of compliance. Scope, jurisdiction, operating procedures and audit evidence must be assessed for the actual deployment. The local Compose configuration uses development identity and cleartext private-network transports; it is intended for synthetic data on one machine.

## Data handling

| Data | Handling | Exposure boundary |
| --- | --- | --- |
| PAN and optional cardholder name | Encrypted together using AES-256-GCM | Ingestion memory and encrypted vault only |
| PAN-derived token | HMAC-SHA256 with a separate key and tenant namespace | Broker and feature-store keys; still linkable pseudonymous data |
| Full IP, bodies, authorization headers, free text | Omitted by the log provider and request middleware | Not intentionally written to platform logs or traces |
| Amount and derived features | Retained with scoring evidence | Authorized service databases and auditor endpoints |
| Model score, tier, hash and version | Retained in analyst read model | Authenticated users in that tenant |
| Identity and administrative secrets | Generated into ignored local configuration for development | Operator-controlled development files and container environments |

Sensitive authentication data such as CVV, PIN blocks and full track data is not part of the accepted API contract. Adding those fields changes the threat and compliance scope.

### AES and keys

Each encrypted payload has a fresh random 96-bit nonce and 128-bit GCM tag. A 256-bit key is selected from a versioned key ring. Associated authenticated data includes a purpose/version prefix, tenant and transaction ID, so moving ciphertext between records fails authentication. Decryption fails on unknown keys, altered ciphertext, altered tags or incorrect context.

Encryption, tokenization and audit signing use separate random keys. The encryption key ring can retain old versions while a new active version encrypts new data. Tokenization key rotation requires a migration strategy because changing it changes velocity identities. The development setup refuses to silently overwrite keys beneath existing persisted volumes.

Temporary byte buffers are cleared when feasible. Managed strings containing input cannot be reliably zeroed; never describe this service as eliminating all plaintext from memory. The public ingestion contract expects transport encryption at deployment boundaries. AES here protects the retained private payload; it is not a claim of client-to-worker end-to-end ciphertext processing.

For production, load keys through a managed secret service or KMS/HSM-backed envelope process, scope access per service, protect backups, and record rotation and restore procedures. No KMS integration is falsely represented as already configured.

### Logging and telemetry

The logger never calls an untrusted message formatter for output. It retains numeric event codes, level, category and exception type, and allows only route template, status, duration and trace ID fields. Names cannot be reliably scrubbed from arbitrary prose with a regular expression, so raw prose and unapproved fields are dropped entirely.

The HTTP middleware records matched route templates instead of arbitrary raw paths. No request-body logging is enabled. Custom OpenTelemetry spans carry operation names and trace context only; histogram labels do not contain tenants, tokens, IDs or customer attributes. External proxies, runtime crash dumps and independently configured third-party agents must follow the same data policy.

## Authentication and authorization

- APIs validate signed RS256 JWTs, issuer, audience, expiration and signing keys through Keycloak. Inbound claim renaming is disabled. The tenant must be exactly one valid, bounded claim. Identifier patterns require the actual end of the string, rejecting a trailing newline; scoped transaction identities reject the empty UUID.
- Writer endpoints require `transaction_writer`. Analyst views require `analyst` or `auditor`. Audit endpoints require `auditor`. Merely being authenticated is insufficient.
- Tenant identity always comes from the authenticated principal, never a caller-supplied payload, route tenant, query or SignalR group argument.
- The dashboard uses OIDC authorization code flow with PKCE and a confidential client. Cookies are HttpOnly, use a short absolute lifetime, and are secure outside development. Long-lived Blazor circuits revalidate the local session-expiration claim every 30 seconds; custom SignalR connections close when authentication expires.
- JWT role removal is not instantly reflected in already issued tokens. The development access-token lifetime is five minutes; cookie/circuit authorization can persist for up to 15 minutes. Immediate revocation and high-risk action reauthentication need an environment-specific policy.
- A development-only HTTP backchannel rewrite lets containers reach Keycloak while browser redirects retain the public localhost issuer. Production requires an HTTPS issuer and does not use that rewrite.

The access model combines roles with the authenticated tenant attribute. It is a concrete tenant ABAC rule, not a general external policy engine.

## Audit integrity

Each tenant has 64 ledger shards selected by transaction UUID. A record binds its stream, sequence, previous hash, signing key version, scoring event and timestamp in a canonical JSON payload. HMAC-SHA256 produces the record signature. The ledger head and event append commit together under optimistic concurrency. Separate dashboard checkpoints retain committed stream positions and hashes.

The verifier checks signatures, consecutive sequences and links, and compares the last verified position to the stored head. Verification is paginated and explicitly reports whether more records remain. Starting after a position validates that position's signature but does not independently recheck the entire earlier prefix; start at zero for a full shard traversal.

PostgreSQL triggers reject UPDATE, DELETE and TRUNCATE on audit event rows. These are application/operator protections. A database owner or superuser can remove triggers, restore an old snapshot, delete a whole stream and its head, or otherwise alter storage. The repository therefore uses the term **tamper-evident**, not tamper-proof.

The verifier reports a missing ledger head as invalid when signed event rows still exist. Deletion of all evidence or rollback of a whole history cannot be reliably detected from that same database alone. A production design needs independently controlled signed checkpoints, immutable/WORM retention and restore reconciliation. The dashboard checkpoint is an additional copy, but the local shared database server is not an independent WORM trust boundary.

## Threat model

| Threat | Implemented control | Remaining deployment concern |
| --- | --- | --- |
| Duplicate payment observations | Scoped ID plus keyed payload fingerprint | Origin system must reuse the same transaction UUID on retries |
| Cross-tenant reads | Role and tenant checks at every query/hub boundary | Test identity provisioning and operator access separately |
| Database ciphertext relocation | AES-GCM authenticated context | Key access and privileged memory inspection |
| PII in ordinary application logs | Closed logging field allowlist | External infrastructure logs and crash dumps |
| Changed model files | Model checksum and production-pinned manifest digest | Trusted approval and distribution of the expected digest |
| Forged internal messages | Private broker credentials in development; consumer validation | Separate production broker principals and ACLs, TLS/mTLS |
| Ledger modification | HMAC chain, concurrent head update, row mutation guards | Administrator compromise and full-history rollback |
| Dependency outage | Durable outbox, consumer retries, circuit breaker and bounded concurrency | Dead-letter ownership, recovery drills and backlog capacity |
| Unbounded input | Request/frame/message limits, stream cap, tenant transaction budget | Per-client ingress quotas and distributed rate limiting |

The tenant rate limiter is per ingestion process. Multiple ingestion replicas multiply that allowance. The UI's read cache and SignalR push path are designed for one dashboard replica in the supplied Compose deployment; use an appropriate backplane, broadcast strategy and session routing before scaling it.

## Control mapping for an assessor

| Area | Evidence supplied | Work needed in an operating organization |
| --- | --- | --- |
| Card-data protection | Minimal input contract, authenticated encryption, masking and safe logging tests | CDE scope, key custody, segmentation validation and PCI assessment |
| Security and availability controls | Authorization tests, health checks, durable messaging, telemetry and CI | Access reviews, incident response, change control, backup/restore evidence and independent examination |
| Personal-data protection | Data inventory, tenant controls and a private-vault retention mechanism | Lawful purpose, notices, retention schedule, data-subject handling and cross-border assessment |
| Model governance | Dataset digest, split description, model version, parity check and recorded decisions | Approved datasets, bias/quality review, calibration, accountable review and monitored promotion |

The private vault expires after 24 hours and its worker removes expired rows approximately every five minutes. That interval is a development policy, not a legal retention requirement; backups and database recovery logs need a separate retention policy. Audit records and idempotency receipts are retained indefinitely in this reference to avoid unsafe silent evidence removal. Establish retention and legal-hold rules before real data is introduced.

Consult the authoritative [PCI SSC document library](https://www.pcisecuritystandards.org/document_library/), [AICPA SOC resources](https://www.aicpa-cima.com/resources/landing/system-and-organization-controls-soc-suite-of-services), and [GDPR text](https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng) when defining the actual assessment scope. These links are assessment references, not a claim that this code satisfies every requirement.
