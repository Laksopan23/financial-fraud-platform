using System.Security.Cryptography;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Detection.ML;
using FluentAssertions;
using Xunit;

namespace FinancialFraudPlatform.UnitTests;

public sealed class ModelIntegrityTests
{
    [Fact]
    public void Tampered_model_is_rejected_before_native_inference()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string model = Path.Combine(directory, "model.onnx");
            File.WriteAllBytes(model, [1, 2, 3]);
            File.WriteAllText(Path.Combine(directory, "manifest.json"), Json.Encode(new ModelManifest { ModelSha256 = new string('0', 64) }));
            Action load = () => { using var scorer = new OnnxFraudScorer(model); };
            load.Should().Throw<InvalidDataException>().WithMessage("model_integrity_failure");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public void Unapproved_manifest_is_rejected_before_model_loading()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.json"), "{}");
            Action load = () => { using var scorer = new OnnxFraudScorer(Path.Combine(directory, "model.onnx"), expectedManifestSha256: new string('A',64)); };
            load.Should().Throw<InvalidDataException>().WithMessage("manifest_integrity_failure");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
