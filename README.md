# FinancialFraudPlatform

A .NET 9 distributed fraud-scoring reference implementation with REST, gRPC and binary ingestion, durable messaging, Redis features, ML.NET training, ONNX inference, a signed Marten audit ledger and an authenticated Blazor analyst dashboard.

**Delivery status:** source implementation and runnable verification tooling. C# compilation, package restore, Docker startup, xUnit tests, ONNX export and latency measurements have **not** been run in the authoring environment, which has neither .NET nor Docker and could not reach SDK/NuGet downloads. Read [verification](docs/VERIFICATION.md) before deployment. This is not a certified or production-qualified financial system.

The latest revision fixes retry fingerprinting, strict identifiers, feature-snapshot conflicts, outbox scheduling, orphaned audit-head detection and HTTP body-limit errors. Review [CHANGELOG.md](CHANGELOG.md) and the [upgrade procedure](docs/RUNBOOK.md#upgrading-from-the-initial-generated-package) if you used the initial package.

## Start the development stack

Prerequisites: Python 3.10+, Docker Engine/Desktop with Compose v2 and internet access for images and NuGet packages. Allocate about 8 GB of RAM initially; actual capacity has not been measured. The local .NET 9 SDK is needed for local builds and tests, but Docker performs its own builds.

From this directory:

```bash
python scripts/dev_setup.py
docker compose up --build --wait --wait-timeout 300
python scripts/smoke.py
```

The first command generates development credentials. Open `.env` locally to retrieve them; do not commit it. The setup command refuses to replace existing credentials. First image builds and training may exceed five minutes on a slow machine; inspect the failing service before increasing the wait budget.

| Surface | Address | Sign-in |
| --- | --- | --- |
| Analyst dashboard | [localhost:8084](http://localhost:8084) | `analyst`, `ANALYST_PASSWORD` from `.env` |
| Ingestion REST | [localhost:8082](http://localhost:8082) | Bearer token for `transaction-client` |
| Ingestion gRPC | localhost:50051 | Same bearer token; local HTTP/2 development endpoint |
| Audit API | [localhost:8083](http://localhost:8083) | `audit-client` or a token with the auditor role |
| Keycloak | [localhost:8080](http://localhost:8080) | `admin`, `KEYCLOAK_ADMIN_PASSWORD` |
| Grafana | [localhost:3000](http://localhost:3000) | `admin`, `GRAFANA_PASSWORD` |
| RabbitMQ management | [localhost:15672](http://localhost:15672) | `fraud`, `RABBITMQ_PASSWORD` |

The smoke script obtains its own tokens, submits synthetic transactions, checks retries and tenant boundaries, and verifies audit signatures. It does not print credentials or card numbers. All published development ports bind to loopback.

To stop while retaining data:

```bash
docker compose down
```

## Build and test locally

```bash
dotnet restore FinancialFraudPlatform.sln
dotnet build FinancialFraudPlatform.sln -c Release --no-restore
dotnet test tests/FinancialFraudPlatform.UnitTests -c Release --no-build
dotnet test tests/FinancialFraudPlatform.IntegrationTests -c Release --no-build
```

For the checks that do not require .NET or Docker:

```bash
python -m pip install -r scripts/requirements-dev.txt
python scripts/validate_structure.py
python -m unittest discover -s scripts -p 'test_*.py' -v
```

The Lua script tests require a Lua 5.4/5.3 shared library and explicitly report a skip if unavailable; `FF_REQUIRE_LUA=1` makes absence a failing gate. The shell health-check test requires Bash. These checks use simulated Redis/CJSON for script logic and cannot establish that the C# services build or run.

Integration tests start disposable PostgreSQL, Redis and RabbitMQ containers. They require a running Docker daemon; they are not silently skipped. Run `bash scripts/verify.sh` for build, tests, demo training and the inference budget gate. The supplied GitHub Actions workflow also builds the Native AOT parser and runs a Compose smoke test.

## Train and benchmark

```bash
dotnet run --project tools/FinancialFraudPlatform.ModelTraining -c Release -- --demo --out artifacts/model
dotnet run --project tools/FinancialFraudPlatform.InferenceBenchmark -c Release -- --model artifacts/model/model.onnx --allow-synthetic --max-p99-ms 15 --out artifacts/benchmark.json
```

The trainer creates `model.zip`, `model.onnx`, `manifest.json` and `evaluation.json`. It performs a chronological 70/15/15 split and checks ML.NET/ONNX probability parity. No pretrained weights are bundled: the Compose model initializer trains the synthetic demonstration model before the worker starts.

For your own data, replace `--demo` with `--data path/to/features.csv`. The required numeric CSV format and feature semantics are in [ML_PIPELINE_SPEC.md](docs/ML_PIPELINE_SPEC.md). Real training data, operational thresholds and fraud performance have not been supplied or established.

The current scoring contract accepts **USD only**. The domain Money object demonstrates other currency exponents, but ingestion rejects those currencies until a compatible feature schema and approved model are introduced. No exchange rate is fabricated.

## Read the implementation

| Document | Purpose |
| --- | --- |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | C4 views, transactions, sequence diagrams, trade-offs |
| [IMPLEMENTATION_MAP.md](docs/IMPLEMENTATION_MAP.md) | Every project and the implementation entry points |
| [SECURITY_AND_COMPLIANCE.md](docs/SECURITY_AND_COMPLIANCE.md) | Encryption, identity, threat model, audit integrity and control boundaries |
| [ML_PIPELINE_SPEC.md](docs/ML_PIPELINE_SPEC.md) | Exact feature order, training contract, parity, benchmarks and promotion |
| [NETWORK_TOPOLOGY.md](docs/NETWORK_TOPOLOGY.md) | Ports, trust boundaries, allowed flows, failure handling |
| [RUNBOOK.md](docs/RUNBOOK.md) | Startup, recovery, deployment gates and retention |
| [API_AND_BINARY_PROTOCOL.md](docs/API_AND_BINARY_PROTOCOL.md) | REST, gRPC, binary framing and error semantics |
| [VERIFICATION.md](docs/VERIFICATION.md) | Checks actually run and checks still requiring execution |

.NET 9 is retained as requested. Microsoft's support policy currently lists its end of support as **10 November 2026**; plan an upgrade before that date. [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).
