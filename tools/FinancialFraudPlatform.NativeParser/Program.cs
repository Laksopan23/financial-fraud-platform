using System.Buffers;
using FinancialFraudPlatform.Ingestion.Application;

if (args.Length != 1) { Console.Error.WriteLine("Usage: FinancialFraudPlatform.NativeParser frame-file.bin"); return 1; }
byte[] bytes = File.ReadAllBytes(args[0]);
var remaining = new ReadOnlySequence<byte>(bytes);
long start = GC.GetAllocatedBytesForCurrentThread();
int frames = 0;
while (BinaryTransactionParser.TryRead(ref remaining, out _)) frames++;
long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
if (!remaining.IsEmpty) { Console.Error.WriteLine("truncated_frame"); return 2; }
Console.WriteLine($"Frames: {frames}; parser allocated bytes: {allocated}. Numeric fields use Span; returned objects and text allocate.");
return 0;
