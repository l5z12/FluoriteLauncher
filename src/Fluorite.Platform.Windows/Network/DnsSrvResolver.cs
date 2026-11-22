using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Fluorite.Core.Servers;

namespace Fluorite.Platform.Windows.Network;

/// <summary>
/// Resolves <c>_minecraft._tcp.&lt;host&gt;</c> with the Windows DNS client (.NET has no SRV lookup), so servers that
/// publish an SRV record ping and join on the right host and port.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class DnsSrvResolver : ISrvResolver
{
    private const ushort DNS_TYPE_SRV = 0x0021;
    private const uint DNS_QUERY_STANDARD = 0;
    private const int DnsFreeRecordList = 1;

    public Task<(string Host, int Port)?> ResolveMinecraftAsync(string host, CancellationToken ct = default) =>
        Task.Run(() => Resolve("_minecraft._tcp." + host.TrimEnd('.')), ct);

    private static (string Host, int Port)? Resolve(string name)
    {
        var status = DnsQuery_W(name, DNS_TYPE_SRV, DNS_QUERY_STANDARD, 0, out var results, 0);
        if (status != 0 || results == 0)
            return null;
        try
        {
            (string Host, int Port, ushort Priority, ushort Weight)? best = null;
            for (var p = results; p != 0;)
            {
                var record = Marshal.PtrToStructure<DnsRecordSrv>(p);
                if (record.wType == DNS_TYPE_SRV && record.pNameTarget != 0 && Marshal.PtrToStringUni(record.pNameTarget) is { Length: > 0 } target)
                {
                    // Lowest priority wins; among equals, the highest weight.
                    if (best is not { } b || record.wPriority < b.Priority || (record.wPriority == b.Priority && record.wWeight > b.Weight))
                        best = (target.TrimEnd('.'), record.wPort, record.wPriority, record.wWeight);
                }
                p = record.pNext;
            }
            return best is { } r ? (r.Host, r.Port) : null;
        }
        finally
        {
            DnsRecordListFree(results, DnsFreeRecordList);
        }
    }

    /// <summary>DNS_RECORDW header followed by its DNS_SRV_DATAW payload.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DnsRecordSrv
    {
        public nint pNext;
        public nint pName;
        public ushort wType;
        public ushort wDataLength;
        public uint Flags;
        public uint dwTtl;
        public uint dwReserved;
        public nint pNameTarget;
        public ushort wPriority;
        public ushort wWeight;
        public ushort wPort;
        public ushort Pad;
    }

    [LibraryImport("dnsapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DnsQuery_W(string pszName, ushort wType, uint options, nint pExtra, out nint ppQueryResults, nint pReserved);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsRecordListFree(nint pRecordList, int freeType);
}
