// TIFF-GIF-HOLO Generic Evaluator / Virtual Console Harness
// Cartridge-specific program, scenes, assets, state and documentation are NOT compiled here.
// This harness supplies only generic TIFF/GIF temporal carrier I/O, integrity checks, a bytecode VM,
// generic graphics/audio/input primitives, loopback console transport and authorized writeback.
// Target: .NET Framework 4.x / Windows 10-11, C# 5 compatible.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace TIFFHolo
{
    internal static class Diagnostics
    {
        private static readonly object Gate = new object();
        private static string logPath;
        private static int uiFaultShown;

        internal static void Init(string path)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path)) return;
                logPath = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(logPath);
                if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                Log("=== TIFF-GIF-HOLO session " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture) + " ===");
            }
            catch { logPath = null; }
        }

        internal static void Log(string message)
        {
            try
            {
                string line = DateTime.Now.ToString("o", CultureInfo.InvariantCulture) + "  " + (message ?? "");
                lock (Gate)
                {
                    if (!String.IsNullOrEmpty(logPath)) File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
                }
                try { Console.WriteLine("[TIFF-GIF-HOLO] " + (message ?? "")); } catch { }
            }
            catch { }
        }

        internal static void Log(Exception ex, string context)
        {
            Log((context ?? "FAULT") + Environment.NewLine + (ex == null ? "(null exception)" : ex.ToString()));
        }

        internal static void UiThreadFault(Exception ex)
        {
            Log(ex, "UI THREAD FAULT");
            if (Interlocked.Exchange(ref uiFaultShown, 1) != 0) return;
            try
            {
                MessageBox.Show("The evaluator caught a UI fault instead of terminating.\r\n\r\n" +
                    (ex == null ? "Unknown error" : ex.Message) +
                    "\r\n\r\nDetails were written to the runtime log.",
                    "TIFF-GIF-HOLO recovered fault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
            Interlocked.Exchange(ref uiFaultShown, 0);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            return Run(args);
        }

        internal static int Run(string[] args)
        {
            string requestedLog = null;
            for (int ai = 0; ai < args.Length; ai++) if (args[ai] == "--log" && ai + 1 < args.Length) requestedLog = args[ai + 1];
            Diagnostics.Init(requestedLog);
            Diagnostics.Log("Process start: " + Environment.CommandLine);
            try
            {
                if (args.Length >= 2 && args[0] == "--verify")
                {
                    Carrier c = CarrierCodec.Load(args[1], true);
                    Console.WriteLine("PASS TIFF-GIF-HOLO/4");
                    Console.WriteLine("pages=" + c.DataPageCount.ToString(CultureInfo.InvariantCulture));
                    Console.WriteLine("entries=" + c.Entries.Count.ToString(CultureInfo.InvariantCulture));
                    Console.WriteLine("sha256=" + Hex(CarrierCodec.FileHash(args[1])));
                    Console.WriteLine("stream=" + Hex(c.StreamHash));
                    return 0;
                }
                if (args.Length >= 2 && args[0] == "--smoke")
                {
                    Diagnostics.Log("SMOKE: loading image-resident runtime and presentation payload");
                    RuntimeEngine smokeEngine = new RuntimeEngine(Path.GetFullPath(args[1]), Path.GetFullPath(args[1]));
                    Snapshot smoke = smokeEngine.GetSnapshot();
                    if (smoke == null) throw new InvalidDataException("Runtime smoke test produced no snapshot");
                    Console.WriteLine("PASS RUNTIME-SMOKE");
                    Console.WriteLine("assets=" + smoke.Assets.Count.ToString(CultureInfo.InvariantCulture));
                    Console.WriteLine("tick=" + JsonUtil.Int(smoke.State.ContainsKey("tick") ? smoke.State["tick"] : null, 0).ToString(CultureInfo.InvariantCulture));
                    smokeEngine.Dispose();
                    return 0;
                }
                if (args.Length >= 2 && args[0] == "--inspect")
                {
                    Carrier c = CarrierCodec.Load(args[1], true);
                    Console.WriteLine("TIFF-GIF-HOLO/4 INSPECT");
                    foreach (CarrierEntry e in c.Entries.Values.OrderBy(x => x.Bank).ThenBy(x => x.Name))
                        Console.WriteLine(e.Bank.PadRight(22) + " " + e.Data.Length.ToString().PadLeft(9) + "  " + e.Name);
                    return 0;
                }
                if (args.Length >= 2 && args[0] == "--launch-mvp")
                {
                    Carrier c = CarrierCodec.Load(args[1], true);
                    Dictionary<string, object> meta = JsonUtil.Dict(c.Get("mvp/meta/ENTRYPOINT.json").Data);
                    string entryName = JsonUtil.Str(meta.ContainsKey("entrypoint") ? meta["entrypoint"] : null, "");
                    if (String.IsNullOrWhiteSpace(entryName)) throw new InvalidDataException("Image-resident entrypoint metadata is empty");
                    string mounted = CarrierMount.MaterializePrefix(c, "mvp/", null);
                    string relEntry = entryName.Replace('/', Path.DirectorySeparatorChar);
                    string entry = Path.GetFullPath(Path.Combine(mounted, relEntry));
                    string mountRoot = Path.GetFullPath(mounted).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!entry.StartsWith(mountRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Image-resident entrypoint escaped mount root");
                    if (!File.Exists(entry)) throw new FileNotFoundException("Image-resident MVP entrypoint was not materialized", entry);
                    Console.WriteLine("PASS HOLOGRAM-CONTAINED-MVP");
                    Console.WriteLine("stream=" + Hex(c.StreamHash));
                    Console.WriteLine("mount=" + mounted);
                    Console.WriteLine("entry=" + entry);
                    Process.Start(new ProcessStartInfo(entry) { UseShellExecute = true });
                    return 0;
                }
                if (args.Length >= 2 && args[0] == "--materialize-mvp")
                {
                    Carrier c = CarrierCodec.Load(args[1], true);
                    string target = args.Length >= 3 ? args[2] : null;
                    string mounted = CarrierMount.MaterializePrefix(c, "mvp/", target);
                    Console.WriteLine("PASS MVP-MATERIALIZE");
                    Console.WriteLine("mount=" + mounted);
                    return 0;
                }

                string carrier = null;
                string seed = null;
                bool noLoopback = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--carrier" && i + 1 < args.Length) carrier = args[++i];
                    else if (args[i] == "--seed" && i + 1 < args.Length) seed = args[++i];
                    else if (args[i] == "--log" && i + 1 < args.Length) { requestedLog = args[++i]; }
                    else if (args[i] == "--no-loopback") noLoopback = true;
                }
                if (String.IsNullOrEmpty(carrier))
                {
                    MessageBox.Show("BOOT.cmd must provide --carrier <current.tiff|current.gif>.", "TIFF-GIF-HOLO", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 64;
                }
                carrier = Path.GetFullPath(carrier);
                seed = String.IsNullOrEmpty(seed) ? carrier : Path.GetFullPath(seed);

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { Diagnostics.UiThreadFault(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Exception ux = e.ExceptionObject as Exception; Diagnostics.Log(ux ?? new Exception(Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture)), "APPDOMAIN UNHANDLED");
                };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Diagnostics.Log("BOOT-STAGE 1/5: authoritative carrier path resolved: " + carrier);
                Diagnostics.Log("BOOT-STAGE 2/5: decoding carrier and constructing image-resident runtime");
                RuntimeEngine engine = new RuntimeEngine(carrier, seed);
                Diagnostics.Log("BOOT-STAGE 3/5: runtime snapshot ready");
                SimulationForm form = new SimulationForm(engine, !noLoopback);
                Diagnostics.Log("BOOT-STAGE 4/5: WinForms shell constructed");
                Diagnostics.Log("BOOT-STAGE 5/5: entering GUI message loop");
                Application.Run(form);
                Diagnostics.Log("GUI message loop exited normally");
                engine.Dispose();
                return 0;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(ex, "FATAL STARTUP ERROR");
                try { MessageBox.Show(ex.ToString() + "\r\n\r\nSee the runtime log for details.", "TIFF-GIF-HOLO fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { try { Console.Error.WriteLine(ex.ToString()); } catch { } }
                return 2;
            }
        }

        internal static string Hex(byte[] b)
        {
            StringBuilder s = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) s.Append(b[i].ToString("x2", CultureInfo.InvariantCulture));
            return s.ToString();
        }
    }

    internal static class JsonUtil
    {
        private static readonly JavaScriptSerializer J;
        static JsonUtil()
        {
            J = new JavaScriptSerializer();
            J.MaxJsonLength = Int32.MaxValue;
            J.RecursionLimit = 512;
        }
        internal static Dictionary<string, object> Dict(byte[] b) { return Dict(Encoding.UTF8.GetString(b)); }
        internal static Dictionary<string, object> Dict(string s)
        {
            object o = J.DeserializeObject(s);
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) throw new InvalidDataException("JSON object required");
            return d;
        }
        internal static byte[] Bytes(object o) { return Encoding.UTF8.GetBytes(J.Serialize(o)); }
        internal static string Text(object o) { return J.Serialize(o); }
        internal static Dictionary<string, object> AsDict(object o)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) throw new InvalidDataException("JSON dictionary required");
            return d;
        }
        internal static List<object> AsList(object o)
        {
            List<object> r = new List<object>();
            if (o == null) return r;
            IEnumerable en = o as IEnumerable;
            if (en == null || o is string) return r;
            foreach (object x in en) r.Add(x);
            return r;
        }
        internal static double Num(object o, double fallback)
        {
            if (o == null) return fallback;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
        internal static int Int(object o, int fallback) { return (int)Math.Round(Num(o, fallback)); }
        internal static string Str(object o, string fallback) { return o == null ? fallback : Convert.ToString(o, CultureInfo.InvariantCulture); }
        internal static bool Bool(object o, bool fallback)
        {
            if (o == null) return fallback;
            bool b; if (Boolean.TryParse(Str(o, ""), out b)) return b;
            return Math.Abs(Num(o, fallback ? 1 : 0)) > 0.5;
        }
    }

    internal sealed class CarrierEntry
    {
        internal string Name;
        internal string Bank;
        internal byte[] Data;
        internal CarrierEntry Clone() { return new CarrierEntry { Name = Name, Bank = Bank, Data = (byte[])Data.Clone() }; }
    }

    internal sealed class Carrier
    {
        internal Dictionary<string, CarrierEntry> Entries = new Dictionary<string, CarrierEntry>(StringComparer.Ordinal);
        internal byte[] StreamHash;
        internal byte[] CartridgeId;
        internal int DataPageCount;
        internal long TickHeader;
        internal long FileBytes;
        internal double DecodeMs;
        internal double VerifyMs;
        internal string ContainerFormat;
        internal int FrameCount;
        internal CarrierEntry Get(string name)
        {
            CarrierEntry e;
            if (!Entries.TryGetValue(name, out e)) throw new InvalidDataException("Carrier entry missing: " + name);
            return e;
        }
    }

    internal static class CarrierMount
    {
        internal static string MaterializePrefix(Carrier c, string prefix, string requestedRoot)
        {
            if (c == null) throw new ArgumentNullException("c");
            if (String.IsNullOrEmpty(prefix)) throw new ArgumentException("prefix required");
            string tag = c.StreamHash == null ? "unknown" : Program.Hex(c.StreamHash).Substring(0, 16);
            string root = String.IsNullOrWhiteSpace(requestedRoot) ? Path.Combine(Path.GetTempPath(), "Lunar127_Hologram_MVP_" + tag) : Path.GetFullPath(requestedRoot);
            Directory.CreateDirectory(root);
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            int count = 0;
            foreach (CarrierEntry e in c.Entries.Values.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (!e.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string rel = e.Name.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(rel) || rel.IndexOf(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) >= 0) throw new InvalidDataException("Unsafe image-resident path: " + e.Name);
                string dst = Path.GetFullPath(Path.Combine(root, rel));
                if (!dst.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Image-resident path escaped mount root: " + e.Name);
                string dir = Path.GetDirectoryName(dst); if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(dst, e.Data); count++;
            }
            if (count < 1) throw new InvalidDataException("No image-resident entries matched prefix: " + prefix);
            string proof = "source_stream_sha256=" + (c.StreamHash == null ? "" : Program.Hex(c.StreamHash)) + Environment.NewLine +
                           "materialized_utc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine +
                           "entries=" + count.ToString(CultureInfo.InvariantCulture) + Environment.NewLine;
            File.WriteAllText(Path.Combine(root, "_HOLOGRAM_MOUNT_PROVENANCE.txt"), proof, Encoding.UTF8);
            return root;
        }
    }

    internal static class CarrierCodec
    {
        private static readonly byte[] PageMagic = new byte[] { (byte)'T',(byte)'G',(byte)'P',(byte)'G',(byte)'4',0,0,0 };
        private static readonly byte[] ArchMagic = new byte[] { (byte)'T',(byte)'H',(byte)'A',(byte)'R',(byte)'C',(byte)'H',(byte)'3',0 };
        internal const int PageSize = 512;
        internal const int HeaderSize = 256;
        internal const int ChunkMax = PageSize * PageSize - HeaderSize;

        internal static byte[] FileHash(string path)
        {
            using (SHA256 s = SHA256.Create()) using (FileStream f = File.OpenRead(path)) return s.ComputeHash(f);
        }
        private static byte[] Hash(byte[] b) { using (SHA256 s = SHA256.Create()) return s.ComputeHash(b); }
        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int v = 0; for (int i = 0; i < a.Length; i++) v |= a[i] ^ b[i]; return v == 0;
        }
        private static ushort U16(byte[] b, int p) { return BitConverter.ToUInt16(b, p); }
        private static uint U32(byte[] b, int p) { return BitConverter.ToUInt32(b, p); }
        private static ulong U64(byte[] b, int p) { return BitConverter.ToUInt64(b, p); }
        private static bool Starts(byte[] b, byte[] magic)
        {
            if (b.Length < magic.Length) return false;
            for (int i = 0; i < magic.Length; i++) if (b[i] != magic[i]) return false;
            return true;
        }
        private static string DetectFormat(byte[] file)
        {
            if (file.Length >= 6 && file[0] == (byte)'G' && file[1] == (byte)'I' && file[2] == (byte)'F') return "GIF";
            if (file.Length >= 4 && ((file[0] == (byte)'I' && file[1] == (byte)'I') || (file[0] == (byte)'M' && file[1] == (byte)'M'))) return "TIFF";
            throw new InvalidDataException("Carrier must be a TIFF or GIF image");
        }
        private static byte PhaseMask(int pos, int page, long tick)
        {
            int x = pos % PageSize; int y = pos / PageSize;
            long q = (x + 3L * y + 37L * page + 3L * (tick & 0xffffffffL)) & 63L;
            long tri = q < 32 ? q : 63 - q;
            return (byte)((tri * 8L + 11L * y + 3L * (x ^ y)) & 255L);
        }

        internal static Carrier Load(string path, bool verifyImmutable)
        {
            Stopwatch all = Stopwatch.StartNew();
            byte[] file = File.ReadAllBytes(path); string format = DetectFormat(file);
            Dictionary<int, Tuple<int, byte[]>> chunks = new Dictionary<int, Tuple<int, byte[]>>();
            int pageCount = -1; int streamLength = -1; byte[] streamHash = null; byte[] cartridgeId = null; long tick = 0;
            byte[] expectedPrev = new byte[32]; int expectedIndex = 0; int frameCount = 0;
            using (MemoryStream ms = new MemoryStream(file, false))
            using (Image image = Image.FromStream(ms, true, true))
            {
                FrameDimension dim = new FrameDimension(image.FrameDimensionsList[0]);
                int frames = image.GetFrameCount(dim); frameCount = frames;
                for (int f = 0; f < frames; f++)
                {
                    image.SelectActiveFrame(dim, f);
                    using (Bitmap src = new Bitmap(image))
                    using (Bitmap bmp = src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format24bppRgb))
                    {
                        if (bmp.Width != PageSize || bmp.Height != PageSize) continue;
                        byte[] symbols = BitmapToSymbols(bmp);
                        if (!Starts(symbols, PageMagic)) continue;
                        ushort ver = U16(symbols, 8); int idx = U16(symbols, 10); int pc = U16(symbols, 12);
                        int pageSize = (int)U32(symbols, 16); int sl = (int)U32(symbols, 20); int off = (int)U32(symbols, 24); int plen = (int)U32(symbols, 28);
                        long thisTick = (long)U64(symbols, 112);
                        if (ver != 4 || pageSize != PageSize || idx < 0 || idx >= pc || plen < 0 || plen > ChunkMax || HeaderSize + plen > symbols.Length)
                            throw new InvalidDataException("TIFF-GIF page header rejected");
                        if (idx != expectedIndex) throw new InvalidDataException("Temporal frame order rejected at data page " + idx.ToString());
                        byte[] sh = Slice(symbols, 32, 32); byte[] ph = Slice(symbols, 64, 32); byte[] cid = Slice(symbols, 96, 16); byte[] prev = Slice(symbols, 128, 32);
                        if (!Same(prev, expectedPrev)) throw new InvalidDataException("Temporal frame chain mismatch at data page " + idx.ToString());
                        byte[] payload = new byte[plen];
                        for (int j = 0; j < plen; j++) payload[j] = (byte)(symbols[HeaderSize + j] ^ PhaseMask(HeaderSize + j, idx, thisTick));
                        if (!Same(Hash(payload), ph)) throw new InvalidDataException("Page payload hash mismatch: " + idx.ToString());
                        if (pageCount < 0) { pageCount = pc; streamLength = sl; streamHash = sh; cartridgeId = cid; tick = thisTick; }
                        if (pc != pageCount || sl != streamLength || !Same(sh, streamHash) || !Same(cid, cartridgeId) || thisTick != tick)
                            throw new InvalidDataException("Temporal page set consistency failure");
                        if (chunks.ContainsKey(idx)) throw new InvalidDataException("Duplicate page " + idx.ToString());
                        chunks[idx] = Tuple.Create(off, payload); expectedPrev = ph; expectedIndex++;
                    }
                }
            }
            if (pageCount <= 0 || chunks.Count != pageCount || expectedIndex != pageCount) throw new InvalidDataException("Incomplete TIFF-GIF temporal page set");
            byte[] stream = new byte[streamLength];
            for (int i = 0; i < pageCount; i++)
            {
                Tuple<int, byte[]> t;
                if (!chunks.TryGetValue(i, out t)) throw new InvalidDataException("Missing page " + i.ToString());
                if (t.Item1 < 0 || t.Item1 + t.Item2.Length > stream.Length) throw new InvalidDataException("Page range rejected");
                Buffer.BlockCopy(t.Item2, 0, stream, t.Item1, t.Item2.Length);
            }
            if (!Same(Hash(stream), streamHash)) throw new InvalidDataException("Archive stream hash mismatch");
            double decodeMs = all.Elapsed.TotalMilliseconds;
            Stopwatch verify = Stopwatch.StartNew();
            Carrier c = ParseArchive(stream);
            c.StreamHash = streamHash; c.CartridgeId = cartridgeId; c.DataPageCount = pageCount; c.TickHeader = tick; c.FileBytes = file.Length; c.DecodeMs = decodeMs; c.ContainerFormat = format; c.FrameCount = frameCount;
            if (verifyImmutable) VerifyImmutable(c);
            c.VerifyMs = verify.Elapsed.TotalMilliseconds;
            return c;
        }

        private static Carrier ParseArchive(byte[] stream)
        {
            if (!Starts(stream, ArchMagic)) throw new InvalidDataException("Archive magic rejected");
            int p = 8; uint count = U32(stream, p); p += 4;
            if (count > 4096) throw new InvalidDataException("Entry count rejected");
            Carrier c = new Carrier();
            for (int n = 0; n < count; n++)
            {
                if (p + 56 > stream.Length) throw new EndOfStreamException();
                int nl = U16(stream, p); int bl = U16(stream, p + 2); uint flags = U32(stream, p + 4); ulong olen = U64(stream, p + 8); ulong plen = U64(stream, p + 16); p += 24;
                byte[] expected = Slice(stream, p, 32); p += 32;
                if (nl < 1 || nl > 1024 || bl < 1 || bl > 256 || olen > 128UL * 1024UL * 1024UL || plen > 128UL * 1024UL * 1024UL) throw new InvalidDataException("Entry bounds rejected");
                if ((ulong)p + (ulong)nl + (ulong)bl + plen > (ulong)stream.Length) throw new EndOfStreamException();
                string name = Encoding.UTF8.GetString(stream, p, nl); p += nl; string bank = Encoding.UTF8.GetString(stream, p, bl); p += bl;
                byte[] payload = Slice(stream, p, checked((int)plen)); p += checked((int)plen);
                byte[] data = (flags & 1) != 0 ? Gunzip(payload, checked((int)olen)) : payload;
                if ((ulong)data.Length != olen || !Same(Hash(data), expected)) throw new InvalidDataException("Entry hash mismatch: " + name);
                if (c.Entries.ContainsKey(name)) throw new InvalidDataException("Duplicate entry: " + name);
                c.Entries.Add(name, new CarrierEntry { Name = name, Bank = bank, Data = data });
            }
            if (p != stream.Length) throw new InvalidDataException("Trailing archive bytes rejected");
            return c;
        }

        internal static void VerifyImmutable(Carrier c)
        {
            Dictionary<string, object> root = JsonUtil.Dict(c.Get("integrity/immutable.json").Data);
            Dictionary<string, object> hashes = JsonUtil.AsDict(root["sha256"]);
            foreach (KeyValuePair<string, object> kv in hashes)
            {
                CarrierEntry e = c.Get(kv.Key);
                string got = Program.Hex(Hash(e.Data));
                if (!String.Equals(got, JsonUtil.Str(kv.Value, ""), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Immutable bank mutation: " + kv.Key);
            }
        }

        internal static void Save(string path, Carrier c, long tick)
        {
            byte[] stream = BuildArchive(c); byte[] sh = Hash(stream);
            int count = (stream.Length + ChunkMax - 1) / ChunkMax;
            if (count < 1 || count > UInt16.MaxValue) throw new InvalidDataException("Temporal page count exceeds format limit");
            List<byte[]> frames = new List<byte[]>(); byte[] prev = new byte[32]; byte[] spec = Hash(Encoding.ASCII.GetBytes("TIFF-GIF-HOLO/4 triangle-xor-v1"));
            for (int idx = 0; idx < count; idx++)
            {
                int off = idx * ChunkMax; int plen = Math.Min(ChunkMax, stream.Length - off); byte[] payload = Slice(stream, off, plen);
                byte[] symbols = new byte[PageSize * PageSize];
                Buffer.BlockCopy(PageMagic, 0, symbols, 0, 8);
                PutU16(symbols, 8, 4); PutU16(symbols, 10, (ushort)idx); PutU16(symbols, 12, (ushort)count); PutU16(symbols, 14, 1);
                PutU32(symbols, 16, PageSize); PutU32(symbols, 20, stream.Length); PutU32(symbols, 24, off); PutU32(symbols, 28, plen);
                Buffer.BlockCopy(sh, 0, symbols, 32, 32); byte[] ph = Hash(payload); Buffer.BlockCopy(ph, 0, symbols, 64, 32);
                byte[] cid = c.CartridgeId == null || c.CartridgeId.Length != 16 ? new byte[16] : c.CartridgeId;
                Buffer.BlockCopy(cid, 0, symbols, 96, 16); PutU64(symbols, 112, (ulong)Math.Max(0, tick));
                byte[] marker = Encoding.ASCII.GetBytes("TGIFv4\0\0"); Buffer.BlockCopy(marker, 0, symbols, 120, 8); Buffer.BlockCopy(prev, 0, symbols, 128, 32); Buffer.BlockCopy(spec, 0, symbols, 160, 32);
                byte[] tag = Encoding.ASCII.GetBytes("TIFF-GIF-HOLO/4"); Buffer.BlockCopy(tag, 0, symbols, 192, tag.Length);
                for (int pos = HeaderSize; pos < symbols.Length; pos++)
                {
                    byte raw = pos < HeaderSize + plen ? payload[pos - HeaderSize] : (byte)0;
                    symbols[pos] = (byte)(raw ^ PhaseMask(pos, idx, tick));
                }
                frames.Add(symbols); prev = ph;
            }
            string tmp = path + ".write"; if (File.Exists(tmp)) File.Delete(tmp);
            string format = String.IsNullOrEmpty(c.ContainerFormat) ? (Path.GetExtension(path).ToLowerInvariant().Contains("gif") ? "GIF" : "TIFF") : c.ContainerFormat;
            if (String.Equals(format, "GIF", StringComparison.OrdinalIgnoreCase)) SaveAnimatedGifCore(tmp, CoverSymbols(c), frames);
            else SaveMultipageTiffCore(tmp, c, frames);
            Carrier check = Load(tmp, true);
            if (check.DataPageCount != frames.Count) { File.Delete(tmp); throw new InvalidDataException("Writeback page count verification failed"); }
            if (File.Exists(path)) File.Delete(path); File.Move(tmp, path);
        }

        private static byte[] BuildArchive(Carrier c)
        {
            using (MemoryStream ms = new MemoryStream()) using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                w.Write(ArchMagic); List<CarrierEntry> es = c.Entries.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToList(); w.Write((uint)es.Count);
                foreach (CarrierEntry e in es)
                {
                    byte[] name = Encoding.UTF8.GetBytes(e.Name); byte[] bank = Encoding.UTF8.GetBytes(e.Bank); byte[] gz = Gzip(e.Data); bool compressed = gz.Length < e.Data.Length; byte[] payload = compressed ? gz : e.Data;
                    w.Write((ushort)name.Length); w.Write((ushort)bank.Length); w.Write((uint)(compressed ? 1 : 0)); w.Write((ulong)e.Data.Length); w.Write((ulong)payload.Length); w.Write(Hash(e.Data)); w.Write(name); w.Write(bank); w.Write(payload);
                }
                w.Flush(); return ms.ToArray();
            }
        }

        private static Bitmap CoverBitmap(Carrier c)
        {
            byte[] data = c.Get("assets/cartridge-cover.png").Data;
            using (MemoryStream ms = new MemoryStream(data, false)) using (Image i = Image.FromStream(ms, true, true))
            {
                Bitmap b = new Bitmap(PageSize, PageSize, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.Black); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(i, new Rectangle(0, 0, PageSize, PageSize)); }
                return b;
            }
        }
        private static byte[] CoverSymbols(Carrier c)
        {
            using (Bitmap bmp = CoverBitmap(c))
            {
                Rectangle r = new Rectangle(0, 0, PageSize, PageSize); BitmapData d = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int stride = Math.Abs(d.Stride); byte[] row = new byte[stride]; byte[] symbols = new byte[PageSize * PageSize]; int k = 0;
                    for (int y = 0; y < PageSize; y++)
                    {
                        IntPtr ptr = IntPtr.Add(d.Scan0, y * d.Stride); System.Runtime.InteropServices.Marshal.Copy(ptr, row, 0, stride);
                        for (int x = 0; x < PageSize; x++) { int p = x * 3; int b = row[p], g = row[p + 1], rr = row[p + 2]; symbols[k++] = (byte)((30 * rr + 59 * g + 11 * b) / 100); }
                    }
                    return symbols;
                }
                finally { bmp.UnlockBits(d); }
            }
        }
        private static void SaveMultipageTiffCore(string path, Carrier c, List<byte[]> frames)
        {
            List<Bitmap> pages = new List<Bitmap>();
            try
            {
                pages.Add(CoverBitmap(c)); foreach (byte[] f in frames) pages.Add(SymbolsToBitmap(f));
                ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(x => x.MimeType == "image/tiff");
                try { SaveTiffPages(path, pages, codec, EncoderValue.CompressionLZW); }
                catch { if (File.Exists(path)) File.Delete(path); SaveTiffPages(path, pages, codec, EncoderValue.CompressionNone); }
            }
            finally { foreach (Bitmap b in pages) b.Dispose(); }
        }
        private static void SaveTiffPages(string path, List<Bitmap> pages, ImageCodecInfo codec, EncoderValue compression)
        {
            EncoderParameters first = new EncoderParameters(2);
            first.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
            first.Param[1] = new EncoderParameter(System.Drawing.Imaging.Encoder.Compression, (long)compression);
            pages[0].Save(path, codec, first);
            EncoderParameters add = new EncoderParameters(2);
            add.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
            add.Param[1] = new EncoderParameter(System.Drawing.Imaging.Encoder.Compression, (long)compression);
            for (int i = 1; i < pages.Count; i++) pages[0].SaveAdd(pages[i], add);
            EncoderParameters flush = new EncoderParameters(1);
            flush.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.Flush);
            pages[0].SaveAdd(flush);
        }

        private static byte[] PaletteBytes()
        {
            byte[] p = new byte[256 * 3];
            for (int i = 0; i < 256; i++) { p[i * 3] = (byte)i; p[i * 3 + 1] = (byte)Math.Min(255, i + 40); p[i * 3 + 2] = (byte)Math.Min(255, i + 90); }
            return p;
        }
        private static void SaveAnimatedGifCore(string path, byte[] cover, List<byte[]> frames)
        {
            using (FileStream fs = File.Create(path)) using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write(Encoding.ASCII.GetBytes("GIF89a")); w.Write((ushort)PageSize); w.Write((ushort)PageSize); w.Write((byte)0xF7); w.Write((byte)0); w.Write((byte)0); w.Write(PaletteBytes());
                w.Write(new byte[] { 0x21, 0xFF, 0x0B }); w.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0")); w.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
                List<byte[]> all = new List<byte[]>(); all.Add(cover); all.AddRange(frames);
                for (int i = 0; i < all.Count; i++)
                {
                    ushort delay = (ushort)(i == 0 ? 90 : 11);
                    w.Write(new byte[] { 0x21, 0xF9, 0x04, 0x00 }); w.Write(delay); w.Write((byte)0); w.Write((byte)0);
                    w.Write((byte)0x2C); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)PageSize); w.Write((ushort)PageSize); w.Write((byte)0);
                    w.Write((byte)8); byte[] compressed = LiteralGifLzw(all[i]);
                    int p = 0; while (p < compressed.Length) { int n = Math.Min(255, compressed.Length - p); w.Write((byte)n); w.Write(compressed, p, n); p += n; } w.Write((byte)0);
                }
                w.Write((byte)0x3B);
            }
        }
        private static byte[] LiteralGifLzw(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                long buf = 0; int bits = 0; Action<int> emit = delegate(int code)
                {
                    buf |= ((long)code << bits); bits += 9;
                    while (bits >= 8) { ms.WriteByte((byte)(buf & 255)); buf >>= 8; bits -= 8; }
                };
                emit(256); int n = 0;
                for (int i = 0; i < data.Length; i++) { emit(data[i]); n++; if (n >= 200) { emit(256); n = 0; } }
                emit(257); if (bits > 0) ms.WriteByte((byte)(buf & 255)); return ms.ToArray();
            }
        }

        private static byte[] Gzip(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (GZipStream z = new GZipStream(ms, CompressionMode.Compress, true)) z.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }
        private static byte[] Gunzip(byte[] data, int expected)
        {
            using (MemoryStream input = new MemoryStream(data, false)) using (GZipStream z = new GZipStream(input, CompressionMode.Decompress)) using (MemoryStream outp = new MemoryStream(expected))
            { z.CopyTo(outp); byte[] b = outp.ToArray(); if (b.Length != expected) throw new InvalidDataException("GZip length mismatch"); return b; }
        }
        private static byte[] Slice(byte[] b, int p, int n) { byte[] r = new byte[n]; Buffer.BlockCopy(b, p, r, 0, n); return r; }
        private static void PutU16(byte[] b, int p, ushort v) { byte[] x = BitConverter.GetBytes(v); Buffer.BlockCopy(x, 0, b, p, 2); }
        private static void PutU32(byte[] b, int p, int v) { byte[] x = BitConverter.GetBytes((uint)v); Buffer.BlockCopy(x, 0, b, p, 4); }
        private static void PutU64(byte[] b, int p, ulong v) { byte[] x = BitConverter.GetBytes(v); Buffer.BlockCopy(x, 0, b, p, 8); }

        private static byte[] BitmapToSymbols(Bitmap bmp)
        {
            Rectangle r = new Rectangle(0, 0, bmp.Width, bmp.Height); BitmapData d = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = Math.Abs(d.Stride); byte[] row = new byte[stride]; byte[] symbols = new byte[bmp.Width * bmp.Height]; int k = 0;
                for (int y = 0; y < bmp.Height; y++)
                {
                    IntPtr ptr = IntPtr.Add(d.Scan0, y * d.Stride); System.Runtime.InteropServices.Marshal.Copy(ptr, row, 0, stride);
                    for (int x = 0; x < bmp.Width; x++) symbols[k++] = row[x * 3 + 2];
                }
                return symbols;
            }
            finally { bmp.UnlockBits(d); }
        }
        private static Bitmap SymbolsToBitmap(byte[] symbols)
        {
            Bitmap bmp = new Bitmap(PageSize, PageSize, PixelFormat.Format24bppRgb); Rectangle r = new Rectangle(0, 0, PageSize, PageSize); BitmapData d = bmp.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = Math.Abs(d.Stride); byte[] row = new byte[stride]; int k = 0;
                for (int y = 0; y < PageSize; y++)
                {
                    Array.Clear(row, 0, row.Length);
                    for (int x = 0; x < PageSize; x++)
                    {
                        int p = x * 3; int v = symbols[k++]; row[p + 2] = (byte)v; row[p + 1] = (byte)Math.Min(255, v + 40); row[p] = (byte)Math.Min(255, v + 90);
                    }
                    IntPtr ptr = IntPtr.Add(d.Scan0, y * d.Stride); System.Runtime.InteropServices.Marshal.Copy(row, 0, ptr, stride);
                }
            }
            finally { bmp.UnlockBits(d); }
            return bmp;
        }
    }


    internal sealed class VmInstruction
    {
        internal byte Op, A, B, C; internal int I, Target, Pad; internal double F;
    }
    internal sealed class VmProgram
    {
        internal List<string> Registers = new List<string>();
        internal List<string> Strings = new List<string>();
        internal List<VmInstruction> Code = new List<VmInstruction>();
        internal static VmProgram Load(Carrier c)
        {
            byte[] b = c.Get("program/mission.vm").Data;
            if (b.Length < 16 || Encoding.ASCII.GetString(b, 0, 5) != "THVM3") throw new InvalidDataException("VM header rejected");
            ushort ver = BitConverter.ToUInt16(b, 8); ushort regs = BitConverter.ToUInt16(b, 10); int count = checked((int)BitConverter.ToUInt32(b, 12));
            if (ver != 3 || regs > 256 || count < 1 || count > 100000 || 16 + count * 24 != b.Length) throw new InvalidDataException("VM bounds rejected");
            VmProgram p = new VmProgram();
            Dictionary<string, object> sy = JsonUtil.Dict(c.Get("program/symbols.json").Data); foreach (object x in JsonUtil.AsList(sy["registers"])) p.Registers.Add(JsonUtil.Str(x, ""));
            Dictionary<string, object> st = JsonUtil.Dict(c.Get("program/strings.json").Data); foreach (object x in JsonUtil.AsList(st["strings"])) p.Strings.Add(JsonUtil.Str(x, ""));
            if (p.Registers.Count != regs) throw new InvalidDataException("VM symbol/register mismatch");
            using (BinaryReader r = new BinaryReader(new MemoryStream(b, false)))
            {
                r.BaseStream.Position = 16;
                for (int n = 0; n < count; n++) p.Code.Add(new VmInstruction { Op = r.ReadByte(), A = r.ReadByte(), B = r.ReadByte(), C = r.ReadByte(), I = r.ReadInt32(), F = r.ReadDouble(), Target = r.ReadInt32(), Pad = r.ReadInt32() });
            }
            return p;
        }
    }
    internal sealed class VmResult { internal int Instructions; internal List<string> Events = new List<string>(); }
    internal static class Vm
    {
        internal static VmResult Execute(VmProgram p, Dictionary<string, object> state, double dt)
        {
            Dictionary<string, object> n = JsonUtil.AsDict(state["n"]); Dictionary<string, int> ix = new Dictionary<string, int>(StringComparer.Ordinal); double[] r = new double[p.Registers.Count];
            for (int i = 0; i < p.Registers.Count; i++) { ix[p.Registers[i]] = i; object v; r[i] = n.TryGetValue(p.Registers[i], out v) ? JsonUtil.Num(v, 0) : 0; }
            int dti; if (ix.TryGetValue("dt", out dti)) r[dti] = dt;
            VmResult result = new VmResult(); int pc = 0;
            while (pc >= 0 && pc < p.Code.Count)
            {
                VmInstruction q = p.Code[pc++]; result.Instructions++; if (result.Instructions > 100000) throw new InvalidDataException("VM instruction budget exceeded");
                if (q.A >= r.Length || q.B >= r.Length || q.C >= r.Length) { if (q.Op != 21 && q.Op != 24 && q.Op != 25) throw new InvalidDataException("VM register bounds"); }
                switch (q.Op)
                {
                    case 0: break;
                    case 1: r[q.A] = q.F; break;
                    case 2: r[q.A] = r[q.B]; break;
                    case 3: r[q.A] = r[q.B] + r[q.C]; break;
                    case 4: r[q.A] = r[q.B] - r[q.C]; break;
                    case 5: r[q.A] = r[q.B] * r[q.C]; break;
                    case 6: r[q.A] = Math.Abs(r[q.C]) < 1e-15 ? 0 : r[q.B] / r[q.C]; break;
                    case 7: r[q.A] = Math.Min(r[q.B], r[q.C]); break;
                    case 8: r[q.A] = Math.Max(r[q.B], r[q.C]); break;
                    case 9: r[q.A] = Math.Max(0, Math.Min(1, r[q.B])); break;
                    case 10: r[q.A] = Math.Sin(r[q.B]); break;
                    case 11: r[q.A] = Math.Cos(r[q.B]); break;
                    case 12: r[q.A] = Math.Sqrt(Math.Max(0, r[q.B])); break;
                    case 13: r[q.A] = Math.Abs(r[q.B]); break;
                    case 14: r[q.A] = r[q.B] < r[q.C] ? 1 : 0; break;
                    case 15: r[q.A] = r[q.B] <= r[q.C] ? 1 : 0; break;
                    case 16: r[q.A] = r[q.B] > r[q.C] ? 1 : 0; break;
                    case 17: r[q.A] = r[q.B] >= r[q.C] ? 1 : 0; break;
                    case 18: r[q.A] = Math.Abs(Math.Round(r[q.B]) - Math.Round(r[q.C])) < 0.5 ? 1 : 0; break;
                    case 19: if (Math.Abs(r[q.A]) < 0.5) pc = q.Target; break;
                    case 20: if (Math.Abs(r[q.A]) >= 0.5) pc = q.Target; break;
                    case 21: pc = q.Target; break;
                    case 22: if (q.I < 0 || q.I >= r.Length) throw new InvalidDataException("VM LERP register"); r[q.A] = r[q.B] + (r[q.C] - r[q.B]) * r[q.I]; break;
                    case 23: { double x = Math.Max(0, Math.Min(1, r[q.B])); r[q.A] = x * x * (3 - 2 * x); } break;
                    case 24: result.Events.Add(q.I >= 0 && q.I < p.Strings.Count ? p.Strings[q.I] : "EVENT"); break;
                    case 25: pc = p.Code.Count; break;
                    case 26: r[q.A] = r[q.B] + q.F; break;
                    case 27: r[q.A] = r[q.B] * q.F; break;
                    case 28: r[q.A] = q.I; break;
                    default: throw new InvalidDataException("Unknown VM opcode " + q.Op.ToString());
                }
                if (Double.IsNaN(r[q.A]) || Double.IsInfinity(r[q.A])) throw new InvalidDataException("VM non-finite state");
            }
            for (int i = 0; i < p.Registers.Count; i++) n[p.Registers[i]] = r[i];
            return result;
        }
    }

    internal sealed class RuntimeMetrics
    {
        private readonly object gate = new object();
        internal double DecodeMs, VerifyMs, VmMs, EncodeMs, CommitMs, CheckpointMs, RenderFps;
        internal double PagesPerSec, DecodeMBps, VmInstrPerSec, TransactionsPerSec, VerifyMBps, CompressionRatio;
        internal long CarrierBytes, TotalInstructions, TotalQuanta, DataPages;
        internal string Stage = "IDLE";
        internal void SetStage(string s) { lock (gate) Stage = s; }
        internal RuntimeMetrics Snapshot()
        {
            lock (gate) return (RuntimeMetrics)MemberwiseClone();
        }
        internal void UpdateCycle(Carrier c, int instr, double vmMs, double encodeMs, double totalMs, long newBytes)
        {
            lock (gate)
            {
                DecodeMs = c.DecodeMs; VerifyMs = c.VerifyMs; VmMs = vmMs; EncodeMs = encodeMs; CommitMs = totalMs; CarrierBytes = newBytes; DataPages = c.DataPageCount;
                PagesPerSec = c.DecodeMs > 0 ? c.DataPageCount * 1000.0 / c.DecodeMs : 0;
                DecodeMBps = c.DecodeMs > 0 ? (c.FileBytes / 1048576.0) / (c.DecodeMs / 1000.0) : 0;
                VerifyMBps = c.VerifyMs > 0 ? (c.FileBytes / 1048576.0) / (c.VerifyMs / 1000.0) : 0;
                VmInstrPerSec = vmMs > 0 ? instr * 1000.0 / vmMs : 0;
                TransactionsPerSec = totalMs > 0 ? 1000.0 / totalMs : 0;
                CompressionRatio = newBytes > 0 ? (c.DataPageCount * CarrierCodec.PageSize * CarrierCodec.PageSize * 3.0) / newBytes : 0;
                TotalInstructions += instr; TotalQuanta++; Stage = "IDLE";
            }
        }
        internal void SetFps(double fps) { lock (gate) RenderFps = fps; }
        internal void SetCheckpoint(double ms) { lock (gate) CheckpointMs = ms; }
    }

    internal sealed class Snapshot
    {
        internal Dictionary<string, object> State;
        internal Carrier Carrier;
        internal Dictionary<string, object> Scene;
        internal Dictionary<string, object> Hud;
        internal Dictionary<string, object> Phases;
        internal Dictionary<string, object> Banks;
        internal Dictionary<string, object> TerminalConfig;
        internal Dictionary<string, Image> Assets;
        internal byte[] EngineWave;
        internal string StateJson;
    }

    internal sealed class CommandOutcome
    {
        internal string SaveCheckpoint;
        internal string LoadCheckpoint;
        internal bool Reset;
    }

    internal sealed class RuntimeEngine : IDisposable
    {
        private readonly object gate = new object();
        private int busy;
        private int started;
        private System.Threading.Timer quantumTimer;
        private volatile bool disposed;
        private string draft = "";
        private Dictionary<string, Image> assetCache;
        private byte[] engineWaveCache;
        internal readonly string CarrierPath;
        internal readonly string SeedPath;
        internal readonly string Workspace;
        private string ActiveExtension { get { return String.Equals(Path.GetExtension(CarrierPath), ".gif", StringComparison.OrdinalIgnoreCase) ? ".gif" : ".tiff"; } }
        internal readonly RuntimeMetrics Metrics = new RuntimeMetrics();
        internal Snapshot Current;
        internal event Action SnapshotChanged;
        internal event Action<string> Fatal;

        internal RuntimeEngine(string carrierPath, string seedPath)
        {
            CarrierPath = carrierPath; SeedPath = seedPath; Workspace = Path.GetDirectoryName(carrierPath);
            Directory.CreateDirectory(Workspace); Directory.CreateDirectory(Path.Combine(Workspace, "checkpoints"));
            LoadInitial();
        }
        internal void Start()
        {
            if (disposed) return;
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            quantumTimer = new System.Threading.Timer(QuantumTick, null, 750, 500);
            Diagnostics.Log("Image quantum scheduler started after GUI became visible");
        }
        private void LoadInitial()
        {
            Carrier c = CarrierCodec.Load(CarrierPath, true); Publish(BuildSnapshot(c));
        }
        internal void SetDraft(string text) { draft = text == null ? "" : text; }
        private void QuantumTick(object ignored)
        {
            if (disposed) return;
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            try { CommitQuantum(null); }
            catch (Exception ex) { SetError(ex); }
            finally { Interlocked.Exchange(ref busy, 0); }
        }
        internal void QueueCommand(string command)
        {
            if (String.IsNullOrWhiteSpace(command)) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (Interlocked.CompareExchange(ref busy, 1, 0) != 0) Thread.Sleep(20);
                try { CommitQuantum(command); }
                catch (Exception ex) { SetError(ex); }
                finally { Interlocked.Exchange(ref busy, 0); }
            });
        }
        private void SetError(Exception ex)
        {
            Diagnostics.Log(ex, "RUNTIME QUANTUM FAULT");
            Metrics.SetStage("HALTED");
            try { if (quantumTimer != null) quantumTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            Snapshot s = Current;
            if (s != null && s.State != null)
            {
                try { s.State["lastError"] = ex.Message; AppendScroll(s.State, "[FAULT] " + ex.Message); }
                catch { }
            }
            Action<string> f = Fatal; if (f != null) f(ex.Message);
        }

        private void CommitQuantum(string command)
        {
            Stopwatch cycle = Stopwatch.StartNew(); Metrics.SetStage("DECODE"); Carrier c = CarrierCodec.Load(CarrierPath, true); Metrics.SetStage("VALIDATE");
            Dictionary<string, object> state = JsonUtil.Dict(c.Get("state/machine.json").Data);
            Dictionary<string, object> n = JsonUtil.AsDict(state["n"]);
            Dictionary<string, object> term = JsonUtil.AsDict(state["terminal"]); term["draft"] = draft;
            CommandOutcome outcome = new CommandOutcome();
            if (!String.IsNullOrEmpty(command))
            {
                string loadName = DetectCheckpointLoad(command, c);
                if (loadName != null)
                {
                    string cp = Path.Combine(Workspace, "checkpoints", loadName + ActiveExtension);
                    if (!File.Exists(cp)) { AppendScroll(state, "checkpoint not found: " + loadName); }
                    else
                    {
                        File.Copy(cp, CarrierPath + ".load", true); Carrier loadCheck = CarrierCodec.Load(CarrierPath + ".load", true);
                        if (File.Exists(CarrierPath + ".previous")) File.Delete(CarrierPath + ".previous");
                        File.Copy(CarrierPath, CarrierPath + ".previous", true); File.Delete(CarrierPath); File.Move(CarrierPath + ".load", CarrierPath);
                        c = loadCheck; state = JsonUtil.Dict(c.Get("state/machine.json").Data); n = JsonUtil.AsDict(state["n"]); term = JsonUtil.AsDict(state["terminal"]); term["draft"] = "";
                        AppendScroll(state, "checkpoint loaded: " + loadName); AppendJournal(c, state, new List<string> { "CHECKPOINT_LOAD:" + loadName }, command); outcome.LoadCheckpoint = loadName;
                    }
                }
                else outcome = ApplyCommand(c, state, command);
                n = JsonUtil.AsDict(state["n"]);
            }
            Metrics.SetStage("EXECUTE"); VmResult vr = new VmResult(); Stopwatch vmwatch = Stopwatch.StartNew();
            double running = Num(n, "running", 1); double dt = JsonUtil.Num(state.ContainsKey("quantumSeconds") ? state["quantumSeconds"] : null, .5);
            if (running > .5 && outcome.LoadCheckpoint == null)
            {
                VmProgram p = VmProgram.Load(c); vr = Vm.Execute(p, state, dt);
                state["tick"] = JsonUtil.Int(state.ContainsKey("tick") ? state["tick"] : null, 0) + 1;
                AddTrail(c, state);
            }
            vmwatch.Stop();
            foreach (string ev in vr.Events) state["lastEvent"] = ev;
            PersistOperationalMetrics(state, c, vr.Instructions);
            Metrics.SetStage("JOURNAL");
            if (vr.Events.Count > 0 || command == null) AppendJournal(c, state, vr.Events, command);
            c.Get("state/machine.json").Data = JsonUtil.Bytes(state);
            Metrics.SetStage("ENCODE"); Stopwatch enc = Stopwatch.StartNew();
            string tmpTarget = CarrierPath + ".next"; if (File.Exists(tmpTarget)) File.Delete(tmpTarget);
            CarrierCodec.Save(tmpTarget, c, JsonUtil.Int(state["tick"], 0)); enc.Stop();
            Metrics.SetStage("COMMIT");
            // CarrierCodec.Save wrote atomically to tmpTarget; validate again, then replace authoritative image.
            Carrier post = CarrierCodec.Load(tmpTarget, true);
            AtomicReplace(tmpTarget, CarrierPath);
            cycle.Stop(); Metrics.UpdateCycle(c, vr.Instructions, vmwatch.Elapsed.TotalMilliseconds, enc.Elapsed.TotalMilliseconds, cycle.Elapsed.TotalMilliseconds, new FileInfo(CarrierPath).Length);
            if (!String.IsNullOrEmpty(outcome.SaveCheckpoint))
            {
                Stopwatch cpw = Stopwatch.StartNew(); string cp = Path.Combine(Workspace, "checkpoints", outcome.SaveCheckpoint + ActiveExtension); File.Copy(CarrierPath, cp, true); cpw.Stop(); Metrics.SetCheckpoint(cpw.Elapsed.TotalMilliseconds);
                Carrier cpcheck = CarrierCodec.Load(cp, true); // fail closed if copy was not complete
            }
            Publish(BuildSnapshot(post));
        }

        private static void AtomicReplace(string src, string dst)
        {
            string bak = dst + ".previous"; if (File.Exists(bak)) File.Delete(bak);
            if (File.Exists(dst))
            {
                try { File.Replace(src, dst, bak, true); }
                catch { File.Copy(dst, bak, true); File.Delete(dst); File.Move(src, dst); }
            }
            else File.Move(src, dst);
        }

        private void PersistOperationalMetrics(Dictionary<string, object> state, Carrier c, int instr)
        {
            Dictionary<string, object> m = JsonUtil.AsDict(state["metrics"]); RuntimeMetrics rm = Metrics.Snapshot();
            m["totalQuanta"] = JsonUtil.Num(m.ContainsKey("totalQuanta") ? m["totalQuanta"] : null, 0) + 1;
            m["totalVmInstructions"] = JsonUtil.Num(m.ContainsKey("totalVmInstructions") ? m["totalVmInstructions"] : null, 0) + instr;
            m["totalDecodedBytes"] = JsonUtil.Num(m.ContainsKey("totalDecodedBytes") ? m["totalDecodedBytes"] : null, 0) + c.FileBytes;
            m["lastDecodeMs"] = c.DecodeMs; m["lastVerifyMs"] = c.VerifyMs; m["lastVmMs"] = rm.VmMs; m["lastEncodeMs"] = rm.EncodeMs; m["lastCommitMs"] = rm.CommitMs; m["lastCheckpointMs"] = rm.CheckpointMs;
        }

        private CommandOutcome ApplyCommand(Carrier c, Dictionary<string, object> state, string raw)
        {
            CommandOutcome outc = new CommandOutcome(); Dictionary<string, object> term = JsonUtil.AsDict(state["terminal"]); List<object> hist = JsonUtil.AsList(term["history"]); hist.Add(raw); while (hist.Count > 64) hist.RemoveAt(0); term["history"] = hist; term["draft"] = ""; draft = "";
            AppendScroll(state, "> " + raw);
            string cmd = ExpandAlias(c, raw.Trim()); string lower = cmd.ToLowerInvariant(); Dictionary<string, object> n = JsonUtil.AsDict(state["n"]);
            if (lower == "help")
            {
                Dictionary<string, object> cfg = JsonUtil.Dict(c.Get("terminal/config.json").Data); foreach (object x in JsonUtil.AsList(cfg["help"])) AppendScroll(state, "  " + JsonUtil.Str(x, ""));
            }
            else if (lower == "status")
            {
                AppendScroll(state, "tick=" + JsonUtil.Int(state["tick"], 0) + " phase=" + Num(n, "phaseIndex", 0).ToString("0", CultureInfo.InvariantCulture) + " altitude=" + Num(n, "altitude", 0).ToString("0.0", CultureInfo.InvariantCulture) + "m fuel=" + (Num(n, "fuel", 0) * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%");
            }
            else if (lower == "run") { n["running"] = 1.0; AppendScroll(state, "simulation running"); }
            else if (lower == "pause") { n["running"] = 0.0; AppendScroll(state, "simulation paused"); }
            else if (lower == "reset")
            {
                Dictionary<string, object> init = JsonUtil.Dict(c.Get("state/initial.json").Data); CarrierEntry stateEntry = c.Get("state/machine.json"); stateEntry.Data = JsonUtil.Bytes(init); outc.Reset = true; AppendScroll(init, "machine reset from image-resident initial state");
                foreach (KeyValuePair<string, object> kv in init) state[kv.Key] = kv.Value;
            }
            else if (lower.StartsWith("warp "))
            {
                double w; if (!Double.TryParse(cmd.Substring(5).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w)) AppendScroll(state, "usage: warp <0.25..8>");
                else { w = Math.Max(.25, Math.Min(8, w)); n["userWarp"] = w; AppendScroll(state, "warp=" + w.ToString("0.##", CultureInfo.InvariantCulture)); }
            }
            else if (lower.StartsWith("camera "))
            {
                string id = cmd.Substring(7).Trim().ToLowerInvariant(); if (CameraExists(c, id)) { state["camera"] = id; AppendScroll(state, "camera=" + id); } else AppendScroll(state, "unknown camera: " + id);
            }
            else if (lower == "verify") AppendScroll(state, "PASS: all TIFF/GIF temporal frames, archive entries and immutable image banks validated");
            else if (lower == "spool")
            {
                RuntimeMetrics m = Metrics.Snapshot(); AppendScroll(state, "stage=" + m.Stage + " pages/s=" + m.PagesPerSec.ToString("0.0", CultureInfo.InvariantCulture) + " decode=" + m.DecodeMBps.ToString("0.0", CultureInfo.InvariantCulture) + "MB/s vm=" + m.VmInstrPerSec.ToString("0", CultureInfo.InvariantCulture) + " instr/s");
            }
            else if (lower.StartsWith("checkpoint save "))
            {
                string name = SafeName(cmd.Substring(16).Trim()); if (name == null) AppendScroll(state, "checkpoint name must use A-Z a-z 0-9 _ -");
                else { AddInternalCheckpoint(c, state, name); outc.SaveCheckpoint = name; AppendScroll(state, "checkpoint queued: " + name); }
            }
            else if (lower == "checkpoint list")
            {
                string[] fs = Directory.GetFiles(Path.Combine(Workspace, "checkpoints"), "*" + ActiveExtension); if (fs.Length == 0) AppendScroll(state, "no checkpoints");
                foreach (string f in fs.OrderBy(x => x)) AppendScroll(state, "  " + Path.GetFileNameWithoutExtension(f));
            }
            else if (lower.StartsWith("checkpoint load ")) { AppendScroll(state, "checkpoint load is applied before command mutation"); }
            else if (lower == "pwd") AppendScroll(state, JsonUtil.Str(JsonUtil.AsDict(state["terminal"])["cwd"], "/"));
            else if (lower == "ls" || lower.StartsWith("ls ")) FsList(state, lower.Length > 2 ? cmd.Substring(2).Trim() : null);
            else if (lower.StartsWith("cd ")) FsCd(state, cmd.Substring(3).Trim());
            else if (lower.StartsWith("cat ")) FsCat(state, cmd.Substring(4).Trim());
            else if (lower.StartsWith("write ")) FsWrite(state, cmd.Substring(6));
            else AppendScroll(state, "unknown command; type help");
            AppendJournal(c, state, new List<string> { "COMMAND" }, raw); return outc;
        }

        private string DetectCheckpointLoad(string raw, Carrier c)
        {
            string cmd = ExpandAlias(c, raw.Trim()); if (!cmd.ToLowerInvariant().StartsWith("checkpoint load ")) return null; return SafeName(cmd.Substring(16).Trim());
        }
        private string ExpandAlias(Carrier c, string raw)
        {
            Dictionary<string, object> cfg = JsonUtil.Dict(c.Get("terminal/config.json").Data); Dictionary<string, object> al = JsonUtil.AsDict(cfg["aliases"]); object v; if (al.TryGetValue(raw.ToLowerInvariant(), out v)) return JsonUtil.Str(v, raw); return raw;
        }
        private static string SafeName(string s)
        {
            if (String.IsNullOrEmpty(s) || s.Length > 64) return null; foreach (char c in s) if (!(Char.IsLetterOrDigit(c) || c == '_' || c == '-')) return null; return s;
        }
        private bool CameraExists(Carrier c, string id)
        {
            Dictionary<string, object> scene = JsonUtil.Dict(c.Get("scene/showcase.json").Data); foreach (object o in JsonUtil.AsList(scene["cameras"])) { Dictionary<string, object> d = JsonUtil.AsDict(o); if (JsonUtil.Str(d["id"], "") == id) return true; } return false;
        }
        private static double Num(Dictionary<string, object> d, string key, double fallback) { object v; return d.TryGetValue(key, out v) ? JsonUtil.Num(v, fallback) : fallback; }

        private void AddTrail(Carrier c, Dictionary<string, object> state)
        {
            Dictionary<string, object> n = JsonUtil.AsDict(state["n"]); List<object> trail = JsonUtil.AsList(state["trail"]); string xv = "x", yv = "y";
            try { Dictionary<string, object> sc = JsonUtil.Dict(c.Get("scene/showcase.json").Data); Dictionary<string, object> mp = JsonUtil.AsDict(sc["map"]); xv = JsonUtil.Str(mp["xVar"], xv); yv = JsonUtil.Str(mp["yVar"], yv); } catch { }
            Dictionary<string, object> p = new Dictionary<string, object>(); p["x"] = Num(n, xv, 0); p["y"] = Num(n, yv, 0); p["phase"] = Num(n, "phaseIndex", 0); p["tick"] = JsonUtil.Int(state["tick"], 0); trail.Add(p); while (trail.Count > 400) trail.RemoveAt(0); state["trail"] = trail;
        }
        private void AddInternalCheckpoint(Carrier c, Dictionary<string, object> state, string name)
        {
            Dictionary<string, object> cp = JsonUtil.Dict(c.Get("checkpoint/history.json").Data); List<object> slots = JsonUtil.AsList(cp["slots"]); Dictionary<string, object> s = new Dictionary<string, object>();
            byte[] bytes = JsonUtil.Bytes(state); using (SHA256 h = SHA256.Create()) s["sha256"] = Program.Hex(h.ComputeHash(bytes)); s["name"] = name; s["tick"] = JsonUtil.Int(state["tick"], 0); s["state"] = Encoding.UTF8.GetString(bytes); slots.Add(s); while (slots.Count > 8) slots.RemoveAt(0); cp["slots"] = slots; c.Get("checkpoint/history.json").Data = JsonUtil.Bytes(cp);
        }
        private void AppendJournal(Carrier c, Dictionary<string, object> state, List<string> events, string command)
        {
            string old = Encoding.UTF8.GetString(c.Get("journal/events.ndjson").Data); string[] lines = old.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 4095) lines = lines.Skip(lines.Length - 4095).ToArray(); string prior = String.Join("\n", lines) + (lines.Length > 0 ? "\n" : "");
            byte[] stateBytes = JsonUtil.Bytes(state); string prevHash; using (SHA256 h = SHA256.Create()) prevHash = Program.Hex(h.ComputeHash(Encoding.UTF8.GetBytes(prior)));
            string stateHash; using (SHA256 h = SHA256.Create()) stateHash = Program.Hex(h.ComputeHash(stateBytes));
            Dictionary<string, object> j = new Dictionary<string, object>(); j["tick"] = JsonUtil.Int(state["tick"], 0); j["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); j["events"] = events; if (command != null) j["command"] = command; j["prev"] = prevHash; j["state"] = stateHash;
            c.Get("journal/events.ndjson").Data = Encoding.UTF8.GetBytes(prior + JsonUtil.Text(j) + "\n");
        }
        private static void AppendScroll(Dictionary<string, object> state, string line)
        {
            Dictionary<string, object> t = JsonUtil.AsDict(state["terminal"]); List<object> s = JsonUtil.AsList(t["scrollback"]); s.Add(line); while (s.Count > 80) s.RemoveAt(0); t["scrollback"] = s;
        }
        private static string NormPath(string cwd, string p)
        {
            if (String.IsNullOrWhiteSpace(p)) return cwd; string q = p.StartsWith("/") ? p : (cwd.TrimEnd('/') + "/" + p); Stack<string> st = new Stack<string>(); foreach (string x in q.Split('/')) { if (x == "" || x == ".") continue; if (x == "..") { if (st.Count > 0) st.Pop(); } else st.Push(x); } string[] a = st.Reverse().ToArray(); return "/" + String.Join("/", a);
        }
        private static void FsList(Dictionary<string, object> state, string p)
        {
            Dictionary<string, object> t = JsonUtil.AsDict(state["terminal"]); Dictionary<string, object> fs = JsonUtil.AsDict(t["vfs"]); string cwd = JsonUtil.Str(t["cwd"], "/"); string basep = NormPath(cwd, p); List<string> names = new List<string>(); foreach (string k in fs.Keys) if (k.StartsWith(basep, StringComparison.Ordinal)) names.Add(k); if (names.Count == 0) AppendScroll(state, "(empty)"); else foreach (string n in names.OrderBy(x => x)) AppendScroll(state, n);
        }
        private static void FsCd(Dictionary<string, object> state, string p) { Dictionary<string, object> t = JsonUtil.AsDict(state["terminal"]); t["cwd"] = NormPath(JsonUtil.Str(t["cwd"], "/"), p); }
        private static void FsCat(Dictionary<string, object> state, string p) { Dictionary<string, object> t = JsonUtil.AsDict(state["terminal"]); Dictionary<string, object> fs = JsonUtil.AsDict(t["vfs"]); string q = NormPath(JsonUtil.Str(t["cwd"], "/"), p); object v; AppendScroll(state, fs.TryGetValue(q, out v) ? JsonUtil.Str(v, "") : "not found: " + q); }
        private static void FsWrite(Dictionary<string, object> state, string args)
        {
            int sp = args.IndexOf(' '); if (sp < 1) { AppendScroll(state, "usage: write <path> <text>"); return; } string p = args.Substring(0, sp); string text = args.Substring(sp + 1); Dictionary<string, object> t = JsonUtil.AsDict(state["terminal"]); Dictionary<string, object> fs = JsonUtil.AsDict(t["vfs"]); string q = NormPath(JsonUtil.Str(t["cwd"], "/"), p); fs[q] = text; AppendScroll(state, "wrote " + q);
        }

        private Snapshot BuildSnapshot(Carrier c)
        {
            Snapshot s = new Snapshot(); s.Carrier = c; s.State = JsonUtil.Dict(c.Get("state/machine.json").Data); s.Scene = JsonUtil.Dict(c.Get("scene/showcase.json").Data); s.Hud = JsonUtil.Dict(c.Get("ui/hud.json").Data); s.Phases = JsonUtil.Dict(c.Get("mission/phases.json").Data); s.Banks = JsonUtil.Dict(c.Get("cartridge/banks.json").Data); s.TerminalConfig = JsonUtil.Dict(c.Get("terminal/config.json").Data); s.StateJson = Encoding.UTF8.GetString(c.Get("state/machine.json").Data);
            if (assetCache == null)
            {
                assetCache = new Dictionary<string, Image>(StringComparer.Ordinal);
                foreach (CarrierEntry e in c.Entries.Values) if (e.Name.StartsWith("assets/", StringComparison.Ordinal) && e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using (MemoryStream ms = new MemoryStream(e.Data, false))
                        using (Image srcImage = Image.FromStream(ms, true, true))
                            assetCache[e.Name] = new Bitmap(srcImage);
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Log(ex, "ASSET DECODE FALLBACK: " + e.Name);
                        Bitmap fallback = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
                        using (Graphics fg = Graphics.FromImage(fallback))
                        {
                            fg.Clear(Color.FromArgb(8, 24, 32));
                            using (Pen fp = new Pen(Color.FromArgb(80, 190, 220), 2)) fg.DrawRectangle(fp, 2, 2, 59, 59);
                        }
                        assetCache[e.Name] = fallback;
                    }
                }
                CarrierEntry wav0; if (c.Entries.TryGetValue("assets/engine.wav", out wav0)) engineWaveCache = (byte[])wav0.Data.Clone();
            }
            s.Assets = assetCache; s.EngineWave = engineWaveCache; return s;
        }
        private void Publish(Snapshot s)
        {
            lock (gate) { Current = s; }
            Action a = SnapshotChanged; if (a != null) a();
        }
        internal Snapshot GetSnapshot() { lock (gate) return Current; }
        internal string StatusJson() { Snapshot s = GetSnapshot(); return s == null ? "{}" : s.StateJson; }
        public void Dispose() { disposed = true; if (quantumTimer != null) quantumTimer.Dispose(); if (assetCache != null) foreach (Image i in assetCache.Values) try { i.Dispose(); } catch { } }
    }

    internal sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener listener; private readonly Thread thread; private readonly RuntimeEngine engine; private volatile bool stop; internal readonly string Token; internal readonly int Port;
        internal LoopbackServer(RuntimeEngine e)
        {
            engine = e; byte[] r = new byte[18]; using (RandomNumberGenerator g = RandomNumberGenerator.Create()) g.GetBytes(r); Token = Program.Hex(r);
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(16); Port = ((IPEndPoint)listener.LocalEndpoint).Port; thread = new Thread(Run); thread.IsBackground = true; thread.Name = "TIFF-GIF-HOLO loopback"; thread.Start();
        }
        private void Run()
        {
            while (!stop)
            {
                try
                {
                    TcpClient c = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { Handle(c); }
                        catch (Exception ex)
                        {
                            Diagnostics.Log(ex, "LOOPBACK CLIENT FAULT - CONNECTION DROPPED, PROCESS CONTINUES");
                            try { c.Close(); } catch { }
                        }
                    });
                }
                catch (Exception ex)
                {
                    if (!stop) { Diagnostics.Log(ex, "LOOPBACK ACCEPT FAULT - RETRYING"); Thread.Sleep(100); }
                }
            }
        }
        private void Handle(TcpClient c)
        {
            using (c) using (NetworkStream ns = c.GetStream())
            {
                ns.ReadTimeout = 3000; ns.WriteTimeout = 3000; StreamReader r = new StreamReader(ns, Encoding.UTF8, false, 4096, true); string first = r.ReadLine(); if (String.IsNullOrEmpty(first)) return;
                string[] f = first.Split(' '); if (f.Length < 2) { Respond(ns, 400, "text/plain", "bad request"); return; } string method = f[0]; string path = f[1]; int len = 0; string line;
                while (!String.IsNullOrEmpty(line = r.ReadLine())) { int k = line.IndexOf(':'); if (k > 0 && line.Substring(0, k).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) Int32.TryParse(line.Substring(k + 1).Trim(), out len); }
                string prefix = "/" + Token + "/";
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) { Respond(ns, 404, "text/plain", "not found"); return; }
                string sub = path.Substring(prefix.Length);
                if (method == "GET" && sub == "status") Respond(ns, 200, "application/json", engine.StatusJson());
                else if (method == "POST" && sub == "command") { char[] b = new char[Math.Max(0, Math.Min(len, 8192))]; int n = r.ReadBlock(b, 0, b.Length); string cmd = new string(b, 0, n); engine.QueueCommand(cmd); Respond(ns, 202, "application/json", "{\"accepted\":true}"); }
                else Respond(ns, 404, "text/plain", "not found");
            }
        }
        private static void Respond(Stream s, int code, string type, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body); string h = "HTTP/1.1 " + code.ToString() + (code == 200 ? " OK" : code == 202 ? " Accepted" : " Error") + "\r\nContent-Type: " + type + "; charset=utf-8\r\nContent-Length: " + b.Length.ToString() + "\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n"; byte[] hb = Encoding.ASCII.GetBytes(h); s.Write(hb, 0, hb.Length); s.Write(b, 0, b.Length); s.Flush();
        }
        public void Dispose() { stop = true; try { listener.Stop(); } catch { } }
    }

    internal sealed class SimulationForm : Form
    {
        private readonly RuntimeEngine engine; private readonly LoopbackServer loopback; private readonly System.Windows.Forms.Timer frameTimer; private readonly TextBox input;
        private readonly Font fSmall, fTiny, fTitle, fMetric, fMono; private readonly Stopwatch fpsClock = Stopwatch.StartNew(); private int frameCount; private double fps; private string fatal = ""; private SoundPlayer engineSound; private MemoryStream engineSoundStream; private bool soundOn; private bool closing; private string loopbackStatus = "DIRECT";
        private readonly object soundGate = new object();
        private const int BW = 1920, BH = 1080;

        internal SimulationForm(RuntimeEngine e, bool enableLoopback)
        {
            engine = e; Text = "TIFF-GIF-HOLO Generic Image Evaluator"; BackColor = Color.FromArgb(2, 8, 13); ForeColor = Color.White; DoubleBuffered = true; KeyPreview = true; MinimumSize = new Size(1100, 700); ClientSize = new Size(1600, 900); StartPosition = FormStartPosition.CenterScreen; WindowState = FormWindowState.Maximized;
            fSmall = MakeFont("Consolas", 15, FontStyle.Regular); fTiny = MakeFont("Consolas", 12, FontStyle.Regular); fTitle = MakeFont("Consolas", 24, FontStyle.Bold); fMetric = MakeFont("Consolas", 18, FontStyle.Bold); fMono = MakeFont("Consolas", 14, FontStyle.Regular);
            input = new TextBox(); input.BorderStyle = BorderStyle.FixedSingle; input.BackColor = Color.FromArgb(4, 18, 25); input.ForeColor = Color.FromArgb(120, 230, 255); input.Font = fMono; input.AcceptsReturn = false; input.TabStop = true; input.KeyDown += InputKeyDown; input.TextChanged += delegate { engine.SetDraft(input.Text); }; Controls.Add(input);
            if (enableLoopback)
            {
                try { loopback = new LoopbackServer(engine); loopbackStatus = "127.0.0.1:" + loopback.Port.ToString(CultureInfo.InvariantCulture) + "/" + loopback.Token.Substring(0, 10) + "..."; }
                catch (Exception ex) { loopback = null; loopbackStatus = "DIRECT / LOOPBACK UNAVAILABLE"; Diagnostics.Log(ex, "LOOPBACK START FAILED - CONTINUING IN DIRECT CONSOLE MODE"); }
            }
            else { loopback = null; loopbackStatus = "DIRECT / LOOPBACK DISABLED"; }
            engine.SnapshotChanged += EngineSnapshotChanged; engine.Fatal += EngineFatal;
            frameTimer = new System.Windows.Forms.Timer(); frameTimer.Interval = 16; frameTimer.Tick += FrameTick; frameTimer.Start();
            Resize += delegate { SafeUi(delegate { PlaceInput(); }, "resize"); }; KeyDown += FormKeyDown; FormClosing += FormClosingSafe; Shown += delegate { SafeUi(delegate { Diagnostics.Log("GUI visible; enabling image execution"); PlaceInput(); input.Focus(); engine.Start(); }, "shown"); };
        }
        private static Font MakeFont(string name, float size, FontStyle style) { try { return new Font(name, size, style, GraphicsUnit.Pixel); } catch { return new Font(FontFamily.GenericMonospace, size, style, GraphicsUnit.Pixel); } }
        private void SafeUi(MethodInvoker action, string context) { if (closing || IsDisposed || Disposing) return; try { action(); } catch (Exception ex) { fatal = ex.Message; Diagnostics.Log(ex, "UI " + context); } }
        private void SafeBegin(MethodInvoker action, string context)
        {
            if (closing || IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { SafeUi(action, context); }); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException ex) { Diagnostics.Log(ex, "BEGININVOKE " + context); }
        }
        private void EngineSnapshotChanged() { SafeBegin(delegate { Snapshot s = engine.GetSnapshot(); if (s != null) { Dictionary<string, object> t = JsonUtil.AsDict(s.State["terminal"]); string d = JsonUtil.Str(t["draft"], ""); if (!input.Focused) input.Text = d; } Invalidate(); }, "snapshot"); }
        private void EngineFatal(string x) { fatal = x ?? "runtime fault"; SafeBegin(delegate { Invalidate(); }, "fatal overlay"); }
        private void FrameTick(object sender, EventArgs e)
        {
            try { frameCount++; if (fpsClock.ElapsedMilliseconds >= 1000) { fps = frameCount * 1000.0 / fpsClock.ElapsedMilliseconds; frameCount = 0; fpsClock.Restart(); engine.Metrics.SetFps(fps); } UpdateSound(); Invalidate(); }
            catch (Exception ex) { fatal = ex.Message; Diagnostics.Log(ex, "FRAME TIMER FAULT"); try { frameTimer.Stop(); } catch { } Invalidate(); }
        }
        private void FormClosingSafe(object sender, FormClosingEventArgs e)
        {
            closing = true; Diagnostics.Log("GUI close requested: " + e.CloseReason.ToString());
            try { frameTimer.Stop(); } catch { }
            try { engine.SnapshotChanged -= EngineSnapshotChanged; engine.Fatal -= EngineFatal; } catch { }
            lock (soundGate) { try { if (engineSound != null) engineSound.Stop(); } catch { } try { if (engineSoundStream != null) engineSoundStream.Dispose(); } catch { } engineSound = null; engineSoundStream = null; soundOn = false; }
            try { if (loopback != null) loopback.Dispose(); } catch (Exception ex) { Diagnostics.Log(ex, "LOOPBACK DISPOSE"); }
            try { engine.Dispose(); } catch (Exception ex) { Diagnostics.Log(ex, "ENGINE DISPOSE"); }
        }
        private void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { string cmd = input.Text; input.Clear(); engine.SetDraft(""); engine.QueueCommand(cmd); e.SuppressKeyPress = true; }
        }
        private void FormKeyDown(object sender, KeyEventArgs e)
        {
            if (input.Focused) return;
            if (e.KeyCode >= Keys.F1 && e.KeyCode <= Keys.F6) { Snapshot sn = engine.GetSnapshot(); if (sn != null) { List<object> cams = JsonUtil.AsList(sn.Scene["cameras"]); int ci = (int)e.KeyCode - (int)Keys.F1; if (ci >= 0 && ci < cams.Count) { Dictionary<string, object> cd = JsonUtil.AsDict(cams[ci]); engine.QueueCommand("camera " + JsonUtil.Str(cd["id"], "")); } } e.Handled = true; }
            else if (e.KeyCode == Keys.Space) { Snapshot s = engine.GetSnapshot(); if (s != null) { double run = StateNum(s.State, "running", 1); engine.QueueCommand(run > .5 ? "pause" : "run"); } e.Handled = true; }
            else if (e.KeyCode == Keys.Oemtilde) { input.Focus(); }
            else if (e.KeyCode == Keys.F11) { if (FormBorderStyle == FormBorderStyle.None) { FormBorderStyle = FormBorderStyle.Sizable; WindowState = FormWindowState.Maximized; } else { FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Maximized; } }
        }
        private void PlaceInput()
        {
            float sc = Math.Min(ClientSize.Width / (float)BW, ClientSize.Height / (float)BH); float ox = (ClientSize.Width - BW * sc) / 2f; float oy = (ClientSize.Height - BH * sc) / 2f;
            int x = (int)(ox + 32 * sc), y = (int)(oy + 1020 * sc), w = (int)(365 * sc), h = Math.Max(24, (int)(34 * sc)); input.SetBounds(x, y, Math.Max(160, w), h); input.Font = MakeFont("Consolas", Math.Max(12, 14 * sc), FontStyle.Regular);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                base.OnPaint(e); Snapshot s = engine.GetSnapshot(); if (s == null) return; Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.CompositingQuality = CompositingQuality.HighQuality;
                float sc = Math.Min(ClientSize.Width / (float)BW, ClientSize.Height / (float)BH); float ox = (ClientSize.Width - BW * sc) / 2f; float oy = (ClientSize.Height - BH * sc) / 2f; GraphicsState root = g.Save();
                try
                {
                    g.TranslateTransform(ox, oy); g.ScaleTransform(sc, sc);
                    DrawBackdrop(g); DrawHeader(g, s); DrawCartridge(g, s, new RectangleF(12, 70, 405, 390)); DrawTerminal(g, s, new RectangleF(12, 470, 405, 595)); DrawMain(g, s, new RectangleF(430, 70, 1040, 690)); DrawTelemetry(g, s, new RectangleF(430, 772, 1040, 168)); DrawPipeline(g, s, new RectangleF(430, 950, 1040, 115)); DrawRight(g, s, new RectangleF(1483, 70, 425, 995));
                    if (!String.IsNullOrEmpty(fatal)) { using (SolidBrush b = new SolidBrush(Color.FromArgb(225, 55, 6, 12))) g.FillRectangle(b, 420, 420, 1080, 180); using (Pen p = new Pen(Color.FromArgb(255, 100, 110), 3)) g.DrawRectangle(p, 420, 420, 1080, 180); DrawText(g, "EXECUTION HALTED - IMAGE VALIDATION OR RUNTIME FAULT", fMetric, Color.FromArgb(255, 130, 140), new RectangleF(450, 450, 1020, 35), StringAlignment.Center); DrawText(g, fatal, fSmall, Color.White, new RectangleF(470, 500, 980, 70), StringAlignment.Center); }
                }
                finally { g.Restore(root); }
            }
            catch (Exception ex)
            {
                fatal = ex.Message; Diagnostics.Log(ex, "RENDER FAULT - GUI KEPT OPEN");
                try { e.Graphics.Clear(Color.FromArgb(2, 8, 13)); using (SolidBrush b = new SolidBrush(Color.White)) e.Graphics.DrawString("TIFF-GIF-HOLO render fault caught. The shell remains open.\r\n" + ex.Message + "\r\nSee workspace\\logs\\runtime.log", SystemFonts.MessageBoxFont, b, new RectangleF(30, 30, Math.Max(100, ClientSize.Width - 60), 180)); } catch { }
            }
        }

        private void DrawBackdrop(Graphics g)
        {
            using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, BW, BH), Color.FromArgb(2, 8, 13), Color.FromArgb(4, 18, 28), 90f)) g.FillRectangle(b, 0, 0, BW, BH);
            using (Pen p = new Pen(Color.FromArgb(20, 70, 100), 1)) { for (int x = 0; x < BW; x += 64) g.DrawLine(p, x, 0, x, BH); for (int y = 0; y < BH; y += 64) g.DrawLine(p, 0, y, BW, y); }
        }
        private void DrawHeader(Graphics g, Snapshot s)
        {
            string title = JsonUtil.Str(s.Hud.ContainsKey("title") ? s.Hud["title"] : null, "TIFF-GIF-HOLO"); string sub = JsonUtil.Str(s.Hud.ContainsKey("subtitle") ? s.Hud["subtitle"] : null, "IMAGE-RESIDENT VM");
            DrawText(g, title, fTitle, Color.FromArgb(90, 220, 255), new RectangleF(22, 10, 900, 36), StringAlignment.Near); DrawText(g, sub, fTiny, Color.FromArgb(100, 175, 200), new RectangleF(22, 44, 1000, 22), StringAlignment.Near);
            string endpoint = "LOCAL VIRTUAL CONSOLE " + loopbackStatus; DrawText(g, endpoint, fSmall, Color.FromArgb(100, 225, 190), new RectangleF(1080, 18, 810, 28), StringAlignment.Far);
        }
        private void Panel(Graphics g, RectangleF r, string title)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(205, 5, 20, 30))) g.FillRectangle(b, r); using (Pen p = new Pen(Color.FromArgb(33, 125, 170), 2)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height); using (SolidBrush hb = new SolidBrush(Color.FromArgb(35, 90, 125))) g.FillRectangle(hb, r.X, r.Y, r.Width, 32); DrawText(g, title, fSmall, Color.FromArgb(110, 225, 250), new RectangleF(r.X + 10, r.Y + 5, r.Width - 20, 24), StringAlignment.Near);
        }
        private void DrawCartridge(Graphics g, Snapshot s, RectangleF r)
        {
            Panel(g, r, "TIFF-GIF PROCESSABLE HOLOGRAM / AUTHORITATIVE"); float cx = r.X + 78, cy = r.Y + 75;
            int pages = s.Carrier.DataPageCount; for (int i = Math.Min(pages, 9) - 1; i >= 0; i--) { float dx = i * 11, dy = i * 7; using (SolidBrush b = new SolidBrush(Color.FromArgb(26, 30 + i * 7, 105 + i * 6, 145 + i * 8))) g.FillRectangle(b, cx + dx, cy + dy, 145, 182); using (Pen p = new Pen(Color.FromArgb(60, 185, 235), 2)) g.DrawRectangle(p, cx + dx, cy + dy, 145, 182); }
            DrawText(g, "TGIF", fTitle, Color.FromArgb(90, 220, 255), new RectangleF(cx + 28, cy + 66, 100, 40), StringAlignment.Center);
            float y = r.Y + 55; int bi = 0; foreach (KeyValuePair<string, object> kv in s.Banks.Take(10)) { float yy = y + bi * 28; using (SolidBrush dot = new SolidBrush(BankColor(bi))) g.FillEllipse(dot, r.Right - 170, yy + 5, 8, 8); DrawText(g, kv.Key.Replace(" Bank", ""), fTiny, Color.FromArgb(175, 215, 225), new RectangleF(r.Right - 155, yy, 145, 20), StringAlignment.Near); bi++; }
            string hash = Program.Hex(s.Carrier.StreamHash); DrawText(g, pages + " DATA PAGES  |  " + (s.Carrier.FileBytes / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MiB", fTiny, Color.FromArgb(100, 205, 225), new RectangleF(r.X + 12, r.Bottom - 58, r.Width - 24, 20), StringAlignment.Near); DrawText(g, "STREAM " + hash.Substring(0, 22) + "...", fTiny, Color.FromArgb(80, 160, 180), new RectangleF(r.X + 12, r.Bottom - 34, r.Width - 24, 20), StringAlignment.Near);
        }
        private Color BankColor(int i) { Color[] c = new Color[] { Color.FromArgb(80,220,255), Color.FromArgb(255,205,80), Color.FromArgb(255,110,165), Color.FromArgb(60,200,255), Color.FromArgb(95,230,175), Color.FromArgb(220,205,85), Color.FromArgb(210,110,245), Color.FromArgb(115,165,255), Color.FromArgb(255,145,85), Color.FromArgb(100,235,210) }; return c[i % c.Length]; }
        private void DrawTerminal(Graphics g, Snapshot s, RectangleF r)
        {
            Panel(g, r, "VIRTUAL CONSOLE  |  127.0.0.1"); Dictionary<string, object> t = JsonUtil.AsDict(s.State["terminal"]); List<object> lines = JsonUtil.AsList(t["scrollback"]); int max = 23; int start = Math.Max(0, lines.Count - max); float y = r.Y + 42; for (int i = start; i < lines.Count; i++) { string line = JsonUtil.Str(lines[i], ""); Color c = line.StartsWith(">") ? Color.FromArgb(120, 230, 255) : line.StartsWith("[FAULT]") ? Color.FromArgb(255, 120, 135) : Color.FromArgb(125, 205, 175); DrawText(g, Clip(line, 52), fTiny, c, new RectangleF(r.X + 12, y, r.Width - 24, 20), StringAlignment.Near); y += 20; }
            DrawText(g, JsonUtil.Str(s.TerminalConfig.ContainsKey("prompt") ? s.TerminalConfig["prompt"] : null, "tiff@image:~$ "), fTiny, Color.FromArgb(105, 225, 185), new RectangleF(r.X + 12, r.Bottom - 55, r.Width - 24, 20), StringAlignment.Near);
        }
        private void DrawMain(Graphics g, Snapshot s, RectangleF r)
        {
            Panel(g, r, HudTitle(s, "main", "IMAGE-RESIDENT EXECUTION")); RectangleF view = new RectangleF(r.X + 8, r.Y + 38, r.Width - 16, r.Height - 46); using (SolidBrush b = new SolidBrush(Color.Black)) g.FillRectangle(b, view); DrawScene(g, s, view); using (Pen p = new Pen(Color.FromArgb(40, 135, 175), 1)) g.DrawRectangle(p, view.X, view.Y, view.Width, view.Height);
            int pi = (int)Math.Round(StateNum(s.State, "phaseIndex", 0)); string pn = PhaseName(s, pi); string cam = JsonUtil.Str(s.State.ContainsKey("camera") ? s.State["camera"] : null, "chase"); DrawText(g, pn + "  /  " + cam.ToUpperInvariant(), fSmall, Color.FromArgb(110, 225, 255), new RectangleF(view.X + 16, view.Y + 12, 600, 28), StringAlignment.Near); DrawText(g, "F1-F6 CAMERAS  |  SPACE PAUSE/RUN  |  ` TERMINAL", fTiny, Color.FromArgb(90, 150, 175), new RectangleF(view.Right - 500, view.Y + 15, 480, 22), StringAlignment.Far);
        }

        private void DrawScene(Graphics g, Snapshot s, RectangleF view)
        {
            Dictionary<string, object> scene = s.Scene; string camid = JsonUtil.Str(s.State.ContainsKey("camera") ? s.State["camera"] : null, "chase"); Dictionary<string, object> cam = FindCamera(scene, camid); string renderer = JsonUtil.Str(cam.ContainsKey("renderer") ? cam["renderer"] : null, "world");
            GraphicsState gs = g.Save(); g.SetClip(view); g.TranslateTransform(view.X, view.Y); g.ScaleTransform(view.Width / 1000f, view.Height / 650f);
            if (renderer == "map") DrawMap(g, s); else DrawWorld(g, s, cam);
            g.Restore(gs);
        }
        private Dictionary<string, object> FindCamera(Dictionary<string, object> scene, string id)
        {
            foreach (object o in JsonUtil.AsList(scene["cameras"])) { Dictionary<string, object> d = JsonUtil.AsDict(o); if (JsonUtil.Str(d["id"], "") == id) return d; } return JsonUtil.AsDict(JsonUtil.AsList(scene["cameras"])[0]);
        }
        private void DrawWorld(Graphics g, Snapshot s, Dictionary<string, object> cam)
        {
            int pi = (int)Math.Round(StateNum(s.State, "phaseIndex", 0)); Dictionary<string, object> ps = JsonUtil.AsDict(JsonUtil.AsDict(s.Scene["phaseScenes"])[pi.ToString(CultureInfo.InvariantCulture)]); List<object> layers = JsonUtil.AsList(ps["layers"]);
            double zoom = JsonUtil.Num(cam.ContainsKey("zoom") ? cam["zoom"] : null, 1); double fx = JsonUtil.Num(cam.ContainsKey("focusX") ? cam["focusX"] : null, 500), fy = JsonUtil.Num(cam.ContainsKey("focusY") ? cam["focusY"] : null, 325);
            string focusLayer = JsonUtil.Str(cam.ContainsKey("focusLayer") ? cam["focusLayer"] : null, ""); if (focusLayer.Length > 0) foreach (object lo in layers) { Dictionary<string, object> ld = JsonUtil.AsDict(lo); if (JsonUtil.Str(ld.ContainsKey("id") ? ld["id"] : null, "") == focusLayer) { fx = Resolve(ld.ContainsKey("x") ? ld["x"] : null, s) + Resolve(ld.ContainsKey("w") ? ld["w"] : null, s) / 2; fy = Resolve(ld.ContainsKey("y") ? ld["y"] : null, s) + Resolve(ld.ContainsKey("h") ? ld["h"] : null, s) / 2; } }
            GraphicsState camState = g.Save(); if (Math.Abs(zoom - 1) > .001) { g.TranslateTransform(500, 325); g.ScaleTransform((float)zoom, (float)zoom); g.TranslateTransform((float)-fx, (float)-fy); }
            foreach (object lo in layers) DrawLayer(g, s, JsonUtil.AsDict(lo)); g.Restore(camState);
            string overlay = JsonUtil.Str(cam.ContainsKey("overlay") ? cam["overlay"] : null, ""); Image oi; if (overlay.Length > 0 && s.Assets.TryGetValue(overlay, out oi)) g.DrawImage(oi, new RectangleF(0, 0, 1000, 650));
        }
        private void DrawLayer(Graphics g, Snapshot s, Dictionary<string, object> l)
        {
            string type = JsonUtil.Str(l["type"], "");
            if (type == "gradient")
            {
                Color a = ParseColor(JsonUtil.Str(l["c1"], "#000000"), Color.Black), b = ParseColor(JsonUtil.Str(l["c2"], "#102030"), Color.Black); using (LinearGradientBrush br = new LinearGradientBrush(new RectangleF(0, 0, 1000, 650), a, b, 90f)) g.FillRectangle(br, 0, 0, 1000, 650);
            }
            else if (type == "starfield") DrawStars(g, l);
            else if (type == "image") DrawImageLayer(g, s, l);
            else if (type == "heightfield") DrawHeightfield(g, s, l);
            else if (type == "particles") DrawParticles(g, s, l, false);
            else if (type == "dust") DrawParticles(g, s, l, true);
            else if (type == "trajectory") DrawTrajectory(g, s, l);
            else if (type == "line") { using (Pen p = new Pen(ParseColor(JsonUtil.Str(l["color"], "#5ad8ff"), Color.Cyan), (float)JsonUtil.Num(l.ContainsKey("width") ? l["width"] : null, 1))) g.DrawLine(p, (float)Resolve(l["x1"], s), (float)Resolve(l["y1"], s), (float)Resolve(l["x2"], s), (float)Resolve(l["y2"], s)); }
        }
        private void DrawStars(Graphics g, Dictionary<string, object> l)
        {
            int seed = JsonUtil.Int(l.ContainsKey("seed") ? l["seed"] : null, 1), count = JsonUtil.Int(l.ContainsKey("count") ? l["count"] : null, 200); Random r = new Random(seed); for (int i = 0; i < count; i++) { int a = 80 + r.Next(176); float x = r.Next(1000), y = r.Next(650), z = 1 + (float)r.NextDouble() * 2; using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 190 + r.Next(66), 205 + r.Next(51), 225 + r.Next(31)))) g.FillEllipse(b, x, y, z, z); }
        }
        private void DrawImageLayer(Graphics g, Snapshot s, Dictionary<string, object> l)
        {
            string asset = JsonUtil.Str(l["asset"], ""); Image im; if (!s.Assets.TryGetValue(asset, out im)) return; float x = (float)Resolve(l["x"], s), y = (float)Resolve(l["y"], s), w = (float)Resolve(l["w"], s), h = (float)Resolve(l["h"], s), rot = (float)Resolve(l.ContainsKey("rotation") ? l["rotation"] : null, s); float alpha = (float)Resolve(l.ContainsKey("alpha") ? l["alpha"] : 1.0, s); alpha = Math.Max(0, Math.Min(1, alpha));
            GraphicsState st = g.Save(); if (Math.Abs(rot) > .001) { g.TranslateTransform(x + w / 2, y + h / 2); g.RotateTransform(rot); x = -w / 2; y = -h / 2; }
            if (alpha >= .999) g.DrawImage(im, new RectangleF(x, y, w, h)); else { ColorMatrix cm = new ColorMatrix(); cm.Matrix33 = alpha; ImageAttributes ia = new ImageAttributes(); ia.SetColorMatrix(cm); g.DrawImage(im, Rectangle.Round(new RectangleF(x, y, w, h)), 0, 0, im.Width, im.Height, GraphicsUnit.Pixel, ia); ia.Dispose(); }
            g.Restore(st);
        }
        private void DrawHeightfield(Graphics g, Snapshot s, Dictionary<string, object> l)
        {
            int seed = JsonUtil.Int(l.ContainsKey("seed") ? l["seed"] : null, 1); float ybase = (float)Resolve(l["y"], s), amp = (float)JsonUtil.Num(l.ContainsKey("amplitude") ? l["amplitude"] : null, 80); Random r = new Random(seed); List<PointF> pts = new List<PointF>(); double phase = r.NextDouble() * 10; for (int x = -20; x <= 1020; x += 12) { double yy = Math.Sin(x * .018 + phase) * .46 + Math.Sin(x * .061 + phase * 1.7) * .23 + Math.Sin(x * .137 + 1.1) * .11; yy += (r.NextDouble() - .5) * .18; pts.Add(new PointF(x, ybase + (float)(yy * amp))); } pts.Add(new PointF(1020, 680)); pts.Add(new PointF(-20, 680));
            using (GraphicsPath path = new GraphicsPath()) { path.AddPolygon(pts.ToArray()); string asset = JsonUtil.Str(l.ContainsKey("asset") ? l["asset"] : null, ""); Image im; if (asset.Length > 0 && s.Assets.TryGetValue(asset, out im)) { using (TextureBrush tb = new TextureBrush(im, WrapMode.TileFlipX)) { tb.ScaleTransform(.55f, .55f); g.FillPath(tb, path); } } else using (SolidBrush b = new SolidBrush(ParseColor(JsonUtil.Str(l["color"], "#596069"), Color.Gray))) g.FillPath(b, path); using (Pen p = new Pen(Color.FromArgb(135, 165, 175), 2)) g.DrawPath(p, path); }
        }
        private void DrawParticles(Graphics g, Snapshot s, Dictionary<string, object> l, bool dust)
        {
            double strengthSpec = l.ContainsKey("strength") ? Resolve(l["strength"], s) : 1; if (strengthSpec <= .02) return; int seed = JsonUtil.Int(l.ContainsKey("seed") ? l["seed"] : null, 1) + JsonUtil.Int(s.State["tick"], 0) * 7919; int count = (int)(JsonUtil.Num(l.ContainsKey("count") ? l["count"] : null, 80) * Math.Min(1, strengthSpec)); float ox = (float)Resolve(l["x"], s), oy = (float)Resolve(l["y"], s); float spread = (float)JsonUtil.Num(l.ContainsKey("spread") ? l["spread"] : null, 50), length = (float)JsonUtil.Num(l.ContainsKey("length") ? l["length"] : null, 180); Random rr = new Random(seed);
            for (int i = 0; i < count; i++) { float t = (float)rr.NextDouble(); float dx = ((float)rr.NextDouble() - .5f) * spread * (dust ? (1 + 2 * t) : (.25f + t)); float dy = t * length; int a = (int)(170 * (1 - t)); Color c = dust ? Color.FromArgb(a, 175, 168, 145) : Color.FromArgb(a, 80 + rr.Next(80), 175 + rr.Next(70), 255); float sz = dust ? 2 + t * 7 : 2 + (1 - t) * 4; using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, ox + dx, oy + dy, sz, sz * (dust ? .6f : 2.5f)); }
        }
        private void DrawTrajectory(Graphics g, Snapshot s, Dictionary<string, object> l)
        {
            float cx = (float)Resolve(l["cx"], s), cy = (float)Resolve(l["cy"], s), rx = (float)Resolve(l["rx"], s), ry = (float)Resolve(l["ry"], s); using (Pen p = new Pen(ParseColor(JsonUtil.Str(l["color"], "#56d6ff"), Color.Cyan), 2)) { p.DashStyle = DashStyle.Dash; g.DrawEllipse(p, cx - rx, cy - ry, rx * 2, ry * 2); } double a = StateNum(s.State, "phaseProgress", 0) * Math.PI * 2; float x = cx + (float)Math.Cos(a) * rx, y = cy + (float)Math.Sin(a) * ry; using (SolidBrush b = new SolidBrush(Color.FromArgb(130, 235, 255))) g.FillEllipse(b, x - 5, y - 5, 10, 10);
        }
        private void DrawMap(Graphics g, Snapshot s)
        {
            DrawStars(g, new Dictionary<string, object> { { "seed", 8891 }, { "count", 240 } }); Dictionary<string, object> m = JsonUtil.AsDict(s.Scene["map"]); List<object> ea = JsonUtil.AsList(m["bodyA"]), mo = JsonUtil.AsList(m["bodyB"]); float ex = (float)JsonUtil.Num(ea[0], 120), ey = (float)JsonUtil.Num(ea[1], 320), er = (float)JsonUtil.Num(ea[2], 150); float mx = (float)JsonUtil.Num(mo[0], 850), my = (float)JsonUtil.Num(mo[1], 320), mr = (float)JsonUtil.Num(mo[2], 105);
            Image bodyA, bodyB; if (s.Assets.TryGetValue(JsonUtil.Str(m["bodyAAsset"], ""), out bodyA)) g.DrawImage(bodyA, ex - er, ey - er, er * 2, er * 2); if (s.Assets.TryGetValue(JsonUtil.Str(m["bodyBAsset"], ""), out bodyB)) g.DrawImage(bodyB, mx - mr, my - mr, mr * 2, mr * 2);
            using (Pen p = new Pen(Color.FromArgb(70, 190, 230), 2)) { p.DashStyle = DashStyle.Dash; g.DrawBezier(p, ex + er * .7f, ey - 20, 360, 75, 660, 90, mx - mr * .7f, my - 10); }
            double xr = StateNum(s.State, JsonUtil.Str(m["xVar"], "x"), 0) / Math.Max(1e-9, JsonUtil.Num(m["xMax"], 1)); xr = Math.Max(0, Math.Min(1, xr)); float cx = (float)(ex + (mx - ex) * xr); float cy = (float)(320 - Math.Sin(xr * Math.PI) * 185); using (SolidBrush b = new SolidBrush(Color.FromArgb(100, 225, 255))) g.FillEllipse(b, cx - 8, cy - 8, 16, 16);
            List<object> trail = JsonUtil.AsList(s.State["trail"]); if (trail.Count > 1) { List<PointF> pts = new List<PointF>(); foreach (object po in trail) { Dictionary<string, object> pd = JsonUtil.AsDict(po); double tx = JsonUtil.Num(pd["x"], 0) / Math.Max(1e-9, JsonUtil.Num(m["xMax"], 1)); tx = Math.Max(0, Math.Min(1, tx)); pts.Add(new PointF((float)(ex + (mx - ex) * tx), (float)(320 - Math.Sin(tx * Math.PI) * 185))); } if (pts.Count > 1) using (Pen p = new Pen(Color.FromArgb(80, 210, 255), 2)) g.DrawLines(p, pts.ToArray()); }
            DrawText(g, JsonUtil.Str(m.ContainsKey("bodyALabel") ? m["bodyALabel"] : null, "A"), fSmall, Color.FromArgb(120, 215, 240), new RectangleF(ex - 100, ey + er + 12, 200, 25), StringAlignment.Center); DrawText(g, JsonUtil.Str(m.ContainsKey("bodyBLabel") ? m["bodyBLabel"] : null, "B"), fSmall, Color.FromArgb(190, 205, 215), new RectangleF(mx - 100, my + mr + 12, 200, 25), StringAlignment.Center); DrawText(g, JsonUtil.Str(m.ContainsKey("caption") ? m["caption"] : null, "IMAGE-CARRIED TRAJECTORY STATE"), fTiny, Color.FromArgb(85, 165, 190), new RectangleF(300, 590, 400, 25), StringAlignment.Center);
        }
        private double Resolve(object spec, Snapshot s)
        {
            if (spec == null) return 0; if (spec is string) { string ss = (string)spec; double nv; if (Double.TryParse(ss, NumberStyles.Float, CultureInfo.InvariantCulture, out nv)) return nv; return StateNum(s.State, ss, 0); }
            if (spec is int || spec is long || spec is double || spec is decimal || spec is float) return JsonUtil.Num(spec, 0); Dictionary<string, object> d = spec as Dictionary<string, object>; if (d == null) return JsonUtil.Num(spec, 0); double v = 0;
            if (d.ContainsKey("from") && d.ContainsKey("to")) { double a = JsonUtil.Num(d["from"], 0), b = JsonUtil.Num(d["to"], 0); string by = JsonUtil.Str(d.ContainsKey("by") ? d["by"] : null, "phaseProgress"); double p = StateNum(s.State, by, 0); v = a + (b - a) * Math.Max(0, Math.Min(1, p)); }
            else if (d.ContainsKey("var")) v = StateNum(s.State, JsonUtil.Str(d["var"], ""), 0); else v = 0;
            if (d.ContainsKey("mul")) v *= JsonUtil.Num(d["mul"], 1); if (d.ContainsKey("add")) v += JsonUtil.Num(d["add"], 0); if (d.ContainsKey("min")) v = Math.Max(v, JsonUtil.Num(d["min"], v)); if (d.ContainsKey("max")) v = Math.Min(v, JsonUtil.Num(d["max"], v)); return v;
        }

        private void DrawTelemetry(Graphics g, Snapshot s, RectangleF r)
        {
            Panel(g, r, "REAL-TIME TELEMETRY / IMAGE STATE"); List<object> rows = JsonUtil.AsList(s.Hud["telemetry"]); float w = (r.Width - 20) / rows.Count; for (int i = 0; i < rows.Count; i++) { Dictionary<string, object> d = JsonUtil.AsDict(rows[i]); double v = StateNum(s.State, JsonUtil.Str(d["var"], ""), 0) * JsonUtil.Num(d.ContainsKey("scale") ? d["scale"] : null, 1); string fmt = JsonUtil.Str(d.ContainsKey("fmt") ? d["fmt"] : null, "0.0"); string unit = JsonUtil.Str(d.ContainsKey("unit") ? d["unit"] : null, ""); float x = r.X + 10 + i * w; DrawText(g, JsonUtil.Str(d["label"], ""), fTiny, Color.FromArgb(95, 165, 185), new RectangleF(x, r.Y + 48, w - 6, 20), StringAlignment.Center); DrawText(g, v.ToString(fmt, CultureInfo.InvariantCulture), fMetric, Color.FromArgb(190, 235, 245), new RectangleF(x, r.Y + 75, w - 6, 34), StringAlignment.Center); DrawText(g, unit, fTiny, Color.FromArgb(85, 155, 175), new RectangleF(x, r.Y + 112, w - 6, 20), StringAlignment.Center); }
        }
        private void DrawPipeline(Graphics g, Snapshot s, RectangleF r)
        {
            Panel(g, r, HudTitle(s, "pipeline", "AUTHORITATIVE TIFF-GIF QUANTUM")); List<object> stages = JsonUtil.AsList(s.Hud["spoolStages"]); RuntimeMetrics m = engine.Metrics.Snapshot(); float x = r.X + 20, y = r.Y + 52, box = (r.Width - 40 - (stages.Count - 1) * 18) / stages.Count;
            for (int i = 0; i < stages.Count; i++) { string st = JsonUtil.Str(stages[i], ""); bool on = m.Stage == st; using (SolidBrush b = new SolidBrush(on ? Color.FromArgb(45, 145, 170) : Color.FromArgb(15, 42, 55))) g.FillRectangle(b, x, y, box, 38); using (Pen p = new Pen(on ? Color.FromArgb(95, 230, 255) : Color.FromArgb(30, 105, 135), on ? 2 : 1)) g.DrawRectangle(p, x, y, box, 38); DrawText(g, st, fTiny, on ? Color.White : Color.FromArgb(130, 185, 200), new RectangleF(x, y + 8, box, 20), StringAlignment.Center); if (i < stages.Count - 1) { using (Pen p = new Pen(Color.FromArgb(40, 150, 185), 2)) g.DrawLine(p, x + box + 3, y + 19, x + box + 15, y + 19); } x += box + 18; }
        }
        private void DrawRight(Graphics g, Snapshot s, RectangleF r)
        {
            RectangleF a = new RectangleF(r.X, r.Y, r.Width, 255), b = new RectangleF(r.X, r.Y + 265, r.Width, 310), c = new RectangleF(r.X, r.Y + 585, r.Width, 410); Panel(g, a, HudTitle(s, "status", "IMAGE STATUS")); Panel(g, b, HudTitle(s, "subsystems", "SUBSYSTEMS")); Panel(g, c, HudTitle(s, "metrics", "MEASURED PERFORMANCE"));
            int pi = (int)Math.Round(StateNum(s.State, "phaseIndex", 0)); DrawText(g, PhaseName(s, pi), fMetric, Color.FromArgb(90, 225, 255), new RectangleF(a.X + 15, a.Y + 48, a.Width - 30, 35), StringAlignment.Near); DrawText(g, "T+ " + FormatTime(StateNum(s.State, "simTime", 0)), fSmall, Color.FromArgb(185, 220, 230), new RectangleF(a.X + 15, a.Y + 88, a.Width - 30, 25), StringAlignment.Near); DrawText(g, "CAM " + JsonUtil.Str(s.State["camera"], "chase").ToUpperInvariant(), fSmall, Color.FromArgb(120, 190, 210), new RectangleF(a.X + 15, a.Y + 116, a.Width - 30, 25), StringAlignment.Near); DrawText(g, "TICK " + JsonUtil.Int(s.State["tick"], 0).ToString(), fSmall, Color.FromArgb(120, 190, 210), new RectangleF(a.X + 15, a.Y + 144, a.Width - 30, 25), StringAlignment.Near); DrawText(g, StateNum(s.State, "running", 1) > .5 ? "RUNNING" : "PAUSED", fSmall, StateNum(s.State, "running", 1) > .5 ? Color.FromArgb(80, 230, 175) : Color.FromArgb(240, 200, 95), new RectangleF(a.X + 15, a.Y + 178, a.Width - 30, 25), StringAlignment.Near); DrawText(g, "Last: " + Clip(JsonUtil.Str(s.State["lastEvent"], ""), 32), fTiny, Color.FromArgb(95, 160, 180), new RectangleF(a.X + 15, a.Y + 210, a.Width - 30, 25), StringAlignment.Near);
            List<object> subs = JsonUtil.AsList(s.Hud["subsystems"]); float yy = b.Y + 45; foreach (object o in subs) { using (SolidBrush dot = new SolidBrush(Color.FromArgb(80, 235, 175))) g.FillEllipse(dot, b.X + 18, yy + 5, 8, 8); DrawText(g, JsonUtil.Str(o, ""), fTiny, Color.FromArgb(175, 215, 225), new RectangleF(b.X + 36, yy, b.Width - 110, 20), StringAlignment.Near); DrawText(g, "ONLINE", fTiny, Color.FromArgb(90, 225, 180), new RectangleF(b.Right - 75, yy, 60, 20), StringAlignment.Far); yy += 25; }
            RuntimeMetrics m = engine.Metrics.Snapshot(); float my = c.Y + 46; Metric(g, c, ref my, "Image pages / s", m.PagesPerSec, "0.0"); Metric(g, c, ref my, "Decode throughput MB/s", m.DecodeMBps, "0.0"); Metric(g, c, ref my, "VM instructions / s", m.VmInstrPerSec, "0"); Metric(g, c, ref my, "State transactions / s", m.TransactionsPerSec, "0.00"); Metric(g, c, ref my, "Render frames / s", m.RenderFps, "0.0"); Metric(g, c, ref my, "Commit latency ms", m.CommitMs, "0.0"); Metric(g, c, ref my, "Validation MB/s", m.VerifyMBps, "0.0"); Metric(g, c, ref my, "Carrier ratio", m.CompressionRatio, "0.00"); DrawText(g, "All values above are measured host-side.", fTiny, Color.FromArgb(90, 150, 170), new RectangleF(c.X + 16, c.Bottom - 45, c.Width - 32, 20), StringAlignment.Near); DrawText(g, "No optical-MHz claim is made.", fTiny, Color.FromArgb(90, 150, 170), new RectangleF(c.X + 16, c.Bottom - 25, c.Width - 32, 20), StringAlignment.Near);
        }
        private void Metric(Graphics g, RectangleF r, ref float y, string label, double v, string fmt)
        {
            DrawText(g, label, fTiny, Color.FromArgb(120, 175, 190), new RectangleF(r.X + 16, y, 245, 22), StringAlignment.Near); DrawText(g, v.ToString(fmt, CultureInfo.InvariantCulture), fSmall, Color.FromArgb(150, 225, 240), new RectangleF(r.Right - 145, y - 2, 125, 24), StringAlignment.Far); using (Pen p = new Pen(Color.FromArgb(18, 70, 90), 1)) g.DrawLine(p, r.X + 16, y + 24, r.Right - 16, y + 24); y += 34;
        }
        private void UpdateSound()
        {
            Snapshot s = engine.GetSnapshot(); if (s == null || s.EngineWave == null) return; bool want = StateNum(s.State, "engineOn", 0) > .5 && StateNum(s.State, "running", 1) > .5;
            lock (soundGate)
            {
                if (want && !soundOn) { try { engineSoundStream = new MemoryStream(s.EngineWave, false); engineSound = new SoundPlayer(engineSoundStream); engineSound.PlayLooping(); soundOn = true; } catch { soundOn = false; } }
                else if (!want && soundOn) { try { engineSound.Stop(); } catch { } try { engineSoundStream.Dispose(); } catch { } engineSound = null; engineSoundStream = null; soundOn = false; }
            }
        }
        private static string HudTitle(Snapshot s, string key, string fallback) { object p; if (!s.Hud.TryGetValue("panelTitles", out p)) return fallback; Dictionary<string, object> d = JsonUtil.AsDict(p); object v; return d.TryGetValue(key, out v) ? JsonUtil.Str(v, fallback) : fallback; }
        private static double StateNum(Dictionary<string, object> state, string key, double fallback) { Dictionary<string, object> n = JsonUtil.AsDict(state["n"]); object v; return n.TryGetValue(key, out v) ? JsonUtil.Num(v, fallback) : fallback; }
        private string PhaseName(Snapshot s, int idx) { foreach (object o in JsonUtil.AsList(s.Phases["phases"])) { Dictionary<string, object> p = JsonUtil.AsDict(o); if (JsonUtil.Int(p["id"], -1) == idx) return JsonUtil.Str(p["name"], "PHASE " + idx.ToString()); } return "PHASE " + idx.ToString(); }
        private static string FormatTime(double sec) { if (sec < 0) sec = 0; long s = (long)Math.Round(sec); long d = s / 86400; s %= 86400; long h = s / 3600; s %= 3600; long m = s / 60; s %= 60; return (d > 0 ? d.ToString() + "d " : "") + h.ToString("00") + ":" + m.ToString("00") + ":" + s.ToString("00"); }
        private static Color ParseColor(string h, Color fallback) { try { if (h.StartsWith("#")) h = h.Substring(1); if (h.Length == 6) return Color.FromArgb(255, Int32.Parse(h.Substring(0, 2), NumberStyles.HexNumber), Int32.Parse(h.Substring(2, 2), NumberStyles.HexNumber), Int32.Parse(h.Substring(4, 2), NumberStyles.HexNumber)); } catch { } return fallback; }
        private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect, StringAlignment align) { using (SolidBrush b = new SolidBrush(color)) using (StringFormat sf = new StringFormat()) { sf.Alignment = align; sf.LineAlignment = StringAlignment.Near; sf.Trimming = StringTrimming.EllipsisCharacter; sf.FormatFlags = StringFormatFlags.NoWrap; g.DrawString(text, font, b, rect, sf); } }
        private static string Clip(string s, int n) { if (s == null) return ""; return s.Length <= n ? s : s.Substring(0, Math.Max(0, n - 1)) + "..."; }
    }

    public static class HarnessEntry
    {
        public static int Run(string[] args) { return Program.Run(args); }
    }

}
