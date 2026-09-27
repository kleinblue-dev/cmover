using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CMover
{
    public class MainForm : Form
    {
        TableLayoutPanel root;
        Label lblHead, lblSub, lblStep, lblStatus, lblTargetHint;
        Button btnScan, btnGo, btnOpen, btnRecords, btnReport, btnLog;
        ListView list;
        ComboBox cboTarget;
        ProgressBar bar;
        TextBox txtLog;

        ScanResult scan;
        List<Item> shown = new List<Item>();
        bool busy;
        volatile bool scanning;     // 扫描期间为真；见 DoScan 里对进度回调的说明

        // 大于这个数才值得单列一行（能让动的那两类另有一条低得多的门槛，
        // 见 Scanner.ActionFloorBytes）。写成常量是因为下面那句
        // 「没找到大于 X 的文件夹」得跟它说同一个数 —— 原来两边各写各的，
        // 改了门槛这句提示就会变成假话，而且没人会发现。
        const long ScanFloorBytes = 2L * 1024 * 1024 * 1024;

        public MainForm()
        {
            Text = "C盘轻量迁移清理器";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1020, 780);
            MinimumSize = new Size(900, 640);
            Font = UiFont(10F);
            BackColor = Color.FromArgb(249, 249, 247);

            root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.Padding = new Padding(20, 16, 20, 16);
            root.RowCount = 6;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // header
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // step1
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // list
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // target
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // progress
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150)); // log
            Controls.Add(root);

            // ---------------- 标题 ----------------
            lblHead = new Label();
            lblHead.Text = "C 盘轻量迁移清理器";
            lblHead.Font = UiFont(17F, FontStyle.Bold);
            lblHead.AutoSize = true;
            lblHead.Margin = new Padding(0, 0, 0, 2);
            root.Controls.Add(lblHead, 0, 0);

            lblSub = new Label();
            lblSub.Text = "把 C 盘的大文件夹搬到别的盘，老位置留一个「传送门」—— 快捷方式不用改，程序照常启动。全程离线，不联网。";
            lblSub.ForeColor = Color.FromArgb(82, 81, 78);
            lblSub.AutoSize = true;
            lblSub.Margin = new Padding(0, 0, 0, 14);
            root.Controls.Add(lblSub, 0, 0);
            // 两行都放第 0 行不合适，改用面板
            Panel headPanel = new Panel();
            headPanel.Dock = DockStyle.Fill;
            headPanel.AutoSize = true;
            headPanel.Controls.Add(lblSub);
            headPanel.Controls.Add(lblHead);
            lblHead.Location = new Point(0, 0);
            lblSub.Location = new Point(2, 34);
            headPanel.Height = 62;
            root.Controls.Remove(lblHead);
            root.Controls.Remove(lblSub);
            root.Controls.Add(headPanel, 0, 0);

            // ---------------- 第 1 步 ----------------
            Panel stepPanel = new Panel();
            stepPanel.Dock = DockStyle.Fill;
            stepPanel.Height = 56;

            btnScan = BigButton("第 1 步：扫描 C 盘（约 1-2 分钟）", 0, 4, 300);
            btnScan.Click += delegate { DoScan(); };
            stepPanel.Controls.Add(btnScan);

            lblStep = new Label();
            lblStep.Text = "扫描只看不动，不会删改你任何东西。";
            lblStep.ForeColor = Color.FromArgb(82, 81, 78);
            lblStep.AutoSize = true;
            lblStep.Location = new Point(316, 22);
            stepPanel.Controls.Add(lblStep);

            // ★ 「打开上次的报告」和左边那句提示【共用同一个位置】，靠显隐互斥。
            //   一开始写成左上角各占 (316,4) 和 (316,22)：一个高 34、一个高 17，
            //   在 y 22..38 这段直接叠在一起，屏幕上是一团糊字（截图对比过）。
            //   摆到右边也躲不掉 —— 窗口最小宽度 900 时会被切掉。
            //   而且这两句本来就不同时有用：没扫过时提示「只看不动」，
            //   扫完了才需要「打开报告」。所以让它们轮流用这块地方，重叠无从发生。
            btnReport = new Button();
            btnReport.Text = "打开上次的报告";
            btnReport.Size = new Size(150, 34);
            btnReport.Location = new Point(316, 10);
            btnReport.FlatStyle = FlatStyle.System;
            btnReport.Visible = false;
            btnReport.Click += delegate { OpenReport(); };
            stepPanel.Controls.Add(btnReport);

            root.Controls.Add(stepPanel, 0, 1);

            // ---------------- 列表 ----------------
            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.GridLines = false;
            list.Font = UiFont(9.5F);
            list.Columns.Add("文件夹", 430);
            list.Columns.Add("大小", 90, HorizontalAlignment.Right);
            list.Columns.Add("该怎么办", 160);
            list.Columns.Add("说明", 300);
            // ★ 列宽必须是【比例】，不能是写死的像素。
            //   踩过的坑：manifest 里开了 DPI 感知之后（这台机器屏幕 125%）,
            //   AutoScaleMode.Font 会把整个窗体等比缩到 0.8 —— 别的控件都跟着缩了，
            //   只有 ListView 的列宽是像素、不参与缩放，于是 980 px 的列挤在 816 px
            //   的客户区里，「说明」那一列直接被切掉半个字（实测 816 vs 980）。
            //   按比例分之后，任何缩放、任何窗口宽度都装得下；
            //   顺带治好「把窗口拉宽、列宽不动、右边空一片」。
            LayoutColumns();
            list.Resize += delegate { LayoutColumns(); };
            list.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                Item it = e.Item.Tag as Item;
                if (it == null) return;
                if (it.Advice.Verdict != Verdict.SafeMove && e.Item.Checked)
                {
                    e.Item.Checked = false;
                    MessageBox.Show(this,
                        "【" + it.Advice.Title + "】不能用本工具搬。\n\n" + it.Advice.HowTo +
                        (string.IsNullOrEmpty(it.Advice.Warn) ? "" : "\n\n" + it.Advice.Warn),
                        "这个不能搬", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                UpdateGoButton();
            };
            list.MouseDoubleClick += delegate { OpenSelected(); };
            root.Controls.Add(list, 0, 2);

            // ---------------- 目标盘 ----------------
            Panel tgt = new Panel();
            tgt.Dock = DockStyle.Fill;
            // ★ 112 是算出来的，不是拍的：btnGo 在 y=62、高 42，底边落在 104，
            //   再留 8 px 余量。原来写 92 —— 面板比按钮矮，第 3 步那个按钮
            //   底下被裁掉 12 px（Dock=Fill 的面板按自身 Height 定行高，
            //   不会因为子控件更大就自己长高）。截图里它比旁边的按钮明显矮一截。
            tgt.Height = 112;

            Label l1 = new Label();
            l1.Text = "第 2 步：搬到哪个盘";
            l1.Font = UiFont(11F, FontStyle.Bold);
            l1.AutoSize = true;
            l1.Location = new Point(0, 8);
            tgt.Controls.Add(l1);

            cboTarget = new ComboBox();
            cboTarget.DropDownStyle = ComboBoxStyle.DropDownList;
            cboTarget.Location = new Point(0, 34);
            cboTarget.Width = 400;
            cboTarget.Font = UiFont(10F);
            tgt.Controls.Add(cboTarget);

            lblTargetHint = new Label();
            lblTargetHint.ForeColor = Color.FromArgb(82, 81, 78);
            lblTargetHint.AutoSize = true;
            lblTargetHint.Location = new Point(412, 38);
            tgt.Controls.Add(lblTargetHint);

            btnGo = BigButton("第 3 步：开始搬（会先复制 + 逐位校验，全都对上了才删 C 盘那份）", 0, 62, 560);
            btnGo.Enabled = false;
            btnGo.Click += delegate { DoMigrate(); };
            tgt.Controls.Add(btnGo);

            btnOpen = new Button();
            btnOpen.Text = "打开选中的位置";
            btnOpen.Size = new Size(140, 32);
            btnOpen.Location = new Point(572, 62);
            btnOpen.FlatStyle = FlatStyle.System;
            btnOpen.Click += delegate { OpenSelected(); };
            tgt.Controls.Add(btnOpen);

            btnRecords = new Button();
            btnRecords.Text = "搬运记录 / 撤销";
            btnRecords.Size = new Size(150, 32);
            btnRecords.Location = new Point(720, 62);
            btnRecords.FlatStyle = FlatStyle.System;
            btnRecords.Click += delegate { ShowRecords(); };
            tgt.Controls.Add(btnRecords);

            // ★ 日志文件住在 %LOCALAPPDATA%\CMover\ 里，小白根本找不到 ——
            //   找不到的日志等于没有日志。所以必须给一个点一下就到的入口。
            //   位置 (720, 24)：btnRecords 在 y=62，这里 24+32=56，不压到它；
            //   左边 cboTarget 只到 x=400，也不打架。
            btnLog = new Button();
            btnLog.Text = "打开日志文件夹";
            btnLog.Size = new Size(150, 32);
            btnLog.Location = new Point(720, 24);
            btnLog.FlatStyle = FlatStyle.System;
            btnLog.Click += delegate { OpenLogFolder(); };
            tgt.Controls.Add(btnLog);

            root.Controls.Add(tgt, 0, 3);

            // ---------------- 进度 ----------------
            Panel prog = new Panel();
            prog.Dock = DockStyle.Fill;
            prog.Height = 46;

            bar = new ProgressBar();
            bar.Location = new Point(0, 10);
            bar.Size = new Size(660, 20);
            prog.Controls.Add(bar);

            lblStatus = new Label();
            lblStatus.Text = "准备好了。点上面第 1 步开始。";
            lblStatus.AutoSize = true;
            lblStatus.Location = new Point(670, 13);
            prog.Controls.Add(lblStatus);

            root.Controls.Add(prog, 0, 4);

            // ---------------- 日志 ----------------
            txtLog = new TextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.Multiline = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.ReadOnly = true;
            txtLog.BackColor = Color.White;
            txtLog.Font = UiFont(9F);
            root.Controls.Add(txtLog, 0, 5);

            BuildTargets();
            Log("本工具做的事：把 C 盘的大文件夹复制到别的盘 → 逐位比对确认一个字节都没差 → 在 C 盘老位置建一个「传送门」→ 才删掉 C 盘原文件。");
            Log("任何一步没过，C 盘一个字节都不会动。");
        }

        // 四列按比例分完整个宽度。留 22 px 给竖滚动条 —— 不留的话，
        // 最后一列会把滚动条挤出来，滚动条一出现客户区又变窄，来回抖。
        void LayoutColumns()
        {
            int w = list.ClientSize.Width - 22;
            if (w < 220) return;
            int c0 = (int)(w * 0.44);
            int c1 = (int)(w * 0.09);
            int c2 = (int)(w * 0.16);
            list.Columns[0].Width = c0;
            list.Columns[1].Width = c1;
            list.Columns[2].Width = c2;
            list.Columns[3].Width = w - c0 - c1 - c2;
        }

        static Font UiFont(float size) { return UiFont(size, FontStyle.Regular); }
        static Font UiFont(float size, FontStyle st)
        {
            try { return new Font("Microsoft YaHei UI", size, st); }
            catch { }
            try { return new Font("微软雅黑", size, st); }
            catch { }
            Font f = SystemFonts.MessageBoxFont;
            return new Font(f.FontFamily, size, st);
        }

        Button BigButton(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 42);
            b.Font = UiFont(10F, FontStyle.Bold);
            b.FlatStyle = FlatStyle.System;
            b.TextAlign = ContentAlignment.MiddleCenter;
            return b;
        }

        void Log(string s)
        {
            // ★ 界面上给用户看的每一行，同时写进日志文件。
            //   接在这一个点上，是因为界面上的东西本来就是「用户当时看到了什么」，
            //   事后排查要的正是这个。放在 txtLog 的空判断【之前】—— 那样即使
            //   日志框还没建好（或已被清空），文件里也照样有记录。
            Logger.W(s);

            if (txtLog == null) return;
            txtLog.AppendText(s + Environment.NewLine);
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        void SetBusy(bool b, string status)
        {
            busy = b;
            btnScan.Enabled = !b;
            btnRecords.Enabled = !b;
            cboTarget.Enabled = !b;
            if (status != null) lblStatus.Text = status;
            UpdateGoButton();
            Cursor = b ? Cursors.WaitCursor : Cursors.Default;
        }

        void UpdateGoButton()
        {
            int n = 0;
            for (int i = 0; i < list.Items.Count; i++)
                if (list.Items[i].Checked) n++;
            btnGo.Enabled = !busy && n > 0 && cboTarget.SelectedIndex >= 0;
            btnGo.Text = n == 0
                ? "第 3 步：开始搬（先勾选上面「能搬」的文件夹）"
                : string.Format("第 3 步：开始搬（已选 {0} 个，先复制 + 逐位校验，全都对上了才删 C 盘那份）", n);
        }

        // ================= 目标盘 =================
        class Target
        {
            public string Path;
            public string Label;
            public override string ToString() { return Label; }
        }

        void BuildTargets()
        {
            cboTarget.Items.Clear();
            DriveInfo[] ds = null;
            try { ds = DriveInfo.GetDrives(); } catch { }
            if (ds != null)
            {
                for (int i = 0; i < ds.Length; i++)
                {
                    DriveInfo d = ds[i];
                    try
                    {
                        if (d.DriveType != DriveType.Fixed) continue;
                        if (string.Equals(d.Name, "C:\\", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!d.IsReady) continue;
                        string root = d.Name;
                        string sub = null;
                        if (CanWrite(root)) sub = Path.Combine(root, "C盘迁移");
                        else
                        {
                            // ★ 兜底：盘根写不进去，就试「盘根\<当前用户名>」。
                            //   这里【绝不能】写死某个具体的用户名 —— 那在作者机器上能用，
                            //   换个人（名字不一样）就变成「这个盘整个不在下拉框里」，
                            //   而且不报错、不提示，用户只会觉得「我的 D 盘呢」。
                            //   Path.GetFileName 而不是 DirectoryInfo：空串不会抛。
                            string uname = Path.GetFileName(Scanner.ProfileRoot());
                            if (uname.Length > 0 && CanWrite(Path.Combine(root, uname)))
                                sub = Path.Combine(root, uname, "C盘迁移");
                        }
                        if (sub == null) continue;
                        Target t = new Target();
                        t.Path = sub;
                        t.Label = string.Format("{0}  (还剩 {1})", sub, Scanner.Fmt(d.AvailableFreeSpace));
                        cboTarget.Items.Add(t);
                    }
                    catch { }
                }
            }
            if (cboTarget.Items.Count > 0)
            {
                cboTarget.SelectedIndex = 0;
                lblTargetHint.Text = "★ 建议别和 C 盘在同一个物理硬盘上，那样才真的腾出空间。";
            }
            else
            {
                lblTargetHint.Text = "没找到可写的别的盘。插一个 U 盘或移动硬盘也行，但要小心别拔掉。";
            }
            UpdateGoButton();
        }

        static bool CanWrite(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    // 试建一层，建完就删
                    string probe = Path.Combine(Path.GetDirectoryName(dir.TrimEnd('\\')), "~cmtest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(probe);
                    Directory.Delete(probe, false);
                    return true;
                }
                string f = Path.Combine(dir, "~cmtest_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                File.WriteAllText(f, "x");
                File.Delete(f);
                return true;
            }
            catch { return false; }
        }

        // 状态栏那一行放不下整条路径，掐头留尾 —— 尾巴上才是目录名
        static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= 44) return s;
            return "..." + s.Substring(s.Length - 41);
        }

        // ================= 扫描 =================
        void DoScan()
        {
            SetBusy(true, "正在扫描 C 盘……大概 1-2 分钟。");
            scanning = true;
            btnReport.Visible = false;
            lblStep.Visible = true;      // 跟 btnReport 互斥，见构造区那段注释
            txtLog.Clear();
            list.Items.Clear();
            shown.Clear();
            bar.Style = ProgressBarStyle.Marquee;

            Thread t = new Thread(delegate()
            {
                ScanResult r = null;
                string err = null;
                DateTime t0 = DateTime.Now;
                try
                {
                    r = Scanner.Scan(5, ScanFloorBytes, delegate(long n, string d)
                    {
                        // 不加这个判断的话，扫描结束后还在排队的进度回调
                        // 会把「扫描完了」那句状态覆盖掉。
                        if (!scanning) return;
                        this.BeginInvoke((MethodInvoker)delegate()
                        {
                            if (!scanning) return;
                            lblStatus.Text = "正在扫描……已经扫了 " + n + " 个文件夹。最新一个：" + Shorten(d);
                        });
                    });
                }
                catch (Exception ex) { err = ex.Message; }
                scanning = false;
                double sec = (DateTime.Now - t0).TotalSeconds;

                this.BeginInvoke((MethodInvoker)delegate()
                {
                    bar.Style = ProgressBarStyle.Blocks;
                    bar.Value = 0;
                    if (err != null)
                    {
                        SetBusy(false, "扫描出错。");
                        Logger.W("[扫描] 失败: " + err);
                        Log("✗ " + err);
                        MessageBox.Show(this, err, "扫描出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    scan = r;
                    FillList(r);
                    string html = ReportPath();
                    string txt = Path.ChangeExtension(html, ".txt");
                    try
                    {
                        File.WriteAllText(html, Report.Html(r, Program.IsAdmin()), new UTF8Encoding(false));
                        File.WriteAllText(txt, Report.Text(r), new UTF8Encoding(true));
                        btnReport.Visible = true;
                        lblStep.Visible = false;
                        Log("报告已写到桌面：" + Path.GetFileName(html));
                    }
                    catch (Exception ex) { Log("★ 报告写不出去：" + ex.Message); }

                    SetBusy(false, string.Format("扫描完了，用了 {0:N1} 秒，找到 {1} 个大文件夹。", sec, shown.Count));
                    Log(string.Format("C 盘已用 {0}，剩 {1}。扫了 {2} 个目录，跳过 {3} 个系统链接。",
                        Scanner.Fmt(r.UsedC), Scanner.Fmt(r.FreeC), r.DirCount, r.SkippedJunctions));
                    Log("最快能拿回来约 " + Scanner.Fmt(Report.Reclaimable(r.Items)) +
                        "（只有标「能搬」的能用本工具搬）。");
                    Log("★ 标「要用软件自己搬」的别硬搬 —— 报告里写了每个该去哪儿点。");

                    // 带 [标签] 的这几行是给人眼在日志文件里搜的：
                    // 整份日志很长，有这一行就能一秒看出这次扫描的结果。
                    Logger.W(string.Format(
                        "[扫描] 完成 用时 {0:N1}s  目录 {1}  跳链接 {2}  列表 {3} 行  报告 {4}",
                        sec, r.DirCount, r.SkippedJunctions, shown.Count, Path.GetFileName(html)));

                    if (r.Items.Count == 0)
                        MessageBox.Show(this,
                            "没找到大于 " + (ScanFloorBytes / 1024 / 1024 / 1024) + " GB 的文件夹，" +
                            "也没找到能清理的缓存。\n\nC 盘已经挺干净了。",
                            "扫描完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void FillList(ScanResult r)
        {
            list.BeginUpdate();
            list.Items.Clear();
            shown.Clear();
            for (int i = 0; i < r.Items.Count; i++)
            {
                Item it = r.Items[i];
                shown.Add(it);
                string[] cells = new string[4];
                cells[0] = it.Path + (it.IsLeaf ? "" : "   (含下面的小项)");
                cells[1] = Scanner.Fmt(it.Size);
                cells[2] = Report.VerdictLabel(it.Advice.Verdict);
                cells[3] = it.Advice.Title;
                ListViewItem li = new ListViewItem(cells);
                li.Tag = it;
                if (it.Advice.Verdict == Verdict.SafeMove) li.ForeColor = Color.FromArgb(11, 11, 11);
                else if (it.Advice.Verdict == Verdict.DeleteOnly) li.ForeColor = Color.FromArgb(12, 110, 12);
                else if (it.Advice.Verdict == Verdict.MustUseApp) li.ForeColor = Color.FromArgb(138, 97, 0);
                else li.ForeColor = Color.FromArgb(150, 40, 40);
                list.Items.Add(li);
            }
            list.EndUpdate();
            UpdateGoButton();
        }

        // ================= 搬运 =================
        void DoMigrate()
        {
            List<Item> picks = new List<Item>();
            for (int i = 0; i < list.Items.Count; i++)
                if (list.Items[i].Checked) picks.Add((Item)list.Items[i].Tag);
            if (picks.Count == 0) return;

            Target tg = cboTarget.SelectedItem as Target;
            if (tg == null) { MessageBox.Show(this, "先选一个目标盘。", "还没选目标盘"); return; }
            if (tg.Path != null && tg.Path.Length > 1 && tg.Path[1] == ':')
            {
                try
                {
                    DriveInfo di = new DriveInfo(tg.Path.Substring(0, 3));
                    if (di.AvailableFreeSpace < 200L * 1024 * 1024)
                    { MessageBox.Show(this, "那个盘空间不够了。", "空间不够"); return; }
                }
                catch { }
            }

            long total = 0;
            for (int i = 0; i < picks.Count; i++) total += picks[i].Size;

            StringBuilder q = new StringBuilder();
            q.AppendLine("马上要搬这 " + picks.Count + " 个，一共 " + Scanner.Fmt(total) + "：");
            q.AppendLine();
            for (int i = 0; i < picks.Count && i < 8; i++) q.AppendLine("  · " + picks[i].Path);
            if (picks.Count > 8) q.AppendLine("  · ...还有 " + (picks.Count - 8) + " 个");
            q.AppendLine();
            q.AppendLine("搬到：" + tg.Path);
            q.AppendLine();
            q.AppendLine("过程是：先完整复制一份到目标盘 → 逐位比对每个文件 → 建「传送门」→ 确认通了，才删 C 盘那份。");
            q.AppendLine("任何一步没过，C 盘一个字节都不会动。");
            q.AppendLine();
            q.AppendLine("★ 大文件夹要复制很久（几十 GB 可能要几十分钟）。中途别关窗口。");

            if (MessageBox.Show(this, q.ToString(), "确认开始搬运",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            SetBusy(true, "正在搬运……");
            bar.Value = 0;
            txtLog.Clear();

            Thread t = new Thread(delegate()
            {
                int ok = 0, fail = 0;
                long freed = 0;
                for (int i = 0; i < picks.Count; i++)
                {
                    Item it = picks[i];
                    int idx = i;
                    this.BeginInvoke((MethodInvoker)delegate()
                    {
                        lblStatus.Text = string.Format("({0}/{1}) {2}", idx + 1, picks.Count, it.Path);
                    });

                    string leaf = new DirectoryInfo(it.Path.TrimEnd('\\')).Name;
                    Migrator m = new Migrator();
                    m.SrcPath = it.Path;
                    m.DstPath = Path.Combine(tg.Path, leaf);
                    m.FullVerify = true;

                    Migrator mm = m;
                    m.OnProgress = delegate(Progress p)
                    {
                        this.BeginInvoke((MethodInvoker)delegate()
                        {
                            if (p.Percent >= 0 && p.Percent <= 100)
                            { bar.Style = ProgressBarStyle.Blocks; bar.Value = p.Percent; }
                            if (!string.IsNullOrEmpty(p.Detail)) Log("  " + p.Phase + "｜" + p.Detail);
                        });
                    };

                    this.BeginInvoke((MethodInvoker)delegate()
                    { Log(""); Log("═══ " + it.Path + "  (" + Scanner.Fmt(it.Size) + ") ═══"); });

                    MigrateResult mr = mm.Run();
                    bool o = mr.Ok;
                    Logger.W(string.Format("[搬运] {0}  ({1})  ->  {2}",
                        it.Path, Scanner.Fmt(it.Size), m.DstPath));
                    Logger.W(o
                        ? "[搬运] 结果: 成功"
                        : "[搬运] 结果: 失败  " + mr.Message);
                    this.BeginInvoke((MethodInvoker)delegate()
                    {
                        string[] lines = mr.Log.Split('\n');
                        for (int k = 0; k < lines.Length; k++)
                            if (lines[k].Trim().Length > 0) Log("   " + lines[k].TrimEnd());
                        Log(o ? "✓ 成功：" + it.Path : "✗ 失败：" + mr.Message);
                    });
                    if (mr.Ok) { ok++; freed += it.Size; } else fail++;
                }

                int fok = ok, ffail = fail;
                long ffree = freed;
                this.BeginInvoke((MethodInvoker)delegate()
                {
                    SetBusy(false, string.Format("搬完：成功 {0} 个，失败 {1} 个，C 盘腾出约 {2}。",
                        fok, ffail, Scanner.Fmt(ffree)));
                    bar.Value = 0;
                    MessageBox.Show(this,
                        string.Format("成功 {0} 个，失败 {1} 个。\nC 盘腾出约 {2}。\n\n{3}",
                            fok, ffail, Scanner.Fmt(ffree),
                            ffail == 0
                              ? "老位置现在是个「传送门」，桌面快捷方式照常能用。\n想反悔的话点「搬运记录 / 撤销」。"
                              : "失败的那些 C 盘一个字节都没动，你的东西是安全的。\n详细的看下面日志。"),
                        "搬完了", MessageBoxButtons.OK,
                        ffail == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    DoScan();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // ★ 用 explorer.exe 显式打开，而不是 Process.Start(路径)：
        //   后者靠系统关联，在某些机器上会用别的程序去「打开」这个文件夹。
        //   这里的路径是程序自己算出来的常量，不含任何用户输入，没有注入面。
        void OpenLogFolder()
        {
            try
            {
                string f = Logger.FilePath();
                if (f == null || !File.Exists(f))
                {
                    MessageBox.Show(this,
                        "还没有日志文件。\n\n随便做一次扫描或搬运就会有了。",
                        "打开日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Logger.W("(用户点了「打开日志文件夹」)");
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + f + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "打不开文件夹：" + ex.Message + "\n\n路径在这里，你可以自己复制：\n" + Logger.FilePath(),
                    "打开日志", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ================= 记录 / 撤销 =================
        void ShowRecords()
        {
            List<Migrator.Record> rs = Migrator.LoadRecords();
            if (rs.Count == 0)
            {
                MessageBox.Show(this, "还没有搬过任何东西。", "搬运记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RecordForm f = new RecordForm();
            f.ShowDialog(this);
            UpdateGoButton();
        }

        void OpenSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            Item it = list.SelectedItems[0].Tag as Item;
            if (it == null) return;
            try { Process.Start("explorer.exe", "\"" + it.Path + "\""); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "打不开", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        static string ReportPath()
        {
            return Path.Combine(Program.DesktopDir(),
                "C盘体检报告_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".html");
        }

        void OpenReport()
        {
            try
            {
                DirectoryInfo d = new DirectoryInfo(Program.DesktopDir());
                FileInfo[] fs = d.GetFiles("C盘体检报告_*.html");
                if (fs.Length == 0) { MessageBox.Show(this, "桌面上还没有报告。", "找不到报告"); return; }
                Array.Sort(fs, delegate(FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                Process.Start(fs[0].FullName);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "打不开", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    // =============================================================
    // 记录 / 撤销窗口
    // =============================================================
    public class RecordForm : Form
    {
        ListView lv;
        Button btnUndo, btnClose, btnCheck;
        TextBox log;
        List<Migrator.Record> recs;

        public RecordForm()
        {
            Text = "搬运记录 / 撤销";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(860, 520);
            Font = new Font("Microsoft YaHei UI", 9.5F);

            lv = new ListView();
            lv.Dock = DockStyle.Top;
            lv.Height = 240;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.Columns.Add("原位置（C 盘）", 330);
            lv.Columns.Add("现在在哪儿", 300);
            lv.Columns.Add("大小", 80, HorizontalAlignment.Right);
            lv.Columns.Add("传送门", 90);
            Controls.Add(lv);

            Panel p = new Panel();
            p.Dock = DockStyle.Top;
            p.Height = 46;

            btnUndo = new Button();
            btnUndo.Text = "撤销选中的：搬回 C 盘";
            btnUndo.Location = new Point(0, 8);
            btnUndo.Size = new Size(210, 32);
            btnUndo.FlatStyle = FlatStyle.System;
            btnUndo.Click += delegate { DoUndo(); };
            p.Controls.Add(btnUndo);

            btnCheck = new Button();
            btnCheck.Text = "检查所有传送门还通不通";
            btnCheck.Location = new Point(220, 8);
            btnCheck.Size = new Size(210, 32);
            btnCheck.FlatStyle = FlatStyle.System;
            btnCheck.Click += delegate { DoCheck(); };
            p.Controls.Add(btnCheck);

            btnClose = new Button();
            btnClose.Text = "关闭";
            btnClose.Location = new Point(440, 8);
            btnClose.Size = new Size(90, 32);
            btnClose.FlatStyle = FlatStyle.System;
            btnClose.Click += delegate { Close(); };
            p.Controls.Add(btnClose);

            Controls.Add(p);

            log = new TextBox();
            log.Dock = DockStyle.Fill;
            log.Multiline = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.ReadOnly = true;
            log.BackColor = Color.White;
            Controls.Add(log);
            log.BringToFront();

            Load += delegate { Reload(); };
        }

        void Reload()
        {
            recs = Migrator.LoadRecords();
            lv.BeginUpdate();
            lv.Items.Clear();
            for (int i = 0; i < recs.Count; i++)
            {
                Migrator.Record r = recs[i];
                string t = Junction.IsReparsePoint(r.Src) ? Junction.ReadTarget(r.Src) : null;
                bool ok = t != null && string.Equals(t.TrimEnd('\\'), r.Dst.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                string[] c = new string[4];
                c[0] = r.Src;
                c[1] = r.Dst;
                c[2] = Scanner.Fmt(r.Bytes);
                c[3] = ok ? "正常" : (t == null ? "没有了" : "指向不对");
                ListViewItem li = new ListViewItem(c);
                li.Tag = r;
                if (!ok) li.ForeColor = Color.FromArgb(160, 40, 40);
                lv.Items.Add(li);
            }
            lv.EndUpdate();
            log.Text = "说明：撤销 = 把文件从别的盘复制回 C 盘原位置，校验通过后才删掉别的那份。\r\n" +
                       "大文件夹的撤销同样要等很久。如果你只是想确认东西还在，不用撤销 —— 直接用就行。\r\n";
        }

        void DoCheck()
        {
            log.Clear();
            Fx("开始检查 " + recs.Count + " 个传送门……\r\n");
            for (int i = 0; i < recs.Count; i++)
            {
                Migrator.Record r = recs[i];
                string t = Junction.IsReparsePoint(r.Src) ? Junction.ReadTarget(r.Src) : null;
                bool ok = t != null && Directory.Exists(r.Dst) &&
                    string.Equals(t.TrimEnd('\\'), r.Dst.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                Fx((ok ? "✓ " : "✗ ") + r.Src);
                if (ok) Fx("   → 正常指向 " + r.Dst);
                else
                {
                    Fx("   → 期望 " + r.Dst);
                    Fx("   → 实际 " + (t == null ? "(这个位置已经不是传送门了)" : t));
                    if (t == null && Directory.Exists(r.Dst))
                        Fx("   ★ 东西还在 " + r.Dst + "，没丢。要我帮你恢复就说一声。");
                }
            }
            Fx("\r\n检查完了。");
        }

        void DoUndo()
        {
            if (lv.SelectedItems.Count == 0) { MessageBox.Show(this, "先在上面选中一行。", "没选中"); return; }
            Migrator.Record r = lv.SelectedItems[0].Tag as Migrator.Record;
            if (r == null) return;
            string msg = "要把这个搬回 C 盘吗？\n\n" + r.Src + "\n\n" +
                "过程：从 " + r.Dst + " 复制回 C 盘 → 逐位校验 → 才删掉别的那份。\n" +
                "大文件夹要等很久。中途别关窗口。";
            if (MessageBox.Show(this, msg, "确认撤销", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            btnUndo.Enabled = false;
            Cursor = Cursors.WaitCursor;
            Thread t = new Thread(delegate()
            {
                MigrateResult res = Migrator.Undo(r, null);
                // 撤销也是会动数据的操作，必须留痕。（这个窗口有自己的日志框 Fx，
                // 不经过主界面的 Log()，所以要单独记一笔。）
                Logger.W("[撤销] " + r.Src + "  ->  " + r.Dst);
                Logger.W("[撤销] 结果: " + (res.Ok ? "成功" : "失败  " + res.Message));
                this.BeginInvoke((MethodInvoker)delegate()
                {
                    Cursor = Cursors.Default;
                    btnUndo.Enabled = true;
                    log.Clear();
                    Fx(res.Log);
                    Fx(res.Ok ? "✓ 搬回来了。" : "✗ " + res.Message);
                    Reload();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void Fx(string s)
        {
            log.AppendText(s + "\r\n");
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }
    }
}
