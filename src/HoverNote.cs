using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

[assembly: ComVisible(false)]

namespace HoverNote
{
    internal enum TipLevel
    {
        Safe,
        Caution,
        Danger,
        Unknown
    }

    internal enum RuleKind
    {
        Exact,
        Prefix,
        Suffix,
        Glob
    }

    /// <summary>一条规则。由 rules/rules.json 生成，运行时不读任何文件。</summary>
    internal sealed class Rule
    {
        public readonly string Id;
        public readonly string Pattern;
        public readonly RuleKind Kind;
        public readonly TipLevel Level;
        public readonly string Explain;
        public readonly bool Files;
        public readonly bool Dirs;
        public readonly int Priority;

        public Rule(string id, string pattern, RuleKind kind, TipLevel level, string explain, bool files, bool dirs, int priority)
        {
            Id = id;
            Pattern = pattern;
            Kind = kind;
            Level = level;
            Explain = explain;
            Files = files;
            Dirs = dirs;
            Priority = priority;
        }
    }

    internal static class RuleMatcher
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Regex> Cache = new Dictionary<string, Regex>(StringComparer.Ordinal);

        public static bool TryMatch(Rule rule, string norm, out int literal, out int wildcards)
        {
            literal = 0;
            wildcards = 0;

            switch (rule.Kind)
            {
                case RuleKind.Exact:
                    literal = rule.Pattern.Length;
                    return string.Equals(norm, rule.Pattern, StringComparison.OrdinalIgnoreCase);

                case RuleKind.Prefix:
                    literal = rule.Pattern.Length;
                    if (norm.Length < rule.Pattern.Length) return false;
                    if (!norm.StartsWith(rule.Pattern, StringComparison.OrdinalIgnoreCase)) return false;
                    return norm.Length == rule.Pattern.Length || norm[rule.Pattern.Length] == '\\';

                case RuleKind.Suffix:
                    literal = rule.Pattern.Length;
                    string ext = Path.GetExtension(norm);
                    if (string.IsNullOrEmpty(ext)) return false;
                    string[] parts = rule.Pattern.Split('|');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (string.Equals(ext, parts[i].Trim(), StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    return false;

                default:
                    CountParts(rule.Pattern, out literal, out wildcards);
                    Regex re = GetRegex(rule.Pattern);
                    if (IsFileGlob(rule.Pattern)) return re.IsMatch(norm);

                    // 目录型通配：路径本身或任意一层上级命中都算
                    string cur = norm;
                    while (true)
                    {
                        if (re.IsMatch(cur)) return true;
                        int cut = cur.LastIndexOf('\\');
                        if (cut <= 2) return false;
                        cur = cur.Substring(0, cut);
                    }
            }
        }

        private static bool IsFileGlob(string pattern)
        {
            int cut = pattern.LastIndexOf('\\');
            string last = cut < 0 ? pattern : pattern.Substring(cut + 1);
            return last.IndexOf('.') >= 0;
        }

        private static void CountParts(string pattern, out int literal, out int wildcards)
        {
            literal = 0;
            wildcards = 0;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == '*' || pattern[i] == '?') wildcards++;
                else literal++;
            }
        }

