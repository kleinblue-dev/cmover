using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CMover
{
    // 判定：这个东西能不能用本工具搬
    public enum Verdict
    {
        SafeMove,     // 可以用本工具搬（会建 junction 保住原路径）
        MustUseApp,   // 必须用它自己的设置搬，手搬必坏
        DeleteOnly,   // 能删，不必搬
        Never         // 碰都不要碰
    }

    public class Advice
    {
        public string Title = "";
        public Verdict Verdict = Verdict.Never;
        public string Category = "其他";
        public string HowTo = "";
        public string Warn = "";
        // true = 这条是「谁都没认领，按位置兜底」得出的，不是针对这个东西本身。
        // 只有兜底结论才会被「目录群」覆盖 —— 具体规则（Windows/微信/Program Files…）
        // 说得比「目录群」清楚，不该被顶掉。见 Scanner.Scan 里对 mixed 的处理。
        public bool Generic = false;
    }

    public class Item
    {
        public string Path;
        public long Size;
        public Advice Advice;
        public bool IsLeaf = true;     // 图表只画 leaf，避免父子重复计数
        public bool ParentEmitted;     // 是否有祖先已经被列出
    }

    // ---------------------------------------------------------------
    // 知识库：哪些能搬、哪些必须用软件自己改、哪些不能碰
    // 这是本工具真正的价值所在 —— 规则是固定的，不是猜的
    // ---------------------------------------------------------------
    public static class KB
    {
        // 目录名（整段）命中就算「能删的缓存」
        static readonly string[] DeleteCacheDirs = new string[] {
            "temp", "d3dscache", "nv_cache", "amd", "dxcache", "pip",
            "npm-cache", "_cacache", ".cache", "crashpad", "inetcache", "softwaredistribution"
        };

        // ★ 路径判据一律用「整段匹配」，绝不用名字匹配或裸 Contains。
        //
        // 踩过的坑：微信那条原来写的是 目录名字.Contains("xwechat")，
        // 于是 C:\Users\me\xwechat_files\wxid_xxx\msg —— 这个目录名字叫 "msg" ——
        // 落到了兜底的「大文件夹 / 能搬」，7.11 GB 聊天记录被标成可以搬走。
        // 名字匹配还有个反向的坑：p.Contains(@"\tencent\") 要求结尾带反斜杠，
        // 而路径以 "\Tencent" 结尾，于是 Roaming\Tencent 也没命中。
        // 段匹配一次解决两边：祖先命中，整棵子树都跟着。
        static bool HasSeg(string lowerPath, string seg)
        {
            if (lowerPath.Length < seg.Length) return false;
            int i = 0;
            while (true)
            {
                int k = lowerPath.IndexOf(seg, i, StringComparison.Ordinal);
                if (k < 0) return false;
                int end = k + seg.Length;
                bool leftOk = (k == 0) || (lowerPath[k - 1] == '\\');
                bool rightOk = (end == lowerPath.Length) || (lowerPath[end] == '\\');
                if (leftOk && rightOk) return true;
                i = k + 1;
            }
        }

        // ★ 带内核级反作弊的游戏 / 反作弊驱动本身的名字（小写，按整段匹配）。
        //
        // 为什么要单独列一张表：这类游戏的驱动（Vanguard、ACE、BattlEye、
        // EasyAntiCheat）会盯着游戏目录。搬走之后原位置变成一个目录联接，
        // 在它们眼里有可能就算「游戏文件被改动」。
        //
        // ★ 这条风险我【没有验证过】—— 总不能拿个真账号去试会不会封。但代价
        //   不对称：赌输的代价是账号没了，赌赢的收益只是省几十 GB。所以按
        //   最坏情况处理，而不是按「大概率没事」处理。
        //
        // ★ 名字黑名单天生是漏的：游戏装在 C:\我的游戏\aaa\ 这种地方就认不出来。
        //   它是「尽力而为」的一层，不是保证 —— 所以配套的提示语必须把【正确做法】
        //   说清楚（用启动器自带的移动功能），而不是只丢一句「不许搬」。
        public static readonly string[] AntiCheatNames = new string[] {
            "valorant", "riot", "riot games", "vanguard", "无畏契约",
            "pubg", "绝地求生",
            "genshin impact", "genshinimpact", "原神", "yuan shen",
            "honkai", "崩坏", "star rail", "星穹铁道", "zenless", "绝区零",
            "ace-base", "ace-game", "anticheatexpert",
            "easyanticheat", "easy anti-cheat", "battleye", "gameguard",
            "nprotect", "xigncode", "腾讯游戏", "穿越火线", "三角洲行动",
            // 这三个是评审别人的方案时补的 —— 我原来的名单漏了。
            // Roblox 用的是 Hyperion（Byfron），内核级；Fortnite 是 EAC+BattlEye；
            // Battlegrounds 是 PUBG 的目录名（PUBG 自己的名字我已经有了）。
            "roblox", "fortnite", "battlegrounds"
        };

        public static Advice Lookup(string fullPath, string profileRoot)
        {
            string p = fullPath.ToLowerInvariant();
            // ★ profileRoot 必须跟着一起转小写。
            //   上面 p 已经 ToLower 了，而 Scanner.ProfileRoot() 返回的是 "C:\Users\me" ——
            //   大小写混着的。StartsWith 是大小写敏感的，于是下面「桌面/文档/图片/下载」
            //   那几条在真实扫描里全部静默失配：桌面落到了兜底的「能搬」。
            //   而桌面下有活的 .venv / node_modules（路径写死绝对路径），搬了当场炸。
            //   教训：测试里 profileRoot 和路径两边大小写一致，就【测不出】这个 bug。
            string prof = profileRoot == null ? "" : profileRoot.ToLowerInvariant();
            string name = Path.GetFileName(fullPath);
            string nl = name.ToLowerInvariant();

            // 0) 跟搬运守卫用【同一个真源】：凡是 Scanner.LockedNames 里有的名字，
            //    报告里也必须说「不能碰」。
            //    否则会出现「报告写着能搬，用户点了却被守卫拒掉」—— 那是这工具说话不算话。
            //    krscycle / AMDAcp_v6.0.0.95_id_0 就属于这种：守卫里一直有，
            //    但知识库没有对应规则，于是落到了兜底的「能搬」。
            for (int i = 0; i < Scanner.LockedNames.Length; i++)
                if (nl == Scanner.LockedNames[i])
                    return Make("系统目录", Verdict.Never, "系统",
                        "这是 Windows 自己的目录，不要动。", "★★ 动了系统会坏，而且修不回来。");

            // 1) 系统
            if (p == @"c:\windows" || p.StartsWith(@"c:\windows\") ||
                p == @"c:\programdata" || p.StartsWith(@"c:\programdata\") ||
                p.StartsWith(@"c:\$recycle.bin") || p.StartsWith(@"c:\system volume information") ||
                p.StartsWith(@"c:\recovery") || p.StartsWith(@"c:\perflogs") ||
                p.StartsWith(@"c:\msocache") || p.StartsWith(@"c:\inetpub") ||
                p.StartsWith(@"c:\onedrivetemp") || p.StartsWith(@"c:\$winreagent") ||
                // 公共用户目录。不能靠把 "public" 加进 LockedNames 解决 ——
                // 那是个很常见的文件夹名（前端项目的 public 就叫这个）。
                p == @"c:\users\public" || p.StartsWith(@"c:\users\public\"))
                return Make("系统目录", Verdict.Never, "系统",
                    "这是 Windows 自己的目录，不要动。", "★★ 动了系统会坏，且修不回来。");

            // 2) Program Files
            if (p.StartsWith(@"c:\program files\") || p == @"c:\program files" ||
                p.StartsWith(@"c:\program files (x86)\") || p == @"c:\program files (x86)")
                return Make("程序目录", Verdict.Never, "程序",
                    "程序装在这里时，注册表和快捷方式里记的是这个绝对路径。手搬会让程序找不到自己。",
                    "★ 不要用本工具搬。正确做法是卸载后重装到别的盘。");

            // 2b) 用户级安装的程序（VS Code 这类装到 AppData 里的）
            if (p.Contains(@"\appdata\local\programs"))
                return Make("用户级程序目录", Verdict.Never, "程序",
                    "这是装到用户目录里的程序（VS Code 这类）。它的快捷方式和配置里记的是这个绝对路径。",
                    "★ 正确做法是卸载后重装到别的盘，不要手搬。");

            // 2c) 商店应用（UWP）的数据根目录。
            //     单列一条是因为它的答案跟别的 AppData 目录不一样 ——
            //     商店应用没有「改保存位置」的开关，只能卸载或不管。
            //     而且这里搬起来【必坏】：走 robocopy /COPY:DAT，不带权限，
            //     而 Packages 下每个应用子目录都挂着它自己的 AppContainer SID 权限；
            //     复制到 D 盘后 ACL 变成 D 盘继承来的那套，所有商店应用当场读不到自己的数据。
            if (p.Contains(@"\appdata\local\packages"))
                return Make("商店应用数据", Verdict.MustUseApp, "程序",
                    "这是 Windows 商店应用（UWP）自己的数据，它没有「改保存位置」的设置。要腾地方就到「设置 → 应用 → 已安装的应用」里卸载不用的那些；「设置 → 系统 → 存储」里也能看谁占得多。",
                    "★★ 不要搬。这些文件夹的权限是按应用单独设的，复制到别的盘会把权限丢掉，商店应用就都打不开自己的数据了。");

            // 3) 开发环境（路径写死）
            if (HasSeg(p, ".venv") || HasSeg(p, "venv") || HasSeg(p, "env") || HasSeg(p, "node_modules") ||
                HasSeg(p, ".conda") || HasSeg(p, "site-packages"))
                return Make(HasSeg(p, "node_modules") ? "Node 依赖" : "Python 虚拟环境",
                    Verdict.Never, "开发",
                    "这个文件夹里的路径是写死的绝对路径，搬走后它记录的还是老位置。",
                    "★ 实测过：搬了当场坏。正确做法是删掉重建。");

            // 4) WSL 虚拟机磁盘
            if (p.Contains(@"\appdata\local\wsl") || HasSeg(p, "ext4.vhdx") ||
                p.Contains(@"\appdata\local\packages\") && p.Contains("linux"))
                return Make("Linux 子系统磁盘", Verdict.MustUseApp, "虚拟机",
                    "这是一个 Linux 虚拟机的大文件，必须用 wsl 自己的命令导出再导入。",
                    "★★ 用本工具搬会让你的 Linux 环境彻底起不来。");

            // 5) 能删的缓存
            for (int i = 0; i < DeleteCacheDirs.Length; i++)
                if (HasSeg(p, DeleteCacheDirs[i]))
                    return CacheAdvice(name, p);

            // 6) 聊天记录
            if (HasSeg(p, "xwechat_files") || HasSeg(p, "wechat") || HasSeg(p, "tencent") ||
                HasSeg(p, "qq") || p.Contains(@"\documents\tencent files"))
                return Make("微信/QQ 聊天记录", Verdict.MustUseApp, "聊天",
                    "在微信里改：设置 → 文件管理 → 更改文件保存位置。它会自己把历史文件搬过去。",
                    "★ 手搬会导致聊天记录里的图片和文件点不开。");

            // 7) Steam
            if (HasSeg(p, "steamapps") || HasSeg(p, "steamlibrary"))
                return Make("Steam 游戏", Verdict.MustUseApp, "游戏",
                    "在 Steam 里改：设置 → 存储 → 添加一个别的盘 → 选中游戏 → 移动。",
                    "★ 手搬会被 Steam 判定为文件丢失，然后重新下载一遍。");

            // 8) 其他启动器
            if (HasSeg(p, "epic games") || HasSeg(p, "epicgames"))
                return Make("Epic 游戏", Verdict.MustUseApp, "游戏",
                    "在 Epic 客户端里改：设置 → 找到该游戏的「移动」按钮。", "");
            // 8b) ★ 反作弊游戏 —— 【故意】不让勾。
            //   原先这里是一条「Riot 游戏 → 能搬」，那是错的：Valorant 正是
            //   内核级反作弊（Vanguard）里最凶的一个，偏偏被判成最安全的一档。
            for (int i = 0; i < AntiCheatNames.Length; i++)
                if (HasSeg(p, AntiCheatNames[i]))
                    return Make("带反作弊的游戏", Verdict.MustUseApp, "游戏",
                        "这类游戏要用它自己的启动器搬：Steam 是「设置 → 存储 → 移动」，" +
                        "Riot 客户端和 WeGame 里也各有一个「移动安装位置」。找不到就先别搬 —— " +
                        "C 盘挤一点，总比账号没了强。",
                        "★★ 不要用本工具搬。这类游戏装着内核级反作弊驱动，会检查游戏目录；" +
                        "原位置变成传送门之后，有可能被判定成文件被改动。" +
                        "这个风险我没法验证（不会拿真账号去试），但赌输的代价是封号，不值。");

            // 9) 办公 / 浏览器 / 工具
            if (HasSeg(p, "kingsoft") || HasSeg(p, "wps") || HasSeg(p, "wps office"))
                return Make("WPS / 办公软件", Verdict.MustUseApp, "办公",
                    "在 WPS 里改：备份中心 → 清理或修改备份位置。", "");
            if (HasSeg(p, "google") || HasSeg(p, "microsoft") || HasSeg(p, "microsoftedge") ||
                HasSeg(p, "edge"))
                return Make("浏览器缓存", Verdict.MustUseApp, "浏览器",
                    "到浏览器里清：设置 → 隐私 → 清除浏览数据。",
                    "★ 别去文件夹里手删 —— 那里面同时装着你的书签和保存的密码。");
            if (HasSeg(p, "githubdesktop"))
                return Make("GitHub Desktop", Verdict.MustUseApp, "开发",
                    "在 GitHub Desktop 里改：File → Options → Git → 修改仓库根目录。", "");

            // 9b) npm 的全局工具目录。它在 AppData 里，但属于【真能搬】的那一类，
            //     所以必须放在 10b 那条 AppData 兜底【前面】，否则会被一起拒掉。
            //     npm 是用 %APPDATA% 现算这个路径的，不写死在哪儿，
            //     搬完 junction 一通，命令行里装过的工具照常跑。实测这台机器 1.41 GB。
            if (p.EndsWith(@"\appdata\roaming\npm"))
                return Make("npm 全局工具目录", Verdict.SafeMove, "开发",
                    "可以用本工具搬。npm 是按 %APPDATA% 现算这个路径的，不写死，搬完老路径照样能用。",
                    "⚠ 搬之前先把正在跑的命令行窗口都关掉。");

            // 10) 个人目录（注意用的是小写后的 prof，不是原始的 profileRoot）
            if (p.StartsWith(prof + @"\downloads"))
                return Make("下载文件", Verdict.SafeMove, "下载",
                    "可以用本工具搬。建议先自己打开看一眼，确认没有还想留着的东西。",
                    "⚠ 搬之前先自己看一眼，别把还要用的东西搬走了。");
            if (p.StartsWith(prof + @"\desktop") || p.StartsWith(prof + @"\documents") ||
                p.StartsWith(prof + @"\pictures") || p.StartsWith(prof + @"\videos") ||
                p.StartsWith(prof + @"\music"))
                return Make("个人文件", Verdict.Never, "个人",
                    "这是你自己的文件，本工具默认不碰。", "★★ 默认锁死，防止误操作。");

            // 10b) AppData 下面没被上面任何一条规则认领的东西 —— 一律不给「一键搬」。
            //
            //      为什么单列这一档：把门槛调低之后（Scanner.ActionFloorBytes），
            //      AppData 里一堆几百 MB 的目录冒了出来，全都落到了下面那条兜底
            //      「大文件夹 / 能搬」。实测一次就冒出 6 个这种目录 —— 编辑器、网盘、
            //      游戏启动器、某个桌面助手的自动更新器 —— 目录名根本看不出是干什么的，
            //      但它们全都是程序自己的状态目录。
            //      「能拿回来」从 5.56 GB 涨到 14.22 GB，涨的这部分全是不能碰的。
            //
            //      这里的东西路径是程序运行时用 %APPDATA% / %LOCALAPPDATA% 现算的，
            //      所以文件系统这一层 junction 确实通（npm 就是这么能搬的，它上面单有一条）。
            //      但两个真风险：(a) 复制走 robocopy /COPY:DAT，不带权限；
            //      (b) 认不出来就说明不知道该不该搬 —— 那就不搬，让它自己在软件里清。
            if (p.Contains(@"\appdata\local\") || p.Contains(@"\appdata\roaming\") ||
                p.EndsWith(@"\appdata\local") || p.EndsWith(@"\appdata\roaming") || p.EndsWith(@"\appdata"))
                return Make("程序数据目录", Verdict.MustUseApp, "程序",
                    "这是某个程序自己存数据的地方。先打开那个程序的设置，找「缓存」「保存位置」「清理」这类选项，在它自己里面处理。",
                    "★ 本工具不搬 AppData 里认不出来的目录：复制带不过去权限，而且程序可能记着自己的路径。硬搬可能让那个软件丢配置。",
                    true);

            // 11) 大文件夹——没有专门规则但很大
            return Make("大文件夹", Verdict.SafeMove, "其他",
                "可以用本工具搬。搬完老路径依然可用。",
                "⚠ 如果里面有程序，搬之前先确认它是绿色版（免安装）的。装了注册表的程序请重装。",
                true);
        }

        static Advice CacheAdvice(string name, string p)
        {
            string title = "临时文件";
            string how = "直接删。系统和软件会自己重建。";
            string warn = "";
            if (p.Contains("d3dscache") || p.Contains("dxcache")) { title = "显卡驱动缓存"; how = "直接删。显卡驱动会自己重建，只是下次进某个游戏第一次会慢一点。"; }
            else if (p.Contains("pip") || p.Contains("npm") || p.Contains("_cacache")) { title = "开发包缓存"; how = "直接删。下次安装包时会重新下载。"; }
            else if (p.Contains("inetcache")) { title = "浏览器缓存"; how = "★ 到浏览器里清，别手删。"; warn = "★ 手删会丢书签和密码。"; }
            return Make(title, Verdict.DeleteOnly, "缓存", how, warn);
        }

        static Advice Make(string title, Verdict v, string cat, string how, string warn)
        {
            return Make(title, v, cat, how, warn, false);
        }

        // generic=true 表示「这是兜底结论」，可以被「目录群」覆盖。见 Advice.Generic。
        static Advice Make(string title, Verdict v, string cat, string how, string warn, bool generic)
        {
            Advice a = new Advice();
            a.Title = title; a.Verdict = v; a.Category = cat; a.HowTo = how; a.Warn = warn;
            a.Generic = generic;
            return a;
        }
    }

    // ---------------------------------------------------------------
    // 扫描
    // ---------------------------------------------------------------
    public class ScanResult
    {
        public List<Item> Items = new List<Item>();        // 「该怎么办」用：只放叶子，不重复计数
        public List<Item> TopItems = new List<Item>();     // 「哪儿占地方」用：C 盘每个顶层目录各一行
        public long TopFilesBytes;                         // C 盘根目录下的文件（hiberfil.sys / pagefile.sys 等）
        public long TotalC;
        public long FreeC;
        public long UsedC;
        public long ScannedBytes;
        public int SkippedJunctions;
        public int DirCount;
    }

    public static class Scanner
    {
        public static readonly string[] LockedNames = new string[] {
            "windows", "programdata", "$recycle.bin", "system volume information",
            "recovery", "perflogs", "msocache", "inetpub", "onedrivetemp",
            // ★ 这台机器上真实存在的是 KRECYCLE（金山毒霸的回收站）。
            //   原来这一格只写了 krscycle —— 多一个 s，一个永远匹配不上的拼错名字；
            //   而真正存在的那个反而不在名单里，被兜底判成了「能搬」。
            //   两个都留着：多一条匹配不上的死规则没有风险，漏掉一个才危险。
            "krecycle", "krscycle",
            "documents and settings", "$winreagent", "amdacp_v6.0.0.95_id_0",
            // ★ users 是这里最要命的一个。原先没有它，于是：
            //   --migrate C:\Users  一路通过（名字不在守卫里、也不是个人目录、
            //   知识库按兜底判成「大文件夹/能搬」），然后就要 Directory.Move 整个
            //   C:\Users。56 GB、装着所有用户的一切。报告里的占比表也会理直气壮地
            //   在 C:\Users 后面印一个「能搬」。
            //   代价：任何叫 users 的文件夹都会被拒。宁可误拒，不可误搬。
            "users"
        };

        // ★★ 上面这个 LockedNames 是【搬运守卫】，一个都不能删、不能放松。
        // 下面这个是【扫描时不去数它】，跟守卫是两码事。
        // 分家的原因：windows / programdata 不能让本工具搬，但报告里必须出现 ——
        // 否则「已用 142 GB」下面只列出 20 GB，账对不上，用户会怀疑整份报告。
        static readonly string[] ScanSkipNames = new string[] {
            "documents and settings",       // 兼容 junction，指回 C:\Users
            "system volume information"     // 权限必然被拒，扫了也只是报错
        };

        // ★ 门槛【分两档】—— 只有本工具动不了的东西才吃用户给的 min-gb。
        //
        // 踩过的坑：两档共用一个门槛时，这台机器上 2 GB 门槛下唯一能搬的两样
        //   AppData\Roaming\npm（1.41 GB）和 .vscode\...\vscode-remote-wsl（1.66 GB）
        //   全被挡在报告外 —— 界面上一行可勾选的都没有，工具看着就是坏的。
        //   同样是「被门槛挡掉」：不能碰的多列几行只是噪音，能搬的少列一行是功能没了。
        public const long ActionFloorBytes = 256L * 1024 * 1024;

        // 「用户能拿它做点什么」——能搬或能删。只用来决定门槛高低，不参与最终判定。
        static bool IsActionable(string path)
        {
            Verdict v = KB.Lookup(path, ProfileRoot()).Verdict;
            return v == Verdict.SafeMove || v == Verdict.DeleteOnly;
        }

        public static bool IsScanSkip(string name)
        {
            string n = name.ToLowerInvariant();
            for (int i = 0; i < ScanSkipNames.Length; i++) if (n == ScanSkipNames[i]) return true;
            return false;
        }

        public static bool IsLockedName(string name)
        {
            string n = name.ToLowerInvariant();
            for (int i = 0; i < LockedNames.Length; i++) if (n == LockedNames[i]) return true;
            return false;
        }

        public static string ProfileRoot()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        // 个人目录默认锁死（桌面/文档/图片/视频/音乐）
        static readonly string[] LockedProfileDirs = new string[] {
            "desktop", "documents", "pictures", "videos", "music",
            "favorites", "links", "contacts", "searches", "saved games", "onedrive", "3d objects"
        };

        public static bool IsLockedProfileDir(string dirPath)
        {
            string prof = ProfileRoot();
            if (!dirPath.StartsWith(prof + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            string rest = dirPath.Substring(prof.Length + 1);
            if (rest.IndexOf('\\') >= 0) return false;
            string n = rest.ToLowerInvariant();
            for (int i = 0; i < LockedProfileDirs.Length; i++) if (n == LockedProfileDirs[i]) return true;
            return false;
        }

        // onProgress 每扫过几千个目录回一次（实测整盘约 17 万个目录、80 多秒）。
        // 界面必须在这 80 秒里说话 —— 不然小白以为卡死了，直接强杀进程。
        public static ScanResult Scan(int maxDepth, long floorBytes, Action<long, string> onProgress)
        {
            ScanResult r = new ScanResult();
            Dictionary<string, long> sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            string root = "C:\\";
            long total = 0;

            DirectoryInfo rootDi = new DirectoryInfo(root);
            DirectoryInfo[] topDirs = null;
            try { topDirs = rootDi.GetDirectories(); } catch { }

            if (topDirs != null)
            {
                for (int i = 0; i < topDirs.Length; i++)
                {
                    DirectoryInfo d = topDirs[i];
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) { r.SkippedJunctions++; continue; }
                    if (IsScanSkip(d.Name)) { continue; }
                    // ★ 这里【不】跳过桌面/文档那类个人目录。
                    //   要跳过的只有「搬运守卫」——这里是扫描，只读大小。
                    //   跳过它们会让 C:\Users 少算（实测桌面就有 1.60 GB），
                    //   于是报告里的数跟「已用」对不上账，用户会怀疑整份报告。
                    //   安全由知识库兜着：那些路径一律判「不能碰」，列出来也不会被搬。
                    long s = Walk(d.FullName, 1, sizes, parents, depths, r, onProgress);
                    sizes[d.FullName] = s;
                    depths[d.FullName] = 1;
                    total += s;

                    // 顶层全景：不管能不能碰都列出来，这样报告里的数字才能跟「已用」对上账
                    if (s > 0)      // 0 字节的空壳（杀软回收站、管家下载目录之类）列了只是噪音
                    {
                        Item t = new Item();
                        t.Path = d.FullName;
                        t.Size = s;
                        t.IsLeaf = false;
                        t.Advice = TopAdvice(d.FullName);
                        r.TopItems.Add(t);
                    }
                }
                // C 盘根目录下的文件（hiberfil.sys / pagefile.sys 等）
                try { foreach (FileInfo f in rootDi.GetFiles()) { try { r.TopFilesBytes += f.Length; } catch { } } } catch { }
                total += r.TopFilesBytes;
            }

            r.ScannedBytes = total;

            // 把 C 盘总量也算出来
            try
            {
                DriveInfo ci = new DriveInfo("C");
                r.TotalC = ci.TotalSize; r.FreeC = ci.AvailableFreeSpace; r.UsedC = ci.TotalSize - ci.AvailableFreeSpace;
            }
            catch { }

            r.TopItems.Sort(delegate(Item a, Item b) { return b.Size.CompareTo(a.Size); });

            // ---- 选出要列出的目录 ----
            // 深度【只】在这里起作用：决定哪些目录单独列一行。
            // 大小是在 Walk 里全量算出来的，跟这个无关。
            List<string> candidates = new List<string>();
            long lowFloor = Math.Min(floorBytes, ActionFloorBytes);
            foreach (KeyValuePair<string, long> kv in sizes)
            {
                // 顺序要紧：sizes 里有十几万条，KB.Lookup 相对贵得多。
                // 先用最便宜的门槛筛掉绝大多数，只对落在这两档之间的一小撮做判定。
                if (kv.Value < lowFloor) continue;
                int d; depths.TryGetValue(kv.Key, out d);
                if (d > maxDepth) continue;
                if (kv.Value < floorBytes && !IsActionable(kv.Key)) continue;
                candidates.Add(kv.Key);
            }
            candidates.Sort(delegate(string a, string b) { return sizes[b].CompareTo(sizes[a]); });

            // ★ 一个目录只要【里面有需要区别对待的子目录】，就不许整体搬走。
            //   例：AppData\Local 大部分是能删的缓存，但里面有 17 GB 是 WSL 的 ext4.vhdx，
            //   整体搬走 = 你的 Linux 环境当场没了。光看父目录自己的判定发现不了这件事。
            //   处理顺序：按路径长度从长到短 —— 父的路径一定是子的前缀，所以父一定更短，
            //   这样处理到父的时候，子目录都已经有结论了。
            Dictionary<string, bool> mixed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            List<string> byLen = new List<string>(candidates);
            byLen.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
            for (int i = 0; i < byLen.Count; i++)
            {
                string c = byLen[i];
                for (int j = 0; j < byLen.Count; j++)
                {
                    string c2 = byLen[j];
                    if (c2.Length <= c.Length) continue;
                    if (!c2.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (mixed.ContainsKey(c2)) { mixed[c] = true; break; }
                    Verdict cv = KB.Lookup(c2, ProfileRoot()).Verdict;
                    if (cv == Verdict.MustUseApp || cv == Verdict.Never) { mixed[c] = true; break; }
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string c = candidates[i];

                // 有没有候选子目录 —— 决定「进不进能拿回来的合计」（防止父子重复计数）。
                // 同时找最大的那个子目录：一个目录如果几乎只是包着它，那单列一行
                // 就是纯噪音 —— C:\Users 56.41 GB 和 C:\Users\me 56.41 GB
                // 说的是同一件事，用户读完两遍还是同一个数。
                // 去掉它不丢信息：顶层全景那一节本来就列着 C:\Users。
                bool hasChild = false;
                long maxChild = 0;
                for (int j = 0; j < candidates.Count; j++)
                {
                    string c2 = candidates[j];
                    if (c2.Length <= c.Length) continue;
                    if (!c2.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    hasChild = true;
                    if (sizes[c2] > maxChild) maxChild = sizes[c2];
                }
                if (hasChild && maxChild > 0 && sizes[c] - maxChild <= sizes[c] / 25) continue;  // 差不到 4%

                Advice own = KB.Lookup(c, ProfileRoot());
                // 会被「目录群」顶掉的两种情况：
                //   ① 自己判能搬/能删（能搬，但里面混着不能搬的 → 整体不能搬）
                //   ② 自己那条本来就是兜底结论（AppData 底下的、认不出来的大文件夹）
                //      这种情况的「程序数据目录 / 大文件夹」说得太笼统，
                //      「目录群（得分开处理）」才是有用的那句话。
                // 反过来，Windows / 微信 / wsl 这些具体规则说得更清楚，不该被顶掉。
                bool ownGeneric = own.Generic ||
                    own.Verdict == Verdict.SafeMove || own.Verdict == Verdict.DeleteOnly;

                Item it = new Item();
                it.Path = c;
                it.Size = sizes[c];
                it.IsLeaf = !hasChild;
                // 只有「自己说能搬、但里面有不能搬的」才降级成「目录群」。
                // C:\Windows 这种自己就写着「不能碰」的，用它自己的说法更清楚。
                it.Advice = (ownGeneric && mixed.ContainsKey(c)) ? GroupAdvice() : own;
                r.Items.Add(it);
            }

            r.Items.Sort(delegate(Item a, Item b) { return b.Size.CompareTo(a.Size); });
            return r;
        }

        // 一个目录只要【里面有需要区别对待的子目录】，整体搬走就必然是错的。
        // 例：AppData\Local 大部分是能删的缓存，但其中 17 GB 是 WSL 的 ext4.vhdx，
        // 那东西一搬你的 Linux 环境就起不来了。所以这种「目录群」一律标成不能碰。
        static Advice GroupAdvice()
        {
            Advice a = new Advice();
            a.Title = "目录群（得分开处理）";
            a.Verdict = Verdict.Never;
            a.Category = "混合";
            a.HowTo = "这里面每一块的处理方式都不一样 —— 有的是能删的缓存，有的是必须用它自己的设置搬的。别整个搬走，照下面列出来的每一项分开做。";
            a.Warn = "★★ 整体搬走一定会出事：这里面混着必须留在原地的东西。";
            return a;
        }

        // 顶层目录：能碰的照知识库判，系统自己的直接讲清楚为什么不能碰
        static Advice TopAdvice(string fullPath)
        {
            string n = Path.GetFileName(fullPath).ToLowerInvariant();
            if (n == "windows" || n == "programdata")
            {
                Advice a = new Advice();
                a.Title = "Windows 系统";
                a.Verdict = Verdict.Never;
                a.Category = "系统";
                a.HowTo = "这是 Windows 自己的东西，动不了。想让它变小只能靠「磁盘清理」和卸载没用的更新。";
                return a;
            }
            // C:\Users 得单独说。它在 LockedNames 里（守卫会拒），
            // 但知识库对它只会给出「系统目录 / 这是 Windows 自己的目录」——
            // 这句话对着 C:\Users 说是不对的，用户看着会莫名其妙。
            if (n == "users")
            {
                Advice a = new Advice();
                a.Title = "所有用户的个人文件夹";
                a.Verdict = Verdict.Never;
                a.Category = "个人";
                a.HowTo = "这是所有用户的个人文件。里面的桌面、文档、图片本工具默认就不碰，整个搬走更是不行。";
                a.Warn = "★★ 这个位置动不得。";
                return a;
            }
            return KB.Lookup(fullPath, ProfileRoot());
        }

        static string ParentOf(Dictionary<string, string> parents, string p)
        {
            string v;
            if (parents.TryGetValue(p, out v)) return v;
            return null;
        }

        // ★★★ 这个函数【故意没有】maxDepth 参数 —— 别再给它加回去。
        //
        // 曾经写成 if (depth < maxDepth) 才往下递归。后果：
        //   depth=5 的目录只数自己的直接文件、不数子目录，它的父目录跟着一起少算，
        //   一路传染到 C:\Users —— 报告印出「C:\Users 3.51 GB」，
        //   而实测真值 34 GB 以上，差一个数量级。
        //   而且看不出来：数字精确到小数点后两位，用户会照着做决定。
        //   （实测对照：AppData\Local\wsl 真实 17.25 GB，那版扫描器里它压根不存在。）
        //
        // 划清界限：尺寸永远递归到底，只跳过重解析点；
        // 深度只用来决定「哪些目录单独列一行」，跟大小的计算完全无关。
        static long Walk(string dir, int depth, Dictionary<string, long> sizes,
                         Dictionary<string, string> parents, Dictionary<string, int> depths, ScanResult r,
                         Action<long, string> onProgress)
        {
            long total = 0;
            DirectoryInfo di;
            try { di = new DirectoryInfo(dir); } catch { return 0; }
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0) { r.SkippedJunctions++; return 0; }

            r.DirCount++;
            if (onProgress != null && (r.DirCount % 3000) == 0) onProgress(r.DirCount, dir);
            try { foreach (FileInfo f in di.GetFiles()) { try { total += f.Length; } catch { } } } catch { }

            DirectoryInfo[] subs = null;
            try { subs = di.GetDirectories(); } catch { }
            if (subs != null)
            {
                for (int i = 0; i < subs.Length; i++)
                {
                    DirectoryInfo sd = subs[i];
                    if ((sd.Attributes & FileAttributes.ReparsePoint) != 0) { r.SkippedJunctions++; continue; }
                    if (IsScanSkip(sd.Name)) continue;
                    // 个人目录（桌面/文档/图片…）照样量大小，只是判「不能碰」
                    long s = Walk(sd.FullName, depth + 1, sizes, parents, depths, r, onProgress);
                    sizes[sd.FullName] = s;
                    parents[sd.FullName] = dir;
                    depths[sd.FullName] = depth + 1;
                    total += s;
                }
            }
            return total;
        }

        public static string Fmt(long bytes)
        {
            double b = bytes;
            if (b >= 1024.0 * 1024 * 1024 * 1024) return (b / (1024.0 * 1024 * 1024 * 1024)).ToString("N2") + " TB";
            if (b >= 1024.0 * 1024 * 1024) return (b / (1024.0 * 1024 * 1024)).ToString("N2") + " GB";
            if (b >= 1024.0 * 1024) return (b / (1024.0 * 1024)).ToString("N1") + " MB";
            if (b >= 1024.0) return (b / 1024.0).ToString("N0") + " KB";
            return bytes + " B";
        }
    }
}
