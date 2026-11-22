using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Xml;
using Fluorite.Core.Net;

namespace Fluorite.Modding.Util;

/// <summary>HTTP helpers that walk mirror candidates (official/BMCLAPI) until one succeeds.</summary>
internal static class Remote
{
    public static async Task<byte[]> GetBytesAsync(HttpClient http, MirrorTable mirrors, string url, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var uri in mirrors.Candidates(url))
        {
            try
            {
                return await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException($"Could not fetch {url}");
    }

    public static async Task<T> GetJsonAsync<T>(HttpClient http, MirrorTable mirrors, string url, JsonTypeInfo<T> info, CancellationToken ct)
    {
        var bytes = await GetBytesAsync(http, mirrors, url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, info) ?? throw new InvalidDataException($"Empty response from {url}");
    }

    /// <summary>Versions listed in a maven-metadata.xml, in document order.</summary>
    public static IReadOnlyList<string> ParseMavenVersions(byte[] xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        var nodes = doc.SelectNodes("/metadata/versioning/versions/version");
        var result = new List<string>();
        if (nodes is null)
            return result;
        foreach (XmlNode n in nodes)
        {
            if (!string.IsNullOrWhiteSpace(n.InnerText))
                result.Add(n.InnerText.Trim());
        }
        return result;
    }
}
