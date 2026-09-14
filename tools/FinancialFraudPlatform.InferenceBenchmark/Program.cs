using System.Diagnostics;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Detection.ML;

static string? Option(string[] args, string key)
{
    int index = Array.IndexOf(args, key);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
string model = Option(args, "--model") ?? "artifacts/model/model.onnx";
int iterations = int.Parse(Option(args, "--iterations") ?? "20000");
int concurrency = int.Parse(Option(args, "--concurrency") ?? "1");
double budget = double.Parse(Option(args, "--max-p99-ms") ?? "15", System.Globalization.CultureInfo.InvariantCulture);
if (iterations is < 1000 or > 1000000 || concurrency is < 1 or > 128) throw new ArgumentException("invalid_benchmark_bounds");
using var scorer = new OnnxFraudScorer(model, allowSynthetic: args.Contains("--allow-synthetic"));
FeatureVector[] vectors = [new(50, 1, 2, 10, 0, 0, 0, .1f, 1, 1, 0), new(3000, 18, 60, 250, 500, 2000, 10, .9f, 8, 1, 0)];
for (int i = 0; i < 1000; i++) scorer.Score(vectors[i % vectors.Length]);
var durations = new double[iterations];
long bytesBefore = GC.GetTotalAllocatedBytes(precise: true);
long start = Stopwatch.GetTimestamp();
Parallel.For(0, iterations, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, i =>
{
    long tick = Stopwatch.GetTimestamp();
    scorer.Score(vectors[i % vectors.Length]);
    durations[i] = Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
});
double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
long allocated = GC.GetTotalAllocatedBytes(precise: true) - bytesBefore;
Array.Sort(durations);
double Percentile(double p) => durations[(int)Math.Ceiling(p * iterations) - 1];
var report = new
{
    iterations, concurrency, p50Ms = Percentile(.5), p95Ms = Percentile(.95), p99Ms = Percentile(.99),
    maxMs = durations[^1], callsPerSecond = iterations / seconds, approximateAllocatedBytesPerCall = allocated / (double)iterations,
    budgetMs = budget, pass = Percentile(.99) < budget, scorer.Manifest.ModelSha256,
    cpuCount = Environment.ProcessorCount, runtime = Environment.Version.ToString(),
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
};
string output = Option(args, "--out") ?? "artifacts/benchmark.json";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, Json.Encode(report));
Console.WriteLine(Json.Encode(report));
return report.pass ? 0 : 2;
