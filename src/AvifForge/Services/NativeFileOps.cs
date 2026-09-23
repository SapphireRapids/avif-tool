using System.Runtime.InteropServices;

namespace AvifForge.Services;

/// <summary>
/// Windows 原生 Shell 接口（shell32 SHFileOperationW）：把删除的原图送入回收站而非直接抹掉。
/// </summary>
internal static class NativeFileOps
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAbandoned;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW pFileOp);

    /// <summary>将文件移入回收站。成功返回 true；失败（如盘无回收站）返回 false。</summary>
    public static bool TryRecycle(string path, IntPtr ownerWindow = default)
    {
        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                hwnd = ownerWindow,
                wFunc = FO_DELETE,
                // pFrom 要求以两个 null 结尾
                pFrom = path + "\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            return SHFileOperationW(ref op) == 0;
        }
        catch
        {
            return false;
        }
    }
}
