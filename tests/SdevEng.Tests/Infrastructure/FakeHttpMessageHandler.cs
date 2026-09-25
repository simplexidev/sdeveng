using System.Net;

namespace SdevEng.Tests;

public sealed class FakeHttpMessageHandler(string response, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public string? Body { get; private set; }
    public string? Authorization { get; private set; }
    public string? AuthorizationParameter { get; private set; }
    public bool Fail { get; init; }
    public bool Timeout { get; init; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Authorization = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;
        if (Fail) throw new HttpRequestException("private response body");
        if (Timeout) throw new TaskCanceledException("private endpoint");
        return new(code) { Content = new StringContent(response) };
    }
}
