using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>
/// The one way this feature reaches the network: https only, size-capped, and quick to give up on
/// a host that is not answering.
/// </summary>
/// <remarks>
/// <para>
/// Navigation is interactive, so the cost of a failure is measured in how long the user waits for
/// F12 to do nothing. A host that has just timed out is remembered so the next twenty navigations
/// fall straight through to decompilation — but only for a few minutes, because the common reason
/// a host stops answering is a VPN toggling, and a developer who reconnects should not have to
/// restart the server to get real source back.
/// </para>
/// </remarks>
internal static class HttpFetch
{
    /// <summary>How long a host stays written off after it fails to answer.</summary>
    private static readonly TimeSpan FailureMemo = TimeSpan.FromMinutes(5);

    private static readonly ProductInfoHeaderValue s_userAgent = new("RoslynSense", "1.0");

    /// <summary>General-purpose client. Source files and PDBs come through this one.</summary>
    private static readonly HttpClient s_http = CreateClient(TimeSpan.FromSeconds(30));

    /// <summary>
    /// GitHub's API client, kept separate so an access token can be attached to it and only it.
    /// Redirects are not followed, so a redirect cannot carry that token to another host.
    /// </summary>
    private static readonly HttpClient s_gitHubApi = CreateClient(
        TimeSpan.FromSeconds(20), new HttpClientHandler { AllowAutoRedirect = false });

    private static readonly ConcurrentDictionary<string, DateTimeOffset> s_failedHosts = new();

    private static HttpClient CreateClient(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = timeout;

        // GitHub rejects requests without one, and it is the polite thing to send everywhere else.
        client.DefaultRequestHeaders.UserAgent.Add(s_userAgent);
        return client;
    }

    /// <summary>The GitHub API client, for callers that need to add their own headers.</summary>
    public static HttpClient GitHubApi => s_gitHubApi;

    /// <summary>
    /// Downloads a resource, or returns null when it cannot be had. Never throws for an ordinary
    /// network failure — a caller that cannot fetch falls back rather than failing.
    /// </summary>
    /// <param name="uri">Must be https. Plain http would let anything on the path serve the bytes
    /// that get read as a dependency's source.</param>
    /// <param name="maxBytes">Refuses anything larger, by header and again by what actually arrived.</param>
    /// <param name="client">Which client to use; the general one by default.</param>
    /// <param name="decorate">Applied to the request before it is sent, for per-call headers.</param>
    public static async Task<byte[]?> GetAsync(
        Uri uri,
        long maxBytes,
        CancellationToken ct,
        HttpClient? client = null,
        Action<HttpRequestMessage>? decorate = null)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;

        if (IsWrittenOff(uri.Host))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            decorate?.Invoke(request);

            using var response = await (client ?? s_http)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            if (response.Content.Headers.ContentLength > maxBytes)
                return null;

            byte[] content = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            // A server that under-reports or omits Content-Length still does not get to hand us
            // more than the cap.
            return content.Length > maxBytes ? null : content;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The client's own timeout surfaces as a cancellation with our token unset.
            WriteOff(uri.Host);
            return null;
        }
        catch (HttpRequestException)
        {
            WriteOff(uri.Host);
            return null;
        }
    }

    /// <summary>
    /// Sends a request and hands the caller the response to inspect, for callers that need the
    /// status code or the headers rather than only the body.
    /// </summary>
    /// <returns>Null when the host is written off or unreachable; the response otherwise, at any
    /// status code. The caller disposes it.</returns>
    public static async Task<HttpResponseMessage?> SendAsync(
        HttpRequestMessage request, CancellationToken ct, HttpClient? client = null)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || IsWrittenOff(uri.Host))
        {
            request.Dispose();
            return null;
        }

        try
        {
            return await (client ?? s_http)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            WriteOff(uri.Host);
            return null;
        }
        finally
        {
            request.Dispose();
        }
    }

    private static bool IsWrittenOff(string host)
    {
        if (!s_failedHosts.TryGetValue(host, out var until))
            return false;

        if (DateTimeOffset.UtcNow < until)
            return true;

        s_failedHosts.TryRemove(host, out _);
        return false;
    }

    private static void WriteOff(string host)
    {
        s_failedHosts[host] = DateTimeOffset.UtcNow + FailureMemo;
        ServiceLog.Warn(
            $"'{host}' did not answer; external source from it is skipped for " +
            $"{FailureMemo.TotalMinutes:0} minutes.",
            key: $"external-host-down:{host}");
    }

    /// <summary>Forgets which hosts are written off. For tests.</summary>
    internal static void ResetFailedHosts() => s_failedHosts.Clear();
}
