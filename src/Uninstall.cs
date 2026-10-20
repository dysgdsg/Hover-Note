using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HoverNote.Uninstall
{
    internal static class Program
    {
        private const string Clsid = "{B6F3A1C2-7D4E-4A9B-9C31-5E7A2D8F1B60}";
        private const string Iid = "{00021500-0000-0000-C000-000000000046}";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string sure = "确定要卸载 HoverNote 吗？\r\n\r\n"
                        + "会删掉这些：\r\n"
                        + "  · 你自己账号里的几条注册表设置\r\n"
                        + "  · 安装目录里的 HoverNote.dll、README.txt、Uninstall.exe\r\n\r\n"
                        + "不会碰你自己的任何文件。";

            if (MessageBox.Show(sure, "卸载 HoverNote", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            string installDir = ReadInstallPath();
            string errors = "";

            try
            {
                foreach (string scope in new string[] { "*", "Directory", "Drive" })
                {
                    TryDeleteKey(@"Software\Classes\" + scope + @"\shellex\" + Iid);
                }
                TryDeleteKey(@"Software\Classes\CLSID\" + Clsid);
                TryDeleteKey(@"Software\HoverNote");
            }
            catch (Exception ex)
            {
                errors = errors + "清理设置时出错：" + ex.Message + "\r\n";
            }

            string self = Assembly.GetExecutingAssembly().Location;
            string dir = Path.GetDirectoryName(self);
            if (string.IsNullOrEmpty(installDir)) installDir = dir;

            foreach (string name in new string[] { "HoverNote.dll", "README.txt" })
            {
                try
                {
                    string f = Path.Combine(installDir, name);
                    if (File.Exists(f)) File.Delete(f);
                }
                catch (Exception ex)
                {
                    errors = errors + "删不掉 " + name + "：" + ex.Message + "\r\n";
                }
            }

            if (errors.Length > 0)
            {
                MessageBox.Show("大部分已经卸载，但有几处没弄干净：\r\n\r\n" + errors
                    + "\r\n可以重启电脑后再运行一次 Uninstall.exe。",
                    "卸载 HoverNote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show("卸载完成。\r\n\r\n谢谢用过它。", "卸载 HoverNote",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            // 自己删自己：先等两秒，让本程序退出，再删文件、删空目录
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + self + "\" & rmdir /q \"" + installDir + "\"");
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch
            {
                // 删不掉就算了，留着也不影响
            }
        }

        private static string ReadInstallPath()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\HoverNote"))
                {
                    if (k == null) return null;
                    return k.GetValue("InstallPath") as string;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void TryDeleteKey(string subKey)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(subKey, false);
            }
            catch
            {
                // 没有就算了
            }
        }
    }
}
