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

/// <summary>Linux: the ESTABLISHED client socket in <c>/proc/net/tcp</c> or <c>/proc/net/tcp6</c> is owned by uid 0. Windows: the owning PID of the ESTABLISHED client socket from <c>GetExtendedTcpTable</c> is the PID of the running <c>Tailscale</c> service or of a direct child of it with the same image path. Other platforms: never.</summary>
internal sealed class TailscaleConnectionOwner : IConnectionOwner
{
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const int Ipv4RowSize = 24;
    private const int Ipv6RowSize = 56;
    private const int MibTcpStateEstab = 5;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const int ServiceStatusProcessSize = 36;
    private const int ServiceRunning = 4;
    private const uint Th32csSnapProcess = 0x2;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly IntPtr InvalidHandleValue = new(-1);

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

    /// <summary>True when exactly one line of <paramref name="procNetTcpText"/> (the <c>/proc/net/tcp</c> or <c>tcp6</c> format) has <paramref name="remote"/> as its local address, <paramref name="local"/> as its remote address, state ESTABLISHED (<c>01</c>), and uid 0.</summary>
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
        string? state = null;
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
            state = fields[3];
            uid = fields[7];
        }

        return matches == 1 && state == "01" && uid == "0";
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
                return TailscaleServicePid() is int servicePid && IsServiceOrWorker(pid, servicePid, WindowsProcessInfo);
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="ownerPid"/> is <paramref name="servicePid"/>, or a direct child of it whose image path equals the service's own image path (ordinal, case-insensitive). <paramref name="process"/> returns the parent PID and image path of a PID, or null when unknown; any unknown is false.</summary>
    internal static bool IsServiceOrWorker(int ownerPid, int servicePid, Func<int, (int ParentPid, string? ImagePath)?> process)
    {
        if (servicePid <= 0 || ownerPid <= 0)
        {
            return false;
        }

        if (ownerPid == servicePid)
        {
            return true;
        }

        return process(ownerPid) is { } owner
            && owner.ParentPid == servicePid
            && process(servicePid) is { ImagePath: { Length: > 0 } serviceImage }
            && string.Equals(owner.ImagePath, serviceImage, StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static (int ParentPid, string? ImagePath)? WindowsProcessInfo(int pid)
    {
        try
        {
            return ParentPid(pid) is int parent && ImagePath(pid) is string image ? (parent, image) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? ParentPid(int pid)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var entry = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                if (entry.th32ProcessID == (uint)pid)
                {
                    return (int)entry.th32ParentProcessID;
                }
            }

            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ImagePath(int pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) && size > 0 && size <= buffer.Length
                ? new string(buffer, 0, size)
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? TailscaleServicePid()
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            return null;
        }

        var service = IntPtr.Zero;
        var buffer = Marshal.AllocHGlobal(ServiceStatusProcessSize);
        try
        {
            service = OpenServiceW(scm, "Tailscale", ServiceQueryStatus);
            if (service == IntPtr.Zero
                || !QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, ServiceStatusProcessSize, out _)
                || Marshal.ReadInt32(buffer, 4) != ServiceRunning)
            {
                return null;
            }

            var pid = Marshal.ReadInt32(buffer, 28);
            return pid != 0 ? pid : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }

            CloseServiceHandle(scm);
        }
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
            if (Marshal.ReadInt32(table, row) == MibTcpStateEstab
                && Marshal.ReadInt32(table, row + 4) == remoteAddr
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
            if (Marshal.ReadInt32(table, row + 48) != MibTcpStateEstab
                || Port(Marshal.ReadInt32(table, row + 20)) != remote.Port
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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr hService, int infoLevel, IntPtr lpBuffer, int cbBufSize, out int pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, [Out] char[] lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
