#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore FinancialFraudPlatform.sln
dotnet build FinancialFraudPlatform.sln -c Release --no-restore
dotnet test tests/FinancialFraudPlatform.UnitTests -c Release --no-build --logger trx
dotnet test tests/FinancialFraudPlatform.IntegrationTests -c Release --no-build --logger trx
dotnet run --project tools/FinancialFraudPlatform.ModelTraining -c Release --no-build -- --demo --out artifacts/model
dotnet run --project tools/FinancialFraudPlatform.InferenceBenchmark -c Release --no-build -- --model artifacts/model/model.onnx --allow-synthetic --max-p99-ms 15 --out artifacts/benchmark.json
