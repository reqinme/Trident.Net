using TridentCore.Abstractions.Utilities;

namespace TridentCore.Core.Utilities;

public static class DownloadHelper
{
    public static async Task DownloadAsync(HttpClient client, Uri url, string path, FileHash? hash, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Candidates are ordered mirror-first; the origin stays as the fallback so a mirror outage,
        // a rate limit, or a redirect target that rejects the request cannot break a deployment.
        var candidates = DownloadMirrorHelper.Candidates(url);
        Exception? failure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                await TransferAsync(client, candidate, path, hash, token).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                failure = exception;
            }
        }
        throw failure!;
    }

    private static async Task TransferAsync(HttpClient client, Uri url, string path, FileHash? hash, CancellationToken token)
    {
        var temporary = path + ".downloading-" + Guid.NewGuid().ToString("N");
        var attemptTimeout = DownloadPolicyHelper.AttemptTimeout;
        using CancellationTokenSource? attempt =
            attemptTimeout.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
        attempt?.CancelAfter(attemptTimeout ?? Timeout.InfiniteTimeSpan);
        var scope = attempt?.Token ?? token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (DownloadMirrorHelper.IsMirrored(url))
                request.Headers.UserAgent.ParseAdd(DownloadMirrorHelper.UserAgent);
            using var response = await client
                                      .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, scope)
                                      .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(scope).ConfigureAwait(false))
            await using (var output = File.Create(temporary))
                await input.CopyToAsync(output, scope).ConfigureAwait(false);
            if (!FileHelper.VerifyModified(temporary, null, hash))
                throw new InvalidDataException($"Downloaded file failed verification: {path}");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
