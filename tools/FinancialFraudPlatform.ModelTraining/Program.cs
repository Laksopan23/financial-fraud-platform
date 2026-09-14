using System.Globalization;
using System.Security.Cryptography;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Detection.ML;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;

namespace FinancialFraudPlatform.ModelTraining;

public sealed class TrainingRow
{
    public bool Label { get; set; }
    [VectorType(FeatureVector.Count)] public float[] Features { get; set; } = new float[FeatureVector.Count];
}
public sealed class InferenceRow
{
    [VectorType(FeatureVector.Count)] public float[] Features { get; set; } = new float[FeatureVector.Count];
}
public sealed class Prediction
{
    public bool Label { get; set; }
    public bool PredictedLabel { get; set; }
    public float Probability { get; set; }
    public float Score { get; set; }
}
public sealed record TimedRow(long Timestamp, TrainingRow Row);
public static class Program
{
    public static int Main(string[] args)
    {
        try { Train(args); return 0; }
        catch (Exception error) { Console.Error.WriteLine($"Training failed: {error.GetType().Name}: {error.Message}"); return 1; }
    }
    private static string? Option(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private static void Train(string[] args)
    {
        bool demo = args.Contains("--demo");
        string? dataPath = Option(args, "--data");
        if (demo == (dataPath is not null)) throw new ArgumentException("Choose exactly one of --demo or --data numeric-features.csv");
        string output = Path.GetFullPath(Option(args, "--out") ?? "artifacts/model");
        Directory.CreateDirectory(output);
        TimedRow[] ordered = demo ? DemoRows(30000) : ReadCsv(dataPath!);
        if (ordered.Length < 1000) throw new InvalidDataException("At least 1000 rows are required");
        if (ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.Timestamp > pair.Second.Timestamp))
            throw new InvalidDataException("CSV must be in ascending event-time order");
        int trainEnd = (int)(ordered.Length * 0.70), validationEnd = (int)(ordered.Length * 0.85);
        var trainRows = ordered[..trainEnd].Select(x => x.Row).ToArray();
        var validationRows = ordered[trainEnd..validationEnd].Select(x => x.Row).ToArray();
        var testRows = ordered[validationEnd..].Select(x => x.Row).ToArray();
        foreach (var split in new[] { trainRows, validationRows, testRows })
            if (split.Select(x => x.Label).Distinct().Count() != 2) throw new InvalidDataException("Each chronological split must contain both labels");
        var ml = new MLContext(seed: 42);
        IDataView train = ml.Data.LoadFromEnumerable(trainRows);
        var pipeline = ml.Transforms.NormalizeMinMax("Features")
            .Append(ml.BinaryClassification.Trainers.FastTree(new FastTreeBinaryTrainer.Options
            {
                LabelColumnName = "Label", FeatureColumnName = "Features", NumberOfLeaves = 24,
                NumberOfTrees = 100, MinimumExampleCountPerLeaf = 20, LearningRate = 0.1,
                NumberOfThreads = 1
            }));
        var model = pipeline.Fit(train);
        ml.Model.Save(model, train.Schema, Path.Combine(output, "model.zip"));
        string modelPath = Path.Combine(output, "model.onnx");
        // Supply only inference columns; Label is deliberately absent from the exported input schema.
        var inferenceData = ml.Data.LoadFromEnumerable(trainRows.Take(1).Select(x => new InferenceRow { Features = x.Features }));
        using (var onnx = File.Create(modelPath)) ml.Model.ConvertToOnnx(model, inferenceData, onnx);
        var manifest = new ModelManifest
        {
            ModelSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath))),
            SyntheticDemo = demo, Currency = "USD", TrainedAt = DateTimeOffset.UtcNow,
            MediumThreshold = 0.5f, CriticalThreshold = 0.85f
        };
        File.WriteAllText(Path.Combine(output, "manifest.json"), Json.Encode(manifest));
        using var scorer = new OnnxFraudScorer(modelPath, allowSynthetic: demo);
        using var predictor = ml.Model.CreatePredictionEngine<InferenceRow, Prediction>(model);
        double maxParityError = 0;
        foreach (var row in testRows.Take(256))
        {
            float[] v = row.Features;
            var features = new FeatureVector(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10]);
            float expected = predictor.Predict(new InferenceRow { Features = v }).Probability;
            maxParityError = Math.Max(maxParityError, Math.Abs(expected - scorer.Score(features).Probability));
        }
        if (maxParityError > 1e-5) throw new InvalidDataException("ONNX probability parity exceeds tolerance");
        object Evaluate(TrainingRow[] rows)
        {
            var scored = model.Transform(ml.Data.LoadFromEnumerable(rows));
            var metrics = ml.BinaryClassification.Evaluate(scored);
            var predictions = ml.Data.CreateEnumerable<Prediction>(scored, reuseRowObject: false).ToArray();
            return new
            {
                rows = rows.Length, positiveRate = rows.Count(x => x.Label) / (double)rows.Length,
                metrics.AreaUnderRocCurve, metrics.AreaUnderPrecisionRecallCurve, metrics.F1Score,
                metrics.PositivePrecision, metrics.PositiveRecall, metrics.LogLoss,
                brierScore = predictions.Average(x => Math.Pow(x.Probability - (x.Label ? 1 : 0), 2)),
                reviewRate = predictions.Count(x => x.Probability >= manifest.MediumThreshold) / (double)rows.Length,
                criticalRate = predictions.Count(x => x.Probability >= manifest.CriticalThreshold) / (double)rows.Length
            };
        }
        var report = new
        {
            syntheticDemo = demo, source = demo ? "Seeded synthetic feature generator, seed=42" : "Provided precomputed as-of USD features",
            datasetDigest = demo ? Json.Digest(ordered) : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dataPath!))),
            split = "chronological 70/15/15; normalization fitted on training only",
            trainingRows = trainRows.Length, validation = Evaluate(validationRows), test = Evaluate(testRows),
            maxOnnxProbabilityDifference = maxParityError,
            thresholds = "Fixed demonstration thresholds. No business operating point has been approved.",
            manifest.ModelSha256
        };
        File.WriteAllText(Path.Combine(output, "evaluation.json"), Json.Encode(report));
        Console.WriteLine($"Created model.zip, model.onnx, manifest.json and evaluation.json in {output}");
    }
    public static TimedRow[] ReadCsv(string path)
    {
        using var lines = File.ReadLines(path).GetEnumerator();
        string expected = "timestamp,label," + string.Join(",", FeatureVector.Names);
        if (!lines.MoveNext() || lines.Current.TrimStart('\uFEFF') != expected) throw new InvalidDataException("CSV header does not match feature schema");
        var rows = new List<TimedRow>();
        while (lines.MoveNext())
        {
            if (string.IsNullOrWhiteSpace(lines.Current)) continue;
            string[] cells = lines.Current.Split(',');
            if (cells.Length != FeatureVector.Count + 2 || cells[1] is not ("0" or "1")) throw new InvalidDataException("Invalid numeric CSV row");
            long time = long.Parse(cells[0], CultureInfo.InvariantCulture);
            _ = DateTimeOffset.FromUnixTimeMilliseconds(time);
            float[] features = cells[2..].Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            if (features.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Nonfinite training features");
            rows.Add(new(time, new TrainingRow { Label = cells[1] == "1", Features = features }));
        }
        return rows.ToArray();
    }
    public static TimedRow[] DemoRows(int count)
    {
        var random = new Random(42);
        var rows = new TimedRow[count];
        long epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        for (int i = 0; i < count; i++)
        {
            float amount = (float)Math.Exp(1 + random.NextDouble() * 8);
            float one = random.Next(1, 25), five = one + random.Next(0, 80), hour = five + random.Next(0, 300);
            float distance = random.NextDouble() < 0.1 ? random.Next(100, 5000) : (float)random.NextDouble() * 5;
            float speed = Math.Min(100000, distance / (0.01f + (float)random.NextDouble()));
            float z = (float)random.NextDouble() * 12, merchant = (float)random.NextDouble();
            float delta = (float)random.NextDouble() * 12, history = random.NextDouble() < 0.15 ? 0 : 1, late = random.NextDouble() < 0.02 ? 1 : 0;
            double logit = -6.2 + 0.15 * one + 0.25 * z + 2 * merchant + (speed > 900 ? 2.5 : 0) + (amount > 1500 ? 0.8 : 0) + late;
            bool fraud = random.NextDouble() < 1 / (1 + Math.Exp(-logit));
            rows[i] = new(epoch + i * 1000L, new TrainingRow { Label = fraud,
                Features = [amount, one, five, hour, distance, speed, z, merchant, delta, history, late] });
        }
        return rows;
    }
}
