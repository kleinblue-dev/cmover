using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CMover
{
    // ===============================================================
    // 目录联接（junction）—— 本工具安全性的核心
    // 搬完之后在老位置留一个「传送门」，原路径照样能用。
    // 所以「搬走游戏」不会破坏硬编码路径、快捷方式、启动器记录。
    // ===============================================================
    public static class Junction
    {
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
        const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
        const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, byte[] lpInBuffer,
            int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        public static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(path);
                return (a & FileAttributes.ReparsePoint) != 0;
            }
            catch { return false; }
        }

        // 建 junction：linkPath -> targetPath（targetPath 必须是绝对的本机路径）
        //
        // ★★ 这个布局是【实测】出来的，不是照微软文档抄的。别"照文档"改回去。
        //
        // 拿 fsutil 建了一个 junction，再用 FSCTL_GET_REPARSE_POINT 把 Windows
        // 自己写出来的字节 dump 出来，得到（以 D:\moved\...\FakeGame 为例）：
        //
        //   @0   ReparseTag        4    = 0xA0000003
        //   @4   ReparseDataLength 2    = 164  ← = 8 + 路径区长度，【不含】Flags
        //   @6   Reserved          2    = 0
        //   @8   SubstituteNameOffset 2 = 0
        //   @10  SubstituteNameLength 2 = 80   （"\??\D:\..." 的字节数，不含结尾 0）
        //   @12  PrintNameOffset      2 = 82   （= 80 + 2，跳过结尾的 0）
        //   @14  PrintNameLength      2 = 72
        //   @16  PathBuffer            ← 【直接从 16 开始，中间没有 Flags 那一格】
        //          "\??\D:\..." + 00 00 + "D:\..." + 00 00
        //
        // 整个缓冲区 172 字节 = 8 + 164，和 DeviceIoControl 回填的 ReturnLength 一致。
        //
        // 踩过的两个错（都会报 4392 ERROR_INVALID_REPARSE_DATA）：
        //   ① 按文档在 @16 插了 4 字节 Flags  → 路径被推到 @20，数据对不上
        //   ② 长度按 8+4+路径区算 → 164 算成 168，长度对不上
        public static void Create(string linkPath, string targetPath)
        {
            if (IsReparsePoint(linkPath)) throw new Exception("目标位置已经是一个链接了：" + linkPath);
            bool createdHere = !Directory.Exists(linkPath);
            if (createdHere) Directory.CreateDirectory(linkPath);

            try
            {
                byte[] substB = Encoding.Unicode.GetBytes(@"\??\" + targetPath);
                byte[] printB = Encoding.Unicode.GetBytes(targetPath);
                int pathBufLen = substB.Length + 2 + printB.Length + 2;
                int dataLen = 8 + pathBufLen;          // 4 个 ushort + 路径区；没有 Flags

                byte[] buf = new byte[8 + dataLen];
                int o = 0;
                PutU32(buf, ref o, IO_REPARSE_TAG_MOUNT_POINT);
                PutU16(buf, ref o, (ushort)dataLen);
                PutU16(buf, ref o, 0);                            // Reserved
                PutU16(buf, ref o, 0);                            // SubstituteNameOffset
                PutU16(buf, ref o, (ushort)substB.Length);
                PutU16(buf, ref o, (ushort)(substB.Length + 2));  // PrintNameOffset
                PutU16(buf, ref o, (ushort)printB.Length);
                substB.CopyTo(buf, o); o += substB.Length; PutU16(buf, ref o, 0);
                printB.CopyTo(buf, o); o += printB.Length; PutU16(buf, ref o, 0);

                IntPtr h = CreateFileW(linkPath, GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero,
                    OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
                if (h == new IntPtr(-1))
                    throw new Exception("打不开目标位置（错误码 " + Marshal.GetLastWin32Error() + "）：" + linkPath);

                try
                {
                    int ret;
                    bool ok = DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, buf, buf.Length, null, 0, out ret, IntPtr.Zero);
                    if (!ok)
                        throw new Exception("建立链接失败（错误码 " + Marshal.GetLastWin32Error() + "）。" +
                            "如果原位置在 C:\\Program Files 里，需要右键用管理员身份运行本工具。");
                }
                finally { CloseHandle(h); }
            }
            catch
            {
                // 失败时绝不能留下一个空文件夹把原路径占着 ——
                // 那会让回滚以为「原文件夹还在」，于是备份永远改不回原名。
                if (createdHere && !IsReparsePoint(linkPath))
                {
                    try { Directory.Delete(linkPath, false); } catch { }
                }
                throw;
            }
        }

        static void PutU16(byte[] b, ref int o, ushort v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 2; }
        static void PutU32(byte[] b, ref int o, uint v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }

        // 读回 junction 指向哪儿，用来确认它真的指对了地方
        public static string ReadTarget(string linkPath)
        {
            IntPtr h = CreateFileW(linkPath, 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h == new IntPtr(-1)) return null;
            try
            {
                byte[] outBuf = new byte[32768];
                int ret;
                if (!DeviceIoControl(h, FSCTL_GET_REPARSE_POINT, null, 0, outBuf, outBuf.Length, out ret, IntPtr.Zero))
                    return null;
                uint tag = BitConverter.ToUInt32(outBuf, 0);
                if (tag != IO_REPARSE_TAG_MOUNT_POINT) return null;
                int substOff = BitConverter.ToUInt16(outBuf, 8);
                int substLen = BitConverter.ToUInt16(outBuf, 10);
                string s = Encoding.Unicode.GetString(outBuf, 16 + substOff, substLen);
                if (s.StartsWith(@"\??\")) s = s.Substring(4);
                if (s.StartsWith(@"\\?\")) s = s.Substring(4);
                return s;
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }

        // 只删链接本身，绝不递归进目标
        public static void DeleteLinkOnly(string linkPath)
        {
            if (!IsReparsePoint(linkPath)) throw new Exception("这不是一个链接，拒绝删除：" + linkPath);
            Directory.Delete(linkPath, false);
        }
    }

    // ===============================================================
    // 结果
    // ===============================================================
    public class MigrateResult
    {
        public bool Ok;
        public string Message = "";
        public string Log = "";
        public long Bytes;
        public int Files;
        public bool Recovered;   // 出错后是否已完整回滚
    }

    public class Progress
    {
        public string Phase = "";
        public int Percent;
        public string Detail = "";
    }

    // ===============================================================
    // 迁移引擎
    // ===============================================================
    public class Migrator
    {
        public string SrcPath;
        public string DstPath;
        public bool FullVerify = true;
        public bool DryRun = false;
        public bool KeepBackup = false;
        public Action<Progress> OnProgress;

        StringBuilder logSb = new StringBuilder();

        void Log(string s)
        {
            logSb.AppendLine(s);
            Set("", -1, s);
        }

        void Set(string phase, int pct, string detail)
        {
            if (OnProgress != null)
                OnProgress(new Progress { Phase = phase, Percent = pct, Detail = detail });
        }

        public static string RecordsDir()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "CMover");
            Directory.CreateDirectory(Path.Combine(d, "records"));
            return Path.Combine(d, "records");
        }

        public MigrateResult Run()
        {
            MigrateResult res = new MigrateResult();
            string backup = null;
            bool junctionMade = false;

            try
            {
                string src = Path.GetFullPath(SrcPath).TrimEnd('\\');
                string dst = Path.GetFullPath(DstPath).TrimEnd('\\');
                this.SrcPath = src; this.DstPath = dst;

                // ---------- 前置检查 ----------
                Set("检查", 0, "正在检查……");
                if (!Directory.Exists(src)) throw new Exception("源文件夹不存在：" + src);
                if (Junction.IsReparsePoint(src)) throw new Exception("源文件夹已经是一个链接了，说明它已经被搬过一次：" + src);
                if (Scanner.IsLockedName(Path.GetFileName(src))) throw new Exception("这是系统目录，本工具拒绝操作：" + src);
                if (Scanner.IsLockedProfileDir(src)) throw new Exception("这是你的个人目录（桌面/文档/图片等），本工具默认不碰：" + src);

                string srcRoot = Path.GetPathRoot(src);
                string dstRoot = Path.GetPathRoot(dst);
                if (string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("源和目标在同一个盘（都是 " + srcRoot + "），这样搬不会腾出任何空间。");
                if (dst.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase) ||
                    src.StartsWith(dst + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("源文件夹和目标文件夹互相包含，无法操作。");

                Advice adv = KB.Lookup(src, Scanner.ProfileRoot());
                if (adv.Verdict == Verdict.Never)
                    throw new Exception("【" + adv.Title + "】" + adv.HowTo);
                if (adv.Verdict == Verdict.MustUseApp)
                    throw new Exception("【" + adv.Title + "】必须用它自己的设置搬，不能用本工具。\n\n" + adv.HowTo);
                if (adv.Verdict == Verdict.DeleteOnly)
                    throw new Exception("【" + adv.Title + "】这个东西不需要搬，直接删掉就行。\n\n" + adv.HowTo);

                // 目标盘的父目录必须存在且可写
                string dstParent = Path.GetDirectoryName(dst);
                if (!Directory.Exists(dstParent))
                {
                    try { Directory.CreateDirectory(dstParent); }
                    catch (Exception ex) { throw new Exception("建不了目标文件夹 " + dstParent + "：" + ex.Message); }
                }

                // 空间检查
                long srcBytes = DirBytes(src, true);
                DriveInfo di = new DriveInfo(dstRoot);
                long need = (long)(srcBytes * 1.02) + 200L * 1024 * 1024;
                if (di.AvailableFreeSpace < need)
                    throw new Exception(string.Format("{0} 空间不够。需要 {1}，现在只剩 {2}。",
                        dstRoot, Scanner.Fmt(need), Scanner.Fmt(di.AvailableFreeSpace)));

                res.Bytes = srcBytes;
                Log("源：" + src);
                Log("大小：" + Scanner.Fmt(srcBytes));
                Log("目标：" + dst);
                Log("校验方式：" + (FullVerify ? "逐位校验（每个文件都比 SHA256）" : "快速校验（文件数 + 大小）"));

                if (DryRun)
                {
                    res.Ok = true; res.Message = "试运行，什么都没动。" ; res.Log = logSb.ToString();
                    return res;
                }

                // ---------- 1) 复制 ----------
                if (Directory.Exists(dst) && DirBytes(dst, false) > 0)
                    throw new Exception("目标位置已经有东西了：" + dst + "\n请换一个目标，或先把它删掉。");

                Set("复制", 1, "正在复制……大文件夹可能要几分钟到几十分钟。");
                Log("[1/5] 开始复制……");
                int rc = RunRobocopy(src, dst);
                int totalFailBit = rc & 16, partialBit = rc & 8;
                Log("   robocopy 返回码 " + rc + "（1=有复制 2=有多余 4=不匹配 8=部分失败 16=完全失败）");
                if (totalFailBit != 0)
                    throw new Exception("复制完全失败（返回码 " + rc + "）。C 盘一个字节都没动。");
                if (partialBit != 0)
                    Log("   ⚠ 返回码含 8，说明有部分文件没复制成功 —— 下面的逐位校验会把它抓出来。");

                // ---------- 2) 校验 ----------
                Set("校验", 20, "正在逐位校验……这一步最慢，但它是唯一能保证搬全了的办法。");
                Log("[2/5] 开始校验……");
                string diff = Verify(src, dst, FullVerify);
                if (diff != null)
                    throw new Exception("校验没通过，两边内容对不上：\n" + diff +
                        "\n\n★ C 盘一个字节都没动，你的东西是安全的。目标位置那份可以删掉重试。");
                Log("   校验通过：文件数、大小、内容全部一致。");

                // ---------- 3) 备份改名（瞬间完成，不占额外空间）----------
                Set("建立链接", 60, "正在建立「传送门」……");
                Log("[3/5] 把 C 盘原文件夹改名成备份……");
                backup = src + ".迁移备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                Directory.Move(src, backup);
                Log("   " + backup);

                // ---------- 4) 建 junction + 写穿透测试 ----------
                Set("建立链接", 70, "正在验证「传送门」真的通了……");
                Junction.Create(src, dst);
                junctionMade = true;
                Log("[4/5] 已建立链接，现在做「写穿透」测试……");

                string target = Junction.ReadTarget(src);
                Log("   链接指向：" + (target == null ? "(读不出来)" : target));
                if (target == null || !string.Equals(NormalizeLink(target), dst, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("链接指向不对。期望 " + dst + "，实际 " + target);

                // 真·端到端：从 C 盘老路径写一个文件，看它有没有出现在新位置
                string probeName = ".cmover_probe_" + Guid.NewGuid().ToString("N") + ".tmp";
                string viaOld = Path.Combine(src, probeName);
                string viaNew = Path.Combine(dst, probeName);
                File.WriteAllText(viaOld, "probe");
                bool thru = File.Exists(viaNew);
                if (thru) File.Delete(viaOld);
                bool cleaned = !File.Exists(viaNew);
                Log("   写穿透：" + (thru ? "通过（从老路径写文件，新位置立刻看得到）" : "失败"));
                if (!thru)
                    throw new Exception("写穿透测试失败：从 " + src + " 写文件写不到 " + dst + "。");
                if (!cleaned)
                    Log("   ⚠ 测试文件没删干净，请手动删掉 " + viaNew);

                // ---------- 5) 删掉 C 盘那份 ----------
                if (KeepBackup)
                {
                    Log("[5/5] 你选了保留备份，所以 C 盘那份先留着：" + backup);
                    Log("   ★ 空间还没腾出来。确认游戏能正常玩之后，再点「确认并删除备份」。");
                }
                else
                {
                    Set("释放空间", 90, "正在删除 C 盘原文件……");
                    Log("[5/5] 删除 C 盘那一份……");
                    long freed = DirBytes(backup, false);
                    DeleteTree(backup);
                    Log("   已释放约 " + Scanner.Fmt(freed));
                    backup = null;
                }

                // ---------- 记录 ----------
                SaveRecord(src, dst, srcBytes, FullVerify, backup, junctionMade);
                Set("完成", 100, "搬完了。");
                res.Ok = true;
                res.Message = "搬完了。老位置依然可用（已建立传送门），桌面快捷方式不用改。";
                res.Log = logSb.ToString();
                return res;
            }
            catch (Exception ex)
            {
                Log("✗ 出错：" + ex.Message);
                // ---------- 回滚 ----------
                try
                {
                    if (junctionMade && Junction.IsReparsePoint(SrcPath))
                    {
                        Junction.DeleteLinkOnly(SrcPath);
                        Log("   已撤销传送门。");
                    }
                    if (backup != null && Directory.Exists(backup))
                    {
                        // 原路径上如果只剩一个空壳，先清掉，否则改不回名字
                        if (Directory.Exists(SrcPath) && !Junction.IsReparsePoint(SrcPath))
                        {
                            try
                            {
                                if (Directory.GetFileSystemEntries(SrcPath).Length == 0)
                                    Directory.Delete(SrcPath, false);
                            }
                            catch { }
                        }
                        if (!Directory.Exists(SrcPath))
                        {
                            Directory.Move(backup, SrcPath);
                            Log("   已把 C 盘原文件夹改回原名，你的东西完好无损。");
                            res.Recovered = true;
                        }
                        else
                        {
                            Log("   ⚠ 原位置被别的东西占着，没能改回名字。你的文件完整地在这儿：" + backup);
                        }
                    }
                }
                catch (Exception rex)
                {
                    Log("   ⚠ 自动回滚也失败了：" + rex.Message);
                    Log("   ★ 你的文件一定还在，只是位置可能不对，先别删任何东西，把这段日志发出来。");
                }
                res.Ok = false;
                res.Message = ex.Message + (res.Recovered ? "\n\n（已自动还原，C 盘什么都没变。）" : "");
                res.Log = logSb.ToString();
                return res;
            }
        }

        static string NormalizeLink(string p)
        {
            string s = p.Replace('/', '\\');
            if (s.Length > 3) s = s.TrimEnd('\\');
            return s;
        }

        // robocopy —— 用 /XJ 排除 junction，否则会无限递归（Windows 上写递归遍历必须处理这个）
        int RunRobocopy(string src, string dst)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
            psi.Arguments = "\"" + src + "\" \"" + dst + "\" /E /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /MT:16 /NP /NFL /NDL /NJH /NJS";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();   // 不解析内容，只用来记录
            string errp = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (outp.Length > 0) logSb.AppendLine("   robocopy 输出：" + outp.Trim());
            if (errp.Length > 0) logSb.AppendLine("   robocopy 错误：" + errp.Trim());
            return p.ExitCode;
        }

        // 返回 null = 通过；否则返回差异说明
        public static string Verify(string src, string dst, bool full)
        {
            long aBytes, bBytes; int aCount, bCount;
            Dictionary<string, long> a = EnumFiles(src, out aBytes, out aCount);
            Dictionary<string, long> b = EnumFiles(dst, out bBytes, out bCount);
            if (aCount != bCount)
                return string.Format("文件个数不一样：C 盘 {0} 个，新位置 {1} 个。", aCount, bCount);
            if (aBytes != bBytes)
                return string.Format("总大小不一样：C 盘 {0}，新位置 {1}。", Scanner.Fmt(aBytes), Scanner.Fmt(bBytes));

            List<string> missing = new List<string>();
            List<string> sizediff = new List<string>();
            foreach (KeyValuePair<string, long> kv in a)
            {
                long bs;
                if (!b.TryGetValue(kv.Key, out bs)) { if (missing.Count < 20) missing.Add(kv.Key); }
                else if (bs != kv.Value) { if (sizediff.Count < 20) sizediff.Add(kv.Key); }
            }
            if (missing.Count > 0) return "这些文件在新位置找不到：\n" + string.Join("\n", missing.ToArray());
            if (sizediff.Count > 0) return "这些文件大小对不上：\n" + string.Join("\n", sizediff.ToArray());

            if (!full) return null;

            // 逐位校验
            List<string> bad = new List<string>();
            int done = 0;
            foreach (KeyValuePair<string, long> kv in a)
            {
                done++;
                if ((done % 500) == 0 && bad.Count == 0)
                    SetStatic(done, a.Count);
                string h1 = HashFile(Path.Combine(src, kv.Key));
                string h2 = HashFile(Path.Combine(dst, kv.Key));
                if (h1 == null || h2 == null || h1 != h2)
                {
                    if (bad.Count < 20) bad.Add(kv.Key + "  (" + (h1 == null ? "读不到" : h1.Substring(0, 12)) + " vs " + (h2 == null ? "读不到" : h2.Substring(0, 12)) + ")");
                }
            }
            if (bad.Count > 0) return "这些文件内容对不上：\n" + string.Join("\n", bad.ToArray());
            return null;
        }

        static Action<Progress> staticProgress;
        static void SetStatic(int done, int total)
        {
            if (staticProgress != null)
                staticProgress(new Progress { Phase = "校验", Percent = 20 + (int)(65.0 * done / Math.Max(1, total)), Detail = "已比对 " + done + " / " + total + " 个文件" });
        }
        public Action<Progress> OnProgressRef { set { staticProgress = value; } }

        static string HashFile(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        // 枚举文件（相对路径 -> 大小），跳过 junction
        public static Dictionary<string, long> EnumFiles(string root, out long totalBytes, out int count)
        {
            Dictionary<string, long> map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            totalBytes = 0; count = 0;
            Stack<string> st = new Stack<string>();
            st.Push(root);
            while (st.Count > 0)
            {
                string d = st.Pop();
                DirectoryInfo di;
                try { di = new DirectoryInfo(d); } catch { continue; }
                try
                {
                    foreach (FileInfo f in di.GetFiles())
                    {
                        string rel = f.FullName.Substring(root.Length).TrimStart('\\');
                        try { map[rel] = f.Length; totalBytes += f.Length; count++; } catch { }
                    }
                }
                catch { }
                try
                {
                    foreach (DirectoryInfo sd in di.GetDirectories())
                    {
                        if ((sd.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        st.Push(sd.FullName);
                    }
                }
                catch { }
            }
            return map;
        }

        public static long DirBytes(string root, bool skipReparse)
        {
            long t; int c;
            EnumFiles(root, out t, out c);
            return t;
        }

        static void DeleteTree(string dir)
        {
            // 先清只读属性，否则删不掉（游戏文件夹里很常见）
            Stack<string> st = new Stack<string>();
            st.Push(dir);
            while (st.Count > 0)
            {
                string d = st.Pop();
                DirectoryInfo di;
                try { di = new DirectoryInfo(d); } catch { continue; }
                FileInfo[] fs = null; DirectoryInfo[] ds = null;
                try { fs = di.GetFiles(); } catch { }
                try { ds = di.GetDirectories(); } catch { }
                if (fs != null)
                    for (int i = 0; i < fs.Length; i++)
                    {
                        try { if ((fs[i].Attributes & FileAttributes.ReadOnly) != 0) fs[i].Attributes = fs[i].Attributes & ~FileAttributes.ReadOnly; } catch { }
                    }
                if (ds != null)
                    for (int i = 0; i < ds.Length; i++)
                    {
                        if ((ds[i].Attributes & FileAttributes.ReparsePoint) != 0) { try { Junction.DeleteLinkOnly(ds[i].FullName); } catch { } continue; }
                        st.Push(ds[i].FullName);
                    }
            }
            Directory.Delete(dir, true);
        }

        // ---------------------------------------------------------------
        // 记录（就几个字段，用 key=value，避免引入 JSON 库）
        // ---------------------------------------------------------------
        public static void SaveRecord(string src, string dst, long bytes, bool full, string backup, bool junction)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("src=" + src);
                sb.AppendLine("dst=" + dst);
                sb.AppendLine("bytes=" + bytes);
                sb.AppendLine("verify=" + (full ? "full" : "fast"));
                sb.AppendLine("backup=" + (backup == null ? "" : backup));
                sb.AppendLine("junction=" + (junction ? "1" : "0"));
                sb.AppendLine("when=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                string name = SafeName(src) + ".rec";
                File.WriteAllText(Path.Combine(RecordsDir(), name), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        static string SafeName(string p)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else sb.Append('_');
            }
            string s = sb.ToString();
            if (s.Length > 120) s = s.Substring(s.Length - 120);
            return s;
        }

        public class Record
        {
            public string Src, Dst, Backup, When, Verify;
            public long Bytes;
            public bool HasJunction;
            public string File;
        }

        public static List<Record> LoadRecords()
        {
            List<Record> list = new List<Record>();
            string[] files = null;
            try { files = Directory.GetFiles(RecordsDir(), "*.rec"); } catch { }
            if (files == null) return list;
            for (int i = 0; i < files.Length; i++)
            {
                Record r = new Record();
                r.File = files[i];
                try
                {
                    string[] lines = File.ReadAllLines(files[i]);
                    for (int j = 0; j < lines.Length; j++)
                    {
                        int eq = lines[j].IndexOf('=');
                        if (eq <= 0) continue;
                        string k = lines[j].Substring(0, eq);
                        string v = lines[j].Substring(eq + 1);
                        if (k == "src") r.Src = v;
                        else if (k == "dst") r.Dst = v;
                        else if (k == "bytes") { try { r.Bytes = long.Parse(v); } catch { } }
                        else if (k == "backup") r.Backup = v;
                        else if (k == "when") r.When = v;
                        else if (k == "verify") r.Verify = v;
                        else if (k == "junction") r.HasJunction = (v == "1");
                    }
                    if (r.Src != null && r.Dst != null) list.Add(r);
                }
                catch { }
            }
            return list;
        }

        // 撤销：把东西从目标盘搬回 C 盘原位置
        public static MigrateResult Undo(Record r, Action<Progress> prog)
        {
            MigrateResult res = new MigrateResult();
            StringBuilder lg = new StringBuilder();
            try
            {
                if (prog != null) prog(new Progress { Phase = "撤销", Percent = 0, Detail = "检查中……" });
                if (!Directory.Exists(r.Dst)) throw new Exception("目标位置的文件夹不在了：" + r.Dst);

                bool isLink = Junction.IsReparsePoint(r.Src);
                if (isLink)
                {
                    string t = Junction.ReadTarget(r.Src);
                    if (t == null || !string.Equals(t.TrimEnd('\\'), r.Dst.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        throw new Exception("老位置的链接指向的不是 " + r.Dst + "，我不敢动它。实际指向：" + t);
                }

                string tmp = r.Src + ".撤销临时_" + DateTime.Now.ToString("yyyyMMddHHmmss");
                if (isLink)
                {
                    lg.AppendLine("删掉传送门……");
                    Junction.DeleteLinkOnly(r.Src);
                }
                else if (Directory.Exists(r.Src))
                {
                    lg.AppendLine("老位置现在是个真文件夹，先改名让路……");
                    Directory.Move(r.Src, tmp);
                }

                if (prog != null) prog(new Progress { Phase = "撤销", Percent = 10, Detail = "正在把文件搬回 C 盘……" });
                lg.AppendLine("复制回 C 盘……");
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
                psi.Arguments = "\"" + r.Dst + "\" \"" + r.Src + "\" /E /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /MT:16 /NP /NFL /NDL /NJH /NJS";
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
                int rc = p.ExitCode;
                lg.AppendLine("robocopy 返回码 " + rc);
                if ((rc & 16) != 0) throw new Exception("复制回 C 盘失败（返回码 " + rc + "）。目标盘那份没删，你的东西是安全的。");

                if (prog != null) prog(new Progress { Phase = "撤销", Percent = 70, Detail = "校验中……" });
                lg.AppendLine("校验……");
                string diff = Verify(r.Dst, r.Src, true);
                if (diff != null) throw new Exception("搬回 C 盘之后校验没通过：\n" + diff + "\n目标盘那份没删。");

                lg.AppendLine("删掉目标盘那份……");
                DeleteTree(r.Dst);
                try { File.Delete(r.File); } catch { }
                // 下面这两个都不删。宁可多占一点地方，也不替用户决定删什么 ——
                // 一旦删错，那是不可逆的。
                if (Directory.Exists(tmp))
                    lg.AppendLine("★ 原位置当时被一个真文件夹占着，我把它挪到这儿了，没动它：" + tmp);
                if (!string.IsNullOrEmpty(r.Backup) && Directory.Exists(r.Backup))
                    lg.AppendLine("★ 你之前选过「保留备份」，那份还在：" + r.Backup + "（确认没问题后可以自己删）");

                res.Ok = true;
                res.Message = "已搬回 C 盘。";
                if (prog != null) prog(new Progress { Phase = "撤销", Percent = 100, Detail = "完成" });
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Message = ex.Message;
            }
            res.Log = lg.ToString();
            return res;
        }
    }
}
