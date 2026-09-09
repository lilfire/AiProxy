using System.Net;
using System.Net.Http.Headers;
using AiProxy.Contracts;

namespace AiProxy.Services;

public sealed class ImageInputResolver : IImageInputResolver
{
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private const int MaxRequestBytes = 20 * 1024 * 1024;
    private const int MaxRedirects = 3;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<ResolvedImages> ResolveAsync(IEnumerable<OpenAiImageInput> inputs, CancellationToken cancellationToken = default)
    {
        var paths = new List<string>();
        var totalBytes = 0;
        try
        {
            foreach (var input in inputs)
            {
                var bytes = await ReadImageAsync(input.Url, cancellationToken);
                totalBytes += bytes.Length;
                if (totalBytes > MaxRequestBytes)
                    throw new ImageInputException("Bildevedleggene er større enn grensen på 20 MB per forespørsel.");

                var extension = GetImageExtension(bytes);
                if (extension == null)
                    throw new ImageInputException("Bare PNG, JPEG og WebP støttes som bildevedlegg.");

                var path = Path.Combine(Path.GetTempPath(), $"aiproxy-image-{Guid.NewGuid():N}{extension}");
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                paths.Add(path);
            }

            return new ResolvedImages(paths);
        }
        catch
        {
            await new ResolvedImages(paths).DisposeAsync();
            throw;
        }
    }

    private static async Task<byte[]> ReadImageAsync(string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ImageInputException("Bilde-URL mangler.");

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return ReadDataUrl(value);

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ImageInputException("Bilde-URL må være en data:-URL eller en HTTPS-URL.");

        for (var redirects = 0; ; redirects++)
        {
            await EnsurePublicHostAsync(uri, cancellationToken);
            using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= MaxRedirects || response.Headers.Location == null)
                    throw new ImageInputException("Bilde-URL har for mange videresendinger.");
                uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                if (uri.Scheme != Uri.UriSchemeHttps)
                    throw new ImageInputException("Bilde-URL kan ikke videresendes til en usikker adresse.");
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new ImageInputException($"Kunne ikke hente bilde-URL (HTTP {(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength is > MaxImageBytes)
                throw new ImageInputException("Bildet er større enn grensen på 10 MB.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await ReadLimitedAsync(stream, cancellationToken);
        }
    }

    private static byte[] ReadDataUrl(string value)
    {
        var comma = value.IndexOf(',');
        if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
            throw new ImageInputException("Data-URL-en må være base64-kodet.");
        try
        {
            var bytes = Convert.FromBase64String(value[(comma + 1)..]);
            if (bytes.Length > MaxImageBytes)
                throw new ImageInputException("Bildet er større enn grensen på 10 MB.");
            return bytes;
        }
        catch (FormatException)
        {
            throw new ImageInputException("Bilde-data er ikke gyldig base64.");
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > MaxImageBytes)
                throw new ImageInputException("Bildet er større enn grensen på 10 MB.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static async Task EnsurePublicHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(IsPrivateOrReserved))
            throw new ImageInputException("Bilde-URL peker til en lokal eller reservert nettadresse.");
    }

    private static bool IsPrivateOrReserved(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any) || address.GetAddressBytes()[0] is 0xfc or 0xfd;
        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 ||
            (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
            (bytes[0] == 169 && bytes[1] == 254) ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 198 && bytes[1] is 18 or 19) ||
            bytes[0] >= 224;
    }

    private static string? GetImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return ".jpg";
        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
}
