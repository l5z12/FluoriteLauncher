using System.Diagnostics;
using Fluorite.Core.Platform;

namespace Fluorite.Core.Java;

/// <summary>Identifies a Java home without trusting its folder name.</summary>
public static class JavaProbe
{
    /// <summary>Reads the <c>release</c> file and the PE header; falls back to running <c>java -XshowSettings</c>.</summary>
    public static async Task<JavaInstallation?> ProbeAsync(string home, JavaSource source, CancellationToken ct = default)
    {
        home = Path.GetFullPath(home.TrimEnd('\\', '/'));
        var java = Path.Combine(home, "bin", "java.exe");
        var javaw = Path.Combine(home, "bin", "javaw.exe");
        if (!File.Exists(java) && !File.Exists(javaw))
            return null;

        var arch = ReadPeArch(File.Exists(java) ? java : javaw);
        string? version = null, vendor = null;

        var release = Path.Combine(home, "release");
        if (File.Exists(release))
        {
            foreach (var line in await File.ReadAllLinesAsync(release, ct).ConfigureAwait(false))
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                    continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim().Trim('"');
                switch (key)
                {
                    case "JAVA_VERSION":
                        version = value;
                        break;
                    case "IMPLEMENTOR":
                        vendor = value;
                        break;
                    case "OS_ARCH" when arch == CpuArch.Unknown:
                        arch = CpuArchExtensions.Parse(value);
                        break;
                }
            }
        }

        if (version is null && File.Exists(java))
        {
            (version, vendor, var probedArch) = await RunSettingsProbeAsync(java, ct).ConfigureAwait(false);
            if (arch == CpuArch.Unknown)
                arch = probedArch;
        }

        if (version is null)
            return null;
        var major = JavaInstallation.ParseMajor(version);
        return major == 0 ? null : new JavaInstallation(home, version, major, arch, vendor, source);
    }

    private static async Task<(string? Version, string? Vendor, CpuArch Arch)> RunSettingsProbeAsync(string java, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(java)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-XshowSettings:properties");
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            if (p is null)
                return (null, null, CpuArch.Unknown);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await stderr.ConfigureAwait(false) + await stdout.ConfigureAwait(false);

            string? Prop(string name)
            {
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.StartsWith(name + " = ", StringComparison.Ordinal))
                        return line[(name.Length + 3)..].Trim();
                }
                return null;
            }
            return (Prop("java.version"), Prop("java.vendor"), CpuArchExtensions.Parse(Prop("os.arch")));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return (null, null, CpuArch.Unknown);
        }
    }

    /// <summary>Reads the COFF machine type from a PE file.</summary>
    public static CpuArch ReadPeArch(string exe)
    {
        try
        {
            using var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) // MZ
                return CpuArch.Unknown;
            fs.Position = 0x3C;
            var peOffset = br.ReadInt32();
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) // PE\0\0
                return CpuArch.Unknown;
            return br.ReadUInt16() switch
            {
                0x014C => CpuArch.X86,
                0x8664 => CpuArch.X64,
                0xAA64 => CpuArch.Arm64,
                _ => CpuArch.Unknown,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return CpuArch.Unknown;
        }
    }
}
