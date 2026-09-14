using System.Diagnostics;
using System.Security.Cryptography;
using FinancialFraudPlatform.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FinancialFraudPlatform.Detection.ML;

public sealed class ModelManifest
{
    public string ModelSha256 { get; set; } = "";
    public string FeatureSchema { get; set; } = FeatureVector.SchemaVersion;
    public string[] FeatureNames { get; set; } = FeatureVector.Names;
    public string Currency { get; set; } = "USD";
    public bool SyntheticDemo { get; set; }
    public float MediumThreshold { get; set; } = 0.5f;
    public float CriticalThreshold { get; set; } = 0.85f;
    public string Trainer { get; set; } = "ML.NET FastTree binary classifier";
    public DateTimeOffset TrainedAt { get; set; }
}
public sealed class OnnxFraudScorer : IFraudScorer, IDisposable
{
    private readonly InferenceSession session;
    public ModelManifest Manifest { get; }
    public OnnxFraudScorer(string modelPath, bool allowSynthetic = false, string? expectedManifestSha256 = null)
    {
        string manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(modelPath))!, "manifest.json");
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        if (expectedManifestSha256 is not null && !Convert.ToHexString(SHA256.HashData(manifestBytes)).Equals(expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("manifest_integrity_failure");
        Manifest = Json.Decode<ModelManifest>(System.Text.Encoding.UTF8.GetString(manifestBytes));
        byte[] model = File.ReadAllBytes(modelPath);
        string digest = Convert.ToHexString(SHA256.HashData(model));
        if (!digest.Equals(Manifest.ModelSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("model_integrity_failure");
        if (Manifest.FeatureSchema != FeatureVector.SchemaVersion || !Manifest.FeatureNames.SequenceEqual(FeatureVector.Names))
            throw new InvalidDataException("feature_schema_mismatch");
        if (Manifest.Currency != "USD") throw new InvalidDataException("unsupported_model_currency");
        if (Manifest.SyntheticDemo && !allowSynthetic) throw new InvalidOperationException("synthetic_model_not_authorized");
        _ = RiskThresholds.Classify(0, Manifest.MediumThreshold, Manifest.CriticalThreshold);
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 1, InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        session = new InferenceSession(model, options);
        if (session.InputMetadata.Count != 1 || !session.InputMetadata.TryGetValue("Features", out var input)
            || input.ElementType != typeof(float) || input.Dimensions.Length != 2 || input.Dimensions[1] != FeatureVector.Count
            || !session.OutputMetadata.ContainsKey("Probability"))
        { session.Dispose(); throw new InvalidDataException("onnx_contract_mismatch"); }
        for (int i = 0; i < 32; i++) _ = Score(new(10, 1, 1, 1, 0, 0, 0, 0.1f, 0, 0, 0));
    }
    public RiskAssessment Score(FeatureVector features)
    {
        long start = Stopwatch.GetTimestamp();
        var tensor = new DenseTensor<float>(features.ToArray(), [1, FeatureVector.Count]);
        var input = NamedOnnxValue.CreateFromTensor("Features", tensor);
        using var output = session.Run([input], ["Probability"]);
        float probability = output.Single().AsEnumerable<float>().Single();
        RiskTier tier = RiskThresholds.Classify(probability, Manifest.MediumThreshold, Manifest.CriticalThreshold);
        return new(probability, tier, Manifest.ModelSha256, Manifest.SyntheticDemo, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    public void Dispose() => session.Dispose();
}
