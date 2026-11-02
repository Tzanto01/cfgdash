using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cfgdash.Host;

/// <summary>Decides who owns the client end of an accepted backend connection (fleet-v1, peer auth on the receiving side).</summary>
internal interface IConnectionOwner
{
    /// <summary>True only when the client socket <paramref name="remote"/> connected to <paramref name="local"/> is owned by the Tailscale daemon; any doubt is false.</summary>
    bool IsTailscaleDaemon(IPEndPoint remote, IPEndPoint local);
}

/// <summary>Linux: the client socket in <c>/proc/net/tcp</c> or <c>/proc/net/tcp6</c> is owned by uid 0. Windows: the owning process from <c>GetExtendedTcpTable</c> is <c>tailscaled</c>. Other platforms: never.</summary>
internal sealed class TailscaleConnectionOwner : IConnectionOwner
{
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const int Ipv4RowSize = 24;
    private const int Ipv6RowSize = 56;

    /// <inheritdoc/>
    public bool IsTailscaleDaemon(IPEndPoint remote, IPEndPoint local)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return IsTailscaleDaemonLinux(remote, local);
            }

            if (OperatingSystem.IsWindows())
            {
                return IsTailscaleDaemonWindows(remote, local);
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when exactly one line of <paramref name="procNetTcpText"/> (the <c>/proc/net/tcp</c> or <c>tcp6</c> format) has <paramref name="remote"/> as its local address, <paramref name="local"/> as its remote address, and uid 0.</summary>
    internal static bool IsRootOwned(string procNetTcpText, IPEndPoint remote, IPEndPoint local)
    {
        if (!BitConverter.IsLittleEndian)
        {
            return false;
        }

        var peer = Encode(remote);
        var server = Encode(local);
        var matches = 0;
        string? uid = null;
        foreach (var line in procNetTcpText.Split('\n').Skip(1))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8
                || !string.Equals(fields[1], peer, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(fields[2], server, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches++;
            uid = fields[7];
        }

        return matches == 1 && uid == "0";
    }

    private static string Encode(IPEndPoint endpoint)
    {
        var bytes = endpoint.Address.GetAddressBytes();
        for (var group = 0; group < bytes.Length; group += 4)
        {
            Array.Reverse(bytes, group, 4);
        }

        return Convert.ToHexString(bytes) + ":" + endpoint.Port.ToString("X4");
    }

    private static IEnumerable<(IPEndPoint Remote, IPEndPoint Local)> Forms(IPEndPoint remote, IPEndPoint local)
    {
        yield return (remote, local);
        if (remote.Address.IsIPv4MappedToIPv6 && local.Address.IsIPv4MappedToIPv6)
        {
            yield return (new IPEndPoint(remote.Address.MapToIPv4(), remote.Port), new IPEndPoint(local.Address.MapToIPv4(), local.Port));
        }
    }

    private static bool IsTailscaleDaemonLinux(IPEndPoint remote, IPEndPoint local)
    {
        var tables = new[] { ReadOrNull("/proc/net/tcp"), ReadOrNull("/proc/net/tcp6") };
        foreach (var (r, l) in Forms(remote, local))
        {
            foreach (var table in tables)
            {
                if (table is not null && IsRootOwned(table, r, l))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTailscaleDaemonWindows(IPEndPoint remote, IPEndPoint local)
    {
        foreach (var (r, l) in Forms(remote, local))
        {
            if (FindOwnerPid(r, l) is int pid)
            {
                using var process = Process.GetProcessById(pid);
                return string.Equals(process.ProcessName, "tailscaled", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static int? FindOwnerPid(IPEndPoint remote, IPEndPoint local)
    {
        if (remote.AddressFamily != local.AddressFamily
            || remote.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return null;
        }

        var af = remote.AddressFamily == AddressFamily.InterNetwork ? 2 : 23;
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidAll, 0);
        for (var attempt = 0; attempt < 2 && result == ErrorInsufficientBuffer && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, af, TcpTableOwnerPidAll, 0);
                if (result == 0)
                {
                    return af == 2 ? FindIpv4Row(buffer, size, remote, local) : FindIpv6Row(buffer, size, remote, local);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static int? FindIpv4Row(IntPtr table, int size, IPEndPoint remote, IPEndPoint local)
    {
        var count = Marshal.ReadInt32(table);
        if (count < 0 || 4 + ((long)count * Ipv4RowSize) > size)
        {
            return null;
        }

        var remoteAddr = BitConverter.ToInt32(remote.Address.GetAddressBytes());
        var localAddr = BitConverter.ToInt32(local.Address.GetAddressBytes());
        for (var i = 0; i < count; i++)
        {
            var row = 4 + (i * Ipv4RowSize);
            if (Marshal.ReadInt32(table, row + 4) == remoteAddr
                && Port(Marshal.ReadInt32(table, row + 8)) == remote.Port
                && Marshal.ReadInt32(table, row + 12) == localAddr
                && Port(Marshal.ReadInt32(table, row + 16)) == local.Port)
            {
                return Marshal.ReadInt32(table, row + 20);
            }
        }

        return null;
    }

    private static int? FindIpv6Row(IntPtr table, int size, IPEndPoint remote, IPEndPoint local)
    {
        var count = Marshal.ReadInt32(table);
        if (count < 0 || 4 + ((long)count * Ipv6RowSize) > size)
        {
            return null;
        }

        var remoteAddr = remote.Address.GetAddressBytes();
        var localAddr = local.Address.GetAddressBytes();
        var address = new byte[16];
        for (var i = 0; i < count; i++)
        {
            var row = 4 + (i * Ipv6RowSize);
            if (Port(Marshal.ReadInt32(table, row + 20)) != remote.Port
                || Port(Marshal.ReadInt32(table, row + 44)) != local.Port)
            {
                continue;
            }

            Marshal.Copy(table + row, address, 0, 16);
            if (!address.AsSpan().SequenceEqual(remoteAddr))
            {
                continue;
            }

            Marshal.Copy(table + row + 24, address, 0, 16);
            if (address.AsSpan().SequenceEqual(localAddr))
            {
                return Marshal.ReadInt32(table, row + 52);
            }
        }

        return null;
    }

    private static int Port(int networkOrderPort) => (ushort)IPAddress.NetworkToHostOrder((short)networkOrderPort);

    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, uint reserved);
}