        private static Regex GetRegex(string pattern)
        {
            lock (Gate)
            {
                Regex cached;
                if (Cache.TryGetValue(pattern, out cached)) return cached;

                StringBuilder sb = new StringBuilder("^");
                for (int i = 0; i < pattern.Length; i++)
                {
                    char c = pattern[i];
                    if (c == '*') sb.Append("[^\\\\]*");
                    else if (c == '?') sb.Append("[^\\\\]");
                    else if (c == '\\') sb.Append("\\\\");
                    else if (".$^+()[]{}|#".IndexOf(c) >= 0)
                    {
                        sb.Append('\\');
                        sb.Append(c);
                    }
                    else sb.Append(c);
                }
                sb.Append('$');

                Regex re = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                Cache[pattern] = re;
                return re;
            }
        }
    }

    internal static class RuleEngine
    {
        public static Rule Match(string norm, bool isDir)
        {
            Rule best = null;
            int bestScore = int.MinValue;

            Rule[] all = RulesData.All;
            for (int i = 0; i < all.Length; i++)
            {
                Rule r = all[i];
                if (isDir && !r.Dirs) continue;
                if (!isDir && !r.Files) continue;

                int literal;
                int wildcards;
                if (!RuleMatcher.TryMatch(r, norm, out literal, out wildcards)) continue;

                int score = Score(r.Kind, literal, wildcards) + r.Priority;
                if (score > bestScore || (score == bestScore && best != null && r.Pattern.Length > best.Pattern.Length))
                {
                    best = r;
                    bestScore = score;
                }
            }

            return best;
        }

        // 越具体越优先：精确 > 长前缀 > 通配少 > 后缀
        private static int Score(RuleKind kind, int literal, int wildcards)
        {
            switch (kind)
            {
                case RuleKind.Exact: return 100000;
                case RuleKind.Prefix: return literal * 10;
                case RuleKind.Suffix: return literal * 2;
                default: return literal * 10 - wildcards * 5;
            }
        }

        public static string LevelText(TipLevel level)
        {
            switch (level)
            {
                case TipLevel.Safe: return "安全 · 可以放心删";
                case TipLevel.Caution: return "谨慎 · 删前先看一眼里面的东西";
                case TipLevel.Danger: return "危险 · 系统核心，别删";
                default: return "不确定 · 建议先别动";
            }
        }
    }


    // 运行时才知道的事：系统告诉我们的「桌面/下载在哪」，以及按长相猜的文件夹类型。
    // 全部只读：不写文件、不联网、不上传。
    internal static class LocalKnowledge
    {
        private const int MaxScan = 400;

        private sealed class ShellFolder
        {
            public string Label;
            public string Explain;
            public TipLevel Level;
        }

        private static Dictionary<string, ShellFolder> _shell;
        private static readonly object Gate = new object();

        public static bool TryDescribe(string norm, bool isDir, out string explain, out TipLevel level)
        {
            explain = null;
            level = TipLevel.Unknown;

            Dictionary<string, ShellFolder> shell = ShellFolders();

            ShellFolder exact;
            if (shell.TryGetValue(norm, out exact))
            {
                explain = exact.Explain;
                level = exact.Level;
                return true;
            }

            string leaf = LeafOf(norm).ToLowerInvariant();
            if (NameKnowledge(leaf, out explain, out level)) return true;

            if (!isDir) return false;

            bool confident;
            bool described = GuessByContent(norm, out explain, out level, out confident);
            if (described && confident) return true;

            string parent = ParentOf(norm);
            if (parent != null)
            {
                ShellFolder p;
                if (shell.TryGetValue(parent, out p))
                {
                    explain = "这是你" + p.Label + "里的东西。";
                    level = p.Level;
                    return true;
                }
            }

            return described;
        }

        private static bool NameKnowledge(string leaf, out string explain, out TipLevel level)
        {
            explain = null;
            level = TipLevel.Unknown;

            if (leaf == "temp" || leaf == "tmp" || leaf == "cache" || leaf == "caches"
                || leaf == ".cache" || leaf == ".pnpm-store" || leaf == "logs" || leaf == "log"
                || leaf == "__pycache__" || leaf == ".gradle" || leaf == ".m2" || leaf == ".nuget"
                || leaf == "crashdumps" || leaf == "prefetch")
            {
                explain = "这是缓存或临时文件，程序用着用着会自己重建，删了不心疼。";
                level = TipLevel.Safe;
                return true;
            }

            if (leaf == "node_modules" || leaf == "site-packages" || leaf == "venv" || leaf == ".venv")
            {
                explain = "这是某个项目依赖的库，删了那个项目就跑不起来了，用的时候得重新装一遍。";
                level = TipLevel.Safe;
                return true;
            }

            if (leaf == ".git" || leaf == ".svn" || leaf == ".hg")
            {
                explain = "这是版本记录，删了以后就回不到以前的版本了。";
                level = TipLevel.Caution;
                return true;
            }

            if (leaf == "desktop" || leaf == "桌面")
            {
                explain = "看名字这是「桌面」文件夹，放常用东西的地方。";
                level = TipLevel.Caution;
                return true;
            }
            if (leaf == "downloads" || leaf == "下载")
            {
                explain = "看名字这是「下载」文件夹，浏览器下载的东西放这儿。";
                level = TipLevel.Caution;
                return true;
            }
            if (leaf == "documents" || leaf == "文档" || leaf == "my documents")
            {
                explain = "看名字这是「文档」文件夹，你自己存的文件常在这儿。";
                level = TipLevel.Caution;
                return true;
            }
            if (leaf == "pictures" || leaf == "图片" || leaf == "my pictures")
            {
                explain = "看名字这是「图片」文件夹，放照片和截图的地方。";
                level = TipLevel.Caution;
                return true;
            }
            if (leaf == "videos" || leaf == "视频" || leaf == "my video")
            {
                explain = "看名字这是「视频」文件夹，放录像和下载的视频。";
                level = TipLevel.Caution;
                return true;
            }
            if (leaf == "music" || leaf == "音乐" || leaf == "my music")
            {
                explain = "看名字这是「音乐」文件夹，放歌的地方。";
                level = TipLevel.Caution;
                return true;
            }

            return false;
        }

        private static bool GuessByContent(string dir, out string explain, out TipLevel level, out bool confident)
        {
            explain = null;
            level = TipLevel.Unknown;
            confident = false;

            int files = 0;
            int dirs = 0;
            int exe = 0;
            int dll = 0;
            int pak = 0;
            int jar = 0;
            int img = 0;
            int vid = 0;
            int lnk = 0;
            bool uninstall = false;
            bool git = false;
            bool project = false;
            int scanned = 0;

            try
            {
                DirectoryInfo di = new DirectoryInfo(dir);
                foreach (FileSystemInfo fsi in di.EnumerateFileSystemInfos())
                {
                    if (++scanned > MaxScan) break;

                    bool isSub;
                    try
                    {
                        isSub = (fsi.Attributes & FileAttributes.Directory) != 0;
                    }
                    catch
                    {
                        // 看不出来就当文件数，别把条目整个丢掉
                        files++;
                        continue;
                    }

                    if (isSub)
                    {
                        dirs++;
                        string sub = fsi.Name.ToLowerInvariant();
                        if (sub == ".git") git = true;
                        if (sub == "bin" || sub == "obj" || sub == "target" || sub == "build") project = true;
                        continue;
                    }

                    files++;
                    string ext = Path.GetExtension(fsi.Name).ToLowerInvariant();
                    string name = fsi.Name.ToLowerInvariant();

                    if (name.StartsWith("unins") || name.StartsWith("uninstall") || name == "uninst.exe") uninstall = true;

                    switch (ext)
                    {
                        case ".exe": exe++; break;
                        case ".dll": case ".sys": case ".ocx": dll++; break;
                        case ".pak": case ".bin": case ".dat2": pak++; break;
                        case ".jar": jar++; break;
                        case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".webp": img++; break;
                        case ".mp4": case ".mov": case ".mkv": case ".avi": case ".flv": vid++; break;
                        case ".lnk": case ".url": lnk++; break;
                        case ".sln": case ".csproj": case ".vcxproj": case ".uproject":
                        case ".blend": case ".blend1": case ".c": case ".cpp": case ".cs": case ".py":
                            project = true; break;
                        case ".json": case ".toml": case ".xml":
                            if (name == "package.json" || name == "cargo.toml" || name == "pom.xml") project = true;
                            break;
                        case ".txt":
                            if (name == "requirements.txt" || name == "cmakelists.txt") project = true;
                            break;
                    }
                }
            }
            catch
            {
                return false;
            }

            if (uninstall)
            {
                explain = "这个文件夹里装着一个软件（里面有卸载程序），删了那个软件就用不了。";
                level = TipLevel.Caution;
                confident = true;
                return true;
            }

            if (exe >= 3 || (exe >= 1 && dll >= 3) || (exe >= 1 && pak >= 2) || (exe >= 1 && jar >= 2))
            {
                explain = "这里装着软件或游戏（里面有 .exe、.dll 这些程序文件），删了那些程序就用不了。";
                level = TipLevel.Caution;
                confident = true;
                return true;
            }

            if (git || project)
            {
                explain = "看着像你自己做的东西（工程、代码或项目文件），删了心血就没了。";
                level = TipLevel.Caution;
                confident = true;
                return true;
            }

            if (exe >= 1)
            {
                explain = "这个文件夹里放着程序（.exe），删了它就跑不起来了。";
                level = TipLevel.Caution;
                confident = true;
                return true;
            }

            if (files + dirs == 0)
            {
                explain = "这是个空文件夹，里面什么都没有，删了不影响。";
                level = TipLevel.Safe;
                confident = true;
                return true;
            }

            if (img >= 8 && img * 10 >= files * 7)
            {
                explain = "这里基本都是图片，看着像你自己存的照片或素材。";
                level = TipLevel.Caution;
                confident = true;
                return true;
            }

            if (vid >= 3 && vid * 10 >= files * 6)
            {
                explain = "这里主要是视频，看着像你自己录的或下载的视频。";
                level = TipLevel.Caution;
                confident = true;
                return true;
            }

            if (lnk >= 5 && lnk * 10 >= files * 6)
            {
                explain = "这里基本是快捷方式，删了只是少几个图标，程序本身还在别处。";
                level = TipLevel.Safe;
                confident = true;
                return true;
            }

            if (files == 0 && dirs >= 3 && HasProgramsBelow(dir, 4))
            {
                explain = "这里面分门别类装了好几个软件或游戏，删了那些程序就用不了。";
                level = TipLevel.Caution;
                confident = true;
                return true;
            }

            if (dirs == 0)
            {
                explain = "里面放着 " + files + " 个文件，HoverNote 还说不清它们是干什么的。";
            }
            else if (files == 0)
            {
                explain = "里面是 " + dirs + " 个文件夹，HoverNote 还说不清它们是干什么的。";
            }
            else
            {
                explain = "里面装着 " + files + " 个文件、" + dirs + " 个文件夹，HoverNote 还说不清它们是干什么的。";
            }

            level = TipLevel.Unknown;
            confident = false;
            return true;
        }

        // 只看下面几层文件夹里有没有 .exe / .dll / .pak 这类程序文件，每个最多看 30 条
        private static bool HasProgramsBelow(string dir, int maxDirs)
        {
            int dirsSeen = 0;
            try
            {
                foreach (DirectoryInfo sub in new DirectoryInfo(dir).EnumerateDirectories())
                {
                    if (++dirsSeen > maxDirs) break;

                    int seen = 0;
                    try
                    {
                        foreach (FileSystemInfo fsi in sub.EnumerateFileSystemInfos())
                        {
                            if (++seen > 30) break;

                            string name = fsi.Name.ToLowerInvariant();
                            if (name.StartsWith("unins")) return true;

                            string ext = Path.GetExtension(name);
                            if (ext == ".exe" || ext == ".dll" || ext == ".pak" || ext == ".jar" || ext == ".so") return true;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static Dictionary<string, ShellFolder> ShellFolders()
        {
            lock (Gate)
            {
                if (_shell != null) return _shell;

                Dictionary<string, ShellFolder> map = new Dictionary<string, ShellFolder>(StringComparer.OrdinalIgnoreCase);

                Add(map, ReadShell("Desktop", "{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", Environment.SpecialFolder.DesktopDirectory), "桌面",
                    "这是你的桌面，常用东西和快捷方式都放这儿。", TipLevel.Caution);
                Add(map, ReadShell("Personal", "{FDD39AD0-238F-46AF-ADB4-6C85480369C7}", Environment.SpecialFolder.MyDocuments), "文档",
                    "这是「文档」文件夹，你自己存的文件通常在这儿。", TipLevel.Caution);
                Add(map, ReadShell("{374DE290-123F-4565-9164-39C4925E467B}", "{374DE290-123F-4565-9164-39C4925E467B}", Environment.SpecialFolder.MyDocuments), "下载",
                    "这是「下载」文件夹，浏览器下载的东西默认放这儿。没用的可以删，但别一键清空。", TipLevel.Caution);
                Add(map, ReadShell("My Pictures", "{33E28130-4E1E-4676-835A-98395C3BC3BB}", Environment.SpecialFolder.MyPictures), "图片",
                    "这是「图片」文件夹，放照片和截图的地方。", TipLevel.Caution);
                Add(map, ReadShell("My Music", "{4BD8D571-6D19-48D3-BE97-422220080E68}", Environment.SpecialFolder.MyMusic), "音乐",
                    "这是「音乐」文件夹，放歌的地方。", TipLevel.Caution);
                Add(map, ReadShell("My Video", "{18989B1D-99B5-455B-841C-AB7C74E4DDFC}", Environment.SpecialFolder.MyVideos), "视频",
                    "这是「视频」文件夹，放录像和视频的地方。", TipLevel.Caution);
                Add(map, ReadShell("OneDrive", "{A52BBA46-E9E1-435F-B3D9-28DAA648C0F6}", Environment.SpecialFolder.MyDocuments), "OneDrive 同步文件夹",
                    "这是 OneDrive 同步文件夹，删了这里的东西，云上那份也会跟着没。", TipLevel.Caution);

                _shell = map;
                return _shell;
            }
        }

        private static void Add(Dictionary<string, ShellFolder> map, string path, string label, string explain, TipLevel level)
        {
            if (string.IsNullOrEmpty(path)) return;

            string norm;
            try { norm = PathUtil.Normalize(path); }
            catch { return; }
            if (string.IsNullOrEmpty(norm)) return;

            try { if (!Directory.Exists(norm)) return; }
            catch { return; }

            if (norm.Length <= 3) return;

            // 别把「整个用户文件夹」当成桌面/下载
            try
            {
                string profile = PathUtil.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                if (string.Equals(norm, profile, StringComparison.OrdinalIgnoreCase)) return;
            }
            catch
            {
            }

            if (map.ContainsKey(norm)) return;

            ShellFolder f = new ShellFolder();
            f.Label = label;
            f.Explain = explain;
            f.Level = level;
            map[norm] = f;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        private static string KnownFolder(string guid)
        {
            try
            {
                Guid g = new Guid(guid);
                IntPtr buf;
                int hr = SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out buf);
                if (hr != 0 || buf == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(buf); }
                finally { Marshal.FreeCoTaskMem(buf); }
            }
            catch
            {
                return null;
            }
        }

        private static string ReadShell(string key, string knownGuid, Environment.SpecialFolder fallback)
        {
            // 1) 先问 Windows 自己要（最准，也不怕注册表被安全软件挡住）
            string known = KnownFolder(knownGuid);
            if (!string.IsNullOrEmpty(known)) return known;

            // 2) 再试注册表里的 User Shell Folders
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SoftwareMicrosoftWindowsCurrentVersionExplorerUser Shell Folders"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue(key);
                        string s = v as string;
                        if (s != null && s.Length > 0)
                        {
                            s = Environment.ExpandEnvironmentVariables(s);
                            if (s.Length > 0) return s;
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                string p = Environment.GetFolderPath(fallback);
                return string.IsNullOrEmpty(p) ? null : p;
            }
            catch
            {
                return null;
            }
        }

        private static string LeafOf(string norm)
        {
            if (string.IsNullOrEmpty(norm)) return string.Empty;
            int cut = norm.LastIndexOf('\\');
            return cut < 0 ? norm : norm.Substring(cut + 1);
        }

        private static string ParentOf(string norm)
        {
            if (string.IsNullOrEmpty(norm)) return null;
            int cut = norm.LastIndexOf('\\');
            if (cut <= 2) return null;
            return norm.Substring(0, cut);
        }
    }

    // 读取下载标签（Zone.Identifier 备用数据流），只取域名
    internal static class ZoneInfo
    {
        private const uint GenericRead = 0x80000000;
        private const uint ShareAll = 0x00000007;
        private const uint OpenExisting = 3;
        private const uint AttributeNormal = 0x00000080;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static string GetHost(string filePath)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = CreateFileW(filePath + ":Zone.Identifier", GenericRead, ShareAll, IntPtr.Zero,
                    OpenExisting, AttributeNormal, IntPtr.Zero);
                if (h == new IntPtr(-1)) return null;

                byte[] buf = new byte[2048];
                uint read;
                if (!ReadFile(h, buf, (uint)buf.Length, out read, IntPtr.Zero) || read == 0) return null;

                string text = System.Text.Encoding.UTF8.GetString(buf, 0, (int)read);
                string url = FindValue(text, "HostUrl=");
                if (url == null) url = FindValue(text, "ReferrerUrl=");
                if (url == null) return null;

                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return null;
                string host = uri.Host;
                if (string.IsNullOrEmpty(host) || host == "localhost" || host == "0.0.0.0") return null;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host.Substring(4);
                return host.ToLowerInvariant();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
            }
        }

        private static string FindValue(string text, string key)
        {
            int i = 0;
            while (true)
            {
                i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                bool atLineStart = (i == 0) || text[i - 1] == '\n' || text[i - 1] == '\r';
                if (atLineStart)
                {
                    int start = i + key.Length;
                    int end = start;
                    while (end < text.Length && text[end] != '\r' && text[end] != '\n' && text[end] != 0) end++;
                    string v = text.Substring(start, end - start).Trim();
                    if (v.Length > 0) return v;
                }
                i += key.Length;
            }
        }
    }

    internal static class TipText
    {
        public static string Build(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;

                string norm = PathUtil.Normalize(path);
                bool isDir = Directory.Exists(norm);
                DateTime modified;
                string sizeText;

                if (isDir)
                {
                    modified = new DirectoryInfo(norm).LastWriteTime;
                    sizeText = "文件夹";
                }
                else
                {
                    FileInfo fi = new FileInfo(norm);
                    if (!fi.Exists) return null;
                    modified = fi.LastWriteTime;
                    sizeText = HumanSize(fi.Length);
                }

                string explain;
                TipLevel level;
                Rule rule = RuleEngine.Match(norm, isDir);

                if (TrySpecial(norm, out explain, out level))
                {
                    // 网络位置、盘符根：规则表不管，单独说
                }
                else if (rule != null)
                {
                    explain = rule.Explain;
                    level = rule.Level;
                }
                else if (LocalKnowledge.TryDescribe(norm, isDir, out explain, out level))
                {
                    // 运行时认出来的：常用文件夹、按长相猜的类型、或者至少数一下里面有多少东西
                }
                else if (isDir)
                {
                    // 没命中规则、也不是系统位置：不猜内容
                    explain = "你自己建的文件夹，HoverNote 不认识里面的内容。";
                    level = TipLevel.Unknown;
                }
                else
                {
                    explain = "这种文件 HoverNote 还没有经验，建议先别动。";
                    level = TipLevel.Unknown;
                }

                if (!isDir)
                {
                    string host = ZoneInfo.GetHost(norm);
                    if (host != null) explain = explain + "（从 " + host + " 下载来的）";
                }

                string tip = "大小   " + sizeText + "\n"
                           + "修改   " + modified.ToString("yyyy-MM-dd HH:mm") + "\n"
                           + "解释   " + explain + "\n"
                           + "等级   " + RuleEngine.LevelText(level);

                return tip.Length > 1024 ? tip.Substring(0, 1024) : tip;
            }
            catch (Exception ex)
            {
                // 排查用：设了 HOVERNOTE_DEBUG=1 才把异常写进临时文件
                try
                {
                    if (Environment.GetEnvironmentVariable("HOVERNOTE_DEBUG") == "1")
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "hovernote-debug.txt"),
                            DateTime.Now.ToString("HH:mm:ss") + " " + path + " => " + ex + "\r\n");
                    }
                }
                catch
                {
                }

                return null;
            }
        }

        private static bool TrySpecial(string norm, out string explain, out TipLevel level)
        {
            explain = null;
            level = TipLevel.Unknown;

            // 网络位置：\\server\share 或映射成盘符的网络盘
            bool unc = norm.StartsWith(@"\\", StringComparison.Ordinal);

            // 盘符根：C:  或  C:\  （Normalize 后是 "C:" 之外还可能是 "C:\" 两字符）
            bool root = (norm.Length == 2 || norm.Length == 3)
                        && norm[1] == ':'
                        && (norm.Length == 2 || norm[2] == '\\');

            if (!unc && !root) return false;

            if (root)
            {
                string drive = norm.Substring(0, 2);

                bool mappedNetwork = false;
                try
                {
                    mappedNetwork = new DriveInfo(drive).DriveType == DriveType.Network;
                }
                catch
                {
                    mappedNetwork = false;
                }

                if (mappedNetwork)
                {
                    explain = "这是连到网络上的一台电脑或服务器的盘，东西不在你本机，删了可能影响别人。";
                    level = TipLevel.Caution;
                    return true;
                }

                string systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
                bool isSystem = string.Equals(drive + "\\", systemDrive, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(drive, Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

                if (isSystem)
                {
                    explain = "这是系统盘的根目录，Windows 就装在这个盘里，别整盘动。";
                    level = TipLevel.Danger;
                }
                else
                {
                    explain = "这是 " + drive + " 盘的根目录，整个盘里的东西都在它下面。";
                    level = TipLevel.Caution;
                }
                return true;
            }

            explain = "网络上的共享文件夹，东西不在你这台电脑里，删了可能影响别人。";
            level = TipLevel.Caution;
            return true;
        }

        private static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString() + " B";
            double v = bytes / 1024.0;
            if (v < 1024) return v.ToString("0.0") + " KB";
            v = v / 1024.0;
            if (v < 1024) return v.ToString("0.0") + " MB";
            v = v / 1024.0;
            return v.ToString("0.0") + " GB";
        }
    }

    internal static class PathUtil
    {
        public static string Normalize(string path)
        {
            if (path == null) return string.Empty;
            string p = path.Trim();
            if (p.Length > 4 && p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);
            p = p.Replace('/', '\\');
            while (p.Length > 3 && p.EndsWith("\\", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 1);
            return p;
        }
    }

    [ComVisible(true)]
    [Guid("00021500-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IQueryInfo
    {
        [PreserveSig]
        int GetInfoTip(uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszTip);

        [PreserveSig]
        int GetInfoFlags(out uint pdwFlags);
    }

    [ComVisible(true)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComVisible(true)]
    [Guid("B6F3A1C2-7D4E-4A9B-9C31-5E7A2D8F1B60")]
    [ClassInterface(ClassInterfaceType.None)]
    [ProgId("HoverNote.InfoTipHandler")]
    public sealed class InfoTipHandler : IQueryInfo, IPersistFile
    {
        private static readonly Guid ClassId = new Guid("B6F3A1C2-7D4E-4A9B-9C31-5E7A2D8F1B60");
        private string _path = string.Empty;

        public void GetClassID(out Guid pClassID)
        {
            pClassID = ClassId;
        }

        public int IsDirty()
        {
            return 1;
        }

        public void Load(string pszFileName, uint dwMode)
        {
            _path = pszFileName == null ? string.Empty : pszFileName;
        }

        public void Save(string pszFileName, bool fRemember)
        {
        }

        public void SaveCompleted(string pszFileName)
        {
        }

        public void GetCurFile(out string ppszFileName)
        {
            ppszFileName = _path;
        }

        public int GetInfoTip(uint dwFlags, out string ppwszTip)
        {
            ppwszTip = null;

            try
            {
                string tip = TipText.Build(_path);
                if (tip == null) return unchecked((int)0x80004005);

                ppwszTip = tip;
                return 0;
            }
            catch
            {
                return unchecked((int)0x80004005);
            }
        }

        public int GetInfoFlags(out uint pdwFlags)
        {
            pdwFlags = 0;
            return 0;
        }
    }
}
