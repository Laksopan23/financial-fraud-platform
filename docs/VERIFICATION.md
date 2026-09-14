# Verification record

Date: 13 September 2026. Revision: retry and feature integrity.

This is a generated source implementation with limited local verification. The authoring environment did not have a .NET SDK, C# compiler, Docker daemon, Redis server or ONNX runtime. A Lua 5.4 shared library was available and used for script logic tests. Network attempts to retrieve SDK/NuGet resources timed out. C# API compatibility, successful restore/build, container startup and inference performance remain unverified.

## Checks actually executed

| Check | Result | What this establishes |
| --- | --- | --- |
| Structural validator | Passed; see the generated count in structure-validation.json | Seventeen project references and solution entries, package declarations, build inputs, dependency DAGs, Compose connections/ports/mounts and documentation links are structurally consistent |
| JSON/YAML and Python parsing | Passed | Files parse in the available Python environment; this is not Docker's own configuration validation |
| Development-tool unit tests | Passed, 6 tests | Secret generation/key sizes, no accidental rotation, realm claim mapping, generated Compose credentials, binary encoder layout and health-check shell behavior |
| Feature-script logic tests | Passed, 8 tests | Production Lua executes through the native Lua library; deterministic Redis/CJSON doubles check retries, conflict rejection, key-type guards, time boundaries, baseline exclusion and late arrivals |
| Shell syntax | Passed | Bash accepts the PostgreSQL initializer and verification script syntax |
| Stub scan | Passed | C# source contains no unimplemented-method exceptions or unfinished implementation markers |

The health-check test uses a local mock HTTP responder to check the shell command's behavior for HTTP 200 and 503. It does not start Keycloak. The binary test verifies the Python encoder's documented frame layout; the C# parser has separate xUnit tests that still need execution. The Lua tests do not start Redis, do not exercise its embedded Lua/CJSON implementation, and do not establish distributed atomicity. Their Redis command and serialization doubles isolate the production script's logic. The corresponding Testcontainers cases are supplied for real service integration and remain unexecuted. Assertions about project structure are not compilation results.

Detailed structural results are in [structure-validation.json](structure-validation.json). The local test-run counts and case names are in [local-test-results.json](local-test-results.json). Changes and regression coverage are listed in [CHANGELOG.md](../CHANGELOG.md). They can be reproduced with:

```bash
python -m pip install -r scripts/requirements-dev.txt
python scripts/validate_structure.py
python -m unittest discover -s scripts -p 'test_*.py' -v
bash -n infra/postgres/init.sh scripts/verify.sh
```

The Lua tests explicitly report a skip when the Lua 5.4/5.3 shared library is absent. Set `FF_REQUIRE_LUA=1` to require it; CI installs the library and uses that setting. The recorded local run had no skips.

## Required execution gates not run here

| Gate | Status |
| --- | --- |
| NuGet restore and current transitive vulnerability resolution | Not run |
| .NET 9 C# and Razor compilation | Not run |
| xUnit / FluentAssertions unit tests | Test sources supplied; not run |
| Testcontainers PostgreSQL/Redis/RabbitMQ integration tests | Test sources supplied; not run |
| ML.NET training and ONNX export | Executable supplied; not run |
| ML.NET versus ONNX probability parity | Gate supplied; not run |
| Native AOT publish and execution | Executable target supplied; not run |
| Docker Compose startup and end-to-end smoke test | Configurations/scripts supplied; not run |
| Keycloak live authentication and browser dashboard inspection | Not run |
| Inference p99 below 15 ms or pipeline below 50 ms | Unmeasured; no guarantee claimed |
| Container vulnerability scans and image digest verification | Not run |
| Sustained load, process-failure injection and disaster recovery | Not run |
| Security assessment or compliance attestation | Not performed |

Run the README commands or supplied CI workflow in a .NET/Docker environment, fix any resulting errors, and retain actual outputs before qualifying a release. Source implementation, passing structural checks and synthetic training do not make a financial system production ready.
