using System.Buffers;
using System.Security.Cryptography;

namespace Fluorite.Modding.Util;

/// <summary>Hashes used by the content platforms: SHA-1/SHA-512 (Modrinth) and the CurseForge fingerprint.</summary>
public sealed record ContentHashes(string Sha1, string Sha512, uint Murmur2)
{
    /// <summary>Computes all three in one read of the file.</summary>
    public static async Task<ContentHashes> ComputeAsync(string path, CancellationToken ct = default)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var murmur = new CurseForgeFingerprint();
        var buffer = new byte[1 << 16];
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, useAsync: true))
        {
            int read;
            while ((read = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                var span = buffer.AsSpan(0, read);
                sha1.AppendData(span);
                sha512.AppendData(span);
                murmur.Append(span);
            }
        }
        return new ContentHashes(
            Convert.ToHexStringLower(sha1.GetHashAndReset()),
            Convert.ToHexStringLower(sha512.GetHashAndReset()),
            murmur.GetHash());
    }
}

/// <summary>
/// CurseForge file fingerprint: 32-bit MurmurHash2 (seed 1) over the file bytes with tab, LF, CR and space removed.
/// MurmurHash2 is seeded with the input length, which is only known after filtering, so the filtered bytes are
/// buffered (mod files are small; this is what every CurseForge client does).
/// </summary>
public sealed class CurseForgeFingerprint
{
    private const uint M = 0x5bd1e995;
    private const int R = 24;
    private readonly ArrayBufferWriter<byte> _filtered = new(1 << 16);

    public void Append(ReadOnlySpan<byte> data)
    {
        Span<byte> chunk = stackalloc byte[4096];
        var n = 0;
        foreach (var b in data)
        {
            if (b is 9 or 10 or 13 or 32)
                continue;
            chunk[n++] = b;
            if (n == chunk.Length)
            {
                _filtered.Write<byte>(chunk);
                n = 0;
            }
        }
        _filtered.Write<byte>(chunk[..n]);
    }

    public uint GetHash() => Hash(_filtered.WrittenSpan, 1);

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var f = new CurseForgeFingerprint();
        f.Append(data);
        return f.GetHash();
    }

    /// <summary>Plain MurmurHash2 (32-bit, little-endian blocks).</summary>
    public static uint Hash(ReadOnlySpan<byte> data, uint seed)
    {
        var length = data.Length;
        var h = seed ^ (uint)length;
        var i = 0;
        while (length - i >= 4)
        {
            var k = (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
            k *= M;
            k ^= k >> R;
            k *= M;
            h *= M;
            h ^= k;
            i += 4;
        }
        switch (length - i)
        {
            case 3:
                h ^= (uint)data[i + 2] << 16;
                h ^= (uint)data[i + 1] << 8;
                h ^= data[i];
                h *= M;
                break;
            case 2:
                h ^= (uint)data[i + 1] << 8;
                h ^= data[i];
                h *= M;
                break;
            case 1:
                h ^= data[i];
                h *= M;
                break;
        }
        h ^= h >> 13;
        h *= M;
        h ^= h >> 15;
        return h;
    }
}
