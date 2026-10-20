using System;
using System.Reflection;
using System.Runtime.InteropServices;

public static class Program
{
    static readonly Guid CLSID = new Guid("B6F3A1C2-7D4E-4A9B-9C31-5E7A2D8F1B60");
    static readonly Guid IID_IQueryInfo = new Guid("00021500-0000-0000-C000-000000000046");
    static readonly Guid IID_IPersistFile = new Guid("0000010B-0000-0000-C000-000000000046");

    public static int Main(string[] args)
    {
        // 用法一：/dll <HoverNote.dll> <路径> [路径...]  —— 直接加载指定 DLL，不碰注册表
        if (args.Length >= 3 && args[0] == "/dll")
        {
            try
            {
                Assembly asm = Assembly.UnsafeLoadFrom(args[1]);
                Type ht = asm.GetType("HoverNote.InfoTipHandler", true);
                object obj = Activator.CreateInstance(ht);
                for (int i = 2; i < args.Length; i++) Print(obj, args[i]);
                return 0;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Console.WriteLine("失败: " + inner.GetType().Name + " - " + inner.Message);
                return 2;
            }
        }

        string path = args.Length > 0 ? args[0] : @"C:\Windows";
        object com = null;
        IntPtr unk = IntPtr.Zero;
        try
        {
            Type t = Type.GetTypeFromCLSID(CLSID, true);
            com = Activator.CreateInstance(t);
            Console.WriteLine("COM 激活成功: " + com.GetType().FullName);

            unk = Marshal.GetIUnknownForObject(com);
            Guid g1 = IID_IQueryInfo;
            IntPtr p1;
            int hr1 = Marshal.QueryInterface(unk, ref g1, out p1);
            Console.WriteLine("QI IQueryInfo   : 0x" + hr1.ToString("X8") + (hr1 == 0 ? "   [OK]" : "   [FAIL]"));
            if (hr1 == 0) Marshal.Release(p1);

            Guid g2 = IID_IPersistFile;
            IntPtr p2;
            int hr2 = Marshal.QueryInterface(unk, ref g2, out p2);
            Console.WriteLine("QI IPersistFile : 0x" + hr2.ToString("X8") + (hr2 == 0 ? "   [OK]" : "   [FAIL]"));
            if (hr2 == 0) Marshal.Release(p2);

            Print(com, path);
            return 0;
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            Console.WriteLine("失败: " + inner.GetType().Name + " - " + inner.Message);
            return 2;
        }
        finally
        {
            if (unk != IntPtr.Zero) Marshal.Release(unk);
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
    }

    static void Print(object obj, string path)
    {
        Type ct = obj.GetType();
        ct.GetMethod("Load").Invoke(obj, new object[] { path, (uint)0 });
        object[] ps = new object[] { (uint)0, null };
        int hr = (int)ct.GetMethod("GetInfoTip").Invoke(obj, ps);
        Console.WriteLine("=== " + path + "  (hr=0x" + hr.ToString("X8") + ")");
        Console.WriteLine((string)ps[1] ?? "(null —— 交给系统原生提示)");
        Console.WriteLine();
    }
}
