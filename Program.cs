using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CMover
{
    static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        public const string Ver = "1.1";

        [STAThread]
        static int Main(string[] args)
        {
            Logger.Rotate();      // 上次的日志超过 1 MB 就留一份 .log.1
            Banner(args);

            if (args.Length == 0 || args[0] == "--gui")
            {
                // ★ 这两个处理器是日志最值钱的地方。
                //   工具在别人电脑上崩了，没有它们你只能看到一个「已停止工作」。
                Application.ThreadException +=
                    new System.Threading.ThreadExceptionEventHandler(OnUiThreadException);
                AppDomain.CurrentDomain.UnhandledException +=
                    new UnhandledExceptionEventHandler(OnFatal);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }

            // 命令行模式：挂到调用者的控制台上，这样能打印。
            // 挂不上就算了，绝不 AllocConsole —— 那会在用户屏幕上弹一个黑窗口。
            AttachConsole(-1);
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            try
            {
                switch (args[0])
                {
                    case "--scan": return CmdScan(args);
                    case "--migrate": return CmdMigrate(args);
                    case "--verify": return CmdVerify(args);
                    case "--undo": return CmdUndo(args);
                    case "--records": return CmdRecords();
                    case "--check": return CmdCheck();
                    case "--kb": return CmdKb(args);
                    case "--selftest": return CmdSelfTest();
                    case "--log": Console.WriteLine(Logger.FilePath()); return 0;
                    case "--help": Help(); return 0;
                    default: Help(); return 2;
                }
            }
            catch (Exception ex)
            {
                Logger.W("★ 命令行出错: " + ex.ToString());
                Console.WriteLine("出错：" + ex.Message);
                return 1;
            }
        }

        // 每次启动先写一段「这台机器当时是什么样」。
        // 事后看日志时，一半的问题靠这几行就能定位（是不是管理员、什么系统、什么参数）。
        static void Banner(string[] args)
        {
            Logger.W("");
            Logger.W("========== 启动 " + Ver + " ==========");
            Logger.W("系统   " + Environment.OSVersion.VersionString
                     + "   " + (Environment.Is64BitOperatingSystem ? "64位" : "32位"));
            try { Logger.W("程序   " + Application.ExecutablePath); } catch { }
            Logger.W("身份   " + (IsAdmin() ? "管理员" : "普通用户"));
            Logger.W("参数   " + (args.Length == 0 ? "(无，打开界面)" : string.Join(" ", args)));
            Logger.W("日志   " + Logger.FilePath());
        }

        // 界面线程里没接住的异常。
        // 默认的 WinForms 对话框只说「未处理异常」，然后就没了 —— 我们换成
        // 一个能告诉用户【日志在哪】的框，并且先把它写进日志。
        static void OnUiThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            Logger.W("★★ 界面线程未处理异常 ★★");
            Logger.W(e.Exception == null ? "(没有异常对象)" : e.Exception.ToString());
            try
            {
                MessageBox.Show(
                    "出了个没预料到的错，工具没法继续了。\n\n"
                    + (e.Exception == null ? "" : e.Exception.Message)
                    + "\n\n详情已经写进这个文件：\n" + Logger.FilePath()
                    + "\n\n把它发给作者，就能查出问题。",
                    "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        // 进程马上就要死了，除了赶紧记一笔什么都做不了。
        static void OnFatal(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.W("★★ 致命异常，进程即将退出 ★★");
            Logger.W(e.ExceptionObject == null ? "(没有异常对象)" : e.ExceptionObject.ToString());
        }

        static void Help()
        {
            Console.WriteLine("C盘轻量迁移清理器");
            Console.WriteLine("  不带参数              打开界面");
            Console.WriteLine("  --scan <报告.html> [--min-gb 2] [--depth 5] [--txt 报告.txt]");
            Console.WriteLine("  --migrate <文件夹> <目标盘根目录> [--fast] [--dry] [--keep-backup]");
            Console.WriteLine("  --verify <原来> <现在> [--fast]   只比对两边一不一样，什么都不动");
            Console.WriteLine("  --undo <文件夹>      撤销某次搬运");
            Console.WriteLine("  --records            列出所有搬运记录");
            Console.WriteLine("  --check              检查所有传送门是否还通");
            Console.WriteLine("  --kb <路径>          查一个路径该怎么处理（退出码 0=能搬，1=不能搬）");
            Console.WriteLine("  --selftest           自检：知识库和搬运守卫说的是不是同一套话");
            Console.WriteLine("  --log                打印日志文件路径（把那个文件发给作者就能查问题）");
        }

        // 自检：知识库和搬运守卫必须说同样的话。
        // 它直接引用 Scanner.LockedNames，所以两边不可能各走各的 —— 这正是它比
        // 「在测试脚本里再抄一份名字列表」强的地方：抄的那份会悄悄过期，这份不会。
        static int CmdSelfTest()
        {
            int bad = 0;

            // 1) 守卫会拒的，知识库也必须说「不能搬」
            for (int i = 0; i < Scanner.LockedNames.Length; i++)
            {
                string probe = @"C:\" + Scanner.LockedNames[i];
                if (KB.Lookup(probe, @"C:\Users\x").Verdict == Verdict.SafeMove)
                {
                    Console.WriteLine("✗ 守卫会拒、报告却说能搬：" + probe);
                    bad++;
                }
            }

            // 2) 必须拒的具体路径 —— 每一条都是真踩过的坑
            string[] mustRefuse = new string[] {
                @"C:\Users\a\xwechat_files\wxid_b\msg",           // 名字叫 msg，但它爹是微信
                @"C:\Users\a\AppData\Roaming\Tencent",            // 路径结尾没有反斜杠
                @"C:\Users\a\AppData\Local\wsl\{g}\ext4.vhdx",    // WSL 虚拟磁盘
                @"C:\Users\a\proj\.venv\Lib\site-packages",       // 路径写死的虚拟环境
                @"C:\Windows\System32",
                @"C:\Program Files\Whatever",
                @"C:\Users\a\AppData\Local\Temp",                 // 只能删，不能搬
                // 内核级反作弊的游戏。原先 Riot 那条判的是「能搬」——
                // 而 Valorant 恰恰是这类里最凶的一个，判反了。
                @"C:\Games\VALORANT",
                @"C:\Games\PUBG",
                @"C:\我的游戏\原神",
                @"C:\Games\EasyAntiCheat"
            };
            for (int i = 0; i < mustRefuse.Length; i++)
                if (KB.Lookup(mustRefuse[i], @"C:\Users\a").Verdict == Verdict.SafeMove)
                {
                    Console.WriteLine("✗ 该拒的没拒：" + mustRefuse[i]);
                    bad++;
                }

            // 3) 反过来也得验：要是把什么都判成「不能搬」，上面会全绿，
            //    而这工具也就废了。正例必须一起钉住。
            string[] mustAllow = new string[] {
                @"C:\Users\a\Downloads\BigThing",
                @"C:\Games\SomeGame",
                @"C:\MyProjects"
            };
            for (int i = 0; i < mustAllow.Length; i++)
                if (KB.Lookup(mustAllow[i], @"C:\Users\a").Verdict != Verdict.SafeMove)
                {
                    Console.WriteLine("✗ 能搬的被拒了：" + mustAllow[i]);
                    bad++;
                }

            // 4) 大小写失配。Lookup 内部会把路径转小写，profileRoot 必须跟着转，
            //    否则 StartsWith 静默失配 —— 桌面/文档/图片/下载那几条规则全部失效，
            //    桌面就会落到兜底的「能搬」（桌面上有写死路径的 .venv，搬了当场炸）。
            //    ★ 这一块【必须】故意让两边大小写不一致，否则它测不出任何东西 ——
            //      两边都写 "C:\Users\a" 的话，这个 bug 原样躺在代码里也照样全绿。
            string profMixed = @"C:\Users\MixedCase";
            string[] mixedCase = new string[] {
                @"c:\users\mixedcase\desktop",
                @"c:\users\mixedcase\desktop\some-project",
                @"c:\users\mixedcase\documents",
                @"c:\users\mixedcase\pictures\2026"
            };
            for (int i = 0; i < mixedCase.Length; i++)
                if (KB.Lookup(mixedCase[i], profMixed).Verdict == Verdict.SafeMove)
                {
                    Console.WriteLine("✗ 大小写不一致时规则失配：" + mixedCase[i]);
                    bad++;
                }
            // 下载目录本身是允许搬的，但必须走「下载文件」那条规则（会提醒先自己看一眼）
            if (KB.Lookup(@"c:\users\mixedcase\downloads\big.zip", profMixed).Title != "下载文件")
            {
                Console.WriteLine("✗ 下载目录没走「下载文件」规则（大小写失配）");
                bad++;
            }

            Console.WriteLine("检查 " + (Scanner.LockedNames.Length + mustRefuse.Length + mustAllow.Length +
                mixedCase.Length + 1) + " 条判定：" + (bad == 0 ? "全部正确。" : ("有 " + bad + " 条不对。")));
            return bad == 0 ? 0 : 1;
        }

        // 知识库单点查询。判定这种东西是安全关键的，得有办法一条条钉在测试里 ——
        // 不靠「跑一遍扫描看着差不多」。退出码就是结论，方便脚本断言。
        static int CmdKb(string[] a)
        {
            string p = Arg(a, 1);
            if (p == null) { Help(); return 2; }
            Advice adv = KB.Lookup(p, Scanner.ProfileRoot());
            Console.WriteLine(p);
            Console.WriteLine("  判定    ：" + Report.VerdictLabel(adv.Verdict));
            Console.WriteLine("  这是什么：" + adv.Title);
            Console.WriteLine("  怎么办  ：" + adv.HowTo);
            if (!string.IsNullOrEmpty(adv.Warn)) Console.WriteLine("  " + adv.Warn);
            return adv.Verdict == Verdict.SafeMove ? 0 : 1;
        }

        // ---------------------------------------------------------
        static int CmdScan(string[] a)
        {
            string outHtml = Arg(a, 1);
            if (outHtml == null) { Help(); return 2; }
            long minGb = ParseLong(Opt(a, "--min-gb"), 2);
            int depth = (int)ParseLong(Opt(a, "--depth"), 5);
            string txt = Opt(a, "--txt");
            if (txt == null) txt = Path.ChangeExtension(outHtml, ".txt");

            DateTime t0 = DateTime.Now;
            ScanResult r = Scanner.Scan(depth, minGb * 1024L * 1024 * 1024, null);
            double sec = (DateTime.Now - t0).TotalSeconds;

            string htmlDir = Path.GetDirectoryName(Path.GetFullPath(outHtml));
            if (!string.IsNullOrEmpty(htmlDir) && !Directory.Exists(htmlDir)) Directory.CreateDirectory(htmlDir);
            File.WriteAllText(outHtml, Report.Html(r, IsAdmin()), new UTF8Encoding(false));
            File.WriteAllText(txt, Report.Text(r), new UTF8Encoding(true));

            Console.WriteLine(Report.Text(r));
            Console.WriteLine("耗时 " + sec.ToString("N1") + " 秒");
            Console.WriteLine("HTML 报告：" + Path.GetFullPath(outHtml));
            return 0;
        }

        static int CmdMigrate(string[] a)
        {
            string src = Arg(a, 1), dstRoot = Arg(a, 2);
            if (src == null || dstRoot == null) { Help(); return 2; }

            string leaf = new DirectoryInfo(Path.GetFullPath(src).TrimEnd('\\')).Name;
            string dst = Path.Combine(dstRoot, leaf);

            Migrator m = new Migrator();
            m.SrcPath = src;
            m.DstPath = dst;
            m.FullVerify = !Has(a, "--fast");
            m.DryRun = Has(a, "--dry");
            m.KeepBackup = Has(a, "--keep-backup");
            m.OnProgress = delegate(Progress p)
            {
                if (p.Percent >= 0) Console.WriteLine("  [" + p.Percent + "%] " + p.Phase + " " + p.Detail);
            };
            MigrateResult r = m.Run();
            Console.WriteLine();
            Console.WriteLine(r.Log);
            Console.WriteLine(r.Ok ? "结果：成功" : "结果：失败");
            Console.WriteLine(r.Message);

            // 命令行模式也要落盘。否则用命令行跑的时候，日志里只剩一条启动横幅，
            // 真正那次搬运的结果反而没有 —— 那就白记了。
            Logger.W("[搬运] " + src + "  ->  " + dst + (m.DryRun ? "  (试跑)" : ""));
            Logger.W("[搬运] 结果: " + (r.Ok ? "成功" : "失败  " + r.Message));
            if (r.Log != null && r.Log.Trim().Length > 0)
                Logger.W("--- 搬运过程 ---" + Environment.NewLine + r.Log);
            return r.Ok ? 0 : 1;
        }

        static int CmdVerify(string[] a)
        {
            string src = Arg(a, 1), dst = Arg(a, 2);
            if (src == null || dst == null) { Help(); return 2; }
            DateTime t0 = DateTime.Now;
            string diff = Migrator.Verify(src, dst, !Has(a, "--fast"));
            double sec = (DateTime.Now - t0).TotalSeconds;
            if (diff == null)
            {
                Console.WriteLine("两边完全一致（" + sec.ToString("N1") + " 秒）。");
                return 0;
            }
            Console.WriteLine("对不上（" + sec.ToString("N1") + " 秒）：");
            Console.WriteLine(diff);
            return 1;
        }

        static int CmdUndo(string[] a)
        {
            string src = Arg(a, 1);
            if (src == null) { Help(); return 2; }
            string want = Path.GetFullPath(src).TrimEnd('\\');
            List<Migrator.Record> rs = Migrator.LoadRecords();
            Migrator.Record hit = null;
            for (int i = 0; i < rs.Count; i++)
                if (string.Equals(rs[i].Src.TrimEnd('\\'), want, StringComparison.OrdinalIgnoreCase)) hit = rs[i];
            if (hit == null) { Console.WriteLine("找不到 " + want + " 的搬运记录。"); return 1; }
            MigrateResult r = Migrator.Undo(hit, null);
            Console.WriteLine(r.Log);
            Console.WriteLine(r.Message);
            Logger.W("[撤销] " + want + "  ->  " + hit.Dst);
            Logger.W("[撤销] 结果: " + (r.Ok ? "成功" : "失败  " + r.Message));
            if (r.Log != null && r.Log.Trim().Length > 0)
                Logger.W("--- 撤销过程 ---" + Environment.NewLine + r.Log);
            return r.Ok ? 0 : 1;
        }

        static int CmdRecords()
        {
            List<Migrator.Record> rs = Migrator.LoadRecords();
            if (rs.Count == 0) { Console.WriteLine("还没有搬运记录。"); return 0; }
            for (int i = 0; i < rs.Count; i++)
            {
                Migrator.Record r = rs[i];
                bool linkOk = Junction.IsReparsePoint(r.Src);
                string t = linkOk ? Junction.ReadTarget(r.Src) : null;
                bool points = linkOk && t != null &&
                    string.Equals(t.TrimEnd('\\'), r.Dst.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                Console.WriteLine("─────────────────────────────────────────");
                Console.WriteLine("  原位置 : " + r.Src);
                Console.WriteLine("  现位置 : " + r.Dst + (Directory.Exists(r.Dst) ? "  [在]" : "  [★找不到了]"));
                Console.WriteLine("  大小   : " + Scanner.Fmt(r.Bytes) + "   时间：" + r.When);
                Console.WriteLine("  传送门 : " + (points ? "正常" : (linkOk ? "指向不对：" + t : "不存在")));
            }
            return 0;
        }

        static int CmdCheck()
        {
            List<Migrator.Record> rs = Migrator.LoadRecords();
            int bad = 0;
            for (int i = 0; i < rs.Count; i++)
            {
                Migrator.Record r = rs[i];
                string t = Junction.IsReparsePoint(r.Src) ? Junction.ReadTarget(r.Src) : null;
                bool ok = t != null && Directory.Exists(r.Dst) &&
                    string.Equals(t.TrimEnd('\\'), r.Dst.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                Console.WriteLine((ok ? "✓ " : "✗ ") + r.Src);
                if (!ok)
                {
                    bad++;
                    Console.WriteLine("    期望指向 " + r.Dst + "，实际 " + (t == null ? "(不是链接)" : t));
                }
            }
            Console.WriteLine(bad == 0 ? "全部正常。" : ("有 " + bad + " 个有问题。"));
            return bad == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------
        public static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p =
                    new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static string DesktopDir()
        {
            string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(d)) d = Path.Combine(Scanner.ProfileRoot(), "Desktop");
            return d;
        }

        static string Arg(string[] a, int i) { return i < a.Length && !a[i].StartsWith("--") ? a[i] : null; }
        static bool Has(string[] a, string k)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] == k) return true;
            return false;
        }
        static string Opt(string[] a, string k)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == k) return a[i + 1];
            return null;
        }
        static long ParseLong(string s, long dflt)
        {
            long v; if (s != null && long.TryParse(s, out v)) return v; return dflt;
        }
    }
}
