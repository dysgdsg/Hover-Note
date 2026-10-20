using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SHFILEINFO
{
    public IntPtr hIcon;
    public int iIcon;
    public uint dwAttributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string szTypeName;
}

public static class Probe
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    const uint SHGFI_INFOTIP = 0x00000200;

    public static int Main(string[] args)
    {
        string path = args[0];
        SHFILEINFO fi = new SHFILEINFO();
        IntPtr r = SHGetFileInfo(path, 0, ref fi, (uint)Marshal.SizeOf(fi), SHGFI_INFOTIP);
        Console.WriteLine("SHGetFileInfo -> " + r.ToInt64());
        Console.WriteLine("infotip: [" + (fi.szDisplayName ?? "<null>") + "]");
        return 0;
    }
}
