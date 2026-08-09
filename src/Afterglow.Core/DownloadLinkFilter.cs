using Afterglow.Core.Models;

namespace Afterglow.Core;

public static class DownloadLinkFilter
{
    public static IEnumerable<DownloadLink> UsefulOnly(IEnumerable<DownloadLink> links) =>
        DownloadLinkNormalizer.NormalizeAll(links).Select(n => new DownloadLink
        {
            Url = n.Url,
            Host = n.Host,
            Label = n.Platform ?? n.DisplayName,
            Title = n.Title
        });
}
