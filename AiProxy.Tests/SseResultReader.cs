using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Tests;

/// <summary>Kjører et IResult mot en HTTP-kontekst i minnet og returnerer kroppen som tekst.</summary>
internal sealed class SseResultReader
{
    private readonly IServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();

    public async Task<string> ReadAsync(IResult result)
    {
        var context = new DefaultHttpContext { RequestServices = _services };
        var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Position = 0;

        using var reader = new StreamReader(body, Encoding.UTF8);

        return await reader.ReadToEndAsync();
    }
}
