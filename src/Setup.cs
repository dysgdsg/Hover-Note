using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HoverNote.Setup
{
    internal static class Installer
    {
        public const string Clsid = "{B6F3A1C2-7D4E-4A9B-9C31-5E7A2D8F1B60}";
        public const string Iid = "{00021500-0000-0000-C000-000000000046}";

        // 默认安装位置：当前用户的 AppData\Local\HoverNote。
        // 如果这台电脑之前装过（注册表里有 InstallPath），就沿用原来的位置，
        // 免得同一个程序装出两份、老的还留在 D 盘没人管。
        public static string DefaultPath()
        {
            string existing = ReadInstallPath();
            if (!string.IsNullOrEmpty(existing))
            {
                try { if (Directory.Exists(existing)) return existing; }
                catch { }
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "HoverNote");
        }

        public static string ReadInstallPath()
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

        public static void Install(string dir)
        {
            string full = Path.GetFullPath(dir);
            Directory.CreateDirectory(full);

            Extract("app.dll", Path.Combine(full, "HoverNote.dll"));
            Extract("uninstall.exe", Path.Combine(full, "Uninstall.exe"));
            Extract("readme.txt", Path.Combine(full, "README.txt"));

            WriteRegistry(full);
        }

        private static void Extract(string resourceName, string targetPath)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null) throw new InvalidOperationException("安装包缺少内容：" + resourceName);
                using (FileStream fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    s.CopyTo(fs);
                }
            }
        }

        private static void WriteRegistry(string installDir)
        {
            string dllPath = Path.Combine(installDir, "HoverNote.dll");
            string codeBase = new Uri(dllPath).AbsoluteUri;

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + Clsid))
            {
                key.SetValue(null, "HoverNote.InfoTipHandler");
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + Clsid + @"\InprocServer32"))
            {
                key.SetValue(null, "mscoree.dll");
                key.SetValue("ThreadingModel", "Both");
                key.SetValue("Assembly", "HoverNote, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
                key.SetValue("Class", "HoverNote.InfoTipHandler");
                key.SetValue("RuntimeVersion", "v4.0.30319");
                key.SetValue("CodeBase", codeBase);
            }

            foreach (string scope in new string[] { "*", "Directory", "Drive" })
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + scope + @"\shellex\" + Iid))
                {
                    key.SetValue(null, Clsid);
                }
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\HoverNote"))
            {
                key.SetValue("InstallPath", installDir);
            }
        }
    }

    /// <summary>体检报告：只统计文件夹名、大小、后缀分布和 3 个样本名，不读内容、不联网。</summary>
    internal static class Digest
    {
        private const int MaxEntries = 60000;
        private const int MaxDepth = 3;

        private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".psd", ".ai" };
        private static readonly string[] MediaExt = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".mp3", ".flac", ".wav", ".m4a", ".ape", ".ogg" };
        private static readonly string[] CodeExt = { ".cs", ".py", ".js", ".ts", ".java", ".c", ".cpp", ".cc", ".h", ".hpp", ".go", ".rs", ".php", ".rb", ".lua", ".sh", ".bat", ".cmd", ".ps1", ".vb", ".sql", ".vue", ".jsx", ".tsx", ".json", ".xml", ".yml", ".yaml" };
        private static readonly string[] DocExt = { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".rtf", ".wps", ".csv", ".epub" };
        private static readonly string[] ProgExt = { ".exe", ".dll", ".msi", ".sys", ".ocx", ".drv", ".pak", ".jar", ".node", ".so" };
        private static readonly string[] ModExt = { ".esp", ".esm", ".esl", ".bsa", ".ba2", ".vpk", ".pk3", ".pk4", ".mod", ".wad", ".bsp" };
        private static readonly string[] ZipExt = { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".001", ".iso" };
        private static readonly string[] LnkExt = { ".lnk", ".url" };

        public static string DefaultPath()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrEmpty(desktop)) desktop = Path.GetTempPath();
            return Path.Combine(desktop, "HoverNote-体检报告.txt");
        }

        private sealed class Stat
        {
            public long Files;
            public long Dirs;
            public long Bytes;
            public bool Truncated;
            public bool Denied;
            public readonly Dictionary<string, int> Ext = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Samples = new List<string>();
        }

        /// <summary>生成报告并写出文件，返回实际写出的路径。</summary>
        public static string Build(string outPath)
        {
            string text = BuildText();

            if (!string.IsNullOrEmpty(outPath))
            {
                File.WriteAllText(outPath, text, new UTF8Encoding(true));
                return outPath;
            }

            List<string> tried = new List<string>();
            string[] dirs = CandidateDirs();
            for (int i = 0; i < dirs.Length; i++)
            {
                string path = Path.Combine(dirs[i], "HoverNote-体检报告.txt");
                try
                {
                    File.WriteAllText(path, text, new UTF8Encoding(true));
                    return path;
                }
                catch
                {
                    tried.Add(dirs[i]);
                }
            }

            throw new IOException("这几个地方都写不进去：" + string.Join("、", tried.ToArray())
                + "。\r\n多半是安全软件（火绒这类）的「桌面 / 文档保护」挡住了这个没签名的新程序。");
        }

        private static string[] CandidateDirs()
        {
            List<string> list = new List<string>();
            AddDir(list, SafeGetFolder(Environment.SpecialFolder.DesktopDirectory));
            AddDir(list, ReadShellFolder("{374DE290-123F-4565-9164-39C4925E467B}"));
            AddDir(list, SafeGetFolder(Environment.SpecialFolder.MyDocuments));
            AddDir(list, Installer.ReadInstallPath());
            AddDir(list, Path.GetTempPath());
            return list.ToArray();
        }

        private static void AddDir(List<string> list, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try { if (!Directory.Exists(dir)) return; }
            catch { return; }
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], dir, StringComparison.OrdinalIgnoreCase)) return;
            }
            list.Add(dir);
        }

        private static string SafeGetFolder(Environment.SpecialFolder folder)
        {
            try { return Environment.GetFolderPath(folder); }
            catch { return null; }
        }

        public static string BuildText()
        {
            Stopwatch clock = Stopwatch.StartNew();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# HoverNote 电脑体检报告");
            sb.AppendLine();
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("用途：把这份文件，连同 README 里那段提示词，一起发给你的 AI（豆包 / DSH），让它给这台电脑写规则。");
            sb.AppendLine("说明：只统计「文件夹名、大小、后缀分布、3 个样本文件名」，不读取任何文件内容，不联网，不上传。");
            sb.AppendLine("统计口径：每个文件夹最多往下数 3 层、最多 6 万个条目，超出会标「(未数完)」。");
            sb.AppendLine();

            AppendSpecialFolders(sb, clock);

            string[] drives = GetFixedDrives();
            for (int di = 0; di < drives.Length; di++)
            {
                string root = drives[di];
                sb.AppendLine("## " + root.TrimEnd('\\'));
                sb.AppendLine();

                string[] tops;
                try
                {
                    tops = Directory.GetDirectories(root);
                }
                catch
                {
                    sb.AppendLine("（这个盘读不了，跳过）");
                    sb.AppendLine();
                    continue;
                }

                int looseFiles = 0;
                try { looseFiles = Directory.GetFiles(root).Length; }
                catch { }

                sb.AppendLine("- 顶层文件夹 " + tops.Length + " 个，根目录散文件 " + looseFiles + " 个");
                sb.AppendLine();

                for (int i = 0; i < tops.Length; i++)
                {
                    string dir = tops[i];
                    if (clock.Elapsed.TotalSeconds > 120)
                    {
                        sb.AppendLine("> 已经扫了 2 分钟，剩下的先跳过了。想让报告更全，可以在盘里少放点东西，或者直接让 DSH 自己扫。");
                        break;
                    }

                    Stat st = new Stat();
                    Walk(dir, 1, st);

                    sb.AppendLine("### " + dir);
                    sb.AppendLine("- 浅层统计：" + FormatNum(st.Files + st.Dirs) + " 项（文件 " + FormatNum(st.Files) + " / 文件夹 " + FormatNum(st.Dirs) + "），" + HumanSize(st.Bytes) + (st.Truncated ? " (未数完)" : ""));
                    sb.AppendLine("- 后缀占比：" + TopExt(st.Ext));
                    sb.AppendLine("- 像：" + Guess(st));
                    if (st.Samples.Count > 0) sb.AppendLine("- 样本名：" + string.Join("、", st.Samples.ToArray()));
                    if (st.Denied) sb.AppendLine("- （有一部分没权限看，跳过了）");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("---");
            sb.AppendLine("提示：如果 AI 问你「某个文件夹里到底有什么」，你可以只把那个文件夹再导一次，或者直接告诉它你记得的用途。");
            sb.AppendLine("耗时：" + ((int)clock.Elapsed.TotalSeconds) + " 秒");

            return sb.ToString();
        }

        private static readonly string[,] SpecialFolders = new string[,]
        {
            { "桌面", "Desktop" },
            { "文档", "Personal" },
            { "下载", "{374DE290-123F-4565-9164-39C4925E467B}" },
            { "图片", "My Pictures" },
            { "音乐", "My Music" },
            { "视频", "My Video" }
        };

        private static void AppendSpecialFolders(StringBuilder sb, Stopwatch clock)
        {
            StringBuilder body = new StringBuilder();
            string lastDir = null;

            for (int i = 0; i < SpecialFolders.GetLength(0); i++)
            {
                string path = ReadShellFolder(SpecialFolders[i, 1]);
                if (path == null) continue;
                if (lastDir != null && string.Equals(lastDir, path, StringComparison.OrdinalIgnoreCase)) continue;
                lastDir = path;

                try { if (!Directory.Exists(path)) continue; }
                catch { continue; }

                Stat st = new Stat();
                Walk(path, 1, st);

                body.AppendLine("### " + SpecialFolders[i, 0] + "（" + path + "）");
                body.AppendLine("- 浅层统计：" + FormatNum(st.Files + st.Dirs) + " 项（文件 " + FormatNum(st.Files) + " / 文件夹 " + FormatNum(st.Dirs) + "），" + HumanSize(st.Bytes) + (st.Truncated ? " (未数完)" : ""));
                body.AppendLine("- 后缀占比：" + TopExt(st.Ext));
                body.AppendLine("- 像：" + Guess(st));
                if (st.Samples.Count > 0) body.AppendLine("- 样本名：" + string.Join("、", st.Samples.ToArray()));
                body.AppendLine();
            }

            if (body.Length == 0) return;

            sb.AppendLine("## 你常用的几个文件夹");
            sb.AppendLine();
            sb.AppendLine("（这几个是资源管理器左边栏的默认位置，很多电脑上它们不在 C 盘。AI 最容易在这里搞错。）");
            sb.AppendLine();
            sb.Append(body.ToString());
        }

        private static string ReadShellFolder(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                {
                    if (k == null) return null;
                    string v = k.GetValue(name) as string;
                    if (string.IsNullOrEmpty(v)) return null;
                    return Environment.ExpandEnvironmentVariables(v);
                }
            }
            catch
            {
                return null;
            }
        }

        private static string[] GetFixedDrives()
        {
            List<string> list = new List<string>();
            try
            {
                DriveInfo[] all = DriveInfo.GetDrives();
                for (int i = 0; i < all.Length; i++)
                {
                    try
                    {
                        if (all[i].DriveType == DriveType.Fixed && all[i].IsReady) list.Add(all[i].RootDirectory.FullName);
                    }
                    catch { }
                }
            }
            catch { }
            return list.ToArray();
        }

        private static void Walk(string dir, int depth, Stat st)
        {
            if (depth > MaxDepth) return;
            if (st.Files + st.Dirs >= MaxEntries) { st.Truncated = true; return; }

            try
            {
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    if (st.Files + st.Dirs >= MaxEntries) { st.Truncated = true; return; }

                    string name = Path.GetFileName(f);
                    if (IsSystemJunk(name)) continue;

                    st.Files++;
                    try { st.Bytes += new FileInfo(f).Length; }
                    catch { }
                    string ext = Path.GetExtension(f);
                    if (string.IsNullOrEmpty(ext)) ext = "(无后缀)";
                    int c;
                    st.Ext.TryGetValue(ext, out c);
                    st.Ext[ext] = c + 1;
                    if (st.Samples.Count < 3) st.Samples.Add(name);
                }
            }
            catch { st.Denied = true; }

            try
            {
                foreach (string d in Directory.EnumerateDirectories(dir))
                {
                    if (st.Files + st.Dirs >= MaxEntries) { st.Truncated = true; return; }
                    st.Dirs++;
                    Walk(d, depth + 1, st);
                }
            }
            catch { st.Denied = true; }
        }

        private static string Guess(Stat st)
        {
            if (st.Files == 0) return st.Dirs > 0 ? "空壳文件夹（只有子文件夹）" : "空文件夹";

            int[] g = new int[8];
            int total = 0;
            foreach (KeyValuePair<string, int> kv in st.Ext)
            {
                total += kv.Value;
                if (In(kv.Key, ImageExt)) g[0] += kv.Value;
                else if (In(kv.Key, MediaExt)) g[1] += kv.Value;
                else if (In(kv.Key, CodeExt)) g[2] += kv.Value;
                else if (In(kv.Key, DocExt)) g[3] += kv.Value;
                else if (In(kv.Key, ProgExt)) g[4] += kv.Value;
                else if (In(kv.Key, ModExt)) g[5] += kv.Value;
                else if (In(kv.Key, ZipExt)) g[6] += kv.Value;
                else if (In(kv.Key, LnkExt)) g[7] += kv.Value;
            }

            string[] names = { "图片 / 照片素材", "音频视频", "代码 / 工程文件", "文档资料", "已安装的程序", "游戏或模组资源", "压缩包", "一堆快捷方式" };
            int best = -1;
            int bestIdx = -1;
            for (int i = 0; i < g.Length; i++)
            {
                if (g[i] > best) { best = g[i]; bestIdx = i; }
            }
            if (bestIdx < 0 || total == 0) return "混合内容";
            if (best * 100 / total < 55) return "混合内容";
            return names[bestIdx];
        }

        private static bool IsSystemJunk(string name)
        {
            return string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "thumbs.db", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ".ds_store", StringComparison.OrdinalIgnoreCase);
        }

        private static bool In(string ext, string[] set)
        {
            for (int i = 0; i < set.Length; i++)
            {
                if (string.Equals(set[i], ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string TopExt(Dictionary<string, int> map)
        {
            if (map.Count == 0) return "（没有文件）";
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(map);
            list.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                if (c != 0) return c;
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });

            int total = 0;
            for (int i = 0; i < list.Count; i++) total += list[i].Value;

            StringBuilder sb = new StringBuilder();
            int n = Math.Min(3, list.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append("，");
                sb.Append(list[i].Key);
                sb.Append(" ");
                sb.Append((list[i].Value * 100 / total));
                sb.Append("%");
            }
            return sb.ToString();
        }

        private static string FormatNum(long n)
        {
            return n.ToString("N0");
        }

        private static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes / 1024.0;
            if (v < 1024) return v.ToString("0.0") + " KB";
            v = v / 1024.0;
            if (v < 1024) return v.ToString("0.0") + " MB";
            v = v / 1024.0;
            return v.ToString("0.0") + " GB";
        }
    }

    internal sealed class SetupForm : Form
    {
        private TextBox pathBox;

        public SetupForm()
        {
            Text = "HoverNote 安装程序";
            ClientSize = new Size(580, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 10F);
            BackColor = Color.White;

            Label title = new Label();
            title.Text = "HoverNote";
            title.Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold);
            title.Location = new Point(24, 18);
            title.AutoSize = true;
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "鼠标停在文件上，多说两句大白话：这是什么、删了会怎样。";
            sub.Location = new Point(26, 62);
            sub.AutoSize = true;
            sub.ForeColor = Color.FromArgb(70, 70, 70);
            Controls.Add(sub);

            Label sub2 = new Label();
            sub2.Text = "装好以后不用打开任何窗口，直接去文件夹里悬停就行。";
            sub2.Location = new Point(26, 88);
            sub2.AutoSize = true;
            sub2.ForeColor = Color.FromArgb(70, 70, 70);
            Controls.Add(sub2);

            Label whereLabel = new Label();
            whereLabel.Text = "安装到：";
            whereLabel.Location = new Point(26, 132);
            whereLabel.AutoSize = true;
            Controls.Add(whereLabel);

            pathBox = new TextBox();
            pathBox.Location = new Point(96, 129);
            pathBox.Width = 350;
            pathBox.Text = Installer.DefaultPath();
            Controls.Add(pathBox);

            Button browse = new Button();
            browse.Text = "换个地方…";
            browse.Location = new Point(456, 127);
            browse.Width = 96;
            browse.Click += new EventHandler(OnBrowse);
            Controls.Add(browse);

            Label hint = new Label();
            hint.Text = "一般不用改。放在这里你自己找得到，卸载也方便。";
            hint.Location = new Point(96, 160);
            hint.AutoSize = true;
            hint.ForeColor = Color.Gray;
            hint.Font = new Font("Microsoft YaHei UI", 9F);
            Controls.Add(hint);

            Label foot = new Label();
            foot.Text = "不需要管理员权限 · 不联网 · 不删你的文件 · 只改你自己的用户设置";
            foot.Location = new Point(26, 296);
            foot.AutoSize = true;
            foot.ForeColor = Color.Gray;
            foot.Font = new Font("Microsoft YaHei UI", 9F);
            Controls.Add(foot);

            Button install = new Button();
            install.Text = "开始安装";
            install.Location = new Point(300, 230);
            install.Width = 120;
            install.Height = 38;
            install.Click += new EventHandler(OnInstall);
            Controls.Add(install);

            Button quit = new Button();
            quit.Text = "退出";
            quit.Location = new Point(436, 230);
            quit.Width = 100;
            quit.Height = 38;
            quit.Click += new EventHandler(OnQuit);
            Controls.Add(quit);

            Button report = new Button();
            report.Text = "生成体检报告";
            report.Location = new Point(26, 230);
            report.Width = 150;
            report.Height = 38;
            report.Click += new EventHandler(OnReport);
            Controls.Add(report);

            Label reportHint = new Label();
            reportHint.Text = "给 AI 看的：把电脑里每个文件夹大概是什么，列成一张表。";
            reportHint.Location = new Point(26, 272);
            reportHint.AutoSize = true;
            reportHint.ForeColor = Color.Gray;
            reportHint.Font = new Font("Microsoft YaHei UI", 9F);
            Controls.Add(reportHint);
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选一个文件夹放 HoverNote（一般不用改）";
                dlg.SelectedPath = pathBox.Text;
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    pathBox.Text = Path.Combine(dlg.SelectedPath, "HoverNote");
                }
            }
        }

        private void OnQuit(object sender, EventArgs e)
        {
            Close();
        }

        private void OnReport(object sender, EventArgs e)
        {
            UseWaitCursor = true;
            string outPath = null;
            string err = null;

            try
            {
                outPath = Digest.Build(null);
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }
            finally
            {
                UseWaitCursor = false;
            }

            if (outPath == null)
            {
                MessageBox.Show("报告没生成出来：\r\n\r\n" + err, "HoverNote 安装程序",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string desktop = "";
            try { desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
            catch { }
            bool onDesktop = !string.IsNullOrEmpty(desktop)
                          && string.Equals(Path.GetDirectoryName(outPath), desktop, StringComparison.OrdinalIgnoreCase);

            string msg = (onDesktop ? "" : "（桌面被安全软件挡住了，我换了个地方放。）\r\n\r\n")
                       + "报告生成好了，在这里：\r\n\r\n" + outPath + "\r\n\r\n"
                       + "下一步：把这个文件发给你的 AI（豆包或 DSH），跟它说一句：\r\n"
                       + "「按这份报告，帮我写 HoverNote 的规则」。\r\n\r\n"
                       + "现在要打开看看吗？";
            if (MessageBox.Show(msg, "HoverNote 安装程序", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information) == DialogResult.Yes)
            {
                try { Process.Start("notepad.exe", "\"" + outPath + "\""); }
                catch { }
            }
        }

        private void OnInstall(object sender, EventArgs e)
        {
            string dir = pathBox.Text.Trim();
            if (dir.Length == 0)
            {
                MessageBox.Show("先填一个安装位置吧。", "HoverNote 安装程序",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(dir);
            }
            catch (Exception ex)
            {
                MessageBox.Show("这个路径看着不太对：" + ex.Message, "HoverNote 安装程序",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (full.Length <= 3)
            {
                MessageBox.Show("别直接装到磁盘根目录，新建一个子文件夹吧。", "HoverNote 安装程序",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string oldPath = Installer.ReadInstallPath();
            if (!string.IsNullOrEmpty(oldPath) && !string.Equals(oldPath, full, StringComparison.OrdinalIgnoreCase))
            {
                string msg = "你之前装在：\r\n" + oldPath + "\r\n\r\n"
                           + "这次会改到：\r\n" + full + "\r\n\r\n"
                           + "旧位置的那几个文件我不会自动删（按你的规矩，删东西要先问你）。\r\n继续吗？";
                if (MessageBox.Show(msg, "HoverNote 安装程序", MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Information) != DialogResult.OK)
                {
                    return;
                }
            }

            try
            {
                string dll = Path.Combine(full, "HoverNote.dll");
                if (File.Exists(dll))
                {
                    string sure = "这个文件夹里已经有 HoverNote.dll 了，要覆盖成这次的新版本吗？";
                    if (MessageBox.Show(sure, "HoverNote 安装程序", MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question) != DialogResult.Yes)
                    {
                        return;
                    }
                }

                Installer.Install(full);

                string done = "装好了！\r\n\r\n"
                            + "现在去文件夹里，把鼠标停在一个文件上等一两秒。\r\n\r\n"
                            + "如果小黄框里没有多出「解释」和「等级」两行，重启一次电脑就好。\r\n"
                            + "（刚装的这一下，Windows 有时候还记着旧版本。）";
                MessageBox.Show(done, "HoverNote 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                string msg = "安装没成功：\r\n\r\n" + ex.Message + "\r\n\r\n"
                           + "如果是「访问被拒绝」，多半是安全软件（火绒、360 这类）挡住了这个没签名的新程序。\r\n"
                           + "办法一：点上面输入框右边的「换个地方…」，挑一个 D 盘的文件夹再试一次。\r\n"
                           + "办法二：暂时退出安全软件，再运行一次这个安装程序。\r\n\r\n"
                           + "如果是「文件正被占用」，重启电脑以后再运行一次就好。";
                MessageBox.Show(msg, "HoverNote 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            string[] args = Environment.GetCommandLineArgs();

            bool silent = false;
            bool export = false;
            string dirArg = null;
            string exportArg = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "/silent", StringComparison.OrdinalIgnoreCase)) silent = true;
                else if (string.Equals(args[i], "/export", StringComparison.OrdinalIgnoreCase)) export = true;
                else if (args[i].StartsWith("/export=", StringComparison.OrdinalIgnoreCase))
                {
                    export = true;
                    exportArg = args[i].Substring(8).Trim('"');
                }
                else if (args[i].StartsWith("/dir=", StringComparison.OrdinalIgnoreCase)) dirArg = args[i].Substring(5).Trim('"');
            }

            if (export)
            {
                try
                {
                    Digest.Build(exportArg);
                    Environment.Exit(0);
                }
                catch
                {
                    Environment.Exit(1);
                }
                return;
            }

            if (silent)
            {
                try
                {
                    string target = string.IsNullOrEmpty(dirArg) ? Installer.DefaultPath() : dirArg;
                    Installer.Install(target);
                    Environment.Exit(0);
                }
                catch
                {
                    Environment.Exit(1);
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new SetupForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("安装程序自己出错了：\r\n\r\n" + ex.Message, "HoverNote 安装程序",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
