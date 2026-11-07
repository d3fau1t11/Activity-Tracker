using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Activity_Tracker;

[SupportedOSPlatform("windows")]
internal static class WindowsLogonSession
{
    // Authentication ID and logon time survive app restarts, but change on sign-out or reboot.
    public static string GetId()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(identity.AccessToken, 10, out var statistics,
                Marshal.SizeOf<TokenStatistics>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var luid = statistics.AuthenticationId;
        var status = LsaGetLogonSessionData(ref luid, out var buffer);
        if (status != 0) throw new Win32Exception((int)LsaNtStatusToWinError(status));
        try
        {
            if (buffer == IntPtr.Zero || Marshal.ReadInt32(buffer) < Marshal.SizeOf<LogonSessionData>())
                throw new InvalidOperationException("Windows sign-in session is unavailable.");
            var data = Marshal.PtrToStructure<LogonSessionData>(buffer);
            if (data.LogonTime <= 0) throw new InvalidOperationException("Windows sign-in time is unavailable.");
            return $"{identity.User?.Value}:{luid.HighPart:X8}{luid.LowPart:X8}:{data.LogonTime:X16}";
        }
        finally { if (buffer != IntPtr.Zero) LsaFreeReturnBuffer(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public Luid TokenId, AuthenticationId;
        public long ExpirationTime;
        public int TokenType, ImpersonationLevel;
        public uint DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount;
        public Luid ModifiedId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct LogonSessionData
    {
        public uint Size;
        public Luid LogonId;
        public UnicodeString UserName, LogonDomain, AuthenticationPackage;
        public uint LogonType, Session;
        public IntPtr Sid;
        public long LogonTime;
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out TokenStatistics statistics, int length, out int returnLength);
    [DllImport("secur32.dll")]
    private static extern uint LsaGetLogonSessionData(ref Luid logonId, out IntPtr data);
    [DllImport("secur32.dll")]
    private static extern uint LsaFreeReturnBuffer(IntPtr buffer);
    [DllImport("advapi32.dll")]
    private static extern uint LsaNtStatusToWinError(uint status);
}
