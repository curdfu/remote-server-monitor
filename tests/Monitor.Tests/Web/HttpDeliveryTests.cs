using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Monitor.WebApi.Extensions;

namespace Monitor.Tests.Web;

public sealed class HttpDeliveryTests
{
    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task CompressesJsonAndDisablesApiCaching(string encoding)
    {
        using var fixture = new DeliveryFixture();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/sample");
        request.Headers.AcceptEncoding.ParseAdd(encoding);
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(encoding, response.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var payload = await response.Content.ReadAsByteArrayAsync();
        await using var source = new MemoryStream(payload);
        await using Stream decompressed = encoding == "br"
            ? new BrotliStream(source, CompressionMode.Decompress)
            : new GZipStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressed);
        Assert.Contains(new string('x', 8192), await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("/assets/index-Ab12cd34.js", "public,max-age=31536000,immutable")]
    [InlineData("/assets/theme.css", "no-cache")]
    [InlineData("/index.html", "no-cache")]
    [InlineData("/network", "no-cache")]
    public async Task CachesOnlyContentHashedAssets(string path, string expected)
    {
        using var fixture = new DeliveryFixture();
        using var response = await fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, string.Join(',', response.Headers.GetValues("Cache-Control")).Replace(" ", string.Empty));
        Assert.NotNull(response.Headers.ETag);
        if (path == "/network")
        {
            Assert.Contains("Monitor test", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/hubs/monitor/negotiate")]
    public async Task LiveEndpointsNeverReceiveLongLivedCaching(string path)
    {
        using var fixture = new DeliveryFixture();
        using var response = await fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task DoesNotCompressHttpsWithoutAnExplicitPolicy()
    {
        using var fixture = new DeliveryFixture();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost/api/sample");
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    private sealed class DeliveryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "monitor-http-tests", Guid.NewGuid().ToString("N"));
        private readonly PhysicalFileProvider _fileProvider;
        private readonly TestServer _server;

        public DeliveryFixture()
        {
            Directory.CreateDirectory(Path.Combine(_directory, "assets"));
            File.WriteAllText(Path.Combine(_directory, "index.html"), "<!doctype html><title>Monitor test</title>");
            File.WriteAllText(Path.Combine(_directory, "assets", "index-Ab12cd34.js"), "console.log('test');");
            File.WriteAllText(Path.Combine(_directory, "assets", "theme.css"), "body { color: inherit; }");
            _fileProvider = new PhysicalFileProvider(_directory);
            _server = new TestServer(new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddMonitorHttpDelivery();
                })
                .Configure(app =>
                {
                    var files = HttpDeliveryExtensions.CreateMonitorStaticFileOptions();
                    files.FileProvider = _fileProvider;
                    app.UseMonitorHttpDelivery();
                    app.UseStaticFiles(files);
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/api/sample", () => new { text = new string('x', 8192) });
                        endpoints.MapGet("/health", () => new { status = "ok" });
                        endpoints.MapGet("/hubs/monitor/negotiate", () => new { connectionId = "test" });
                        endpoints.MapFallbackToFile("index.html", files);
                    });
                }));
            Client = _server.CreateClient();
        }

        public HttpClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            _server.Dispose();
            _fileProvider.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
