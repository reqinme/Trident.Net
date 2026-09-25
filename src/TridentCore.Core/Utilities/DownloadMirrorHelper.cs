namespace TridentCore.Core.Utilities;

public static class DownloadMirrorHelper
{
    private const string ModeVariable = "TRIDENT_DOWNLOAD_MIRROR";
    private const string BaseVariable = "TRIDENT_DOWNLOAD_MIRROR_BASE";
    private const string BmclapiMode = "bmclapi";
    private const string BmclapiBase = "https://bmclapi2.bangbang93.com";

    private static readonly (string Host, string? Prefix, string? Trim)[] Routes =
    [
        ("libraries.minecraft.net", "maven", null),
        ("maven.neoforged.net", "maven", "releases"),
        ("maven.minecraftforge.net", "maven", null),
        ("maven.fabricmc.net", "maven", null),
        ("resources.download.minecraft.net", "assets", null),
        ("piston-data.mojang.com", null, null),
        ("piston-meta.mojang.com", null, null)
    ];

    // The mirror chain redirects Maven requests to education-network mirrors that answer 403 to a request
    // without a User-Agent, and HttpClient sends none by default.
    public const string UserAgent = "TridentCore/1.0 (+https://github.com/TridentCore/Trident.Net)";

    // Mirrors stay opt-in: an unset variable must leave upstream download behavior byte-for-byte unchanged.
    public static IReadOnlyList<Uri> Candidates(Uri url)
    {
        var rewritten = Rewrite(url);
        return rewritten.Equals(url) ? [url] : [rewritten, url];
    }

    public static bool IsMirrored(Uri url) =>
        GetBase() is { } baseAddress && string.Equals(url.Host, baseAddress.Host, StringComparison.OrdinalIgnoreCase);

    private static Uri Rewrite(Uri url)
    {
        var baseAddress = GetBase();
        if (baseAddress is null)
            return url;
        foreach (var (host, prefix, trim) in Routes)
        {
            if (!string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase))
                continue;
            var path = url.AbsolutePath;
            if (trim is not null)
            {
                if (!path.StartsWith("/" + trim + "/", StringComparison.OrdinalIgnoreCase))
                    return url;
                path = path[(trim.Length + 1)..];
            }
            var rewritten = new Uri(baseAddress, prefix is null ? path : "/" + prefix + path);
            return url.Query.Length > 0 ? new Uri(rewritten + url.Query) : rewritten;
        }
        return url;
    }

    private static Uri? GetBase()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(ModeVariable), BmclapiMode,
                StringComparison.OrdinalIgnoreCase))
            return null;
        var configured = Environment.GetEnvironmentVariable(BaseVariable);
        var candidate = string.IsNullOrWhiteSpace(configured) ? BmclapiBase : configured;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) ? parsed : null;
    }
}
