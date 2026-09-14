using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
try
{
    using var response = await client.GetAsync(args.FirstOrDefault() ?? "http://localhost:8080/health/ready");
    return response.IsSuccessStatusCode ? 0 : 1;
}
catch { return 1; }
