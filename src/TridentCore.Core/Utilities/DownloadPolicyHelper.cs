namespace TridentCore.Core.Utilities;

public static class DownloadPolicyHelper
{
    private const string ParallelismVariable = "TRIDENT_DOWNLOAD_PARALLELISM";
    private const string AttemptTimeoutVariable = "TRIDENT_DOWNLOAD_ATTEMPT_TIMEOUT";

    public static int Parallelism =>
        int.TryParse(Environment.GetEnvironmentVariable(ParallelismVariable), out var value) && value > 0
            ? value
            : Math.Max(Environment.ProcessorCount - 1, 1);

    // A slow mirror hop would otherwise consume the whole HttpClient timeout before the origin fallback
    // engages, so this bound is meant to be set well below the client default when a mirror is enabled.
    public static TimeSpan? AttemptTimeout =>
        int.TryParse(Environment.GetEnvironmentVariable(AttemptTimeoutVariable), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
