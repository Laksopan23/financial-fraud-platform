using System.Diagnostics;
using System.Text.Json;
using FinancialFraudPlatform.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace FinancialFraudPlatform.Security;

// Untrusted free text, formatted messages, exception messages, headers and scopes are never emitted.
public sealed class SafeLogProvider(TextWriter? output = null) : ILoggerProvider
{
    private readonly TextWriter writer = TextWriter.Synchronized(output ?? Console.Out);
    public ILogger CreateLogger(string categoryName) => new SafeLogger(writer, categoryName);
    public void Dispose() { }
    private sealed class SafeLogger(TextWriter writer, string category) : ILogger
    {
        private static readonly HashSet<string> Allowed = ["status_code", "duration_ms", "route", "trace_id"];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var safe = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                foreach (var property in properties)
                    if (Allowed.Contains(property.Key)) safe[property.Key] = property.Value;
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow, level = level.ToString(), category,
                event_code = eventId.Id, exception_type = exception?.GetType().Name, fields = safe
            }));
        }
    }
}
public sealed class SafeRequestMiddleware(RequestDelegate next, ILogger<SafeRequestMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        long start = Stopwatch.GetTimestamp();
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception error)
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.Clear();
            context.Response.StatusCode = error switch
            {
                ValidationException => 400, ConflictException => 409, RateLimitExceededException => 429,
                UnauthorizedAccessException => 403, BadHttpRequestException requestError => requestError.StatusCode,
                _ => 503
            };
            string code = error switch
            {
                ValidationException or ConflictException or RateLimitExceededException => error.Message,
                UnauthorizedAccessException => "forbidden",
                BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => "request_too_large",
                BadHttpRequestException => "invalid_request",
                _ => "temporarily_unavailable"
            };
            logger.LogError(new EventId(1102), error, "request_failed");
            await context.Response.WriteAsJsonAsync(new { error = code, traceId = context.TraceIdentifier }, context.RequestAborted);
        }
        finally
        {
            string route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            logger.LogInformation(new EventId(1101), "{route} {status_code} {duration_ms} {trace_id}", route,
                context.Response.StatusCode, Stopwatch.GetElapsedTime(start).TotalMilliseconds, context.TraceIdentifier);
        }
    }
}
