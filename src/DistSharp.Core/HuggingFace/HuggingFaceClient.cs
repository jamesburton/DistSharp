using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DistSharp.Core.HuggingFace;

/// <summary>Minimal Hugging Face Hub REST client for creating dataset repositories and uploading files.</summary>
public sealed class HuggingFaceClient
{
    private const string BaseUrl = "https://huggingface.co";

    private readonly HttpClient http;
    private readonly string token;

    /// <summary>Initializes a new instance of the <see cref="HuggingFaceClient"/> class.</summary>
    /// <param name="http">An <see cref="HttpClient"/>. Base address need not be set.</param>
    /// <param name="token">An HF API token with <c>write</c> scope.</param>
    public HuggingFaceClient(HttpClient http, string token)
    {
        this.http = http;
        this.token = token;
    }

    /// <summary>Creates the dataset repo if it does not already exist.</summary>
    /// <param name="repoId">The repository identifier, e.g. <c>username/dataset-name</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the repo exists.</returns>
    public async Task EnsureRepoAsync(string repoId, CancellationToken cancellationToken)
    {
        var (org, name) = SplitRepoId(repoId);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/repos/create");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.token);

        var body = new Dictionary<string, object?>
        {
            ["type"] = "dataset",
            ["name"] = name,
        };
        if (!string.IsNullOrEmpty(org))
        {
            body["organization"] = org;
        }

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"HF repo creation failed: HTTP {(int)response.StatusCode}: {responseBody}");
    }

    /// <summary>Uploads <paramref name="localPath"/> to <paramref name="remotePath"/> in the dataset repo.</summary>
    /// <param name="repoId">The repository identifier, e.g. <c>username/dataset-name</c>.</param>
    /// <param name="remotePath">The path inside the repo (e.g. <c>train/data.jsonl</c>).</param>
    /// <param name="localPath">The local file to upload.</param>
    /// <param name="commitMessage">The commit message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the file has been uploaded.</returns>
    public async Task UploadFileAsync(string repoId, string remotePath, string localPath, string commitMessage, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/api/datasets/{repoId}/upload/main/{Uri.EscapeDataString(remotePath)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.token);

        await using var stream = File.OpenRead(localPath);
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.Add("X-Commit-Message", commitMessage);

        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"HF upload failed for {remotePath}: HTTP {(int)response.StatusCode}: {responseBody}");
    }

    private static (string Organization, string Name) SplitRepoId(string repoId)
    {
        var slash = repoId.IndexOf('/');
        if (slash < 0)
        {
            return (string.Empty, repoId);
        }

        return (repoId[..slash], repoId[(slash + 1)..]);
    }
}
