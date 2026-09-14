# ML pipeline specification

## Model and data status

The implemented model is an ML.NET FastTree binary classifier exported to ONNX and evaluated by ONNX Runtime. The repository does not contain real fraud labels or an approved pretrained model. The `--demo` path generates seeded synthetic feature/label rows to exercise the engineering pipeline. Its performance is not evidence of real fraud detection quality.

The model-init container trains the demonstration model before detection starts. Production-mode detection rejects a synthetic model, requires an expected manifest SHA-256 digest and verifies the ONNX model digest and feature contract before warming the session. No heuristic fallback produces a fabricated model probability if loading or inference fails.

## Feature order — schema fraud-features-v1

The ONNX input is a single float32 tensor named `Features`, shape `[batch, 11]`; serving uses batch size 1. Its required float output is `Probability` with exactly one value per request. Startup and training parity checks fail if that contract is not met.

| Index | Name | Definition |
| --- | --- | --- |
| 0 | amount | Positive USD major units; no cross-currency conversion is assumed |
| 1 | velocity_1m | Unique accepted observations for the token in the trailing processing-time minute, including this one |
| 2 | velocity_5m | Same count over five minutes |
| 3 | velocity_1h | Same count over one hour |
| 4 | distance_km | Haversine distance from the last event-time location; zero for missing history or a late event |
| 5 | speed_kph | Distance divided by elapsed event-time hours; minimum denominator one second, capped at 100,000 km/h |
| 6 | spending_z | Absolute deviation from the previous currency baseline, capped at 20 |
| 7 | merchant_risk | Server-owned merchant catalog value in [0,1]; absent merchants are rejected |
| 8 | time_delta_hours | Shortest circular difference between current and previous UTC hour of day, in [0,12] |
| 9 | history_available | 1 when prior location and at least two spending samples exist, otherwise 0 |
| 10 | out_of_order | 1 when the event timestamp predates the latest stored event timestamp |

Geolocation is not proof of the cardholder's physical location. Merchant coordinates, mobile network locations, proxy locations and online merchants can make “impossible travel” misleading. The feature is evidence for a model and analyst, not an automatic factual conclusion.

## Redis state and causality

The Lua script performs lookup, observation and snapshot creation without interleaving other Redis commands. It checks the four key types before writing, since an error after mutation cannot roll back earlier commands. Keys for the same tenant/token share a Redis Cluster hash tag. It maintains a sorted set for velocity, a hash for latest location, a per-currency Welford baseline and a per-transaction snapshot. Redis `TIME` supplies the processing clock.

For previous sample count n, mean μ and M2, adding amount x uses:

