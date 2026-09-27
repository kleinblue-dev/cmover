using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CMover
{
    public static class Report
    {
        public static string VerdictLabel(Verdict v)
        {
            switch (v)
            {
                case Verdict.SafeMove: return "能搬";
                case Verdict.MustUseApp: return "要用软件自己搬";
                case Verdict.DeleteOnly: return "能删";
                default: return "不能碰";
            }
        }

        // 只有「能搬」和「能删」的才算进「能拿回来多少」
        public static long Reclaimable(List<Item> items)
        {
            long t = 0;
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (!it.IsLeaf) continue;
                if (it.Advice.Verdict == Verdict.SafeMove || it.Advice.Verdict == Verdict.DeleteOnly) t += it.Size;
            }
            return t;
        }

        public static string Text(ScanResult r)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("C 盘：已用 " + Scanner.Fmt(r.UsedC) + " / 总共 " + Scanner.Fmt(r.TotalC) + "，还剩 " + Scanner.Fmt(r.FreeC));
            sb.AppendLine("最快能拿回来约 " + Scanner.Fmt(Reclaimable(r.Items)));
            sb.AppendLine("（扫了 " + r.DirCount + " 个目录，跳过 " + r.SkippedJunctions + " 个链接）");
            sb.AppendLine();

            sb.AppendLine("── 这一盘都被谁占着 ──");
            sb.AppendLine(string.Format("{0,-46} {1,10}  {2}", "路径", "大小", "能不能动"));
            sb.AppendLine(new string('-', 100));
            for (int i = 0; i < r.TopItems.Count; i++)
            {
                Item it = r.TopItems[i];
                string p = it.Path;
                if (p.Length > 45) p = "..." + p.Substring(p.Length - 42);
                sb.AppendLine(string.Format("{0,-46} {1,10}  {2}", p, Scanner.Fmt(it.Size),
                    VerdictLabel(it.Advice.Verdict)));
            }
            if (r.TopFilesBytes > 0)
                sb.AppendLine(string.Format("{0,-46} {1,10}  {2}", "（C 盘根目录下的文件：休眠 / 虚拟内存）",
                    Scanner.Fmt(r.TopFilesBytes), "不能碰"));
            sb.AppendLine();

            sb.AppendLine("── 能拿回来的，一件件做 ──");
            sb.AppendLine(string.Format("{0,-46} {1,10}  {2,-14} {3}", "路径", "大小", "判定", "这是什么"));
            sb.AppendLine(new string('-', 100));
            for (int i = 0; i < r.Items.Count; i++)
            {
                Item it = r.Items[i];
                string p = it.Path;
                if (p.Length > 45) p = "..." + p.Substring(p.Length - 42);
                sb.AppendLine(string.Format("{0,-46} {1,10}  {2,-14} {3}",
                    p, Scanner.Fmt(it.Size), VerdictLabel(it.Advice.Verdict), it.Advice.Title));
            }
            sb.AppendLine();
            sb.AppendLine("说明：Windows 目录里有大量硬链接（WinSxS），会计重，所以它的数字偏大；");
            sb.AppendLine("      Program Files 下的 WindowsApps 要系统权限才读得到，数字偏小。");
            sb.AppendLine("      这两块都是「不能碰」，不影响上面「能拿回来多少」那个数。");
            return sb.ToString();
        }

        static string H(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        public static string Html(ScanResult r, bool isAdmin)
        {
            StringBuilder sb = new StringBuilder();
            long reclaim = Reclaimable(r.Items);
            double used = Math.Max(1, r.UsedC);

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.AppendLine("<title>C 盘体检报告</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(@"
.viz-root{
  color-scheme: light;
  --surface-1:#fcfcfb; --page:#f9f9f7;
  --ink-1:#0b0b0b; --ink-2:#52514e; --muted:#898781;
  --grid:#e1e0d9; --baseline:#c3c2b7; --hair:rgba(11,11,11,.10);
  --series:#2a78d6; --series-soft:#cde2fb;
  --good:#0ca30c; --warn:#fab219; --serious:#ec835a; --crit:#d03b3b;
  --chip-ink:#ffffff;
}
@media (prefers-color-scheme: dark){
  :root:where(:not([data-theme=""light""])) .viz-root{
    color-scheme: dark;
    --surface-1:#1a1a19; --page:#0d0d0d;
    --ink-1:#ffffff; --ink-2:#c3c2b7; --muted:#898781;
    --grid:#2c2c2a; --baseline:#383835; --hair:rgba(255,255,255,.10);
    --series:#3987e5; --series-soft:#184f95;
  }
}
*{box-sizing:border-box}
body{margin:0;padding:32px 20px 72px;background:var(--page);color:var(--ink-1);
  font:15px/1.65 system-ui,-apple-system,""Segoe UI"",""Microsoft YaHei"",sans-serif;}
.wrap{max-width:1000px;margin:0 auto}
h1{font-size:26px;margin:0 0 6px;letter-spacing:-.01em}
h2{font-size:18px;margin:40px 0 4px;letter-spacing:-.01em}
.sub{color:var(--ink-2);font-size:13.5px;margin:0 0 8px}
.note{color:var(--muted);font-size:12.5px;margin:2px 0 18px}
.card{background:var(--surface-1);border:1px solid var(--hair);border-radius:12px;padding:22px 24px;margin:18px 0}
.hero{display:flex;align-items:baseline;gap:16px;flex-wrap:wrap}
.hero-num{font-size:46px;font-weight:650;letter-spacing:-.025em;line-height:1.05}
.hero-lab{color:var(--ink-2);font-size:14px;max-width:520px}
.stat{display:flex;gap:34px;flex-wrap:wrap;margin-top:20px;padding-top:18px;border-top:1px solid var(--hair)}
.stat div{min-width:130px}
.stat .k{color:var(--muted);font-size:12.5px}
.stat .v{font-size:19px;font-weight:600;margin-top:2px}
table{width:100%;border-collapse:separate;border-spacing:0 2px;table-layout:fixed}
th{font-size:12px;color:var(--muted);text-align:left;font-weight:500;padding:0 8px 6px;border-bottom:1px solid var(--grid)}
th.r,td.r{text-align:right}
td{padding:7px 8px;vertical-align:middle}
tr.row:hover td{background:color-mix(in srgb,var(--series) 7%,transparent)}
.path{font-size:12.5px;color:var(--ink-2);word-break:break-all;line-height:1.4}
.path b{color:var(--ink-1);font-weight:600}
.track{background:var(--grid);border-radius:0;height:14px;width:100%;position:relative}
.fill{height:14px;background:var(--series);border-radius:0 4px 4px 0;min-width:3px}
.v{font-variant-numeric:tabular-nums;font-size:13.5px;font-weight:600;white-space:nowrap}
.chip{display:inline-block;font-size:11.5px;line-height:1.5;padding:1px 8px;border-radius:99px;
  color:var(--chip-ink);white-space:nowrap;font-weight:600}
.c-move{background:var(--good)} .c-del{background:var(--good)}
.c-app{background:#8a6100} .c-never{background:var(--crit)}
.chipwrap{display:flex;align-items:center;gap:6px}
.icon{font-size:12px;line-height:1}
.sec{margin-top:44px}
.item{border-top:1px solid var(--hair);padding:16px 0}
.item:first-of-type{border-top:0}
.item h3{margin:0 0 6px;font-size:15.5px;display:flex;align-items:center;gap:9px;flex-wrap:wrap}
.item .p{font-size:12.5px;color:var(--muted);word-break:break-all;margin:0 0 8px}
.item .how{margin:0;color:var(--ink-2);font-size:13.5px}
.warn{margin:7px 0 0;padding:7px 11px;border-left:3px solid var(--warn);background:color-mix(in srgb,var(--warn) 12%,transparent);
  font-size:13px;color:var(--ink-1);border-radius:0 6px 6px 0}
.warn.crit{border-left-color:var(--crit);background:color-mix(in srgb,var(--crit) 12%,transparent)}
h3 .sz{color:var(--muted);font-weight:500;font-size:13px;font-variant-numeric:tabular-nums}
.foot{margin-top:52px;padding-top:20px;border-top:1px solid var(--hair);color:var(--muted);font-size:12.5px}
.big{color:var(--ink-1);font-weight:600}
");
            sb.AppendLine("</style></head><body class=\"viz-root\"><div class=\"wrap\">");

            sb.AppendLine("<h1>C 盘体检报告</h1>");
            sb.AppendLine("<p class=\"sub\">" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") +
                " 生成 &middot; 本工具全程离线，没有联网功能，不读取文件内容</p>");
            sb.AppendLine("<p class=\"note\">桌面、文档、图片、视频、音乐这些个人目录默认锁死不动。跳过 " +
                r.SkippedJunctions + " 个系统链接，扫了 " + r.DirCount + " 个目录。</p>");

            // ---- 头部数字 ----
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine("<div class=\"hero\"><div class=\"hero-num\">" + Scanner.Fmt(reclaim) + "</div>");
            sb.AppendLine("<div class=\"hero-lab\">最多能拿回来。下面标「<b>能删</b>」和「<b>能搬</b>」的加起来就是这个数 —— 标「要用软件自己搬」的不算在里面，那些得你自己去软件里点。</div></div>");
            sb.AppendLine("<div class=\"stat\">");
            sb.AppendLine("<div><div class=\"k\">C 盘总共</div><div class=\"v\">" + Scanner.Fmt(r.TotalC) + "</div></div>");
            sb.AppendLine("<div><div class=\"k\">已用</div><div class=\"v\">" + Scanner.Fmt(r.UsedC) + "</div></div>");
            sb.AppendLine("<div><div class=\"k\">剩余</div><div class=\"v\">" + Scanner.Fmt(r.FreeC) + "</div></div>");
            sb.AppendLine("</div></div>");

            // ---- 占比图：C 盘顶层全景，回答「我的盘到底被谁占了」 ----
            // 用 TopItems 而不是 Items：Items 里既有 34 GB 的 AppData\Local
            // 又有它里面 17 GB 的 wsl，画在一张图上就是重复计数。
            sb.AppendLine("<h2>哪儿占地方</h2>");
            sb.AppendLine("<p class=\"note\">这一节只按 C 盘最外层分，一行就是一个顶层文件夹 —— 这样各行的和能跟上面的「已用」对上账。横条长度 = 它占已用空间的比例。</p>");
            sb.AppendLine("<div class=\"card\"><table>");
            sb.AppendLine("<colgroup><col style=\"width:34%\"><col><col style=\"width:96px\"><col style=\"width:150px\"></colgroup>");
            sb.AppendLine("<tr><th>文件夹</th><th>占多少</th><th class=\"r\">大小</th><th>能不能动</th></tr>");

            double wTop = r.TopItems.Count > 0 ? Math.Max(1, r.TopItems[0].Size) : 1;
            for (int i = 0; i < r.TopItems.Count; i++)
            {
                Item it = r.TopItems[i];
                double pct = it.Size * 100.0 / used;
                double w = it.Size * 100.0 / wTop;
                if (w < 0.6) w = 0.6;

                sb.AppendLine("<tr class=\"row\">");
                sb.AppendLine("<td class=\"path\">" + H(it.Path) + "</td>");
                sb.AppendLine("<td><div class=\"track\" title=\"" + H(it.Path) + " — " + Scanner.Fmt(it.Size) +
                    "，占已用空间 " + pct.ToString("N1") + "%\"><div class=\"fill\" style=\"width:" +
                    w.ToString("N1") + "%\"></div></div></td>");
                sb.AppendLine("<td class=\"r v\">" + Scanner.Fmt(it.Size) + "</td>");
                sb.AppendLine("<td><span class=\"chipwrap\"><span class=\"chip " + ChipClass(it.Advice.Verdict) + "\">" +
                    IconOf(it.Advice.Verdict) + " " + H(VerdictLabel(it.Advice.Verdict)) + "</span></span></td>");
                sb.AppendLine("</tr>");
            }
            if (r.TopFilesBytes > 0)
            {
                double pf = r.TopFilesBytes * 100.0 / used;
                double wf = r.TopFilesBytes * 100.0 / wTop;
                if (wf < 0.6) wf = 0.6;
                sb.AppendLine("<tr class=\"row\">");
                sb.AppendLine("<td class=\"path\">C:\\ 根目录下的文件（休眠 hiberfil.sys、虚拟内存 pagefile.sys）</td>");
                sb.AppendLine("<td><div class=\"track\" title=\"占已用空间 " + pf.ToString("N1") +
                    "%\"><div class=\"fill\" style=\"width:" + wf.ToString("N1") + "%\"></div></div></td>");
                sb.AppendLine("<td class=\"r v\">" + Scanner.Fmt(r.TopFilesBytes) + "</td>");
                sb.AppendLine("<td><span class=\"chipwrap\"><span class=\"chip c-never\">" + IconOf(Verdict.Never) +
                    " 不能碰</span></span></td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</table></div>");

            // ---- 一件件说 ----
            sb.AppendLine("<div class=\"sec\"><h2>一件件说，怎么做</h2>");
            sb.AppendLine("<p class=\"note\">按大小排。只有标「能搬」的才适合用本工具搬；其他的一律照下面的步骤做，别硬搬。标「目录群」的，照它下面列出的小项分开处理。</p>");
            for (int i = 0; i < r.Items.Count; i++)
            {
                Item it = r.Items[i];
                Advice a = it.Advice;
                sb.AppendLine("<div class=\"item\">");
                sb.AppendLine("<h3><span class=\"chip " + ChipClass(a.Verdict) + "\">" + IconOf(a.Verdict) + " " +
                    H(VerdictLabel(a.Verdict)) + "</span> " + H(a.Title) +
                    " <span class=\"sz\">" + Scanner.Fmt(it.Size) + "</span></h3>");
                sb.AppendLine("<p class=\"p\">" + H(it.Path) + "</p>");
                sb.AppendLine("<p class=\"how\">" + H(a.HowTo) + "</p>");
                if (!string.IsNullOrEmpty(a.Warn))
                    sb.AppendLine("<p class=\"warn" + (a.Verdict == Verdict.Never ? " crit" : "") + "\">" + H(a.Warn) + "</p>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");

            sb.AppendLine("<div class=\"foot\">");
            if (!isAdmin)
                sb.AppendLine("<p>★ 这次不是以管理员身份运行的。有些系统目录扫不到，报告里的数字会偏小 —— 想看完整的，右键本工具 →「以管理员身份运行」。</p>");
            sb.AppendLine("<p>关于数字准不准：Windows 目录里有大量硬链接（WinSxS 那类），同一个文件会被数好几次，所以它的数字<b>偏大</b>；Program Files 下的 WindowsApps 要系统权限才读得到，数字<b>偏小</b>。这两块都是「不能碰」，不影响上面「能拿回来多少」那个数 —— 但你对账的时候心里有个数。</p>");
            sb.AppendLine("<p>本报告由「C盘轻量迁移清理器」生成。它不联网、不上传、不读文件内容，报告里只有文件夹名和大小。</p>");
            sb.AppendLine("</div>");

            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        static string ChipClass(Verdict v)
        {
            switch (v)
            {
                case Verdict.SafeMove: return "c-move";
                case Verdict.DeleteOnly: return "c-del";
                case Verdict.MustUseApp: return "c-app";
                default: return "c-never";
            }
        }

        // 状态色永远配一个符号 + 文字，不靠颜色单独表意
        static string IconOf(Verdict v)
        {
            switch (v)
            {
                case Verdict.SafeMove: return "✓";      // ✓
                case Verdict.DeleteOnly: return "✓";    // ✓
                case Verdict.MustUseApp: return "⚠";    // ⚠
                default: return "✕";                    // ✕
            }
        }
    }
}
