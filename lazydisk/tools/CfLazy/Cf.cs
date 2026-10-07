using System.Runtime.InteropServices;

static class Cf
{
    [StructLayout(LayoutKind.Sequential)] public struct SyncReg { public uint StructSize; public IntPtr ProviderName, ProviderVersion, SyncRootIdentity; public uint SyncRootIdentityLength; public IntPtr FileIdentity; public uint FileIdentityLength; public Guid ProviderId; }
    [StructLayout(LayoutKind.Sequential)] public struct SyncPolicies { public uint StructSize; public ushort HydPrimary, HydModifier, PopPrimary, PopModifier; public uint InSync, Hardlink, PlaceholderMgmt; }
    [StructLayout(LayoutKind.Sequential)] public struct CallbackReg { public uint Type; public IntPtr Callback; }
    [StructLayout(LayoutKind.Sequential)] public struct PlaceholderInfo { public IntPtr RelativeFileName; public long Creation, LastAccess, LastWrite, Change; public uint Attributes; public long FileSize; public IntPtr FileIdentity; public uint FileIdentityLength; public uint Flags; public int Result; public ulong CreateUsn; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Callback(IntPtr info, IntPtr parameters);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] public static extern int CfRegisterSyncRoot(string path, ref SyncReg reg, ref SyncPolicies pol, uint flags);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] public static extern int CfUnregisterSyncRoot(string path);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] public static extern int CfConnectSyncRoot(string path, CallbackReg[] table, IntPtr ctx, uint flags, out long key);
    [DllImport("cldapi.dll")] public static extern int CfDisconnectSyncRoot(long key);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] public static extern int CfCreatePlaceholders(string baseDir, ref PlaceholderInfo info, uint count, uint flags, out uint processed);
    [DllImport("cldapi.dll")] public static extern int CfExecute(IntPtr opInfo, IntPtr opParams);
    [DllImport("cldapi.dll")] public static extern int CfConvertToPlaceholder(IntPtr h, IntPtr ident, uint identLen, uint flags, IntPtr usn, IntPtr ov);
    [DllImport("cldapi.dll")] public static extern int CfDehydratePlaceholder(IntPtr h, long off, long len, uint flags, IntPtr ov);
    [DllImport("cldapi.dll")] public static extern int CfSetInSyncState(IntPtr h, uint state, uint flags, IntPtr usn);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr CreateFileW(string p, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
