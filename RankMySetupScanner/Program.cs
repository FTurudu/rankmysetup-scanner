// RankMySetup Scanner – bilgisayarın donanımını Windows'un kendi kaynaklarından okur.
// Hiçbir şeyi değiştirmez, sürücü yüklemez, yönetici izni istemez.
// Bilgisayar adı, kullanıcı adı ve seri numaraları okunmaz ve gönderilmez.
// .NET Framework 4.8 hedeflenir: Windows 10/11'de hazır gelir, ek kurulum gerekmez.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RankMySetupScanner
{
    internal static class AppInfo
    {
        public const string Version = "1.2.2";
        public const string BaseUrl = "https://rankmysetup.com";
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string session = SessionFromArgs(args);
            string demo = null, shot = null;
            foreach (var a in args ?? new string[0])
            {
                if (a.StartsWith("--lang=")) Lang.Tr = a.Substring(7).StartsWith("tr", StringComparison.OrdinalIgnoreCase);
                if (a.StartsWith("--demo")) demo = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "done";
                if (a.StartsWith("--shot=")) shot = a.Substring(7);
                // Geliştirme: arayüz açmadan tara ve raporu dosyaya yaz
                if (a.StartsWith("--dump=")) { System.IO.File.WriteAllText(a.Substring(7), Json.Pretty(Json.Serialize(Scanner.Run(_ => { })))); return; }
            }
            if (demo == null) Installer.EnsureInstalled();
            Application.Run(new ScannerForm(session, demo, shot));
        }

        // Site "rankmysetup://scan?s=OTURUM" adresiyle açtığında oturum kodunu al
        private static string SessionFromArgs(string[] args)
        {
            if (args == null) return null;
            foreach (var a in args)
            {
                var m = Regex.Match(a ?? "", @"^rankmysetup://scan/?\?s=([a-z0-9]{16})", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
            }
            return null;
        }
    }

    // ---------------- Kalıcı konum ve tarayıcıdan açılabilme ----------------
    // İlk çalıştırmada program kendini %LOCALAPPDATA%\RankMySetup klasörüne kopyalar ve
    // "rankmysetup://" adresini o kopyaya bağlar. Böylece indirilen dosya silinse bile
    // sitedeki Tara butonu çalışmaya devam eder. Yönetici izni gerekmez.
    public static class Installer
    {
        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RankMySetup"); }
        }

        public static string InstalledExe
        {
            get { return Path.Combine(InstallDir, "RankMySetupScanner.exe"); }
        }

        public static void EnsureInstalled()
        {
            string target = Application.ExecutablePath;
            try
            {
                string current = Path.GetFullPath(Application.ExecutablePath);
                string installed = Path.GetFullPath(InstalledExe);
                if (!string.Equals(current, installed, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(InstallDir);
                    byte[] bytes = File.ReadAllBytes(current);
                    bool needsCopy = !File.Exists(installed) || new FileInfo(installed).Length != bytes.Length
                                     || !File.ReadAllBytes(installed).SequenceEqual(bytes);
                    if (needsCopy) File.WriteAllBytes(installed, bytes);
                    target = installed;
                }
            }
            catch
            {
                // Kopyalanamazsa (ör. eski sürüm o an açık) bulunduğu yerden çalışmaya devam eder
                if (File.Exists(InstalledExe)) target = InstalledExe;
            }
            RegisterProtocol(target);
        }

        private static void RegisterProtocol(string exe)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\rankmysetup"))
                {
                    key.SetValue("", "URL:RankMySetup Scanner");
                    key.SetValue("URL Protocol", "");
                    using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + exe + "\",0");
                    using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue("", "\"" + exe + "\" \"%1\"");
                }
            }
            catch { /* kayıt yapılamazsa program yine çalışır */ }
        }
    }

    // ---------------- JSON (ek kütüphane olmadan) ----------------
    public static class Json
    {
        private static JavaScriptSerializer NewSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        }

        public static string Serialize(object o)
        {
            return NewSerializer().Serialize(o);
        }

        public static Dictionary<string, object> Parse(string json)
        {
            return NewSerializer().DeserializeObject(json) as Dictionary<string, object>;
        }

        // Okunabilir (girintili) hale getirir; yalnızca ekranda göstermek için
        public static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false, escaped = false;
            foreach (char c in json)
            {
                if (inString)
                {
                    sb.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        sb.Append(c); indent++; sb.Append(Environment.NewLine).Append(new string(' ', indent * 2)); break;
                    case '}':
                    case ']':
                        indent--; sb.Append(Environment.NewLine).Append(new string(' ', indent * 2)).Append(c); break;
                    case ',':
                        sb.Append(c).Append(Environment.NewLine).Append(new string(' ', indent * 2)); break;
                    case ':':
                        sb.Append(": "); break;
                    default:
                        sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    // ---------------- Sunucu bağlantısı ----------------
    public class UploadResult
    {
        public string Url;
        public int Score;
        public int Points;
        public string Tier;
        public bool Verified;
        public List<string[]> Tips = new List<string[]>();   // sunucunun önerileri: [başlık, açıklama]
    }

    public static class Api
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("RankMySetupScanner/" + AppInfo.Version);
            c.DefaultRequestHeaders.Add("x-rms-client", "scanner/" + AppInfo.Version);
            return c;
        }

        // Ağ ya da DNS anlık hata verirse birkaç kez yeniden dener
        private static async Task<HttpResponseMessage> SendWithRetry(Func<Task<HttpResponseMessage>> send)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { return await send(); }
                catch (HttpRequestException ex) { last = ex; }
                catch (TaskCanceledException ex) { last = ex; }
                await Task.Delay(1500 * (attempt + 1));
            }
            throw new Exception(Lang.T("RankMySetup’a bağlanılamadı. İnternet bağlantını kontrol edip tekrar dene. Sorun sürerse Komut İstemi’nde “ipconfig /flushdns” yazmak genelde çözer.",
                "Couldn’t reach RankMySetup. Check your internet connection and try again. If it keeps happening, running “ipconfig /flushdns” in Command Prompt usually fixes it.") +
                (last == null ? "" : " (" + Inner(last) + ")"));
        }

        private static string Inner(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        private static Dictionary<string, object> ParseOrThrow(string body, int status)
        {
            Dictionary<string, object> d = null;
            try { d = Json.Parse(body); } catch { }
            if (d == null) throw new Exception(Lang.T("Sunucudan beklenmeyen yanıt geldi (", "Unexpected response from the server (") + status + ").");
            return d;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        public static async Task<string> CreateSession()
        {
            var req = new StringContent("");
            req.Headers.Add("x-rms-lang", Lang.Code);   // rapor, programın diliyle açılsın
            var res = await SendWithRetry(() => Http.PostAsync(AppInfo.BaseUrl + "/api/session", req));
            var body = await res.Content.ReadAsStringAsync();
            var d = ParseOrThrow(body, (int)res.StatusCode);
            if (!res.IsSuccessStatusCode) throw new Exception(Lang.T("Oturum açılamadı (", "Couldn’t start a session (") + (int)res.StatusCode + ").");
            return Str(d, "sessionId");
        }

        // Program açılır açılmaz siteye haber verir; site böylece programın kurulu olduğunu anlar
        public static async Task MarkOpened(string session)
        {
            try { await Http.PostAsync(AppInfo.BaseUrl + "/api/session/" + Uri.EscapeDataString(session) + "/opened", new StringContent("")); } catch { }
        }

        public static async Task<UploadResult> Upload(string session, string json)
        {
            var res = await SendWithRetry(() => Http.PostAsync(AppInfo.BaseUrl + "/api/report?s=" + Uri.EscapeDataString(session),
                new StringContent(json, Encoding.UTF8, "application/json")));
            var body = await res.Content.ReadAsStringAsync();
            var d = ParseOrThrow(body, (int)res.StatusCode);
            if (!res.IsSuccessStatusCode) throw new Exception(ErrorText(Str(d, "error") ?? res.StatusCode.ToString()));
            var up = new UploadResult
            {
                Url = Str(d, "url"),
                Score = Convert.ToInt32(d["score"], CultureInfo.InvariantCulture),
                Tier = Str(d, "tier"),
                Points = d.ContainsKey("points") ? Convert.ToInt32(d["points"], CultureInfo.InvariantCulture) : Convert.ToInt32(d["score"], CultureInfo.InvariantCulture) * 100,
                Verified = d.ContainsKey("verified") && d["verified"] is bool && (bool)d["verified"]
            };
            object tips;
            if (d.TryGetValue("tips", out tips) && tips is object[])
                foreach (var t in (object[])tips)
                {
                    var td = t as Dictionary<string, object>;
                    if (td != null && Str(td, "title") != null) up.Tips.Add(new[] { Str(td, "title"), Str(td, "text") ?? "" });
                    if (up.Tips.Count == 3) break;
                }
            return up;
        }

        private static string ErrorText(string code)
        {
            switch (code)
            {
                case "session_used": return Lang.T("Bu tarama zaten gönderilmiş. Sitede tekrar Tara’ya bas.", "This scan was already sent. Click Scan on the site again.");
                case "session_expired": return Lang.T("Tarama süresi doldu. Sitede tekrar Tara’ya bas.", "The scan timed out. Click Scan on the site again.");
                case "session_not_found": return Lang.T("Tarama oturumu bulunamadı. Sitede tekrar Tara’ya bas.", "Scan session not found. Click Scan on the site again.");
                case "invalid_report": return Lang.T("Rapor eksik görünüyor.", "The report looks incomplete.");
                default: return Lang.T("Sunucu hatası: ", "Server error: ") + code;
            }
        }
    }

    // ---------------- Rapor modeli ----------------
    public class ScanReport
    {
        public string ScannerVersion { get; set; } = AppInfo.Version;
        public string ScannedAtUtc { get; set; }
        public CpuInfo Cpu { get; set; }
        public List<GpuInfo> Gpus { get; set; } = new List<GpuInfo>();
        public RamInfo Ram { get; set; } = new RamInfo();
        public MachineInfo Machine { get; set; } = new MachineInfo();
        public List<DiskInfo> Disks { get; set; } = new List<DiskInfo>();
        public List<DisplayInfo> Displays { get; set; } = new List<DisplayInfo>();
        public List<string> MonitorNames { get; set; } = new List<string>();
        public OsInfo Os { get; set; } = new OsInfo();
        public List<PeripheralInfo> Peripherals { get; set; } = new List<PeripheralInfo>();
        public BenchInfo Bench { get; set; }
        public SystemTweaks Sys { get; set; } = new SystemTweaks();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    // 1.1: oyun performansını etkileyen Windows ayarları, sistem diski, batarya ve BIOS (seri numarası okunmaz)
    public class SystemTweaks
    {
        public string PowerPlan { get; set; }
        public string PowerPlanGuid { get; set; }
        public bool? GameMode { get; set; }
        public bool? Hags { get; set; }
        public double SystemDriveFreeGB { get; set; }
        public double SystemDriveSizeGB { get; set; }
        public int? BatteryHealthPct { get; set; }
        public int? BatteryCycles { get; set; }
        public string BiosVendor { get; set; }
        public string BiosVersion { get; set; }
        public string BiosDate { get; set; }
    }

    public class CpuInfo
    {
        public string Name { get; set; }
        public int Cores { get; set; }
        public int Threads { get; set; }
        public int MaxClockMHz { get; set; }
    }

    public class GpuInfo
    {
        public string Name { get; set; }
        public long? VramMB { get; set; }
        public string VramSource { get; set; }
        public string DriverVersion { get; set; }
        public string Vendor { get; set; }
        public bool Integrated { get; set; }
    }

    public class RamModule
    {
        public double CapacityGB { get; set; }
        public int SpeedMHz { get; set; }
        public int ConfiguredSpeedMHz { get; set; }
        public string Type { get; set; }
        public string Manufacturer { get; set; }
        public string PartNumber { get; set; }
    }

    public class RamInfo
    {
        public double TotalGB { get; set; }
        public int SlotsTotal { get; set; }
        public List<RamModule> Modules { get; set; } = new List<RamModule>();
    }

    public class MachineInfo
    {
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string ProductVersion { get; set; }
        public string BoardManufacturer { get; set; }
        public string BoardProduct { get; set; }
        public bool IsLaptop { get; set; }
    }

    public class DiskInfo
    {
        public string Name { get; set; }
        public double SizeGB { get; set; }
        public string Type { get; set; }   // SSD / HDD / Unknown
        public string TypeSource { get; set; }
        public string Bus { get; set; }    // NVMe / SATA / USB ...
        public string Health { get; set; } // Healthy / Warning / Unhealthy / Unknown
    }

    public class DisplayInfo
    {
        public string Adapter { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int RefreshHz { get; set; }
        public int MaxRefreshHzAtThisResolution { get; set; }
        public bool Primary { get; set; }
    }

    public class PeripheralInfo
    {
        public string Kind { get; set; }      // mouse / keyboard / headset
        public string Name { get; set; }
        public string Brand { get; set; }
        public string VidPid { get; set; }
        public bool Wireless { get; set; }
        public bool Receiver { get; set; }    // adını bildirmeyen kablosuz alıcı (marka biliniyor)
    }

    public class OsInfo
    {
        public string Name { get; set; }
        public string Build { get; set; }
        public string Architecture { get; set; }
    }

    public class BenchInfo
    {
        public long SingleThreadScore { get; set; }   // 1.2: 5 turun ortancası (öncesinde en iyi tur)
        public long MultiThreadScore { get; set; }    // 1.2: 3 turun ortancası
        public int ThreadsUsed { get; set; }
        // 1.2: zaman içinde yavaşlamayı yakalamak için
        public int BenchVersion { get; set; }
        public List<long> SingleRuns { get; set; } = new List<long>();
        public List<long> MultiRuns { get; set; } = new List<long>();
        public double MemoryGBs { get; set; }          // bellek kopyalama hızı (GB/sn)
        public double BackgroundCpuPct { get; set; }   // testten hemen önce işlemcinin meşguliyeti (%)
        public int ClockPct { get; set; }              // yük altında anlık hız / en yüksek hız (%)
        public int ClockLimitPct { get; set; }         // yük altında Windows'un izin verdiği hız sınırı / en yüksek hız (%); düşükse ısınma ya da güç kısıtlaması
        public int DurationMs { get; set; }
    }

    // ---------------- Tarayıcı ----------------
    public static class Scanner
    {
        // Tarama sürerken arayüz bulunanları canlı gösterebilsin diye o anki rapor (yalnızca okunur)
        public static volatile ScanReport Current;

        public static ScanReport Run(Action<string> progress)
        {
            var r = new ScanReport { ScannedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            Current = r;

            progress("cpu");
            ReadCpu(r);
            progress("gpu");
            ReadGpus(r);
            progress("ram");
            ReadRam(r);
            progress("sys");
            ReadSystem(r);
            progress("disk");
            ReadDisks(r);
            progress("display");
            ReadDisplays(r);
            ReadMonitorNames(r);
            progress("peripherals");
            ReadPeripherals(r);
            progress("os");
            ReadOs(r);
            progress("tweaks");
            ReadTweaks(r);
            progress("bench");
            r.Bench = Bench.Run();
            progress("done");
            return r;
        }

        // --- WMI yardımcıları ---
        private static void ForEach(ScanReport r, string scope, string wql, Action<ManagementBaseObject> each)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(scope, wql))
                using (var results = searcher.Get())
                {
                    foreach (ManagementBaseObject o in results)
                    {
                        try { each(o); }
                        catch (Exception ex) { r.Warnings.Add(wql + " -> " + ex.Message); }
                        finally { o.Dispose(); }
                    }
                }
            }
            catch (Exception ex)
            {
                r.Warnings.Add(wql + " -> " + ex.Message);
            }
        }

        private static string S(ManagementBaseObject o, string prop)
        {
            try { var v = o[prop]; return v == null ? null : Clean(v.ToString()); }
            catch { return null; }
        }

        private static long L(ManagementBaseObject o, string prop)
        {
            try { var v = o[prop]; return v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "").Replace("®", "").Replace("™", "");
            return string.Join(" ", s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        // --- İşlemci ---
        private static void ReadCpu(ScanReport r)
        {
            ForEach(r, @"root\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor", o =>
            {
                if (r.Cpu == null)
                {
                    r.Cpu = new CpuInfo
                    {
                        Name = S(o, "Name"),
                        Cores = (int)L(o, "NumberOfCores"),
                        Threads = (int)L(o, "NumberOfLogicalProcessors"),
                        MaxClockMHz = (int)L(o, "MaxClockSpeed")
                    };
                }
                else
                {
                    // Çift işlemcili sistemler
                    r.Cpu.Cores += (int)L(o, "NumberOfCores");
                    r.Cpu.Threads += (int)L(o, "NumberOfLogicalProcessors");
                }
            });
        }

        // --- Ekran kartı ---
        private static readonly string[] VirtualGpuWords =
        {
            "Remote Display", "Basic Render", "Virtual Display", "Parsec", "Citrix", "VMware", "VirtualBox", "Hyper-V", "Meta Virtual", "Spacedesk", "IddSample", "DisplayLink"
        };

        private static void ReadGpus(ScanReport r)
        {
            ForEach(r, @"root\cimv2", "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController", o =>
            {
                var name = S(o, "Name");
                if (name == null) return;
                if (VirtualGpuWords.Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) return;
                if (r.Gpus.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))) return;

                var gpu = new GpuInfo { Name = name, DriverVersion = S(o, "DriverVersion") };
                long? reg = VramFromRegistry(name);
                if (reg.HasValue && reg.Value > 0)
                {
                    gpu.VramMB = reg.Value;
                    gpu.VramSource = "registry";
                }
                else
                {
                    // WMI 4 GB'ın üstünü doğru gösteremez, bu yüzden yalnızca yedek olarak kullanılır
                    long wmi = L(o, "AdapterRAM");
                    if (wmi > 0)
                    {
                        gpu.VramMB = (long)((uint)wmi) / (1024 * 1024);
                        gpu.VramSource = "wmi";
                    }
                }
                r.Gpus.Add(gpu);
            });

            // DirectX bilgisi en doğru kaynak: varsa VRAM'i oradan al
            var dx = ReadDxgi(r);
            foreach (var d in dx)
            {
                var match = r.Gpus.FirstOrDefault(g => string.Equals(g.Name, d.Name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    if (r.Gpus.Any(g => g.Name != null && d.Name != null && (g.Name.IndexOf(d.Name, StringComparison.OrdinalIgnoreCase) >= 0 || d.Name.IndexOf(g.Name, StringComparison.OrdinalIgnoreCase) >= 0)))
                        match = r.Gpus.First(g => g.Name != null && (g.Name.IndexOf(d.Name, StringComparison.OrdinalIgnoreCase) >= 0 || d.Name.IndexOf(g.Name, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                if (match == null)
                {
                    if (d.Name == null) continue;
                    match = new GpuInfo { Name = d.Name };
                    r.Gpus.Add(match);
                }
                match.Vendor = VendorName(d.VendorId);
                if (d.DedicatedMB > 0)
                {
                    match.VramMB = d.DedicatedMB;
                    match.VramSource = "directx";
                }
                // Dahili ekran kartı: kendi belleği çok az, sistem RAM'ini kullanıyor
                match.Integrated = d.DedicatedMB < 1024 && (d.VendorId == 0x8086 || d.VendorId == 0x1002 || d.VendorId == 0x1022);
            }
        }

        private static long? VramFromRegistry(string gpuName)
        {
            const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            try
            {
                using (var root = Registry.LocalMachine.OpenSubKey(classKey))
                {
                    if (root == null) return null;
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        if (!int.TryParse(sub, out _)) continue;
                        try
                        {
                            using (var k = root.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                var desc = Clean(k.GetValue("DriverDesc") as string);
                                if (desc == null || !string.Equals(desc, gpuName, StringComparison.OrdinalIgnoreCase)) continue;
                                long bytes = ToLong(k.GetValue("HardwareInformation.qwMemorySize"));
                                if (bytes <= 0) bytes = ToLong(k.GetValue("HardwareInformation.MemorySize"));
                                if (bytes > 0) return bytes / (1024 * 1024);
                            }
                        }
                        catch { /* erişilemeyen anahtarları atla */ }
                    }
                }
            }
            catch { }
            return null;
        }

        private static long ToLong(object v)
        {
            if (v is long l) return l;
            if (v is int i) return (uint)i;
            if (v is byte[] b)
            {
                if (b.Length >= 8) return BitConverter.ToInt64(b, 0);
                if (b.Length >= 4) return BitConverter.ToUInt32(b, 0);
            }
            return 0;
        }

        // --- DirectX (DXGI): ekran belleğinin en doğru kaynağı ---
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public uint AdapterLuidLow;
            public int AdapterLuidHigh;
            public uint Flags;
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            // IDXGIAdapter
            [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
            [PreserveSig] int GetDesc(IntPtr desc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
            // IDXGIAdapter1
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        }

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            // IDXGIFactory
            [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
            [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
            [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);
            // IDXGIFactory1
            [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 ppAdapter);
            [PreserveSig] int IsCurrent();
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        private class DxgiGpu
        {
            public string Name;
            public uint VendorId;
            public long DedicatedMB;
            public long SharedMB;
        }

        private static List<DxgiGpu> ReadDxgi(ScanReport r)
        {
            var list = new List<DxgiGpu>();
            IntPtr ptr = IntPtr.Zero;
            try
            {
                var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                if (CreateDXGIFactory1(ref iid, out ptr) != 0 || ptr == IntPtr.Zero) return list;
                var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(ptr);
                for (uint i = 0; i < 16; i++)
                {
                    if (factory.EnumAdapters1(i, out IDXGIAdapter1 adapter) != 0 || adapter == null) break;
                    try
                    {
                        if (adapter.GetDesc1(out DXGI_ADAPTER_DESC1 d) != 0) continue;
                        if ((d.Flags & 2) != 0) continue; // yazılım (sanal) adaptör
                        if (d.VendorId == 0x1414) continue; // Microsoft Basic Render
                        list.Add(new DxgiGpu
                        {
                            Name = Clean(d.Description),
                            VendorId = d.VendorId,
                            DedicatedMB = (long)(d.DedicatedVideoMemory.ToUInt64() / (1024 * 1024)),
                            SharedMB = (long)(d.SharedSystemMemory.ToUInt64() / (1024 * 1024))
                        });
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
                Marshal.ReleaseComObject(factory);
            }
            catch (Exception ex)
            {
                r.Warnings.Add("DXGI -> " + ex.Message);
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.Release(ptr);
            }
            return list;
        }

        private static string VendorName(uint id)
        {
            switch (id)
            {
                case 0x10DE: return "NVIDIA";
                case 0x1002: return "AMD";
                case 0x1022: return "AMD";
                case 0x8086: return "Intel";
                case 0x5143: return "Qualcomm";
                default: return null;
            }
        }

        // --- RAM ---
        private static string RamType(long smbiosType)
        {
            switch (smbiosType)
            {
                case 20: return "DDR";
                case 21: return "DDR2";
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 27: return "LPDDR";
                case 28: return "LPDDR2";
                case 29: return "LPDDR3";
                case 30: return "LPDDR4";
                case 34: return "DDR5";
                case 35: return "LPDDR5";
                default: return null;
            }
        }

        private static void ReadRam(ScanReport r)
        {
            ForEach(r, @"root\cimv2", "SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Manufacturer, PartNumber FROM Win32_PhysicalMemory", o =>
            {
                long cap = L(o, "Capacity");
                if (cap <= 0) return;
                r.Ram.Modules.Add(new RamModule
                {
                    CapacityGB = Math.Round(cap / 1073741824.0, 1),
                    SpeedMHz = (int)L(o, "Speed"),
                    ConfiguredSpeedMHz = (int)L(o, "ConfiguredClockSpeed"),
                    Type = RamType(L(o, "SMBIOSMemoryType")),
                    Manufacturer = S(o, "Manufacturer"),
                    PartNumber = S(o, "PartNumber")
                });
            });

            ForEach(r, @"root\cimv2", "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray", o =>
            {
                r.Ram.SlotsTotal += (int)L(o, "MemoryDevices");
            });

            if (r.Ram.Modules.Count > 0)
            {
                r.Ram.TotalGB = Math.Round(r.Ram.Modules.Sum(m => m.CapacityGB), 1);
            }
            else
            {
                ForEach(r, @"root\cimv2", "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", o =>
                {
                    r.Ram.TotalGB = Math.Round(L(o, "TotalPhysicalMemory") / 1073741824.0, 1);
                });
            }
        }

        // --- Sistem / anakart ---
        private static void ReadSystem(ScanReport r)
        {
            ForEach(r, @"root\cimv2", "SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem", o =>
            {
                r.Machine.Manufacturer = S(o, "Manufacturer");
                r.Machine.Model = S(o, "Model");
                if (L(o, "PCSystemType") == 2) r.Machine.IsLaptop = true;
            });
            ForEach(r, @"root\cimv2", "SELECT Version FROM Win32_ComputerSystemProduct", o =>
            {
                r.Machine.ProductVersion = S(o, "Version");
            });
            ForEach(r, @"root\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard", o =>
            {
                r.Machine.BoardManufacturer = S(o, "Manufacturer");
                r.Machine.BoardProduct = S(o, "Product");
            });
            // Batarya tek başına dizüstü demek değil: masaüstüne USB ile bağlı güç kaynağı (UPS) da Windows'ta batarya görünür.
            // Kasa türü taşınabilir ise ya da kasa türü bilinmiyorken UPS olmayan bir batarya varsa dizüstü say.
            bool? portable = null;
            ForEach(r, @"root\cimv2", "SELECT ChassisTypes FROM Win32_SystemEnclosure", o =>
            {
                var types = o["ChassisTypes"] as ushort[];
                if (types == null || types.Length == 0) return;
                bool p = types.Any(t => t == 8 || t == 9 || t == 10 || t == 11 || t == 12 || t == 14 || t == 18 || t == 21 || t == 30 || t == 31 || t == 32);
                bool d = types.Any(t => t == 3 || t == 4 || t == 5 || t == 6 || t == 7 || t == 13 || t == 15 || t == 16 || t == 35 || t == 36);
                if (p) portable = true; else if (d && portable == null) portable = false;
            });
            if (portable == true) r.Machine.IsLaptop = true;
            else if (portable == null)
                ForEach(r, @"root\cimv2", "SELECT Name, DeviceID FROM Win32_Battery", o =>
                {
                    var n = (S(o, "Name") ?? "") + " " + (S(o, "DeviceID") ?? "");
                    if (!Regex.IsMatch(n, @"ups|back-?ups|smart-?ups|cyber ?power|\bapc\b|eaton|powerwalker|tescom|inform|mustek|vertiv|riello", RegexOptions.IgnoreCase)) r.Machine.IsLaptop = true;
                });
        }

        // --- Diskler ---
        private static readonly string[] HddNameHints =
        {
            "TOSHIBA MK", "TOSHIBA DT", "TOSHIBA HDW", "TOSHIBA MQ", "WDC WD", "ST1000", "ST2000", "ST3000", "ST4000", "ST500", "ST320", "ST250",
            "HGST", "Hitachi", "HTS", "HDS", "SAMSUNG HD", "SAMSUNG HM", "SAMSUNG SP", "MAXTOR", "FUJITSU MH"
        };

        private static string GuessTypeFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name.IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0) return "SSD";
            if (HddNameHints.Any(h => name.StartsWith(h, StringComparison.OrdinalIgnoreCase))) return "HDD";
            return null;
        }

        private static void ReadDisks(ScanReport r)
        {
            ForEach(r, @"root\Microsoft\Windows\Storage", "SELECT FriendlyName, MediaType, BusType, Size, HealthStatus, SpindleSpeed FROM MSFT_PhysicalDisk", o =>
            {
                long media = L(o, "MediaType");
                long bus = L(o, "BusType");
                long health = L(o, "HealthStatus");
                long spindle = L(o, "SpindleSpeed");
                var name = S(o, "FriendlyName");

                string type = null, source = null;
                if (media == 4) { type = "SSD"; source = "windows"; }
                else if (media == 3) { type = "HDD"; source = "windows"; }
                else if (media == 5) { type = "SCM"; source = "windows"; }
                if (type == null && bus == 17) { type = "SSD"; source = "bus"; }
                if (type == null && spindle > 0 && spindle < 100000) { type = "HDD"; source = "spindle"; }
                if (type == null) { type = GuessTypeFromName(name); if (type != null) source = "name"; }
                if (type == null) { type = "Unknown"; source = "none"; }

                r.Disks.Add(new DiskInfo
                {
                    Name = name,
                    SizeGB = Math.Round(L(o, "Size") / 1073741824.0),
                    Type = type,
                    TypeSource = source,
                    Bus = BusName(bus),
                    Health = health == 0 ? "Healthy" : health == 1 ? "Warning" : health == 2 ? "Unhealthy" : "Unknown"
                });
            });

            if (r.Disks.Count == 0)
            {
                // Yedek yöntem
                ForEach(r, @"root\cimv2", "SELECT Model, Size, InterfaceType FROM Win32_DiskDrive", o =>
                {
                    var model = S(o, "Model");
                    var t = GuessTypeFromName(model);
                    r.Disks.Add(new DiskInfo
                    {
                        Name = model,
                        SizeGB = Math.Round(L(o, "Size") / 1073741824.0),
                        Type = t ?? "Unknown",
                        TypeSource = t != null ? "name" : "none",
                        Bus = S(o, "InterfaceType"),
                        Health = "Unknown"
                    });
                });
            }
        }

        private static string BusName(long bus)
        {
            switch (bus)
            {
                case 3: return "ATA";
                case 7: return "USB";
                case 8: return "RAID";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 17: return "NVMe";
                default: return "Other";
            }
        }

        // --- Ekranlar (Windows ekran ayarlarından) ---
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int ATTACHED_TO_DESKTOP = 0x1;
        private const int PRIMARY_DEVICE = 0x4;

        private static void ReadDisplays(ScanReport r)
        {
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    var dd = new DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & ATTACHED_TO_DESKTOP) == 0) continue;

                    var cur = NewDevMode();
                    if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur)) continue;

                    int maxHz = cur.dmDisplayFrequency;
                    for (int m = 0; m < 2000; m++)
                    {
                        var mode = NewDevMode();
                        if (!EnumDisplaySettings(dd.DeviceName, m, ref mode)) break;
                        if (mode.dmPelsWidth == cur.dmPelsWidth && mode.dmPelsHeight == cur.dmPelsHeight && mode.dmDisplayFrequency > maxHz)
                            maxHz = mode.dmDisplayFrequency;
                    }

                    r.Displays.Add(new DisplayInfo
                    {
                        Adapter = Clean(dd.DeviceString),
                        Width = cur.dmPelsWidth,
                        Height = cur.dmPelsHeight,
                        RefreshHz = cur.dmDisplayFrequency,
                        MaxRefreshHzAtThisResolution = maxHz,
                        Primary = (dd.StateFlags & PRIMARY_DEVICE) != 0
                    });
                }
            }
            catch (Exception ex)
            {
                r.Warnings.Add("Ekran okunamadı -> " + ex.Message);
            }
        }

        private static DEVMODE NewDevMode()
        {
            var dm = new DEVMODE();
            dm.dmDeviceName = new string('\0', 32);
            dm.dmFormName = new string('\0', 32);
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return dm;
        }

        private static void ReadMonitorNames(ScanReport r)
        {
            ForEach(r, @"root\wmi", "SELECT UserFriendlyName, ManufacturerName FROM WmiMonitorID", o =>
            {
                string name = Edid(o["UserFriendlyName"]);
                string maker = Edid(o["ManufacturerName"]);
                var full = string.Join(" ", new[] { maker, name }.Where(x => !string.IsNullOrEmpty(x)));
                if (!string.IsNullOrEmpty(full) && !r.MonitorNames.Contains(full)) r.MonitorNames.Add(full);
            });
        }

        private static string Edid(object v)
        {
            if (v is ushort[] arr)
            {
                var chars = arr.TakeWhile(c => c != 0).Select(c => (char)c).ToArray();
                var s = new string(chars).Trim();
                return s.Length == 0 ? null : s;
            }
            return null;
        }

        // --- Çevre birimleri: mouse, klavye, kulaklık ---
        private static readonly Dictionary<string, string> UsbVendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "046D", "Logitech" }, { "1532", "Razer" }, { "1038", "SteelSeries" }, { "1B1C", "Corsair" }, { "0951", "HyperX" }, { "03F0", "HyperX" },
            { "045E", "Microsoft" }, { "05AC", "Apple" }, { "0B05", "ASUS" }, { "2516", "Cooler Master" }, { "1E7D", "ROCCAT" }, { "3434", "Keychron" }
        };
        private static readonly Regex GenericName = new Regex(@"^(usb (input|composite) device|usb receiver|receiver|wireless receiver|hid[- ].*|gaming (mouse|keyboard)|usb (mouse|keyboard)|mouse|keyboard|usb device|2\.4g.*|dongle)$", RegexOptions.IgnoreCase);

        private static string BusReportedName(ManagementObject mo)
        {
            try
            {
                var inParams = mo.GetMethodParameters("GetDeviceProperties");
                inParams["devicePropertyKeys"] = new string[] { "DEVPKEY_Device_BusReportedDeviceDesc" };
                var outParams = mo.InvokeMethod("GetDeviceProperties", inParams, null);
                var props = outParams == null ? null : outParams["deviceProperties"] as ManagementBaseObject[];
                if (props == null) return null;
                foreach (var p in props)
                {
                    var data = p["Data"] as string;
                    if (!string.IsNullOrWhiteSpace(data)) return Clean(data);
                }
            }
            catch { }
            return null;
        }

        private static void AddPeripheral(ScanReport r, string kind, string name, string brand, string vidPid, bool wireless)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < 3) return;
            if (!string.IsNullOrEmpty(brand) && name.IndexOf(brand, StringComparison.OrdinalIgnoreCase) < 0) name = brand + " " + name;
            if (r.Peripherals.Any(x => x.Kind == kind && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return;
            if (r.Peripherals.Count(x => x.Kind == kind) >= 4) return;
            r.Peripherals.Add(new PeripheralInfo { Kind = kind, Name = name, Brand = brand, VidPid = vidPid, Wireless = wireless });
        }

        private static void ReadPeripherals(ScanReport r)
        {
            // Mouse ve klavye: Windows genel ad verir, gerçek ad USB aygıtının kendisinden okunur.
            // Aynı USB aygıtı (VID:PID) çoğu zaman hem Mouse hem Keyboard sınıfında görünür (kablosuz alıcılar, makro tuşlu mouse'lar):
            // önce aygıt başına sınıfları topla, sonra her aygıt için tek karar ver.
            var devices = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            ForEach(r, @"root\cimv2", "SELECT PNPClass, DeviceID FROM Win32_PnPEntity WHERE Present = TRUE AND (PNPClass = 'Mouse' OR PNPClass = 'Keyboard')", o =>
            {
                string cls = S(o, "PNPClass"), id = S(o, "DeviceID") ?? "";
                var m = Regex.Match(id, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                if (!m.Success) return;
                string key = m.Groups[1].Value.ToUpperInvariant() + ":" + m.Groups[2].Value.ToUpperInvariant();
                HashSet<string> set;
                if (!devices.TryGetValue(key, out set)) devices[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(string.Equals(cls, "Mouse", StringComparison.OrdinalIgnoreCase) ? "mouse" : "keyboard");
            });
            var receiverRx = new Regex(@"dongle|receiver|\bnano\b|unifying|lightspeed|hyper ?speed|slipstream|2[.,]4 ?g|wireless (device|adapter|transceiver)|alıcı", RegexOptions.IgnoreCase);
            // Mouse sınıfındakiler önce: markası bilinmeyen tek alıcı mouse olarak bildirilsin (klavye mi mouse mu bilinmez, genellikle mouse alıcısıdır)
            foreach (var dev in devices.OrderBy(d => d.Value.Contains("mouse") ? 0 : 1))
            {
                string vid = dev.Key.Substring(0, 4), pid = dev.Key.Substring(5, 4);
                string brand; UsbVendors.TryGetValue(vid, out brand);
                string name = null;
                ForEach(r, @"root\cimv2", "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_" + vid + "&PID_" + pid + "%'", u =>
                {
                    if (name != null) return;
                    var mo = u as ManagementObject;
                    if (mo != null) name = BusReportedName(mo);
                });
                // Tür: adındaki sözcük; yoksa tek sınıfta görünüyorsa o; iki sınıfta da görünüyorsa mouse (alıcılar ve makro tuşlu mouse'lar)
                string kind = name != null && Regex.IsMatch(name, @"mouse|\bfare\b", RegexOptions.IgnoreCase) ? "mouse"
                    : name != null && Regex.IsMatch(name, @"keyboard|klavye|\bkb\b", RegexOptions.IgnoreCase) ? "keyboard"
                    : dev.Value.Count == 1 ? dev.Value.First() : "mouse";
                // Kablosuz alıcı (adı ne olursa olsun) ya da genel ad: model adı yok, yalnızca "kablosuz" bilgisi gider; marka bilinmese de bildirilir
                if (name == null || GenericName.IsMatch(name) || receiverRx.IsMatch(name))
                {
                    bool isReceiver = name == null || receiverRx.IsMatch(name) || Regex.IsMatch(name, "wireless", RegexOptions.IgnoreCase);
                    // Ad da gönderilir: "2.4G Wireless Keyboard" gibi adlar sitenin türü doğru anlamasını sağlar
                    if (isReceiver && !r.Peripherals.Any(x => x.Receiver && x.Kind == kind && x.Brand == brand))
                        r.Peripherals.Add(new PeripheralInfo { Kind = kind, Name = name, Brand = brand, VidPid = dev.Key, Wireless = true, Receiver = true });
                    continue;
                }
                bool wireless = Regex.IsMatch(name, "wireless|lightspeed|hyperspeed|kablosuz|2\\.4", RegexOptions.IgnoreCase);
                AddPeripheral(r, kind, name, brand, dev.Key, wireless);
            }

            // Kulaklık: ses aygıtının adında parantez içindeki model ("Kulaklık (HyperX Cloud II Wireless)")
            ForEach(r, @"root\cimv2", "SELECT Name FROM Win32_PnPEntity WHERE Present = TRUE AND PNPClass = 'AudioEndpoint'", o =>
            {
                var n = S(o, "Name") ?? "";
                var m = Regex.Match(n, @"^(Headphones|Headset|Earphone|Kulaklık|Kulaklıklar|Mikrofon|Microphone|Headset Earphone|Headset Microphone)[^(]*\((.+)\)\s*$", RegexOptions.IgnoreCase);
                if (!m.Success) return;
                var model = Clean(m.Groups[2].Value);
                if (model == null) return;
                // "2- HyperX Cloud Alpha" gibi numaralı adlar: numarayı at (eskiden bunlar tamamen atılıyordu); SteelSeries "Game"/"Chat" kanalları tek cihaz
                model = Regex.Replace(model, @"^\d+\s*-\s*", "");
                model = Regex.Replace(model, @"\s+(Game|Chat|Hands-Free( AG Audio)?|Stereo)$", "", RegexOptions.IgnoreCase).Trim();
                if (Regex.IsMatch(model, @"realtek|high definition|display audio|nvidia|\bamd\b|intel|steam|virtual|vb-audio|voicemeeter|nahimic|sonic studio|bluetooth audio|hands-free|usb (pnp )?(audio|sound)|web ?cam|camera|c9\d0|c270|brio|controller|dualsense|dualshock|adapter|jack|codec|conexant|senary|cirrus|c-media|focusrite|scarlett|sound ?blaster|yeti|generic", RegexOptions.IgnoreCase)) return;
                string brand = null;
                foreach (var b in new[] { "HyperX", "Logitech", "Razer", "SteelSeries", "Corsair", "JBL", "Sony", "Sennheiser", "Beyerdynamic", "ASUS", "Rampage", "Turtle Beach", "Astro", "Audio-Technica", "Apple", "Samsung", "Xiaomi", "Edifier", "Philips", "Plantronics", "Jabra", "EPOS", "Lenovo", "MSI", "Redragon", "Marvo", "Everest", "HP", "Cooler Master" })
                    if (model.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0) { brand = b; break; }
                AddPeripheral(r, "headset", model, brand, null, Regex.IsMatch(model, "wireless|kablosuz|bt|bluetooth", RegexOptions.IgnoreCase));
            });
        }

        // --- İşletim sistemi ---
        private static void ReadOs(ScanReport r)
        {
            ForEach(r, @"root\cimv2", "SELECT Caption, BuildNumber, OSArchitecture FROM Win32_OperatingSystem", o =>
            {
                r.Os.Name = S(o, "Caption")?.Replace("Microsoft ", "");
                r.Os.Build = S(o, "BuildNumber");
                r.Os.Architecture = S(o, "OSArchitecture");
            });
        }

        // --- Oyun ayarları, sistem diski, batarya, BIOS ---
        private static void ReadTweaks(ScanReport r)
        {
            var t = r.Sys;
            ForEach(r, @"root\cimv2\power", "SELECT ElementName, InstanceID FROM Win32_PowerPlan WHERE IsActive = TRUE", o =>
            {
                t.PowerPlan = S(o, "ElementName");
                var m = Regex.Match(S(o, "InstanceID") ?? "", @"\{([0-9a-fA-F-]{36})\}");
                if (m.Success) t.PowerPlanGuid = m.Groups[1].Value.ToLowerInvariant();
            });
            if (t.PowerPlanGuid == null) ReadPowerPlanFromPowercfg(r, t);
            // Oyun Modu: değer yoksa Windows 10 1903 ve sonrasında varsayılan olarak açıktır
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar"))
                {
                    var v = k == null ? null : k.GetValue("AutoGameModeEnabled");
                    t.GameMode = v == null || Convert.ToInt32(v, CultureInfo.InvariantCulture) != 0;
                }
            }
            catch (Exception ex) { r.Warnings.Add("GameMode -> " + ex.Message); }
            // Donanım hızlandırmalı GPU zamanlaması (2 = açık, 1 = kapalı; değer yoksa bilinmiyor)
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers"))
                {
                    var v = k == null ? null : k.GetValue("HwSchMode");
                    if (v != null) t.Hags = Convert.ToInt32(v, CultureInfo.InvariantCulture) == 2;
                }
            }
            catch (Exception ex) { r.Warnings.Add("HAGS -> " + ex.Message); }
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
                t.SystemDriveFreeGB = Math.Round(d.AvailableFreeSpace / 1073741824.0, 1);
                t.SystemDriveSizeGB = Math.Round(d.TotalSize / 1073741824.0, 1);
            }
            catch (Exception ex) { r.Warnings.Add("SystemDrive -> " + ex.Message); }
            if (r.Machine.IsLaptop)
            {
                long design = 0, full = 0;
                ForEach(r, @"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData", o => design += L(o, "DesignedCapacity"));
                ForEach(r, @"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", o => full += L(o, "FullChargedCapacity"));
                ForEach(r, @"root\wmi", "SELECT CycleCount FROM BatteryCycleCount", o => { var c = (int)L(o, "CycleCount"); if (c > 0) t.BatteryCycles = c; });
                if (design > 0 && full > 0) t.BatteryHealthPct = (int)Math.Round(100.0 * full / design);
            }
            ForEach(r, @"root\cimv2", "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", o =>
            {
                t.BiosVendor = S(o, "Manufacturer");
                t.BiosVersion = S(o, "SMBIOSBIOSVersion");
                var rd = S(o, "ReleaseDate");
                if (rd != null && Regex.IsMatch(rd, @"^\d{8}")) t.BiosDate = rd.Substring(0, 4) + "-" + rd.Substring(4, 2) + "-" + rd.Substring(6, 2);
            });
        }

        // Win32_PowerPlan bazı sistemlerde çalışmaz: "powercfg /getactivescheme" çıktısından GUID ve adı al
        private static void ReadPowerPlanFromPowercfg(ScanReport r, SystemTweaks t)
        {
            try
            {
                // Konsol programları Windows'un OEM kod sayfasıyla yazar (ör. Norveççe "Høy ytelse"): aynı kod sayfasıyla okunmazsa harfler bozulur
                var psi = new ProcessStartInfo("powercfg", "/getactivescheme") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage) };
                using (var p = Process.Start(psi))
                {
                    var outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    var m = Regex.Match(outp, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)");
                    if (m.Success) { t.PowerPlanGuid = m.Groups[1].Value.ToLowerInvariant(); t.PowerPlan = m.Groups[2].Value.Trim(); }
                }
            }
            catch (Exception ex) { r.Warnings.Add("powercfg -> " + ex.Message); }
        }
    }

    // ---------------- Kısa işlemci testi ----------------
    public static class Bench
    {
        private static double Chunk(ref ulong x)
        {
            double acc = 0;
            for (int i = 0; i < 20000; i++)
            {
                x ^= x << 13;
                x ^= x >> 7;
                x ^= x << 17;
                acc += Math.Sqrt((x & 0xFFFF) + 1.0);
            }
            return acc;
        }

        private static long CountChunks(int milliseconds, int seed)
        {
            ulong x = 88172645463325252UL + (ulong)seed;
            long count = 0;
            double sink = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < milliseconds)
            {
                sink += Chunk(ref x);
                count++;
            }
            if (sink == 42.0) Debug.WriteLine(sink); // derleyicinin döngüyü silmesini önler
            return count;
        }

        private static long Median(List<long> v)
        {
            var s = v.OrderBy(x => x).ToList();
            return s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        }

        // --- Arka plan yükü: GetSystemTimes ile 1 saniyelik işlemci meşguliyeti ---
        [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint Low, High; public ulong V { get { return ((ulong)High << 32) | Low; } } }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
        private static double BusyPct(int ms)
        {
            FILETIME i1, k1, u1, i2, k2, u2;
            if (!GetSystemTimes(out i1, out k1, out u1)) return -1;
            Thread.Sleep(ms);
            if (!GetSystemTimes(out i2, out k2, out u2)) return -1;
            double idle = i2.V - i1.V, total = (k2.V - k1.V) + (u2.V - u1.V);   // çekirdek süresi boşta süreyi de içerir
            return total <= 0 ? -1 : Math.Round(Math.Max(0, Math.Min(100, (1 - idle / total) * 100)), 1);
        }

        // --- Yük altındaki hız ve hız sınırı: CallNtPowerInformation(ProcessorInformation) ---
        [StructLayout(LayoutKind.Sequential)] private struct PPI { public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState; }
        [DllImport("powrprof.dll")] private static extern uint CallNtPowerInformation(int level, IntPtr inBuf, uint inLen, [Out] PPI[] outBuf, uint outLen);
        private static bool Clock(out double cur, out double lim)
        {
            cur = lim = 0;
            try
            {
                int n = Math.Max(1, Environment.ProcessorCount);
                var a = new PPI[n];
                if (CallNtPowerInformation(11, IntPtr.Zero, 0, a, (uint)(n * Marshal.SizeOf(typeof(PPI)))) != 0) return false;
                double max = a.Average(p => (double)p.MaxMhz);
                if (max <= 0) return false;
                cur = a.Average(p => (double)p.CurrentMhz) / max * 100;
                lim = a.Min(p => (double)p.MhzLimit) / max * 100;
                return true;
            }
            catch { return false; }
        }

        // --- Bellek hızı: 64 MB'lık blokları tekrar tekrar kopyala (okuma + yazma) ---
        private static double MemoryRound()
        {
            const int N = 8 * 1024 * 1024;   // 8M × 8 bayt = 64 MB
            var src = new long[N]; var dst = new long[N];
            for (int i = 0; i < N; i += 512) src[i] = i;
            Array.Copy(src, dst, N);         // ısınma (sayfalar belleğe yerleşsin)
            var sw = Stopwatch.StartNew(); int reps = 0;
            while (sw.ElapsedMilliseconds < 350 || reps < 2) { Array.Copy(src, dst, N); reps++; }
            double sec = sw.Elapsed.TotalSeconds;
            if (dst[N - 512] == 42) Debug.WriteLine(42);
            return Math.Round(2.0 * N * 8 * reps / sec / 1e9, 2);
        }

        public static BenchInfo Run()
        {
            var total = Stopwatch.StartNew();
            var r = new BenchInfo { BenchVersion = 2, ThreadsUsed = Math.Max(1, Environment.ProcessorCount) };
            Thread.Sleep(300);
            r.BackgroundCpuPct = BusyPct(1000);   // tarama bittikten sonra arka plan yükü

            CountChunks(500, 0); // ısınma
            // Tek çekirdek: 5 tur, ortanca (tek bir şanslı/şanssız tur sonucu bozmasın)
            for (int k = 0; k < 5; k++) r.SingleRuns.Add(CountChunks(1000, k + 1));

            // Çok çekirdek: 3 tur, ortanca; yük altındayken hız ve hız sınırı örneklenir
            int n = r.ThreadsUsed; var clk = new List<double>(); var lim = new List<double>();
            for (int round = 0; round < 3; round++)
            {
                var counts = new long[n];
                var threads = new List<Thread>();
                for (int t = 0; t < n; t++)
                {
                    int idx = t;
                    threads.Add(new Thread(() => counts[idx] = CountChunks(1500, idx + 2)) { IsBackground = true });
                }
                foreach (var th in threads) th.Start();
                for (int s = 0; s < 3; s++) { Thread.Sleep(400); double c, l; if (Clock(out c, out l)) { clk.Add(c); lim.Add(l); } }
                foreach (var th in threads) th.Join();
                r.MultiRuns.Add(counts.Sum() * 10 / 15);
            }

            // Bellek hızı: 5 tur, en iyisi (bant genişliğinde en iyi tur arka plan gürültüsünden en az etkilenir)
            try
            {
                double best = 0;
                for (int k = 0; k < 5; k++) best = Math.Max(best, MemoryRound());
                r.MemoryGBs = best;
            }
            catch (OutOfMemoryException) { }
            GC.Collect();

            r.SingleThreadScore = Median(r.SingleRuns);
            r.MultiThreadScore = Median(r.MultiRuns);
            if (clk.Count > 0) { r.ClockPct = (int)Math.Round(clk.Average()); r.ClockLimitPct = (int)Math.Round(lim.Min()); }
            r.DurationMs = (int)total.ElapsedMilliseconds;
            return r;
        }
    }

    // ---------------- Pencere ----------------
    // ---------------- Dil ----------------
    // Windows dili Türkçeyse Türkçe, değilse İngilizce. (Test için: --lang=en)
    internal static class Lang
    {
        public static bool Tr = Detect();
        private static bool Detect()
        {
            var env = Environment.GetEnvironmentVariable("RMS_LANG");
            if (!string.IsNullOrEmpty(env)) return env.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
        }
        public static string Code { get { return Tr ? "tr" : "en"; } }
        public static string T(string tr, string en) { return Tr ? tr : en; }
        public static CultureInfo Num { get { return Tr ? new CultureInfo("tr-TR") : CultureInfo.InvariantCulture; } }
    }

    internal static class Steps
    {
        public static readonly string[] Keys = { "cpu", "gpu", "ram", "sys", "disk", "display", "peripherals", "os", "tweaks", "bench" };
        public static string Label(string k)
        {
            switch (k)
            {
                case "cpu": return Lang.T("İşlemci", "CPU");
                case "gpu": return Lang.T("Ekran kartı", "Graphics card");
                case "ram": return Lang.T("Bellek (RAM)", "Memory (RAM)");
                case "sys": return Lang.T("Sistem ve anakart", "System and motherboard");
                case "disk": return Lang.T("Diskler", "Drives");
                case "display": return Lang.T("Ekran ve monitör", "Display and monitor");
                case "peripherals": return Lang.T("Mouse, klavye, kulaklık", "Mouse, keyboard, headset");
                case "os": return Lang.T("İşletim sistemi", "Operating system");
                case "tweaks": return Lang.T("Oyun ayarları ve BIOS", "Gaming settings and BIOS");
                case "bench": return Lang.T("Performans testi (~15 sn)", "Performance test (~15 s)");
            }
            return k;
        }
    }

    // ---------------- Çizim yardımcıları ----------------
    internal static class Ui
    {
        public static Color Hex(string h) { return ColorTranslator.FromHtml(h); }
        public static readonly Color Bg = Hex("#0A0C10"), Surface = Hex("#141820"), Surface2 = Hex("#1A1F2A"), Line = Hex("#232937"),
            Ink = Hex("#EEF1F6"), Muted = Hex("#98A1B3"), Gold = Hex("#F2C230"), GoldInk = Hex("#1A1405"), Ok = Hex("#4ADE80"), Bad = Hex("#F87171");

        // Sitedeki skor renkleriyle aynı
        public static Color ScoreColor(int p)
        {
            return p >= 9000 ? Hex("#A78BFA") : p >= 8000 ? Hex("#22D3EE") : p >= 6500 ? Hex("#34D399") : p >= 5000 ? Hex("#FACC15") : p >= 3000 ? Hex("#FB923C") : Hex("#F87171");
        }
        public static System.Drawing.Drawing2D.GraphicsPath Round(RectangleF r, float rad)
        {
            var g = new System.Drawing.Drawing2D.GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { g.AddRectangle(r); return g; }
            g.AddArc(r.X, r.Y, d, d, 180, 90);
            g.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            g.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            g.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            g.CloseFigure();
            return g;
        }
        // RankMySetup kalkanı (sitedeki logonun aynısı, vektörel)
        public static void Logo(Graphics g, float x, float y, float size)
        {
            var st = g.Save();
            g.TranslateTransform(x, y);
            g.ScaleTransform(size / 32f, size / 32f);
            using (var outer = new System.Drawing.Drawing2D.GraphicsPath())
            using (var inner = new System.Drawing.Drawing2D.GraphicsPath())
            using (var gold = new SolidBrush(Hex("#E2AE1E")))
            using (var dark = new SolidBrush(Hex("#141820")))
            using (var pen = new Pen(Color.White, 2.6f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            {
                outer.AddLine(16f, 1f, 30f, 7.3f); outer.AddLine(30f, 7.3f, 30f, 17.7f);
                outer.AddBezier(30f, 17.7f, 30f, 25.3f, 24f, 30.7f, 16f, 33f);
                outer.AddBezier(16f, 33f, 8f, 30.7f, 2f, 25.3f, 2f, 17.7f);
                outer.AddLine(2f, 17.7f, 2f, 7.3f); outer.CloseFigure();
                inner.AddLine(16f, 5f, 26f, 9.6f); inner.AddLine(26f, 9.6f, 26f, 17.2f);
                inner.AddBezier(26f, 17.2f, 26f, 22.6f, 21.7f, 26.7f, 16f, 28.4f);
                inner.AddBezier(16f, 28.4f, 10.3f, 26.7f, 6f, 22.6f, 6f, 17.2f);
                inner.AddLine(6f, 17.2f, 6f, 9.6f); inner.CloseFigure();
                g.FillPath(gold, outer); g.FillPath(dark, inner);
                g.DrawLines(pen, new[] { new PointF(10f, 18.5f), new PointF(14.2f, 12.3f), new PointF(17.4f, 16.5f), new PointF(19.6f, 13.4f), new PointF(23.2f, 18.5f) });
            }
            g.Restore(st);
        }
    }

    // Site tarzı yuvarlak köşeli buton
    public class RmsButton : Control
    {
        public bool Primary;
        private bool _hover, _down;
        public RmsButton(string text, bool primary)
        {
            Text = text; Primary = primary; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Ui.Bg; Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        public int PreferredWidth(Graphics g, float s) { return (int)(g.MeasureString(Text, Font).Width + 40 * s); }
        protected override void OnPaint(PaintEventArgs e) { Draw(e.Graphics, true); }
        public void Draw(Graphics g, bool clear)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            if (clear) g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill = Primary ? Ui.Gold : Ui.Surface2, text = Primary ? Ui.GoldInk : Ui.Ink;
            if (!Enabled) { fill = Ui.Surface; text = Ui.Muted; }
            else if (_down) fill = ControlPaint.Dark(fill, 0.05f);
            else if (_hover) fill = Primary ? Ui.Hex("#F7D35C") : Ui.Hex("#232937");
            using (var path = Ui.Round(r, Height / 2.6f))
            using (var b = Primary && Enabled ? (Brush)new System.Drawing.Drawing2D.LinearGradientBrush(r, ControlPaint.Light(fill, 0.25f), fill, 90f) : new SolidBrush(fill))
            using (var pen = new Pen(Primary && Enabled ? ControlPaint.Light(fill, 0.4f) : Ui.Line, 1f))
            {
                g.FillPath(b, path); g.DrawPath(pen, path);
            }
            using (var tb = new SolidBrush(text))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(Text, Font, tb, new RectangleF(0, 0, Width, Height), sf);
        }
    }

    // Tüm pencereyi çizen tuval
    public class Canvas : Control
    {
        public Action<Graphics> Painter;
        public Canvas()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
        }
        protected override void OnPaint(PaintEventArgs e) { if (Painter != null) Painter(e.Graphics); }
    }

    // ---------------- Pencere ----------------
    public class ScannerForm : Form
    {
        private enum State { Scanning, Ready, Sending, Done, Error }
        private State _state = State.Scanning;
        private int _step = -1;
        private float _bar, _gauge, _pulse;
        private string _error;
        private UploadResult _up;
        private ScanReport _result;
        private readonly string _session, _demo, _shot;
        private string _resultUrl;
        private int _closeIn = -1;
        private bool _details;
        private readonly Canvas _canvas;
        private readonly RmsButton _primary, _secondary, _copyText, _copyJson;
        private readonly TextBox _detailsBox;
        private readonly System.Windows.Forms.Timer _timer;
        private float S = 1f;   // ekran ölçeği (DPI)
        private static readonly string DisplayFamily = FontFamily.Families.Any(x => x.Name == "Bahnschrift SemiBold") ? "Bahnschrift SemiBold" : FontFamily.Families.Any(x => x.Name == "Bahnschrift") ? "Bahnschrift" : "Segoe UI";
        private static Font D(float size) { return new Font(DisplayFamily, size, DisplayFamily == "Segoe UI" ? FontStyle.Bold : FontStyle.Regular); }
        private readonly Font _fTitle = D(24f), _fH = D(17f), _fBody = new Font("Segoe UI", 10.5f),
            _fSmall = new Font("Segoe UI", 9f), _fLabel = new Font("Segoe UI", 8.5f, FontStyle.Bold), _fScore = D(40f), _fBrand = D(16f), _fStep = new Font("Segoe UI", 10.5f, FontStyle.Regular),
            _fStepOn = new Font("Segoe UI", 10.5f, FontStyle.Bold), _fTipT = new Font("Segoe UI", 10f, FontStyle.Bold), _fNum = new Font("Segoe UI", 9f, FontStyle.Bold),
            _fRing = D(46f), _fDet = new Font("Segoe UI", 8.5f), _fTier = D(11f);
        private const int TickMs = 16;   // ~60 kare/sn
        private float _spin;             // tarama halkasındaki dönen ışık (derece)

        // Koyu başlık çubuğu: pencere uygulamayla tek parça görünsün (Windows 10 20H1+ / 11; desteklenmiyorsa sessizce geçer)
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1; if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4);
                int cap = 0x00100C0A; DwmSetWindowAttribute(Handle, 35, ref cap, 4);   // başlık rengi = arka plan (#0A0C10)
                int border = 0x00372923; DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { }
        }

        public ScannerForm(string session, string demo = null, string shot = null)
        {
            _session = session; _demo = demo; _shot = shot;
            Text = "RankMySetup Scanner";
            AutoScaleMode = AutoScaleMode.None;
            using (var g = CreateGraphics()) S = Math.Max(1f, g.DpiX / 96f);
            ClientSize = new Size((int)(800 * S), (int)(680 * S));
            MinimumSize = new Size((int)(680 * S), (int)(640 * S));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Ui.Bg; ForeColor = Ui.Ink;
            Font = new Font("Segoe UI", 10f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _canvas = new Canvas { Dock = DockStyle.Fill, Painter = Paint };
            _primary = new RmsButton(Lang.T("Raporumu gör", "See my report"), true);
            _secondary = new RmsButton(Lang.T("Ayrıntılar", "Details"), false);
            _copyText = new RmsButton(Lang.T("Raporu kopyala", "Copy report"), false);
            _copyJson = new RmsButton(Lang.T("JSON kopyala", "Copy JSON"), false);
            _detailsBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Ui.Surface, ForeColor = Ui.Ink, Font = new Font("Consolas", 9.5f), Visible = false };
            foreach (Control c in new Control[] { _primary, _secondary, _copyText, _copyJson, _detailsBox }) _canvas.Controls.Add(c);
            Controls.Add(_canvas);

            _primary.Click += async (s, e) => await OnPrimary();
            _secondary.Click += (s, e) => ToggleDetails();
            _copyText.Click += (s, e) => Copy(_detailsBox.Text, _copyText, Lang.T("Raporu kopyala", "Copy report"));
            _copyJson.Click += (s, e) => Copy(_result == null ? "" : Json.Pretty(Json.Serialize(_result)), _copyJson, Lang.T("JSON kopyala", "Copy JSON"));
            _canvas.Resize += (s, e) => LayoutButtons();

            _timer = new System.Windows.Forms.Timer { Interval = TickMs };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
            LayoutButtons();
            Shown += async (s, e) => { if (_demo != null) await RunDemo(); else await StartScan(); };
        }

        // ---- akış ----
        private async Task StartScan()
        {
            if (_session != null) { var _ = Api.MarkOpened(_session); }
            var p = new Progress<string>(k => { int i = Array.IndexOf(Steps.Keys, k); _step = k == "done" ? Steps.Keys.Length : i; _canvas.Invalidate(); });
            IProgress<string> ip = p;
            try
            {
                _result = await Task.Run(() => Scanner.Run(k => ip.Report(k)));
                _detailsBox.Text = BuildText(_result);
                _step = Steps.Keys.Length;
                if (_session != null) await SendAsync(_session, false);
                else SetState(State.Ready);
            }
            catch (Exception ex)
            {
                _error = Lang.T("Tarama sırasında bir hata oluştu.", "Something went wrong during the scan.") + Environment.NewLine + ex.Message;
                _detailsBox.Text = ex.ToString();
                SetState(State.Error);
            }
        }

        private async Task OnPrimary()
        {
            if (_details) { ToggleDetails(); return; }
            if (_state == State.Done && _resultUrl != null) { OpenUrl(_resultUrl); return; }
            if (_state == State.Ready || _state == State.Error)
            {
                if (_result == null) { SetState(State.Scanning); _step = -1; await StartScan(); }
                else await SendAsync(null, true);
            }
        }

        private async Task SendAsync(string session, bool openBrowser)
        {
            SetState(State.Sending);
            try
            {
                if (session == null) session = await Api.CreateSession();
                _up = await Api.Upload(session, Json.Serialize(_result));
                _resultUrl = _up.Url;
                _gauge = 0;
                SetState(State.Done);
                if (openBrowser) OpenUrl(_up.Url);
                else _closeIn = (_up.Tips.Count > 0 ? 20000 : 9000) / TickMs;   // site sonucu kendi gösteriyor: öneriler okunsun diye ~20 sn, yoksa ~9 sn sonra kapan
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                SetState(State.Error);
            }
        }

        private async Task RunDemo()
        {
            // Yalnızca tasarım önizlemesi için: tarama yapmaz, internete bağlanmaz
            _result = DemoReport(); Scanner.Current = _result;
            _detailsBox.Text = BuildText(_result);
            for (int i = 0; i <= (_demo == "scan" ? 4 : Steps.Keys.Length); i++) { _step = i; _canvas.Invalidate(); await Task.Delay(_shot != null ? 20 : 350); }
            if (_demo == "ready") SetState(State.Ready);
            else if (_demo == "error") { _error = Lang.T("RankMySetup’a bağlanılamadı. İnternet bağlantını kontrol edip tekrar dene.", "Couldn’t reach RankMySetup. Check your internet connection and try again."); SetState(State.Error); }
            else if (_demo != "scan")
            {
                _up = new UploadResult { Points = 5789, Verified = true, Url = AppInfo.BaseUrl, Tier = "silver" };
                _up.Tips.Add(new[] { Lang.T("Güç planını Yüksek performans yap", "Switch to the High performance power plan"), Lang.T("Ücretsiz: Denetim Masası → Güç Seçenekleri. İşlemci oyunda daha hızlı tepki verir.", "Free: Control Panel → Power Options. The CPU responds faster in games.") });
                _up.Tips.Add(new[] { Lang.T("GPU zamanlamasını aç", "Turn on GPU scheduling"), Lang.T("Ücretsiz: Ayarlar → Ekran → Grafik, sonra yeniden başlat.", "Free: Settings → Display → Graphics, then restart.") });
                _up.Tips.Add(new[] { Lang.T("Ekran kartı: RTX 5060 / RX 9060 XT", "Graphics card: RTX 5060 / RX 9060 XT"), Lang.T("En büyük fark bu. Skoruna +14,2 puan.", "The biggest difference. +14.2 points to your score.") });
                _resultUrl = _up.Url; SetState(State.Done);
            }
            if (_shot != null)
            {
                await Task.Delay(2400);   // animasyonlar bitsin
                using (var bmp = new Bitmap(_canvas.ClientSize.Width, _canvas.ClientSize.Height))
                using (var g = Graphics.FromImage(bmp))
                {
                    Paint(g);
                    foreach (var b in new[] { _primary, _secondary, _copyText, _copyJson })
                    {
                        if (!b.Visible) continue;
                        var st = g.Save(); g.TranslateTransform(b.Left, b.Top); b.Draw(g, false); g.Restore(st);
                    }
                    bmp.Save(_shot, System.Drawing.Imaging.ImageFormat.Png);
                }
                Close();
            }
        }

        private static ScanReport DemoReport()
        {
            var r = new ScanReport();
            r.Cpu = new CpuInfo { Name = "AMD Ryzen 7 5700X 8-Core Processor", Cores = 8, Threads = 16, MaxClockMHz = 3400 };
            r.Gpus.Add(new GpuInfo { Name = "AMD Radeon RX 5500 XT", VramMB = 8192, VramSource = "directx" });
            r.Ram.TotalGB = 16; r.Ram.SlotsTotal = 2;
            r.Ram.Modules.Add(new RamModule { CapacityGB = 8, Type = "DDR4", SpeedMHz = 3200, ConfiguredSpeedMHz = 3200 });
            r.Ram.Modules.Add(new RamModule { CapacityGB = 8, Type = "DDR4", SpeedMHz = 3200, ConfiguredSpeedMHz = 3200 });
            r.Displays.Add(new DisplayInfo { Width = 1920, Height = 1080, RefreshHz = 144, Primary = true });
            r.MonitorNames.Add("AOC 24G1WG4");
            r.Peripherals.Add(new PeripheralInfo { Kind = "headset", Name = "HyperX Cloud II Wireless", Brand = "HyperX", Wireless = true });
            r.Peripherals.Add(new PeripheralInfo { Kind = "mouse", Brand = "Logitech", Wireless = true, Receiver = true });
            r.Os.Name = "Windows 11 Pro"; r.Os.Build = "26300"; r.Os.Architecture = "64-bit";
            r.Sys.PowerPlan = "Balanced"; r.Sys.GameMode = true; r.Sys.Hags = false; r.Sys.SystemDriveFreeGB = 182; r.Sys.SystemDriveSizeGB = 476;
            return r;
        }

        private void SetState(State s)
        {
            _state = s;
            if (s != State.Done) _closeIn = -1;
            LayoutButtons();
            _canvas.Invalidate();
        }

        private void ToggleDetails()
        {
            _details = !_details;
            _closeIn = -1;
            LayoutButtons();
            _canvas.Invalidate();
        }

        private void Tick()
        {
            bool busy = _state == State.Scanning || _state == State.Sending;
            float target = _state == State.Scanning ? Math.Max(0, _step) / (float)Steps.Keys.Length : 1f;
            _bar += (target - _bar) * 0.08f;
            _pulse = (_pulse + 0.03f) % 1f;
            _spin = (_spin + 4f) % 360f;
            if (_state == State.Done && _gauge < 1f) _gauge = Math.Min(1f, _gauge + 0.0125f);
            if (_closeIn > 0 && --_closeIn == 0) { Close(); return; }
            if (busy || (_state == State.Done && (_gauge < 1f || _closeIn > 0)) || Math.Abs(target - _bar) > 0.002f) _canvas.Invalidate();
        }

        // ---- düzen ----
        private void LayoutButtons()
        {
            if (_canvas == null) return;
            int w = _canvas.ClientSize.Width, h = _canvas.ClientSize.Height, pad = (int)(32 * S), bh = (int)(44 * S), gap = (int)(10 * S);
            var visible = new List<RmsButton>();
            _primary.Visible = _secondary.Visible = _copyText.Visible = _copyJson.Visible = false;
            if (_details)
            {
                _primary.Text = Lang.T("Geri", "Back"); _primary.Primary = false;
                visible.Add(_copyText); visible.Add(_copyJson); visible.Add(_primary);
            }
            else if (_state == State.Ready || _state == State.Done || _state == State.Error)
            {
                _primary.Primary = true;
                _primary.Text = _state == State.Error ? Lang.T("Tekrar dene", "Try again") : Lang.T("Raporumu gör", "See my report");
                _secondary.Text = Lang.T("Ayrıntılar", "Details");
                visible.Add(_primary); visible.Add(_secondary);
            }
            int x = pad;
            using (var g = CreateGraphics())
                foreach (var b in visible)
                {
                    int bw = Math.Max((int)(120 * S), b.PreferredWidth(g, S));
                    b.SetBounds(x, h - pad - bh, bw, bh); b.Visible = true; b.Enabled = true; b.Invalidate();
                    x += bw + gap;
                }
            _detailsBox.Visible = _details;
            if (_details) _detailsBox.SetBounds(pad, (int)(96 * S), w - 2 * pad, h - (int)(96 * S) - pad - bh - (int)(20 * S));
        }

        // ---- çizim ----
        private void Card(Graphics g, RectangleF r, Color fill, Color border, float radius)
        {
            using (var p = Ui.Round(r, radius))
            using (var b = new SolidBrush(fill))
            using (var pen = new Pen(border, 1f))
            { g.FillPath(b, p); g.DrawPath(pen, p); }
        }

        private new void Paint(Graphics g)
        {
            int w = _canvas.ClientSize.Width, h = _canvas.ClientSize.Height;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Ui.Bg);
            // arka plan ışıkları (skor rengi ya da altın) + hafif ızgara
            Color glow = _state == State.Done && _up != null ? Ui.ScoreColor(_up.Points) : _state == State.Error ? Ui.Bad : Ui.Gold;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddEllipse(w * 0.40f, -h * 0.50f, w * 0.95f, h * 1.0f);
                using (var pb = new System.Drawing.Drawing2D.PathGradientBrush(path) { CenterColor = Color.FromArgb(52, glow), SurroundColors = new[] { Color.FromArgb(0, glow) } })
                    g.FillPath(pb, path);
            }
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddEllipse(-w * 0.35f, h * 0.55f, w * 0.8f, h * 0.8f);
                using (var pb = new System.Drawing.Drawing2D.PathGradientBrush(path) { CenterColor = Color.FromArgb(18, Ui.Hex("#22D3EE")), SurroundColors = new[] { Color.FromArgb(0, Ui.Hex("#22D3EE")) } })
                    g.FillPath(pb, path);
            }
            using (var dot = new SolidBrush(Color.FromArgb(10, Color.White)))
                for (float gx = 24 * S; gx < w; gx += 28 * S)
                    for (float gy = 96 * S; gy < h; gy += 28 * S) g.FillRectangle(dot, gx, gy, 1.4f * S, 1.4f * S);
            // üst bölüm
            float pad = 32 * S;
            Ui.Logo(g, pad, 22 * S, 34 * S);
            using (var ink = new SolidBrush(Ui.Ink)) g.DrawString("RankMySetup", _fBrand, ink, pad + 44 * S, 22 * S);
            string ver = "Scanner " + AppInfo.Version;
            var vs = g.MeasureString(ver, _fLabel);
            var vr = new RectangleF(w - pad - vs.Width - 20 * S, 26 * S, vs.Width + 20 * S, 26 * S);
            Card(g, vr, Color.FromArgb(160, Ui.Surface2), Ui.Line, 13 * S);
            using (var mut = new SolidBrush(Ui.Muted)) g.DrawString(ver, _fLabel, mut, vr, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            using (var lb = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(0, 75 * S, w, 2 * S), Color.FromArgb(170, Ui.Gold), Color.FromArgb(0, Ui.Gold), 0f))
                g.FillRectangle(lb, 0, 76 * S, w * 0.7f, 1.2f * S);
            using (var pen = new Pen(Color.FromArgb(120, Ui.Line))) g.DrawLine(pen, w * 0.7f, 76 * S, w, 76 * S);

            if (_details)
            {
                using (var ink = new SolidBrush(Ui.Ink)) g.DrawString(Lang.T("Teknik rapor", "Technical report"), _fH, ink, pad - 2 * S, 90 * S - 22 * S);
                return;
            }
            float top = 104 * S;
            if (_state == State.Scanning || _state == State.Sending) PaintProgress(g, w, h, pad, top);
            else if (_state == State.Ready) PaintReady(g, w, pad, top);
            else if (_state == State.Done) PaintDone(g, w, h, pad, top);
            else PaintError(g, w, pad, top);
        }

        // Adım tamamlanınca altında gösterilen bulunan donanım (tarama sürerken rapor değişebileceği için hata yutulur)
        private string StepDetail(string k)
        {
            var r = Scanner.Current; if (r == null) return "";
            try
            {
                switch (k)
                {
                    case "cpu": return r.Cpu == null ? "" : Regex.Replace(r.Cpu.Name ?? "", @"\s*\d+-Core Processor", "") + (r.Cpu.Cores > 0 ? " · " + r.Cpu.Cores + Lang.T(" çekirdek", " cores") : "");
                    case "gpu": { var gp = r.Gpus.ToList(); var x = gp.FirstOrDefault(q => !q.Integrated) ?? gp.FirstOrDefault(); return x == null ? "" : x.Name + (x.VramMB >= 1024 ? " · " + Math.Round(x.VramMB.Value / 1024.0) + " GB" : ""); }
                    case "ram": { var m = r.Ram.Modules.ToList().FirstOrDefault(); return r.Ram.TotalGB <= 0 ? "" : Math.Round(r.Ram.TotalGB) + " GB" + (m != null ? " " + m.Type + " " + (m.ConfiguredSpeedMHz > 0 ? m.ConfiguredSpeedMHz : m.SpeedMHz) + " MHz" : ""); }
                    case "sys": return r.Machine.IsLaptop ? (r.Machine.Manufacturer + " " + r.Machine.Model).Trim() : (r.Machine.BoardManufacturer + " " + r.Machine.BoardProduct).Trim();
                    case "disk": { var d = r.Disks.ToList(); double tb = d.Sum(x => x.SizeGB) / 1024.0; return d.Count == 0 ? "" : d.Count + Lang.T(" disk · ", " drives · ") + (tb >= 1 ? tb.ToString("0.0", Lang.Num) + " TB" : Math.Round(tb * 1024) + " GB") + (d.Any(x => x.Bus == "NVMe") ? " · NVMe" : ""); }
                    case "display": { var ds = r.Displays.ToList(); var x = ds.FirstOrDefault(q => q.Primary) ?? ds.FirstOrDefault(); return x == null ? "" : x.Width + "×" + x.Height + " · " + x.RefreshHz + " Hz" + (ds.Count > 1 ? " · " + ds.Count + Lang.T(" ekran", " displays") : ""); }
                    case "peripherals": { var p = r.Peripherals.ToList().Select(x => x.Receiver ? x.Brand : x.Name).Where(x => !string.IsNullOrEmpty(x)).ToList(); return p.Count == 0 ? Lang.T("Adı okunan cihaz yok", "No named devices") : string.Join(" · ", p); }
                    case "os": return r.Os.Name ?? "";
                    case "tweaks": return r.Sys == null ? "" : (r.Sys.PowerPlan ?? "") + (r.Sys.GameMode == true ? Lang.T(" · Oyun Modu açık", " · Game Mode on") : "");
                    case "bench": return r.Bench == null ? "" : Lang.T("Tek ", "Single ") + r.Bench.SingleThreadScore.ToString("N0", Lang.Num) + Lang.T(" · Çok ", " · Multi ") + r.Bench.MultiThreadScore.ToString("N0", Lang.Num);
                }
            }
            catch { }
            return "";
        }

        private void PaintProgress(Graphics g, int w, int h, float pad, float top)
        {
            bool sending = _state == State.Sending;
            using (var ink = new SolidBrush(Ui.Ink)) g.DrawString(sending ? Lang.T("Sonuç gönderiliyor", "Sending your result") : Lang.T("Bilgisayarın taranıyor", "Scanning your PC"), _fTitle, ink, pad - 3 * S, top - 4 * S);
            using (var mut = new SolidBrush(Ui.Muted))
                g.DrawString(sending ? Lang.T("Skorun ve raporun hazırlanıyor…", "Preparing your score and report…") : Lang.T("Dosyalarına ve kişisel bilgilerine dokunulmaz. Yaklaşık 25 saniye sürer.", "Your files and personal data are never touched. Takes about 25 seconds."), _fBody, mut, pad, top + 40 * S);
            float frac = sending ? 1f : _bar;
            // --- tarama halkası ---
            float R = 236 * S, cx = pad + R / 2, cy = top + 96 * S + R / 2, th = 14 * S;
            var ring = new RectangleF(cx - R / 2 + th, cy - R / 2 + th, R - 2 * th, R - 2 * th);
            using (var glow = new System.Drawing.Drawing2D.GraphicsPath())
            {
                glow.AddEllipse(cx - R * 0.62f, cy - R * 0.62f, R * 1.24f, R * 1.24f);
                using (var pb = new System.Drawing.Drawing2D.PathGradientBrush(glow) { CenterColor = Color.FromArgb(40, Ui.Gold), SurroundColors = new[] { Color.FromArgb(0, Ui.Gold) } }) g.FillPath(pb, glow);
            }
            using (var pen = new Pen(Color.FromArgb(150, Ui.Line), th)) g.DrawEllipse(pen, ring);
            // ince dış halka ve 60 çentik
            using (var pen = new Pen(Color.FromArgb(70, Ui.Line), 1.2f * S)) g.DrawEllipse(pen, cx - R / 2, cy - R / 2, R, R);
            for (int t = 0; t < 60; t++)
            {
                double a = t * Math.PI / 30; float r1 = R / 2 - 4 * S, r2 = R / 2 - (t % 5 == 0 ? 9 : 6) * S;
                bool lit = t / 60f <= frac;
                using (var pen = new Pen(lit ? Color.FromArgb(200, Ui.Gold) : Color.FromArgb(70, Ui.Muted), (t % 5 == 0 ? 1.8f : 1f) * S))
                    g.DrawLine(pen, cx + (float)Math.Sin(a) * r1, cy - (float)Math.Cos(a) * r1, cx + (float)Math.Sin(a) * r2, cy - (float)Math.Cos(a) * r2);
            }
            if (frac > 0.005f)
            {
                using (var lb = new System.Drawing.Drawing2D.LinearGradientBrush(ring, Ui.Hex("#FB923C"), Ui.Gold, 45f))
                {
                    using (var pen = new Pen(Color.FromArgb(55, Ui.Gold), th * 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) g.DrawArc(pen, ring, -90, 360 * frac);
                    using (var pen = new Pen(lb, th) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) g.DrawArc(pen, ring, -90, 360 * frac);
                }
            }
            // dönen tarama ışığı
            if (!sending || frac < 1f)
                for (int k2 = 0; k2 < 14; k2++)
                    using (var pen = new Pen(Color.FromArgb((int)(12 + 13 * k2), Color.White), 2.4f * S)) g.DrawArc(pen, ring.X - 9 * S, ring.Y - 9 * S, ring.Width + 18 * S, ring.Height + 18 * S, _spin - 90 + k2 * 3, 3);
            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var ink = new SolidBrush(Ui.Ink)) g.DrawString((Lang.Tr ? "%" : "") + Math.Round(frac * 100) + (Lang.Tr ? "" : "%"), _fRing, ink, new RectangleF(cx - R / 2, cy - 34 * S, R, 60 * S), center);
            using (var mut = new SolidBrush(Ui.Muted)) g.DrawString(sending ? Lang.T("GÖNDERİLİYOR", "SENDING") : Lang.T("TARANIYOR", "SCANNING"), _fLabel, mut, new RectangleF(cx - R / 2, cy + 22 * S, R, 20 * S), center);
            int cur = Math.Max(0, Math.Min(Steps.Keys.Length - 1, _step));
            using (var gold = new SolidBrush(Ui.Gold))
                g.DrawString(sending ? Lang.T("Rapor hazırlanıyor", "Preparing report") : Steps.Label(Steps.Keys[cur]) + "…", _fH, gold, new RectangleF(pad, cy + R / 2 + 14 * S, R, 30 * S), new StringFormat { Alignment = StringAlignment.Center });
            // --- adımlar ve bulunan donanım ---
            int n = Steps.Keys.Length;
            float x0 = pad + R + 36 * S, cw = w - x0 - pad, rowH = 47 * S, y0 = top + 88 * S;
            for (int i = 0; i < n; i++)
            {
                float y = y0 + i * rowH;
                bool done = sending || i < _step, active = !sending && i == _step;
                if (active) Card(g, new RectangleF(x0 - 10 * S, y - 4 * S, cw + 10 * S, rowH - 4 * S), Color.FromArgb(22, Ui.Gold), Color.FromArgb(110, Ui.Gold), 10 * S);
                var c = new RectangleF(x0, y + 6 * S, 22 * S, 22 * S);
                if (i < n - 1) using (var pen = new Pen(done ? Color.FromArgb(120, Ui.Ok) : Ui.Line, 1.5f * S)) g.DrawLine(pen, c.X + 11 * S, c.Bottom + 3 * S, c.X + 11 * S, y + rowH + 3 * S);
                if (done)
                {
                    using (var b = new SolidBrush(Color.FromArgb(45, Ui.Ok))) g.FillEllipse(b, c);
                    using (var pen = new Pen(Ui.Ok, 2.2f * S) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                        g.DrawLines(pen, new[] { new PointF(c.X + 6 * S, c.Y + 11.5f * S), new PointF(c.X + 9.5f * S, c.Y + 15 * S), new PointF(c.X + 16 * S, c.Y + 7.5f * S) });
                }
                else if (active)
                {
                    float r = 4 * S + 7 * S * _pulse;
                    using (var b = new SolidBrush(Color.FromArgb((int)(90 * (1 - _pulse)), Ui.Gold))) g.FillEllipse(b, c.X + 11 * S - r, c.Y + 11 * S - r, 2 * r, 2 * r);
                    using (var b = new SolidBrush(Ui.Gold)) g.FillEllipse(b, c.X + 6 * S, c.Y + 6 * S, 10 * S, 10 * S);
                }
                else using (var pen = new Pen(Ui.Line, 2f * S)) g.DrawEllipse(pen, c.X + 2 * S, c.Y + 2 * S, c.Width - 4 * S, c.Height - 4 * S);
                var nf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                string det = done ? StepDetail(Steps.Keys[i]) : active ? Lang.T("taranıyor…", "scanning…") : "";
                using (var tb = new SolidBrush(done || active ? Ui.Ink : Ui.Muted))
                    g.DrawString(Steps.Label(Steps.Keys[i]), active ? _fStepOn : _fStep, tb, new RectangleF(x0 + 34 * S, y + (det.Length > 0 ? 0 : 7 * S), cw - 34 * S, 22 * S), nf);
                if (det.Length > 0)
                    using (var mb = new SolidBrush(active ? Color.FromArgb(200, Ui.Gold) : Ui.Muted)) g.DrawString(det, _fDet, mb, new RectangleF(x0 + 34 * S, y + 20 * S, cw - 34 * S, 18 * S), nf);
            }
        }

        private List<string[]> Summary()
        {
            var list = new List<string[]>();
            if (_result == null) return list;
            var gpu = _result.Gpus.FirstOrDefault(x => !x.Integrated) ?? _result.Gpus.FirstOrDefault();
            if (gpu != null) list.Add(new[] { Lang.T("EKRAN KARTI", "GRAPHICS CARD"), gpu.Name + (gpu.VramMB.HasValue && gpu.VramMB >= 1024 ? " · " + Math.Round(gpu.VramMB.Value / 1024.0) + " GB" : "") });
            if (_result.Cpu != null) list.Add(new[] { Lang.T("İŞLEMCİ", "CPU"), Regex.Replace(_result.Cpu.Name ?? "", @"\s*\d+-Core Processor", "") });
            if (_result.Ram != null && _result.Ram.TotalGB > 0)
            {
                var m = _result.Ram.Modules.FirstOrDefault();
                list.Add(new[] { "RAM", Math.Round(_result.Ram.TotalGB) + " GB" + (m != null ? " " + m.Type + " " + (m.ConfiguredSpeedMHz > 0 ? m.ConfiguredSpeedMHz : m.SpeedMHz) + " MHz" : "") });
            }
            var d = _result.Displays.FirstOrDefault(x => x.Primary) ?? _result.Displays.FirstOrDefault();
            if (d != null) list.Add(new[] { Lang.T("MONİTÖR", "MONITOR"), (_result.MonitorNames.Count > 0 ? _result.MonitorNames[0] + " · " : "") + d.Width + "×" + d.Height + " · " + d.RefreshHz + " Hz" });
            var gear = _result.Peripherals.Select(p => p.Receiver ? p.Brand + Lang.T(" kablosuz alıcı", " wireless receiver") : p.Name).Where(x => !string.IsNullOrEmpty(x)).ToList();
            if (gear.Count > 0) list.Add(new[] { Lang.T("EKİPMANLAR", "GEAR"), string.Join(" · ", gear) });
            return list;
        }

        private void PaintSummary(Graphics g, float x, float y, float width, int max = 9)
        {
            foreach (var row in Summary().Take(max))
            {
                using (var mut = new SolidBrush(Ui.Muted)) g.DrawString(row[0], _fLabel, mut, x, y);
                using (var ink = new SolidBrush(Ui.Ink)) g.DrawString(row[1], _fBody, ink, new RectangleF(x, y + 17 * S, width, 24 * S), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                y += 48 * S;
            }
        }

        private void PaintReady(Graphics g, int w, float pad, float top)
        {
            using (var ink = new SolidBrush(Ui.Ink)) g.DrawString(Lang.T("Tarama tamamlandı", "Scan complete"), _fTitle, ink, pad - 3 * S, top);
            using (var mut = new SolidBrush(Ui.Muted)) g.DrawString(Lang.T("Skorunu, oyunlardaki FPS’ini ve sana özel raporunu görmek için “Raporumu gör”e bas.", "Click “See my report” to get your score, game FPS and personal report."), _fBody, mut, new RectangleF(pad, top + 44 * S, w - 2 * pad, 50 * S));
            int rows = Summary().Count;
            Card(g, new RectangleF(pad, top + 96 * S, w - 2 * pad, rows * 48 * S + 24 * S), Color.FromArgb(150, Ui.Surface), Ui.Line, 14 * S);
            PaintSummary(g, pad + 20 * S, top + 110 * S, w - 2 * pad - 40 * S);
        }

        private void PaintDone(Graphics g, int w, int h, float pad, float top)
        {
            int pts = _up != null ? _up.Points : 0;
            Color col = Ui.ScoreColor(pts);
            bool tips = _up != null && _up.Tips.Count > 0;
            // skor göstergesi (sitedeki gibi 270°) + skor renginde ışık ve çentikler
            float size = 200 * S, gx = pad, gy = top, th = 15 * S;
            var arc = new RectangleF(gx + th / 2, gy + th / 2, size - th, size - th);
            float ease = 1 - (float)Math.Pow(1 - _gauge, 3), sweep = Math.Max(1f, 270f * Math.Min(1f, pts / 10000f) * ease);
            using (var glow = new System.Drawing.Drawing2D.GraphicsPath())
            {
                glow.AddEllipse(gx - size * 0.2f, gy - size * 0.2f, size * 1.4f, size * 1.4f);
                using (var pb = new System.Drawing.Drawing2D.PathGradientBrush(glow) { CenterColor = Color.FromArgb((int)(60 * ease), col), SurroundColors = new[] { Color.FromArgb(0, col) } }) g.FillPath(pb, glow);
            }
            for (int t = 0; t <= 27; t++)
            {
                double a = (135 + t * 10) * Math.PI / 180; float r1 = size / 2 + 4 * S, r2 = size / 2 + (t % 9 == 0 ? 11 : 7) * S, ccx = gx + size / 2, ccy = gy + size / 2;
                using (var pen = new Pen(t * 10 <= sweep ? Color.FromArgb(200, col) : Color.FromArgb(60, Ui.Muted), (t % 9 == 0 ? 1.8f : 1f) * S))
                    g.DrawLine(pen, ccx + (float)Math.Cos(a) * r1, ccy + (float)Math.Sin(a) * r1, ccx + (float)Math.Cos(a) * r2, ccy + (float)Math.Sin(a) * r2);
            }
            using (var pen = new Pen(Ui.Line, th) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) g.DrawArc(pen, arc, 135, 270);
            using (var pen = new Pen(Color.FromArgb(45, col), th * 1.7f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) g.DrawArc(pen, arc, 135, sweep);
            using (var pen = new Pen(col, th) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) g.DrawArc(pen, arc, 135, sweep);
            var center = new StringFormat { Alignment = StringAlignment.Center };
            using (var mut = new SolidBrush(Ui.Muted)) g.DrawString(Lang.T("RMS SKOR", "RMS SCORE"), _fLabel, mut, new RectangleF(gx, gy + size * 0.30f, size, 20 * S), center);
            using (var ink = new SolidBrush(Ui.Ink)) g.DrawString((pts * ease / 100.0).ToString("0.0", Lang.Num), _fScore, ink, new RectangleF(gx, gy + size * 0.38f, size, 70 * S), center);
            using (var mut = new SolidBrush(Ui.Muted)) g.DrawString("/ 100", _fBody, mut, new RectangleF(gx, gy + size * 0.66f, size, 24 * S), center);
            // sağ sütun
            float x = gx + size + 34 * S, cw = w - x - pad;
            if (_up != null)
            {
                string pill = _up.Verified ? "✓ " + Lang.T("Doğrulanmış tarama", "Verified scan") : Lang.T("Doğrulanmamış", "Unverified");
                Color pc = _up.Verified ? Ui.Ok : Ui.Hex("#FBBF24");
                var ps = g.MeasureString(pill, _fLabel);
                var pr = new RectangleF(x, top + 2 * S, ps.Width + 22 * S, 26 * S);
                using (var p = Ui.Round(pr, 13 * S)) using (var b = new SolidBrush(Color.FromArgb(30, pc))) using (var pen = new Pen(Color.FromArgb(120, pc))) { g.FillPath(b, p); g.DrawPath(pen, p); }
                using (var tb = new SolidBrush(pc)) g.DrawString(pill, _fLabel, tb, pr, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                // lig rozeti (sitedeki ligler)
                string tier = TierName(_up.Tier);
                if (tier != null)
                {
                    var ts = g.MeasureString(tier, _fTier);
                    var tr = new RectangleF(pr.Right + 8 * S, pr.Y, ts.Width + 24 * S, pr.Height);
                    using (var p = Ui.Round(tr, 13 * S)) using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(tr, Color.FromArgb(70, col), Color.FromArgb(20, col), 0f)) using (var pen = new Pen(Color.FromArgb(160, col))) { g.FillPath(b, p); g.DrawPath(pen, p); }
                    using (var tb = new SolidBrush(col)) g.DrawString(tier, _fTier, tb, tr, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
            }
            using (var ink = new SolidBrush(Ui.Ink)) g.DrawString(Lang.T("Skorun hazır!", "Your score is ready!"), _fH, ink, x - 2 * S, top + 36 * S);
            PaintSummary(g, x, top + 78 * S, cw, tips ? 3 : 5);
            // sunucunun 3 önerisi
            if (tips)
            {
                float ty = top + 222 * S, tgap = 12 * S, n = _up.Tips.Count, tw = (w - 2 * pad - tgap * (n - 1)) / n, tch = 124 * S;
                using (var gold = new SolidBrush(Ui.Gold)) g.DrawString(Lang.T("SANA ÖZEL ÖNERİLER", "TIPS FOR YOUR PC"), _fLabel, gold, pad, ty);
                for (int i = 0; i < _up.Tips.Count; i++)
                {
                    var cr = new RectangleF(pad + i * (tw + tgap), ty + 24 * S, tw, tch);
                    Card(g, cr, Color.FromArgb(170, Ui.Surface), Ui.Line, 14 * S);
                    using (var lb = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(cr.X, cr.Y, cr.Width, 3 * S), Color.FromArgb(200, Ui.Gold), Color.FromArgb(0, Ui.Gold), 0f))
                        g.FillRectangle(lb, cr.X + 14 * S, cr.Y, cr.Width * 0.6f, 2f * S);
                    var nc = new RectangleF(cr.X + 14 * S, cr.Y + 14 * S, 24 * S, 24 * S);
                    using (var b = new SolidBrush(Color.FromArgb(40, Ui.Gold))) g.FillEllipse(b, nc);
                    using (var tb = new SolidBrush(Ui.Gold)) g.DrawString((i + 1).ToString(), _fNum, tb, nc, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                    using (var ink = new SolidBrush(Ui.Ink))
                        g.DrawString(_up.Tips[i][0], _fTipT, ink, new RectangleF(cr.X + 46 * S, cr.Y + 12 * S, cr.Width - 58 * S, 40 * S), new StringFormat { Trimming = StringTrimming.EllipsisWord });
                    using (var mut = new SolidBrush(Ui.Muted))
                        g.DrawString(_up.Tips[i][1], _fSmall, mut, new RectangleF(cr.X + 14 * S, cr.Y + 54 * S, cr.Width - 28 * S, cr.Height - 62 * S), new StringFormat { Trimming = StringTrimming.EllipsisWord });
                }
            }
            string note = _closeIn > 0 ? Lang.T("Raporun tarayıcında açıldı. Bu pencere ", "Your report opened in your browser. This window closes in ") + (int)Math.Ceiling(_closeIn * TickMs / 1000.0) + Lang.T(" sn sonra kapanacak.", " s.")
                : Lang.T("Raporunda FPS tahminleri, darboğaz analizi ve yükseltme önerileri seni bekliyor.", "Your report has FPS estimates, bottleneck analysis and upgrade tips.");
            using (var mut = new SolidBrush(Ui.Muted)) g.DrawString(note, _fSmall, mut, new RectangleF(pad, h - 32 * S - 44 * S - 30 * S, w - 2 * pad, 22 * S));
        }

        private static string TierName(string k)
        {
            switch ((k ?? "").ToLowerInvariant())
            {
                case "iron": return Lang.T("DEMİR", "IRON");
                case "bronze": return Lang.T("BRONZ", "BRONZE");
                case "silver": return Lang.T("GÜMÜŞ", "SILVER");
                case "gold": return Lang.T("ALTIN", "GOLD");
                case "platinum": return Lang.T("PLATİN", "PLATINUM");
                case "diamond": return Lang.T("ELMAS", "DIAMOND");
                case "legend": return Lang.T("EFSANE", "LEGEND");
            }
            return null;
        }

        private void PaintError(Graphics g, int w, float pad, float top)
        {
            using (var bad = new SolidBrush(Ui.Bad)) g.DrawString(Lang.T("Bir sorun oluştu", "Something went wrong"), _fTitle, bad, pad - 3 * S, top);
            using (var ink = new SolidBrush(Ui.Ink)) g.DrawString(_error ?? "", _fBody, ink, new RectangleF(pad, top + 48 * S, w - 2 * pad, 120 * S));
        }

        // ---- yardımcılar ----
        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private void Copy(string text, RmsButton b, string label)
        {
            try { Clipboard.SetText(string.IsNullOrEmpty(text) ? " " : text); b.Text = Lang.T("Kopyalandı ✓", "Copied ✓"); }
            catch { b.Text = Lang.T("Kopyalanamadı", "Couldn’t copy"); }
            var t = new System.Windows.Forms.Timer { Interval = 1600 };
            t.Tick += (s, e) => { b.Text = label; t.Stop(); t.Dispose(); };
            t.Start();
        }

        private static string Health(string h)
        {
            switch (h)
            {
                case "Healthy": return Lang.T("Sağlıklı", "Healthy");
                case "Warning": return Lang.T("Uyarı", "Warning");
                case "Unhealthy": return Lang.T("Sorunlu", "Failing");
                default: return Lang.T("Bilinmiyor", "Unknown");
            }
        }

        private static string BuildText(ScanReport r)
        {
            var sb = new StringBuilder();
            string nl = Environment.NewLine;
            sb.Append(Lang.T("İŞLEMCİ", "CPU") + nl);
            if (r.Cpu != null) sb.Append($"  {r.Cpu.Name}{nl}  {r.Cpu.Cores} {Lang.T("çekirdek", "cores")} / {r.Cpu.Threads} {Lang.T("iş parçacığı", "threads")}, {Lang.T("temel hız", "base clock")} {(r.Cpu.MaxClockMHz / 1000.0).ToString("0.0", Lang.Num)} GHz{nl}");
            else sb.Append("  " + Lang.T("Okunamadı", "Couldn’t read") + nl);

            sb.Append(nl + Lang.T("EKRAN KARTI", "GRAPHICS CARD") + nl);
            if (r.Gpus.Count == 0) sb.Append("  " + Lang.T("Okunamadı", "Couldn’t read") + nl);
            foreach (var g in r.Gpus)
            {
                string vram = g.VramMB.HasValue ? (g.VramMB.Value >= 1024 ? (g.VramMB.Value / 1024.0).ToString("0.#", Lang.Num) + " GB" : g.VramMB.Value + " MB") : Lang.T("bilinmiyor", "unknown");
                sb.Append($"  {g.Name}{(g.Integrated ? Lang.T(" (dahili)", " (integrated)") : "")}{nl}    {Lang.T("Bellek", "Memory")}: {vram} ({Lang.T("kaynak", "source")}: {g.VramSource ?? "-"}), {Lang.T("sürücü", "driver")} {g.DriverVersion}{nl}");
            }

            sb.Append(nl + Lang.T("BELLEK (RAM)", "MEMORY (RAM)") + nl);
            sb.Append($"  {Lang.T("Toplam", "Total")} {r.Ram.TotalGB.ToString("0.#", Lang.Num)} GB, {r.Ram.Modules.Count} {Lang.T("modül takılı", "modules installed")} / {r.Ram.SlotsTotal} {Lang.T("yuva", "slots")}{nl}");
            foreach (var m in r.Ram.Modules)
            {
                int speed = m.ConfiguredSpeedMHz > 0 ? m.ConfiguredSpeedMHz : m.SpeedMHz;
                sb.Append($"  - {m.CapacityGB.ToString("0.#", Lang.Num)} GB {m.Type} {speed} MHz ({Lang.T("destekli", "rated")} {m.SpeedMHz} MHz) {m.Manufacturer} {m.PartNumber}{nl}");
            }

            sb.Append(nl + Lang.T("SİSTEM", "SYSTEM") + nl);
            sb.Append($"  {r.Machine.Manufacturer} {r.Machine.Model}{(string.IsNullOrEmpty(r.Machine.ProductVersion) ? "" : " (" + r.Machine.ProductVersion + ")")}{nl}");
            sb.Append($"  {Lang.T("Anakart", "Motherboard")}: {r.Machine.BoardManufacturer} {r.Machine.BoardProduct}{nl}");
            sb.Append($"  {Lang.T("Tür", "Type")}: {(r.Machine.IsLaptop ? Lang.T("Dizüstü", "Laptop") : Lang.T("Masaüstü", "Desktop"))}{nl}");

            sb.Append(nl + Lang.T("DİSKLER", "DRIVES") + nl);
            if (r.Disks.Count == 0) sb.Append("  " + Lang.T("Okunamadı", "Couldn’t read") + nl);
            foreach (var d in r.Disks)
                sb.Append($"  - {d.Name}: {d.SizeGB:0} GB, {(d.Type == "Unknown" ? Lang.T("Bilinmiyor", "Unknown") : d.Type)}, {d.Bus}, {Health(d.Health)}{nl}");

            sb.Append(nl + Lang.T("EKRANLAR", "DISPLAYS") + nl);
            if (r.Displays.Count == 0) sb.Append("  " + Lang.T("Okunamadı", "Couldn’t read") + nl);
            foreach (var d in r.Displays)
            {
                sb.Append($"  - {d.Width} x {d.Height} @ {d.RefreshHz} Hz{(d.Primary ? Lang.T(" (ana ekran)", " (main display)") : "")}{nl}");
                if (d.MaxRefreshHzAtThisResolution > d.RefreshHz)
                    sb.Append("    " + Lang.T($"Dikkat: Monitörün bu çözünürlükte {d.MaxRefreshHzAtThisResolution} Hz'e kadar çıkabiliyor ama şu an {d.RefreshHz} Hz'de çalışıyor!", $"Note: your monitor supports up to {d.MaxRefreshHzAtThisResolution} Hz at this resolution but runs at {d.RefreshHz} Hz!") + nl);
            }
            if (r.MonitorNames.Count > 0) sb.Append($"  {Lang.T("Monitör", "Monitor")}: {string.Join(", ", r.MonitorNames)}{nl}");

            if (r.Peripherals.Count > 0)
            {
                sb.Append(nl + Lang.T("MOUSE / KLAVYE / KULAKLIK", "MOUSE / KEYBOARD / HEADSET") + nl);
                foreach (var p in r.Peripherals)
                {
                    string kind = p.Kind == "mouse" ? "Mouse" : p.Kind == "keyboard" ? Lang.T("Klavye", "Keyboard") : Lang.T("Kulaklık", "Headset");
                    string name = p.Receiver ? p.Brand + Lang.T(" kablosuz alıcı (modeli sitede seçebilirsin)", " wireless receiver (pick the model on the site)") : p.Name + (p.Wireless ? Lang.T(" (kablosuz)", " (wireless)") : "");
                    sb.Append($"  - {kind}: {name}{nl}");
                }
            }

            sb.Append(nl + Lang.T("İŞLETİM SİSTEMİ", "OPERATING SYSTEM") + nl);
            sb.Append($"  {r.Os.Name} ({r.Os.Architecture}), {Lang.T("derleme", "build")} {r.Os.Build}{nl}");

            var t = r.Sys;
            if (t != null)
            {
                Func<bool?, string> onOff = v => v == null ? Lang.T("bilinmiyor", "unknown") : v.Value ? Lang.T("açık", "on") : Lang.T("kapalı", "off");
                sb.Append(nl + Lang.T("OYUN AYARLARI VE SİSTEM", "GAMING SETTINGS AND SYSTEM") + nl);
                sb.Append($"  {Lang.T("Güç planı", "Power plan")}: {t.PowerPlan ?? Lang.T("bilinmiyor", "unknown")}{nl}");
                sb.Append($"  {Lang.T("Oyun Modu", "Game Mode")}: {onOff(t.GameMode)}, {Lang.T("GPU zamanlaması (HAGS)", "GPU scheduling (HAGS)")}: {onOff(t.Hags)}{nl}");
                if (t.SystemDriveSizeGB > 0) sb.Append($"  {Lang.T("Sistem diski", "System drive")}: {t.SystemDriveFreeGB.ToString("0", Lang.Num)} GB {Lang.T("boş", "free")} / {t.SystemDriveSizeGB.ToString("0", Lang.Num)} GB{nl}");
                if (t.BatteryHealthPct.HasValue) sb.Append($"  {Lang.T("Batarya sağlığı", "Battery health")}: %{t.BatteryHealthPct}{(t.BatteryCycles.HasValue ? ", " + t.BatteryCycles + Lang.T(" şarj döngüsü", " charge cycles") : "")}{nl}");
                if (!string.IsNullOrEmpty(t.BiosVersion)) sb.Append($"  BIOS: {t.BiosVendor} {t.BiosVersion}{(t.BiosDate != null ? " (" + t.BiosDate + ")" : "")}{nl}");
            }

            if (r.Bench != null)
            {
                sb.Append(nl + Lang.T("PERFORMANS TESTİ", "PERFORMANCE TEST") + nl);
                sb.Append($"  {Lang.T("Tek çekirdek", "Single-core")}: {r.Bench.SingleThreadScore.ToString("N0", Lang.Num)}{nl}");
                sb.Append($"  {Lang.T("Çok çekirdek", "Multi-core")}: {r.Bench.MultiThreadScore.ToString("N0", Lang.Num)} ({r.Bench.ThreadsUsed} {Lang.T("iş parçacığı", "threads")}){nl}");
                if (r.Bench.MemoryGBs > 0) sb.Append($"  {Lang.T("Bellek hızı", "Memory speed")}: {r.Bench.MemoryGBs.ToString("0.0", Lang.Num)} GB/s{nl}");
                if (r.Bench.BackgroundCpuPct >= 0) sb.Append($"  {Lang.T("Test öncesi arka plan yükü", "Background load before test")}: %{r.Bench.BackgroundCpuPct.ToString("0", Lang.Num)}{nl}");
                if (r.Bench.ClockLimitPct > 0) sb.Append($"  {Lang.T("Yük altında hız sınırı", "Clock limit under load")}: %{r.Bench.ClockLimitPct} ({Lang.T("anlık", "current")} %{r.Bench.ClockPct}){nl}");
                sb.Append($"  {Lang.T("Süre", "Duration")}: {(r.Bench.DurationMs / 1000.0).ToString("0.0", Lang.Num)} sn{nl}");
            }
            if (r.Warnings.Count > 0)
            {
                sb.Append(nl + Lang.T("UYARILAR (teknik)", "WARNINGS (technical)") + nl);
                foreach (var w in r.Warnings) sb.Append("  - " + w + nl);
            }
            return sb.ToString();
        }
    }
}
