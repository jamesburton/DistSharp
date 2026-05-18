using System.Net;
using System.Text;
using DistSharp.Providers.Onnx;
using FluentAssertions;
using Xunit;

namespace DistSharp.Providers.Tests.Onnx;

public sealed class HuggingFaceModelCacheTests : IDisposable
{
    private readonly string testRoot;

    public HuggingFaceModelCacheTests()
    {
        this.testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.testRoot))
        {
            Directory.Delete(this.testRoot, recursive: true);
        }
    }

    // ── Variant-priority resolution (filesystem only) ─────────────────────────
    [Fact]
    public void ResolveLocalVariant_ReturnsOverridePath_WhenVariantOverrideMatches()
    {
        var snapshotDir = this.CreateSnapshotDir("microsoft", "Phi-4-mini-instruct-onnx", "abc123");
        CreateVariantDir(snapshotDir, "cpu-fp32");
        var overrideDir = CreateVariantDir(snapshotDir, "cpu-int4-rtn-block-32-acc-level-4");

        var cache = new HuggingFaceModelCache(this.testRoot, new HttpClient());

        var result = cache.ResolveLocalVariant("microsoft/Phi-4-mini-instruct-onnx", variantOverride: "cpu-int4-rtn-block-32-acc-level-4");

        result.Should().Be(overrideDir);
    }

    [Fact]
    public void ResolveLocalVariant_PrefersCpuInt4_OverCpuFp16()
    {
        var snapshotDir = this.CreateSnapshotDir("microsoft", "Phi-4-mini-instruct-onnx", "abc123");
        CreateVariantDir(snapshotDir, "cpu-fp16");
        var int4Dir = CreateVariantDir(snapshotDir, "cpu-int4-rtn-block-32-acc-level-4");

        var cache = new HuggingFaceModelCache(this.testRoot, new HttpClient());

        var result = cache.ResolveLocalVariant("microsoft/Phi-4-mini-instruct-onnx", variantOverride: null);

        result.Should().Be(int4Dir);
    }

    [Fact]
    public void ResolveLocalVariant_FallsBackToCpuFp16_WhenNoCpuInt4Present()
    {
        var snapshotDir = this.CreateSnapshotDir("microsoft", "Phi-4-mini-instruct-onnx", "abc123");
        var fp16Dir = CreateVariantDir(snapshotDir, "cpu-fp16");
        CreateVariantDir(snapshotDir, "cpu-fp32");

        var cache = new HuggingFaceModelCache(this.testRoot, new HttpClient());

        var result = cache.ResolveLocalVariant("microsoft/Phi-4-mini-instruct-onnx", variantOverride: null);

        result.Should().Be(fp16Dir);
    }

    [Fact]
    public void ResolveLocalVariant_FallsBackToCpuFp32_WhenNoHigherPriorityPresent()
    {
        var snapshotDir = this.CreateSnapshotDir("microsoft", "Phi-4-mini-instruct-onnx", "abc123");
        var fp32Dir = CreateVariantDir(snapshotDir, "cpu-fp32");

        var cache = new HuggingFaceModelCache(this.testRoot, new HttpClient());

        var result = cache.ResolveLocalVariant("microsoft/Phi-4-mini-instruct-onnx", variantOverride: null);

        result.Should().Be(fp32Dir);
    }

    [Fact]
    public void ResolveLocalVariant_ReturnsNull_WhenRepoNotCached()
    {
        var cache = new HuggingFaceModelCache(this.testRoot, new HttpClient());

        var result = cache.ResolveLocalVariant("microsoft/NotCachedModel", variantOverride: null);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveLocalVariant_PrefersExactCpuInt4Match_OverWildcardInt4()
    {
        var snapshotDir = this.CreateSnapshotDir("microsoft", "Phi-4-mini-instruct-onnx", "abc123");
        CreateVariantDir(snapshotDir, "cpu-int4-other-variant");
        var exactDir = CreateVariantDir(snapshotDir, "cpu-int4-rtn-block-32-acc-level-4");

        var cache = new HuggingFaceModelCache(this.testRoot, new HttpClient());

        var result = cache.ResolveLocalVariant("microsoft/Phi-4-mini-instruct-onnx", variantOverride: null);

        result.Should().Be(exactDir);
    }

    // ── Download path (HTTP-mocked) ───────────────────────────────────────────
    [Fact]
    public async Task ResolveVariantAsync_TriggersDownload_WhenRepoNotCached()
    {
        var repoId = "microsoft/Phi-4-mini-instruct-onnx";
        var sha = "deadbeef";

        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.ToString();

            if (uri.Contains($"/api/models/{repoId}") && !uri.Contains("/tree/"))
            {
                var body = $"{{\"sha\":\"{sha}\"}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }

            if (uri.Contains($"/api/models/{repoId}/tree/{sha}"))
            {
                var treeJson = "[{\"path\":\"cpu-int4-rtn-block-32-acc-level-4/model.onnx\",\"type\":\"file\",\"size\":100}]";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(treeJson, Encoding.UTF8, "application/json"),
                };
            }

            if (uri.Contains($"/{repoId}/resolve/{sha}/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("fake-model-data", Encoding.UTF8),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://huggingface.co") };
        var cache = new HuggingFaceModelCache(this.testRoot, http);

        var result = await cache.ResolveVariantAsync(repoId, variantOverride: null, CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().Contain("cpu-int4-rtn-block-32-acc-level-4");
        Directory.Exists(result!).Should().BeTrue();

        var modelFile = Path.Combine(result, "model.onnx");
        File.Exists(modelFile).Should().BeTrue();
    }

    // ── ListCachedRepos ───────────────────────────────────────────────────────
    [Fact]
    public void ListCachedRepos_ReturnsRepoIds_ForExistingCacheEntries()
    {
        var hubDir = Path.Combine(this.testRoot, "hub");
        Directory.CreateDirectory(Path.Combine(hubDir, "models--microsoft--Phi-4-mini-instruct-onnx"));
        Directory.CreateDirectory(Path.Combine(hubDir, "models--meta-llama--Llama-3-8b-onnx"));

        // datasets-- prefix should be ignored
        Directory.CreateDirectory(Path.Combine(hubDir, "datasets--some--dataset"));

        var cache = new HuggingFaceModelCache(this.testRoot, new HttpClient());

        var repos = cache.ListCachedRepos().ToList();

        repos.Should().Contain("microsoft/Phi-4-mini-instruct-onnx");
        repos.Should().Contain("meta-llama/Llama-3-8b-onnx");
        repos.Should().NotContain(r => r.StartsWith("datasets--", StringComparison.Ordinal));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string CreateVariantDir(string snapshotDir, string variant)
    {
        var variantDir = Path.Combine(snapshotDir, variant);
        Directory.CreateDirectory(variantDir);
        File.WriteAllText(Path.Combine(variantDir, "genai_config.json"), "{}");
        return variantDir;
    }

    private string CreateSnapshotDir(string owner, string name, string sha)
    {
        var hubDir = Path.Combine(this.testRoot, "hub");
        var repoDir = Path.Combine(hubDir, $"models--{owner}--{name}");
        var snapshotDir = Path.Combine(repoDir, "snapshots", sha);
        Directory.CreateDirectory(snapshotDir);
        return snapshotDir;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(this.respond(request));
    }
}
