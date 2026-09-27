using System;
using System.IO;
using System.Text;

namespace CMover
{
    // ★ 为什么要有这个类
    //   这工具是发给别人用的。它在你同学的电脑上出了问题，你这边一个字都看不到 ——
    //   只能靠电话里「它说……」「然后呢？」来回猜。有了它，让对方把 cmover.log
    //   发回来，就能看出卡在哪一步、哪个文件、当时是不是管理员。
    //
    // ★ 铁律：这个类里任何一个函数都【不许往外抛异常】。
    //   日志写不出去，最坏的结果是没日志；要是因为写日志把搬运搞砸了，那才叫荒唐。
    //   所以每个可能出事的地方都裹着 try/catch，catch 里什么都不做。
    //
    // ★ 名字叫 Logger 不叫 Log：Ui 里已经有一个方法叫 Log(...)，
    //   再弄一个同名的类，C# 在某些位置上会分不清那到底是方法还是类。
    public static class Logger
    {
        // 超过这个大小，就在【下次启动】时轮转一次（旧的改名成 .log.1）
        const long MaxBytes = 1024 * 1024;

        static readonly object gate = new object();
        static readonly UTF8Encoding enc = new UTF8Encoding(false);
        static bool inited;
        static string file;

        // ★ 名字不能叫 Path —— 会和 System.IO.Path 打架。
        public static string FilePath()
        {
            lock (gate)
            {
                if (inited) return file;
                inited = true;
                try
                {
                    string d = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CMover");
                    Directory.CreateDirectory(d);
                    file = Path.Combine(d, "cmover.log");
                }
                catch { file = null; }
                return file;
            }
        }

        // 每次启动调一次。
        // ★ 特意【不】做「写满就切」—— 那样会在一次搬运进行到一半时把日志切成两半，
        //   最有用的后半段反而被切走。启动时轮转，保证「上一次运行」永远是完整的。
        public static void Rotate()
        {
            try
            {
                string f = FilePath();
                if (f == null) return;
                FileInfo fi = new FileInfo(f);
                if (!fi.Exists || fi.Length < MaxBytes) return;
                string old = f + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(f, old);
            }
            catch { }
        }

        // 写一行。s 里可以有换行，原样落盘。
        public static void W(string s)
        {
            try
            {
                string f = FilePath();
                if (f == null) return;
                byte[] body = enc.GetBytes(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + Environment.NewLine);

                lock (gate)
                {
                    // FileShare.ReadWrite：日志文件正开着（比如用户拿记事本在看）
                    // 也不能让写入失败。别人占用是常态，不是异常。
                    using (FileStream fs = new FileStream(f, FileMode.Append,
                                                          FileAccess.Write, FileShare.ReadWrite))
                    {
                        // ★ BOM 只在文件是空的时候写一次。
                        //   要是每次追加都带 BOM（File.AppendAllText 配 UTF8Encoding(true)
                        //   就是这个下场），文件中间会撒满 EF BB BF，看着像乱码。
                        if (fs.Length == 0)
                            fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                        fs.Write(body, 0, body.Length);
                    }
                }
            }
            catch { }
        }
    }
}
