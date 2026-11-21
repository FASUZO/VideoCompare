using System;
using System.IO;
using System.Threading;

namespace VideoCompare
{
    /// <summary>
    /// 网络（SMB / UNC）大文件的顺序预读缓存。
    /// 后台线程跟着播放头往前顺序读文件，让 SMB 客户端 / 系统文件缓存提前就绪，
    /// 同时把「已经读到哪一段」报告给进度条画灰色缓存条。
    /// 只做只读共享打开，不影响播放器自己读、也不影响删除/移动（FileShare.ReadWrite|Delete）。
    /// </summary>
    public class PrefetchCache
    {
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        const int ChunkSize = 1 << 20;           // 每次读 1MB
        const long LookAheadBytes = 256L << 20;  // 目标：读到播放头前方 256MB
        const long SlabBytes = 320L << 20;       // 窗口拉太开就丢掉，重新从播放头开窗
        const int PaceMs = 18;                   // 每读 1MB 歇一下，约 55MB/s 上限，别跟播放抢带宽

        readonly object gate = new object();
        string path;
        long length;
        long winFrom, winTo;      // 已预读的字节区间
        long playhead;            // 播放头（字节）
        long jumpTo = -1;         // 外部要求立刻从该位置开始预读
        long bytesTotal;          // 累计读了多少（诊断用）
        Thread worker;
        volatile bool running;
        FileStream fs;
        volatile string lastErr;
        long iters, reads, jumps;

        public bool Running { get { return running; } }

        /// <summary>开始预读一个文件（先停掉旧的）。</summary>
        public void Start(string filePath)
        {
            Stop();
            path = filePath;
            try { length = new FileInfo(filePath).Length; }
            catch { length = 0; }
            lock (gate) { winFrom = 0; winTo = 0; playhead = 0; bytesTotal = 0; }
            Interlocked.Exchange(ref jumpTo, -1);
            if (length <= 0) return;
            running = true;
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "prefetch";
            worker = t;
            t.Start();
        }

        public void Stop()
        {
            running = false;
            var t = worker;
            worker = null;
            if (t != null)
            {
                try { t.Join(300); }
                catch { }
            }
            var f = fs;
            fs = null;
            if (f != null)
            {
                try { f.Dispose(); }
                catch { }
            }
            lock (gate) { winFrom = 0; winTo = 0; }
        }

        /// <summary>播放头位置（字节），由界面定时器周期性上报。</summary>
        public void Playhead(long bytePos)
        {
            lock (gate) { playhead = bytePos < 0 ? 0 : bytePos; }
        }

        public long Length { get { return length; } }

        /// <summary>按播放进度比例（0-1）要求预读。</summary>
        public void WarmFraction(double frac)
        {
            if (length <= 0 || double.IsNaN(frac)) return;
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            Warm((long)(frac * length));
        }

        /// <summary>用户跳到未缓存的位置：预读线程立刻从那里开始读。</summary>
        public void Warm(long bytePos)
        {
            if (!running || length <= 0) return;
            if (bytePos < 0) bytePos = 0;
            if (bytePos > length) bytePos = length;
            lock (gate) { playhead = bytePos; }   // 跳转即新的播放头，否则 want 用旧值会算错
            Interlocked.Exchange(ref jumpTo, bytePos);
        }

        /// <summary>取当前已预读区间（字节）。</summary>
        public bool TryGet(out long fromPos, out long toPos, out long total)
        {
            lock (gate)
            {
                fromPos = winFrom;
                toPos = winTo;
                total = length;
            }
            return total > 0 && toPos > fromPos;
        }

        public string Describe()
        {
            long from, to, total, read, head;
            lock (gate) { from = winFrom; to = winTo; total = length; read = bytesTotal; head = playhead; }
            if (total <= 0) return "cache{none}";
            return "cache{from=" + (from * 100.0 / total).ToString("0.00", Inv) +
                   "% to=" + (to * 100.0 / total).ToString("0.00", Inv) +
                   "% head=" + (total > 0 ? (head * 100.0 / total).ToString("0.00", Inv) : "?") +
                   "% rawHeadMB=" + (head >> 20) + " rawLenMB=" + (total >> 20) +
                   "% read=" + (read >> 20) + "MB run=" + (running ? 1 : 0) +
                   " it=" + iters + " rd=" + reads + " jp=" + jumps +
                   (lastErr == null ? "" : " err=" + lastErr) + "}";
        }

        void Loop()
        {
            var buf = new byte[ChunkSize];
            while (running)
            {
                iters++;
                try
                {
                    if (fs == null)
                    {
                        fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                        lock (gate) { winFrom = 0; winTo = 0; }
                    }

                    long jump = Interlocked.Exchange(ref jumpTo, -1);
                    if (jump >= 0 && fs.CanSeek)
                    {
                        fs.Position = jump;
                        lock (gate) { winFrom = jump; winTo = jump; playhead = jump; }
                    }

                    long from, to, head;
                    lock (gate) { from = winFrom; to = winTo; head = playhead; }

                    // 窗口跟播放头脱节就重新开窗：① 窗口整段在播放头后面（用户往前跳了）
                    // ② 窗口起点远在播放头前方（用户往回拖了）③ 窗口拉得太开
                    if (head > 0 && (to + ChunkSize <= head || from > head + SlabBytes || head - from > LookAheadBytes))
                    {
                        long p = head - (head % ChunkSize);
                        if (p < 0) p = 0;
                        fs.Position = p;
                        jumps++;
                        lock (gate) { winFrom = p; winTo = p; }
                        continue;
                    }

                    long basis = head > from ? head : from;   // 窗口起点可能已领先播放头
                    long want = basis + LookAheadBytes;
                    if (want > length) want = length;
                    if (to >= want)
                    {
                        Thread.Sleep(60);
                        continue;
                    }

                    reads++;
                    int n = fs.Read(buf, 0, ChunkSize);
                    if (n <= 0)
                    {
                        Thread.Sleep(150);
                        continue;
                    }
                    lock (gate)
                    {
                        if (winTo <= 0) winFrom = fs.Position - n;
                        winTo = fs.Position;
                        bytesTotal += n;
                    }
                    Thread.Sleep(PaceMs);
                }
                catch (Exception ex)
                {
                    lastErr = ex.GetType().Name + ":" + ex.Message.Replace('\n', ' ');
                    try { if (fs != null) fs.Dispose(); }
                    catch { }
                    fs = null;
                    Thread.Sleep(600);
                }
            }
        }
    }
}
