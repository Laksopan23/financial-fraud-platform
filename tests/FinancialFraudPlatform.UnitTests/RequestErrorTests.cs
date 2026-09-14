using System.Text;
using FinancialFraudPlatform.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FinancialFraudPlatform.UnitTests;

public sealed class RequestErrorTests
{
    [Theory]
    [InlineData(413, "request_too_large")]
    [InlineData(400, "invalid_request")]
    public async Task Framework_request_status_is_preserved_without_exposing_exception_details(int status, string code)
    {
        const string privateDetail = "Rejected request for 4111111111111111";
        var context = new DefaultHttpContext();
        using var response = new MemoryStream();
        context.Response.Body = response;
        var middleware = new SafeRequestMiddleware(_ => throw new BadHttpRequestException(privateDetail, status),
            NullLogger<SafeRequestMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        context.Response.StatusCode.Should().Be(status);
        string body = Encoding.UTF8.GetString(response.ToArray());
        body.Should().Contain(code).And.NotContain(privateDetail).And.NotContain("4111111111111111");
    }
}
