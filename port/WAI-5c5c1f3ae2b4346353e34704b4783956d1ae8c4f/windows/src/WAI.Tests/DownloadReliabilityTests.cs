using System.Net.Http;
using Xunit;
using System.Net;
using System.Security.Cryptography;
using WAI.Core;

namespace WAI.Tests;

public sealed class DownloadReliabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WAI-download-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _bytes = "validated model fixture"u8.ToArray();
    private string Destination => Path.Combine(_directory, "fixture.gguf");
    private string Hash => Convert.ToHexString(SHA256.HashData(_bytes));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task WrongHashNeverBecomesInstalledAndRetryDownloadsAgain()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifiedDownload.DownloadAsync("https://fixture/model", Destination,
            _bytes.Length, new string('0', 64), client: client));
        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(Destination + ".part"));
        await VerifiedDownload.DownloadAsync("https://fixture/model", Destination, _bytes.Length, Hash, client: client);
        Assert.True(VerifiedDownload.IsValid(Destination, _bytes.Length, Hash));
    }

    [Fact]
    public async Task ResumeVerifiesOffsetAndCompleteHash()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Destination + ".part", _bytes[..5]);
        using var client = new HttpClient(new Handler(request => {
            Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(_bytes[5..]) };
            response.Content.Headers.ContentRange = new(5, _bytes.Length - 1, _bytes.Length);
            return response;
        }));
        await VerifiedDownload.DownloadAsync("https://fixture/model", Destination, _bytes.Length, Hash, client: client);
        Assert.Equal(_bytes, File.ReadAllBytes(Destination));
    }

    [Fact]
    public async Task ExistingCorruptionIsReplacedInsteadOfBeingReportedReady()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Destination, new byte[_bytes.Length]);
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) }));
        await VerifiedDownload.DownloadAsync("https://fixture/model", Destination, _bytes.Length, Hash, client: client);
        Assert.True(File.Exists(Destination + ".invalid"));
        Assert.True(VerifiedDownload.IsValid(Destination, _bytes.Length, Hash));
    }

    [Fact]
    public async Task PartialResponseFromWrongOffsetIsRejected()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Destination + ".part", _bytes[..5]);
        using var client = new HttpClient(new Handler(_ => {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(_bytes[5..]) };
            response.Content.Headers.ContentRange = new(4, _bytes.Length - 2, _bytes.Length);
            return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifiedDownload.DownloadAsync("https://fixture/model", Destination,
            _bytes.Length, Hash, client: client));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public void EveryCatalogModelHasPinnedUrlExactSizeAndHash()
    {
        foreach (var model in ModelCatalog.All)
        foreach (var file in model.Files)
        {
            Assert.True(file.Bytes > 0);
            Assert.Matches("^[a-f0-9]{64}$", file.Sha256);
            Assert.DoesNotContain("/main/", file.Url);
            Assert.Matches("/resolve/[a-f0-9]{40}/", file.Url);
        }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
