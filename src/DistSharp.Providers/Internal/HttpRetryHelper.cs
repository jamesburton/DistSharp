using System.Net;
using Microsoft.Extensions.Logging;

namespace DistSharp.Providers.Internal;

/// <summary>Helpers for issuing HTTP requests with retry on transient failures.</summary>
internal static class HttpRetryHelper
{
    private static readonly HashSet<int> RetryableStatusCodes = new()
    {
        (int)HttpStatusCode.RequestTimeout,          // 408
        (int)HttpStatusCode.TooManyRequests,         // 429
        (int)HttpStatusCode.InternalServerError,     // 500
        (int)HttpStatusCode.BadGateway,              // 502
        (int)HttpStatusCode.ServiceUnavailable,      // 503
        (int)HttpStatusCode.GatewayTimeout,          // 504
    };

    /// <summary>Sends <paramref name="buildRequest"/> repeatedly until success or retry budget exhausted. Returns the final <see cref="HttpResponseMessage"/>.</summary>
    /// <param name="http">The <see cref="HttpClient"/> to use.</param>
    /// <param name="buildRequest">Factory that produces a fresh <see cref="HttpRequestMessage"/> for each attempt.</param>
    /// <param name="maxRetries">Maximum number of retry attempts after the initial request.</param>
    /// <param name="initialDelay">Delay before the first retry; doubled on each subsequent retry.</param>
    /// <param name="logger">Logger for retry diagnostics.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The final <see cref="HttpResponseMessage"/>, whether successful or not.</returns>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient http,
        Func<HttpRequestMessage> buildRequest,
        int maxRetries,
        TimeSpan initialDelay,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var delay = initialDelay;
        HttpResponseMessage? response = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            response?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();

            var request = buildRequest();
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode || !RetryableStatusCodes.Contains((int)response.StatusCode))
            {
                return response;
            }

            if (attempt >= maxRetries)
            {
                return response;
            }

            var wait = ComputeWait(response, delay);
            logger.LogDebug(
                "Retrying after {Wait}ms (attempt {Attempt}/{Max}, status {Status})",
                wait.TotalMilliseconds,
                attempt + 1,
                maxRetries,
                (int)response.StatusCode);

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
        }

        return response!;
    }

    private static TimeSpan ComputeWait(HttpResponseMessage response, TimeSpan backoff)
    {
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                return delta;
            }

            if (retryAfter.Date is { } date)
            {
                var wait = date - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    return wait;
                }
            }
        }

        return backoff;
    }
}
