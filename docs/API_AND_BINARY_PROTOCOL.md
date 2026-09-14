# API and binary protocol

## Identity

Obtain an OAuth client-credentials token using the generated transaction client for writes. Use the audit client for audit reads. Each has a server-provisioned tenant claim. The smoke script implements both token requests without printing their secrets.

REST, gRPC and binary requests describe an observation. The accepted transaction ID is the client-generated idempotency key; use a fresh UUID for a distinct transaction and the same UUID for an exact retry.

New receipts use fingerprint version 2: `10`, `10.0` and `10.00` USD have one identity, as do equivalent UTC offsets and positive/negative coordinate zero. Timestamp precision is preserved; values that differ by a tick are different observations. Names and merchant IDs are compared exactly. A retry across protocols must therefore preserve all original fields, including name and timestamp precision. Existing version 1 receipts retain their original comparison rules.

Tenant claims must be one complete lowercase ASCII slug of 1–48 characters; merchant IDs are 1–64 ASCII letters, digits, `_` or `-`. Trailing line breaks and the all-zero transaction UUID are invalid.

## REST

`POST /api/transactions` requires Writer authorization. Example synthetic payload; replace the timestamp with current UTC when manually sending it:

```json
{
  "transactionId": "01234567-89ab-4def-8123-456789abcdef",
  "cardNumber": "4111111111111111",
  "cardholderName": "Synthetic Test",
  "amount": 100.25,
  "currency": "USD",
  "merchantId": "demo-shop",
  "latitude": 6.9271,
  "longitude": 79.8612,
  "occurredAt": "2026-09-13T00:00:00Z"
}
```

This is a test card number, not customer data. Do not add CVV, PIN or track data. Merchant risk is selected from the server-side catalog. Default demo merchants are `demo-shop` and `high-risk-shop`.

A success returns HTTP 202 with transactionId, acceptedAt and status=accepted plus a Location header. `GET /api/transactions/{id}` returns the same scoped receipt. These receipts do not turn into completed scoring results; the audit API is the decision source.

| Status | Meaning |
| --- | --- |
| 202 | Durable acceptance or an equal retry |
| 400 | Invalid ID, card, currency/amount, location, time, merchant or frame |
| 401 | Missing/invalid authentication |
| 403 | Role or tenant authorization failure |
| 404 | No record visible in the authenticated tenant |
| 409 | Same transaction ID reused for different data |
| 413 | Request body exceeds the server limit |
| 415 | Binary endpoint called with another content type |
| 429 | Per-process tenant write budget exhausted |
| 503 | Dependency or unexpected processing failure; no approval implied |

Errors contain a bounded error code and a trace identifier, not exception details or submitted cardholder data.

`GET /api/decisions/{id}` on the audit service requires Auditor authorization. It returns a signed ledger entry and a signatureValid result. `GET /api/ledger/verify?shard=0&after=0&limit=1000` verifies a page; traverse all 64 shards and continue with checkedThrough while hasMore is true to inspect a full tenant ledger.

## gRPC

The protobuf contract is in `src/Services/Ingestion/FinancialFraudPlatform.Ingestion.API/Protos/transactions.proto`. `fraud.v1.TransactionGateway/Stream` is a bidirectional stream. Set the `authorization: Bearer ...` metadata header. Each message supplies amount_minor_units as an int64 and a Unix-millisecond timestamp. The tenant is not a message field.

Every accepted or rejected item receives a reply. Invalid data can be rejected independently. A rate limit terminates the stream with ResourceExhausted; dependency failures use Unavailable. A client retry must reuse transaction IDs to resolve any uncertain acknowledgements. The local endpoint is plaintext HTTP/2 for development; production gRPC requires an authenticated TLS transport configuration.

## Binary ingestion

`POST /api/transactions/binary`, content type `application/octet-stream`, accepts concatenated frames and uses ASP.NET Core's PipeReader. Fixed-width integer fields use network byte order. Each frame is:

| Field | Bytes | Encoding |
| --- | --- | --- |
| Body length | 4 | Signed big-endian int32, excludes this prefix; valid range 60–256 |
| Version | 1 | Exactly 1 |
| Transaction UUID | 16 | RFC/network byte order, matching Guid bigEndian=true |
| Amount | 8 | Signed big-endian minor units |
| Currency | 3 | Uppercase ASCII code; serving currently accepts USD |
| Occurred time | 8 | Signed big-endian Unix milliseconds |
| Latitude | 4 | Signed big-endian microdegrees |
| Longitude | 4 | Signed big-endian microdegrees |
| Merchant length | 1 | 1–64 |
| Merchant | Variable | Printable ASCII; domain narrows to identifier characters |
| PAN length | 1 | 13–19 |
| PAN | Variable | ASCII digits with valid Luhn check |

Trailing bytes inside a frame, unsupported versions, truncated input and malformed lengths are rejected. An incomplete network segment is retained until sufficient data arrives. The parser does not allocate a new array just to parse a segmented frame; it copies at most 256 bytes to the stack. Decoded strings and returned objects still allocate.

The endpoint returns a batch accepted count only after the input completes. Acceptance is **per frame**, not all-or-nothing for a batch: earlier frames may be durable if a later frame fails. Retry using the original IDs or query receipts. `scripts/smoke.py` provides a concrete binary encoder.

## Dashboard and health

`GET /api/alerts` on the dashboard returns up to 100 recent alerts from the authenticated tenant. `/hubs/alerts` supplies a `RiskAlert` event to the server-selected tenant group. Clients cannot request another tenant group. The Blazor UI uses its own SignalR circuit and the same local tenant event feed, plus periodic durable reloads.

Each app has `/health/live` and `/health/ready`. Liveness indicates that the process can respond. Readiness includes the registered database/broker checks and Redis for detection. Model loading and warm-up are mandatory before the worker begins serving. Health endpoints carry no database strings, key material or customer data.