\[
\delta=x-\mu,\quad \mu'=\mu+\delta/(n+1),\quad M2'=M2+\delta(x-\mu').
\]

The current score uses the **old** statistics. For n≥2, the sample standard deviation is \(\sqrt{M2/(n-1)}\). With fewer samples the z feature is zero and history is flagged; with zero variance a nonzero deviation maps to the cap. This is a cumulative baseline over retained activity, not a rolling 90-day mean. The baseline key expires after 90 days of inactivity.

Velocity members older than one hour are pruned. Their key has a 3,700-second inactivity TTL; location and feature snapshots last 48 hours. Snapshot storage version 2 contains the complete transaction-event digest plus the original numeric snapshot. A duplicate live delivery with the same digest returns that snapshot without incrementing counts or reapplying the baseline update. An altered payload, malformed snapshot or earlier snapshot format produces `feature_snapshot_conflict`; it is not silently recounted. The binding is within a tenant/card hash slot; ingestion and durable detection receipts enforce transaction identity across card changes. The model feature schema remains `fraud-features-v1`.

The feature boundary also checks USD/minor precision, merchant identity and event time relative to ReceivedAt before accessing Redis. Out-of-order transactions do not rewind the latest location. Snapshot expiry and loss of Redis history are explicit limits, not exactly-once claims.

For real training, build features from transaction history strictly as of each observation using these same semantics. Labels must be mature and available independently of features. Do not use future chargeback outcomes in feature engineering. A time split alone cannot repair features that were calculated using future data.

## Training input

A numeric, unquoted CSV is accepted. Its first line must be exactly:

```csv
timestamp,label,amount,velocity_1m,velocity_5m,velocity_1h,distance_km,speed_kph,spending_z,merchant_risk,time_delta_hours,history_available,out_of_order
```

`timestamp` is Unix milliseconds in ascending order. `label` is 0 or 1. All feature values must be finite. At least 1,000 rows and both labels in each split are required. Do not include PANs, names, raw IP addresses or other unneeded personal data in this feature file.

```bash
dotnet run --project tools/FinancialFraudPlatform.ModelTraining -c Release -- --data datasets/features.csv --out artifacts/candidate
```

The trainer reserves the first 70% for training, the next 15% for validation and the last 15% for test. Normalization is fitted only on training rows. FastTree uses 100 trees, up to 24 leaves, minimum 20 examples per leaf and one training thread with the ML context seed set to 42. Account-group holdouts, chargeback-delay embargoes and deployment-specific time boundaries are not inferred from a precomputed numeric CSV; apply and document them upstream for real data.

The evaluation report records PR-AUC, ROC-AUC, precision, recall, F1, log loss, Brier score, review rate, critical rate, dataset digest and split sizes. These metrics are only written by an actual completed training run; there are no invented results in this delivery. Fixed medium=0.50 and critical=0.85 thresholds demonstrate tier assignment and are not optimized business thresholds.

## Export and parity

The ML.NET model is saved as `model.zip`. An input schema containing only Features is passed to ONNX conversion, preventing Label from becoming an inference input. The model's SHA-256, feature version/order, thresholds, currency, synthetic flag and training time are written to `manifest.json`.

The exported model is then loaded by the same runtime scorer used in detection. Up to 256 held-out rows are scored by both ML.NET and ONNX. A maximum absolute probability difference over 1e-5 fails training. ONNX export depends on trainer/transform support, as described in [Microsoft's model export guidance](https://learn.microsoft.com/en-us/dotnet/machine-learning/how-to-guides/save-load-machine-learning-models-ml-net).

## Serving and latency evidence

The ONNX session is loaded once and shared. Runtime graph optimization is enabled; intra- and inter-operation thread counts are one. Calls own their tensor/result objects and dispose native results. There are 32 startup warm-up calls. The scorer rejects nonfinite or out-of-range probabilities.

```bash
dotnet run --project tools/FinancialFraudPlatform.InferenceBenchmark -c Release -- --model artifacts/model/model.onnx --allow-synthetic --concurrency 1 --iterations 20000 --max-p99-ms 15 --out artifacts/benchmark.json
```

Repeat under the target concurrency and hardware only after the baseline is understood. The benchmark records operating system, runtime, CPU count, throughput, allocation estimate and latency distribution; it returns exit code 2 when the p99 target fails. It does not measure cold startup, queuing, feature extraction, database persistence, audit propagation or browser delivery. Native model execution is not a cancellable hard real-time deadline.

A probability from a model trained on synthetic data or a shifted population is not automatically calibrated for real transactions. Validate class imbalance, customer segments, currency, merchant mix, label delay, precision/recall at the chosen review capacity and operational cost before approving a model.

## Promotion and continuous operation

Use immutable versioned model directories. Review the validation/test report, dataset provenance, ONNX parity, load and latency evidence, and thresholds. Record the approved manifest's SHA-256 in `Model__ExpectedManifestSha256`, set `Model__AllowSynthetic=false`, mount the corresponding model directory read-only and start a new detection deployment. A scored decision retains the model digest. Do not overwrite weights under an existing deployment and assume they hot-reload; the worker intentionally keeps the loaded session.

Continuous feature updates are implemented. Automatic retraining, automatic threshold adjustment and autonomous model promotion are not enabled. Production drift monitoring needs adjudicated outcomes and segment-aware data quality measures; these cannot be generated from nonexistent real labels.
