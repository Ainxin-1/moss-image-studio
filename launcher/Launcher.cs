using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("MOSS-Image Studio")]
[assembly: AssemblyDescription("MOSS-Image 本地生图启动器")]
[assembly: AssemblyProduct("MOSS-Image Studio")]
[assembly: AssemblyVersion("0.1.1.0")]

namespace MossImageLauncher
{
    internal static class Program
    {
        internal static string Root;
        internal static string ExeDir;
        internal static bool NoLaunch;
        internal static bool NoShortcut;
        internal static bool AutoStart;
        internal static string InstallDirArg;
        internal static string BackendArg;
        internal static string Url { get { return "http://127.0.0.1:" + Config.Port; } }

        [STAThread]
        private static int Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "MossImageLauncher_v1", out created))
            {
                if (!created) return 0;

                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                try { ServicePointManager.DefaultConnectionLimit = Setup.SegmentCount + 8; } catch { }

                ExeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                Root = ExeDir;
                NoLaunch = Array.IndexOf(args, "--no-launch") >= 0;
                NoShortcut = Array.IndexOf(args, "--no-shortcut") >= 0;
                AutoStart = Array.IndexOf(args, "--auto") >= 0;
                InstallDirArg = ArgValue(args, "--install-dir=");
                BackendArg = ArgValue(args, "--backend=");

                try { Config.Load(Root); }
                catch (Exception ex)
                {
                    MessageBox.Show("读取 moss.env 失败：\r\n" + ex.Message, "MOSS-Image Studio",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Crash(e.Exception); };
                    AppDomain.CurrentDomain.UnhandledException +=
                        delegate(object s, UnhandledExceptionEventArgs e) { Crash(e.ExceptionObject as Exception); };
                }
                catch { }

                string installDir = InstallDirArg;
                if (string.IsNullOrEmpty(installDir)) installDir = Setup.DefaultInstallDir(ExeDir);
                List<string> missing = Setup.Validate(installDir);

                if (Array.IndexOf(args, "--check") >= 0)
                {
                    return RunCheck(installDir, missing);
                }

                if (missing.Count > 0)
                {
                    using (InstallForm form = new InstallForm(installDir))
                    {
                        Application.Run(form);
                        if (form.DialogResult != DialogResult.OK) return 1;
                    }

                    bool moved = NormalizePath(installDir);
                    if (moved)
                    {
                        // 先放开单实例锁，否则新进程会和当前进程抢锁、静默退出。
                        try { mutex.ReleaseMutex(); } catch { }
                        if (!NoLaunch) StartInstalled(installDir);
                        return 0;
                    }
                    Root = installDir;
                    Config.Load(Root);
                }

                return RunLauncher();
            }
        }

        private static void Crash(Exception ex)
        {
            try
            {
                string file = Path.Combine(Path.GetTempPath(), "moss-image-studio-crash.log");
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") +
                    (ex == null ? "(null)" : ex.ToString()) + "\r\n", new UTF8Encoding(false));
            }
            catch { }
        }

        private static bool NormalizePath(string installDir)
        {
            if (PathsEqual(installDir, ExeDir)) return false;

            Directory.CreateDirectory(installDir);
            string targetExe = Path.Combine(installDir, "MOSS-Image.exe");
            try
            {
                if (!PathsEqual(Application.ExecutablePath, targetExe))
                    File.Copy(Application.ExecutablePath, targetExe, true);
                if (!NoShortcut) Setup.CreateShortcut(targetExe);
            }
            catch (Exception ex)
            {
                MessageBox.Show("复制程序到安装目录失败：\r\n" + ex.Message, "MOSS-Image Studio",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            return true;
        }

        private static void StartInstalled(string installDir)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(installDir, "MOSS-Image.exe"))
                {
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败，请到安装目录双击 MOSS-Image.exe。\r\n" + ex.Message,
                    "MOSS-Image Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static int RunLauncher()
        {
            bool alreadyUp = TestServer();
            Process serverProc = null;

            if (!alreadyUp)
            {
                using (LauncherForm form = new LauncherForm(Config.Server))
                {
                    Application.Run(form);
                    if (form.Failed) return 1;
                    serverProc = form.ServerProcess;
                }
            }
            else if (IsLanMode())
            {
                WriteLanFile();
            }

            string edge = FindEdge();
            Process edgeProc = null;
            if (edge != null) edgeProc = StartEdge(edge);

            if (edgeProc == null)
            {
                try { Process.Start(Url); }
                catch (Exception ex)
                {
                    MessageBox.Show("无法打开浏览器，请手动访问 " + Url + "\r\n" + ex.Message,
                        "MOSS-Image Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                if (Config.StopOnExit) StopServer(serverProc);
                return 0;
            }

            edgeProc.WaitForExit();
            Thread.Sleep(2000);
            if (Config.StopOnExit) StopServer(serverProc);
            return 0;
        }

        private static int RunCheck(string installDir, List<string> missing)
        {
            bool inPlace = PathsEqual(installDir, ExeDir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("程序目录: " + ExeDir);
            sb.AppendLine("安装目录: " + installDir);
            sb.AppendLine("服务地址: " + Url + (IsLanMode() ? "（含局域网）" : "（仅本机）"));
            sb.AppendLine("sd-server: " + (inPlace ? Config.Server : Setup.Defaults(installDir).Server));
            sb.AppendLine("界面文件: " + (inPlace ? Config.Html : Setup.Defaults(installDir).Html));
            sb.AppendLine("扩散模型: " + (inPlace ? Config.Diffusion : Setup.Defaults(installDir).Diffusion));
            sb.AppendLine("文本编码器: " + (inPlace ? Config.TextEncoder : Setup.Defaults(installDir).TextEncoder));
            sb.AppendLine("VAE: " + (inPlace ? Config.Vae : Setup.Defaults(installDir).Vae));
            sb.AppendLine("Tokenizer: " + (inPlace ? Config.Tokenizer : Setup.Defaults(installDir).Tokenizer));
            sb.AppendLine();
            sb.AppendLine(missing.Count == 0 ? "检查通过，可以启动。" : Setup.MissingMessage(missing));

            try
            {
                Directory.CreateDirectory(Path.Combine(ExeDir, "outputs"));
                File.WriteAllText(Path.Combine(ExeDir, "outputs", "check.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }

            MessageBox.Show(sb.ToString(), "MOSS-Image Studio 环境检查",
                MessageBoxButtons.OK, missing.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return missing.Count == 0 ? 0 : 1;
        }

        private static string ArgValue(string[] args, string prefix)
        {
            foreach (string a in args)
            {
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return a.Substring(prefix.Length).Trim('"');
            }
            return null;
        }

        internal static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        internal static bool IsLanMode()
        {
            string l = Config.Listen.Trim();
            return l.Length > 0 && l != "127.0.0.1" && l != "localhost" && l != "::1";
        }

        internal static string LanAddress()
        {
            try
            {
                foreach (IPAddress ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        return ip.ToString();
                }
            }
            catch { }
            return null;
        }

        internal static void WriteLanFile()
        {
            string ip = LanAddress();
            if (ip == null) return;
            try
            {
                Directory.CreateDirectory(Path.Combine(Root, "outputs"));
                File.WriteAllText(Path.Combine(Root, "outputs", "lan-url.txt"),
                    "http://" + ip + ":" + Config.Port + "\r\n", new UTF8Encoding(false));
            }
            catch { }
        }

        private static string FindEdge()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = new string[]
            {
                Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf64, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(local, @"Microsoft\Edge\Application\msedge.exe")
            };
            foreach (string p in candidates)
            {
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static Process StartEdge(string edge)
        {
            string profile = Path.Combine(Root, ".edge-profile");
            string args =
                "--app=" + Url +
                " --user-data-dir=\"" + profile + "\"" +
                " --no-first-run --no-default-browser-check --disable-sync" +
                " --window-size=1440,900" +
                " --disable-features=Translate,EdgeSidebar,msEdgeSidebar";
            ProcessStartInfo psi = new ProcessStartInfo(edge, args);
            psi.UseShellExecute = false;
            psi.WindowStyle = ProcessWindowStyle.Normal;
            try { return Process.Start(psi); }
            catch { return null; }
        }

        internal static bool TestServer()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Url + "/");
                req.Proxy = null;
                req.Timeout = 2500;
                req.KeepAlive = false;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    return (int)resp.StatusCode == 200;
                }
            }
            catch { return false; }
        }

        private static void StopServer(Process serverProc)
        {
            if (serverProc == null) return;
            try
            {
                if (!serverProc.HasExited)
                {
                    serverProc.Kill();
                    serverProc.WaitForExit(5000);
                }
            }
            catch { }
        }
    }

    internal sealed class InstallPaths
    {
        internal string Root;
        internal string Server;
        internal string Html;
        internal string ModelDir;
        internal string Diffusion;
        internal string TextEncoder;
        internal string Vae;
        internal string Tokenizer;
    }

    internal sealed class InstallForm : Form
    {
        private string _dir;
        private readonly Label _lblInfo;
        private readonly Label _lblBackend;
        private readonly Label _lblStage;
        private readonly Label _lblSpeed;
        private readonly TextBox _txtDir;
        private readonly Button _btnBrowse;
        private readonly Button _btnGo;
        private readonly Button _btnClose;
        private readonly ProgressBar _bar;
        private CancellationTokenSource _cts;
        private bool _running;
        private bool _done;

        public InstallForm(string dir)
        {
            _dir = dir;

            Text = "MOSS-Image Studio 安装";
            ClientSize = new Size(536, 306);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label title = new Label();
            title.Text = "首次运行：准备运行环境";
            title.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            title.AutoSize = false;
            title.Size = new Size(500, 30);
            title.Location = new Point(18, 16);
            Controls.Add(title);

            _lblInfo = new Label();
            _lblInfo.Text = "需要下载 sd.cpp 运行库和 4 个模型文件，约 10 GB，建议磁盘留 20 GB。\n" +
                            "多连接分段下载，支持断点续传；中途取消或断网，重新打开会从断点继续。\n" +
                            "模型优先走 hf-mirror.com，运行库直连失败自动换镜像，秒验 sha256。";
            _lblInfo.AutoSize = false;
            _lblInfo.Size = new Size(500, 60);
            _lblInfo.Location = new Point(18, 50);
            _lblInfo.ForeColor = Color.FromArgb(71, 85, 105);
            Controls.Add(_lblInfo);

            Label dirLabel = new Label();
            dirLabel.Text = "安装位置";
            dirLabel.AutoSize = true;
            dirLabel.Location = new Point(18, 122);
            Controls.Add(dirLabel);

            _txtDir = new TextBox();
            _txtDir.ReadOnly = true;
            _txtDir.Size = new Size(398, 24);
            _txtDir.Location = new Point(18, 144);
            _txtDir.Text = _dir;
            Controls.Add(_txtDir);

            _btnBrowse = new Button();
            _btnBrowse.Text = "更改...";
            _btnBrowse.Size = new Size(84, 26);
            _btnBrowse.Location = new Point(426, 143);
            _btnBrowse.Click += OnBrowse;
            Controls.Add(_btnBrowse);

            _lblBackend = new Label();
            _lblBackend.AutoSize = false;
            _lblBackend.Size = new Size(500, 22);
            _lblBackend.Location = new Point(18, 176);
            _lblBackend.ForeColor = Color.FromArgb(71, 85, 105);
            Controls.Add(_lblBackend);

            _lblStage = new Label();
            _lblStage.AutoSize = false;
            _lblStage.Size = new Size(500, 22);
            _lblStage.Location = new Point(18, 202);
            _lblStage.Text = "";
            Controls.Add(_lblStage);

            _bar = new ProgressBar();
            _bar.Minimum = 0;
            _bar.Maximum = 1000;
            _bar.Size = new Size(500, 14);
            _bar.Location = new Point(18, 226);
            Controls.Add(_bar);

            _lblSpeed = new Label();
            _lblSpeed.AutoSize = false;
            _lblSpeed.Size = new Size(500, 20);
            _lblSpeed.Location = new Point(18, 244);
            _lblSpeed.ForeColor = Color.FromArgb(100, 116, 139);
            Controls.Add(_lblSpeed);

            _btnGo = new Button();
            _btnGo.Text = "开始下载";
            _btnGo.Size = new Size(112, 32);
            _btnGo.Location = new Point(286, 266);
            _btnGo.Click += OnGo;
            Controls.Add(_btnGo);

            _btnClose = new Button();
            _btnClose.Text = "取消";
            _btnClose.Size = new Size(88, 32);
            _btnClose.Location = new Point(408, 266);
            _btnClose.Click += OnClose;
            Controls.Add(_btnClose);

            FormClosing += OnFormClosing;
            UpdateBackend();
            UpdateSpace();

            Shown += delegate
            {
                if (Program.AutoStart)
                    BeginInvoke(new Action(delegate { OnGo(this, EventArgs.Empty); }));
            };
        }

        private void UpdateBackend()
        {
            string backend = Setup.ResolveBackend(Program.BackendArg);
            string why = backend == "cuda12" ? "检测到 NVIDIA 显卡" :
                         backend == "vulkan" ? "未检测到 NVIDIA，使用 Vulkan（兼容 AMD/Intel）" :
                         "使用纯 CPU（很慢）";
            _lblBackend.Text = "运行库: " + backend + "（" + why + "）";
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择安装目录（需要至少 20 GB 可用空间）";
                dlg.SelectedPath = _dir;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _dir = dlg.SelectedPath;
                    _txtDir.Text = _dir;
                    UpdateSpace();
                }
            }
        }

        private long FreeSpace(string dir)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(dir));
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch { return 0; }
        }

        private void UpdateSpace()
        {
            double gb = FreeSpace(_dir) / 1073741824.0;
            if (gb < 20)
            {
                _lblStage.ForeColor = Color.FromArgb(185, 28, 28);
                _lblStage.Text = "该位置只有 " + gb.ToString("0.0") + " GB 可用，至少需要 20 GB，请点「更改」。";
                _btnGo.Enabled = false;
            }
            else
            {
                _lblStage.ForeColor = Color.FromArgb(71, 85, 105);
                _lblStage.Text = "可用空间 " + gb.ToString("0.0") + " GB";
                _btnGo.Enabled = !_running;
            }
        }

        private void OnGo(object sender, EventArgs e)
        {
            try { OnGoCore(); }
            catch (Exception ex)
            {
                Setup.Log(_dir, "安装线程异常：" + ex);
                _lblStage.ForeColor = Color.FromArgb(185, 28, 28);
                _lblStage.Text = "安装出错：" + ex.Message;
                _btnGo.Text = "重试";
                _btnGo.Enabled = true;
                _running = false;
            }
        }

        private async void OnGoCore()
        {
            if (_done)
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            if (_running) return;

            List<string> missing = Setup.Validate(_dir);
            if (missing.Count == 0)
            {
                _done = true;
                _btnGo.Text = "启动";
                _lblStage.Text = "环境已就绪。";
                return;
            }

            _running = true;
            _btnGo.Enabled = false;
            _btnBrowse.Enabled = false;
            _btnClose.Text = "停止";
            _bar.Value = 0;

            _cts = new CancellationTokenSource();
            Progress<InstallProgress> progress = new Progress<InstallProgress>(OnProgress);

            try
            {
                await Task.Run(delegate { Setup.Install(_dir, progress, _cts.Token); });
                _done = true;
                _lblStage.ForeColor = Color.FromArgb(21, 128, 61);
                _lblStage.Text = "安装完成。";
                _lblSpeed.Text = "";
                _bar.Value = 1000;
                _btnGo.Text = "启动 MOSS-Image";
                _btnGo.Enabled = true;
                _btnBrowse.Enabled = false;
                _btnClose.Text = "关闭";
                _btnClose.Enabled = true;
            }
            catch (OperationCanceledException)
            {
                _lblStage.ForeColor = Color.FromArgb(71, 85, 105);
                _lblStage.Text = "已停止。已经下载的部分会保留。";
                _btnGo.Text = "继续下载";
                _btnGo.Enabled = true;
                _btnBrowse.Enabled = true;
                _btnClose.Text = "取消";
            }
            catch (Exception ex)
            {
                _lblStage.ForeColor = Color.FromArgb(185, 28, 28);
                _lblStage.Text = "下载失败：" + ex.Message;
                _lblSpeed.Text = "检查网络后点「重试」；已下载的部分会保留。";
                _btnGo.Text = "重试";
                _btnGo.Enabled = true;
                _btnBrowse.Enabled = true;
                _btnClose.Text = "关闭";
            }
            finally
            {
                _running = false;
            }
        }

        private void OnProgress(InstallProgress p)
        {
            if (p.Stage != null)
            {
                _lblStage.ForeColor = Color.FromArgb(71, 85, 105);
                _lblStage.Text = p.Stage;
            }
            if (p.Percent >= 0)
            {
                int v = (int)Math.Round(p.Percent * 10);
                if (v < 0) v = 0;
                if (v > 1000) v = 1000;
                _bar.Value = v;
            }
            if (p.Speed > 0)
            {
                _lblSpeed.Text = "速度 " + FormatSize((long)p.Speed) + "/s" +
                    (p.Eta > 0 ? "，当前文件剩余 " + FormatTime(p.Eta) : "");
            }
            else if (p.Note != null)
            {
                _lblSpeed.Text = p.Note;
            }
        }

        private void OnClose(object sender, EventArgs e)
        {
            if (_running)
            {
                if (_cts != null) _cts.Cancel();
                return;
            }
            if (_done) DialogResult = DialogResult.OK;
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_running)
            {
                if (_cts != null) _cts.Cancel();
                e.Cancel = true;
                return;
            }
            if (_done) DialogResult = DialogResult.OK;
        }

        internal static string FormatSize(long bytes)
        {
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("0.00") + " GB";
            if (bytes >= 1048576L) return (bytes / 1048576.0).ToString("0.0") + " MB";
            if (bytes >= 1024L) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        internal static string FormatTime(double seconds)
        {
            if (seconds < 60) return ((int)seconds) + " 秒";
            if (seconds < 3600) return ((int)(seconds / 60)) + " 分 " + ((int)(seconds % 60)) + " 秒";
            return ((int)(seconds / 3600)) + " 小时 " + ((int)((seconds % 3600) / 60)) + " 分";
        }
    }

    internal sealed class InstallProgress
    {
        internal string Stage;
        internal double Percent = -1;
        internal double Speed;
        internal double Eta;
        internal string Note;
    }

    internal static class Setup
    {
        private const string ReleaseApi = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest";
        private static readonly string[] HfBases = new string[] { "https://hf-mirror.com", "https://huggingface.co" };
        private const long MinDit = 3000000000L;
        private const long MinTextEncoder = 5000000000L;
        private const long MinVae = 300000000L;
        private const long MinTokenizer = 10000000L;

        internal static void Log(string root, string message)
        {
            try
            {
                string dir = Path.Combine(root, "outputs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "install.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        internal static InstallPaths Defaults(string root)
        {
            InstallPaths p = new InstallPaths();
            p.Root = root;
            p.Server = Path.Combine(root, @"bin\sd-server.exe");
            p.Html = Path.Combine(root, @"web\index.html");
            p.ModelDir = Path.Combine(root, "models");
            p.Diffusion = Path.Combine(root, @"models\MOSS-Image-SFT-Q4_K.gguf");
            p.TextEncoder = Path.Combine(root, @"models\text_encoders\Qwen3.5-9B-Q4_K_M.gguf");
            p.Vae = Path.Combine(root, @"models\vae\flux2-vae.safetensors");
            p.Tokenizer = Path.Combine(root, @"models\tokenizers\tokenizer.json");
            return p;
        }

        internal static InstallPaths Paths(string installDir)
        {
            if (Program.PathsEqual(installDir, Program.ExeDir))
            {
                InstallPaths p = new InstallPaths();
                p.Root = Program.ExeDir;
                p.Server = Config.Server;
                p.Html = Config.Html;
                p.ModelDir = Config.ModelDir;
                p.Diffusion = Config.Diffusion;
                p.TextEncoder = Config.TextEncoder;
                p.Vae = Config.Vae;
                p.Tokenizer = Config.Tokenizer;
                return p;
            }
            return Defaults(installDir);
        }

        internal static List<string> Validate(string installDir)
        {
            InstallPaths p = Paths(installDir);
            List<string> missing = new List<string>();
            if (!File.Exists(p.Server)) missing.Add("MOSS_SERVER（sd-server 主程序）→ " + p.Server);
            if (!File.Exists(p.Html)) missing.Add("MOSS_HTML（界面文件）→ " + p.Html);
            if (!Ready(p, p.Diffusion, MinDit, @"models\MOSS-Image-SFT-Q4_K.gguf"))
                missing.Add("MOSS_DIFFUSION_MODEL（扩散模型）→ " + p.Diffusion);
            if (!Ready(p, p.TextEncoder, MinTextEncoder, @"models\text_encoders\Qwen3.5-9B-Q4_K_M.gguf"))
                missing.Add("MOSS_TEXT_ENCODER（文本编码器）→ " + p.TextEncoder);
            if (!Ready(p, p.Vae, MinVae, @"models\vae\flux2-vae.safetensors"))
                missing.Add("MOSS_VAE（VAE）→ " + p.Vae);
            if (!Ready(p, p.Tokenizer, MinTokenizer, @"models\tokenizers\tokenizer.json"))
                missing.Add("MOSS_TOKENIZER → " + p.Tokenizer);
            return missing;
        }

        // 只对安装器自己下载到默认位置的文件检查最小体积；用户自定义的路径只要求存在，
        // 这样换成更小的量化版本也不会被反复要求“重新安装”。
        private static bool Ready(InstallPaths p, string path, long minBytes, string defaultRel)
        {
            if (!File.Exists(path)) return false;
            string def = Path.Combine(p.Root, defaultRel);
            if (!Program.PathsEqual(path, def)) return true;
            try { return new FileInfo(path).Length >= minBytes; }
            catch { return false; }
        }

        internal static string MissingMessage(List<string> missing)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("以下文件找不到：");
            sb.AppendLine();
            foreach (string m in missing) sb.AppendLine("· " + m);
            sb.AppendLine();
            sb.AppendLine("重新运行 MOSS-Image.exe 会打开安装窗口自动补齐，");
            sb.AppendLine("也可以手动放置文件，或在 moss.env 里指向实际路径。");
            return sb.ToString();
        }

        internal static string DefaultInstallDir(string exeDir)
        {
            string lower = exeDir.ToLowerInvariant();
            if (lower.Contains(@"\downloads") || lower.Contains(@"\desktop") ||
                lower.Contains(@"\temp") || lower.Contains(@"\appdata\local\temp"))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MOSS-Image-Studio");
            }
            return exeDir;
        }

        internal static string ResolveBackend(string requested)
        {
            if (!string.IsNullOrEmpty(requested))
            {
                string r = requested.Trim().ToLowerInvariant();
                if (r == "cuda12" || r == "cuda") return "cuda12";
                if (r == "vulkan") return "vulkan";
                if (r == "cpu") return "cpu";
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("nvidia-smi.exe", "-L");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (p.WaitForExit(4000) && p.ExitCode == 0) return "cuda12";
                }
            }
            catch { }
            try
            {
                if (!File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"))) return "cpu";
            }
            catch { }
            return "vulkan";
        }

        internal static void Install(string installDir, IProgress<InstallProgress> progress, CancellationToken ct)
        {
            InstallPaths p = Paths(installDir);
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(Path.Combine(installDir, "downloads"));
            Directory.CreateDirectory(Path.GetDirectoryName(p.Diffusion));
            Directory.CreateDirectory(Path.GetDirectoryName(p.TextEncoder));
            Directory.CreateDirectory(Path.GetDirectoryName(p.Vae));
            Directory.CreateDirectory(Path.GetDirectoryName(p.Tokenizer));
            Directory.CreateDirectory(p.ModelDir);

            Log(installDir, "安装开始：" + installDir);

            if (!File.Exists(p.Html)) ExtractHtml(p.Html, progress);

            if (!File.Exists(p.Server))
            {
                try { InstallBinaries(installDir, progress, ct); }
                catch (Exception ex) { Log(installDir, "运行库失败：" + ex); throw; }
            }

            List<ModelSpec> models = new List<ModelSpec>();
            if (!Ready(p, p.Diffusion, MinDit, @"models\MOSS-Image-SFT-Q4_K.gguf"))
                models.Add(new ModelSpec("扩散模型 MOSS-Image Q4_K", MinDit,
                    "leejet/MOSS-Image-SFT-GGUF/resolve/main/MOSS-Image-SFT-Q4_K.gguf", p.Diffusion));
            if (!Ready(p, p.TextEncoder, MinTextEncoder, @"models\text_encoders\Qwen3.5-9B-Q4_K_M.gguf"))
                models.Add(new ModelSpec("文本编码器 Qwen3.5-9B", MinTextEncoder,
                    "unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf", p.TextEncoder));
            if (!Ready(p, p.Vae, MinVae, @"models\vae\flux2-vae.safetensors"))
                models.Add(new ModelSpec("FLUX.2 VAE", MinVae,
                    "Comfy-Org/MOSS-Image/resolve/main/vae/flux2-vae.safetensors", p.Vae));
            if (!Ready(p, p.Tokenizer, MinTokenizer, @"models\tokenizers\tokenizer.json"))
                models.Add(new ModelSpec("Tokenizer", MinTokenizer,
                    "OpenMOSS-Team/MOSS-Image-Base/resolve/main/tokenizer/tokenizer.json", p.Tokenizer));

            string[] bases = PickHfBases(ct);
            for (int i = 0; i < models.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ModelSpec m = models[i];
                string label = "(" + (i + 1) + "/" + models.Count + ") " + m.Name;
                List<string> urls = new List<string>();
                foreach (string b in bases) urls.Add(b + "/" + m.HfPath);
                try
                {
                    DownloadAny(urls.ToArray(), m.Path, m.MinBytes, 0, null, label, progress, ct);
                }
                catch (Exception ex)
                {
                    Log(installDir, m.Name + " 下载失败：" + ex);
                    throw;
                }
            }

            Log(installDir, "安装完成");
            List<string> still = Validate(installDir);
            if (still.Count > 0) throw new Exception("安装后仍有文件缺失：" + still[0]);
        }

        private static void ExtractHtml(string htmlPath, IProgress<InstallProgress> progress)
        {
            progress.Report(new InstallProgress { Stage = "写入中文界面...", Note = "来自内置资源，无需网络" });
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("web.index.html"))
            {
                if (s == null) throw new Exception("程序内缺少界面资源，请从 Releases 重新下载 exe。");
                Directory.CreateDirectory(Path.GetDirectoryName(htmlPath));
                using (FileStream fs = new FileStream(htmlPath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                }
            }
        }

        private static void InstallBinaries(string installDir, IProgress<InstallProgress> progress, CancellationToken ct)
        {
            progress.Report(new InstallProgress { Stage = "查询 stable-diffusion.cpp 最新版本...", Note = "GitHub API" });
            string json = GetString(ReleaseApi);
            List<ReleaseAsset> assets = ParseAssets(json);

            string prefer = ResolveBackend(Program.BackendArg);
            string[] order = prefer == "cuda12" ? new string[] { "cuda12", "cpu" }
                           : prefer == "vulkan" ? new string[] { "vulkan", "cpu" }
                           : new string[] { "cpu" };

            ReleaseAsset zip = null;
            string used = null;
            foreach (string backend in order)
            {
                foreach (ReleaseAsset a in assets)
                {
                    if (a.Name.EndsWith("-win-" + backend + "-x64.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zip = a;
                        used = backend;
                        break;
                    }
                }
                if (zip != null) break;
            }
            if (zip == null) throw new Exception("没有找到可用的 Windows 运行库下载地址。");

            string zipPath = Path.Combine(installDir, "downloads", zip.Name);
            DownloadAny(ReleaseUrls(zip), zipPath, 5000000L, zip.Size, zip.Sha256,
                "运行库 stable-diffusion.cpp (" + used + ")", progress, ct);

            progress.Report(new InstallProgress { Stage = "解压运行库...", Percent = -1, Note = zip.Name });
            ExtractZip(zipPath, Path.Combine(installDir, "bin"));
            try { File.Delete(zipPath); } catch { }

            if (used == "cuda12" && !File.Exists(Path.Combine(installDir, @"bin\cublasLt64_12.dll")))
            {
                ReleaseAsset cudart = null;
                foreach (ReleaseAsset a in assets)
                {
                    if (a.Name.StartsWith("cudart-sd-bin-win-cu12", StringComparison.OrdinalIgnoreCase))
                    {
                        cudart = a;
                        break;
                    }
                }
                if (cudart != null)
                {
                    string czip = Path.Combine(installDir, "downloads", cudart.Name);
                    DownloadAny(ReleaseUrls(cudart), czip, 5000000L, cudart.Size, cudart.Sha256,
                        "CUDA 运行库", progress, ct);
                    ExtractZip(czip, Path.Combine(installDir, "bin"));
                    try { File.Delete(czip); } catch { }
                }
            }

            if (!File.Exists(Path.Combine(installDir, @"bin\sd-server.exe")))
                throw new Exception("运行库解压后没有找到 bin\\sd-server.exe。");
        }

        private sealed class ReleaseAsset
        {
            public string Name;
            public string Url;
            public string ApiUrl;
            public long Size;
            public string Sha256;
        }

        // GitHub 直连在国内经常被阻断，失败后依次换 API 资源地址和镜像；
        // 三者都用 API 给出的精确体积和 sha256 校验，避免把半截文件或错误页当成运行库。
        private static string[] ReleaseUrls(ReleaseAsset a)
        {
            List<string> urls = new List<string>();
            urls.Add(a.Url);
            if (a.ApiUrl != null) urls.Add(a.ApiUrl);
            urls.Add("https://gh-proxy.com/" + a.Url);
            return urls.ToArray();
        }

        private static List<ReleaseAsset> ParseAssets(string json)
        {
            List<ReleaseAsset> list = new List<ReleaseAsset>();
            const string marker = "/releases/assets/";
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                int start = json.LastIndexOf(marker, m.Index, StringComparison.Ordinal);
                if (start < 0) continue;
                string block = json.Substring(start, m.Index - start);

                ReleaseAsset a = new ReleaseAsset();
                a.Url = m.Groups[1].Value;
                a.Name = Path.GetFileName(a.Url);
                int idStart = start + marker.Length;
                int idEnd = idStart;
                while (idEnd < json.Length && char.IsDigit(json[idEnd])) idEnd++;
                if (idEnd > idStart)
                    a.ApiUrl = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/assets/" +
                               json.Substring(idStart, idEnd - idStart);
                Match size = Regex.Match(block, "\"size\"\\s*:\\s*(\\d+)");
                if (size.Success) long.TryParse(size.Groups[1].Value, out a.Size);
                Match digest = Regex.Match(block, "\"digest\"\\s*:\\s*\"sha256:([0-9a-fA-F]+)\"");
                if (digest.Success) a.Sha256 = digest.Groups[1].Value.ToLowerInvariant();
                list.Add(a);
            }
            return list;
        }

        private static void ExtractZip(string zip, string dest)
        {
            string temp = zip + ".extract";
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            ZipFile.ExtractToDirectory(zip, temp);

            // 官方包有时把文件放在 sd-master-xxx/ 子目录里，统一摊平到 bin\。
            string source = temp;
            string[] servers = Directory.GetFiles(temp, "sd-server.exe", SearchOption.AllDirectories);
            if (servers.Length > 0) source = Path.GetDirectoryName(servers[0]);

            CopyDir(source, dest);
            Directory.Delete(temp, true);
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(from))
                CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        private static string[] PickHfBases(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Probe(HfBases[0] + "/OpenMOSS-Team/MOSS-Image-Base/resolve/main/tokenizer/tokenizer.json"))
                return HfBases;
            return new string[] { HfBases[1], HfBases[0] };
        }

        private static bool Probe(string url)
        {
            try
            {
                HttpWebRequest req = Create(url);
                req.Timeout = 6000;
                req.ReadWriteTimeout = 6000;
                req.AddRange(0, 0);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    return (int)resp.StatusCode == 200 || (int)resp.StatusCode == 206;
                }
            }
            catch { return false; }
        }

        private static string GetString(string url)
        {
            using (HttpWebResponse resp = (HttpWebResponse)Create(url).GetResponse())
            using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return r.ReadToEnd();
            }
        }

        private static HttpWebRequest Create(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;
            req.UserAgent = "MOSS-Image-Studio/0.1.1";
            if (url.IndexOf("/releases/assets/", StringComparison.Ordinal) >= 0)
                req.Accept = "application/octet-stream";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;
            return req;
        }

        private sealed class IntegrityException : Exception
        {
            public IntegrityException(string message) : base(message) { }
        }

        private sealed class RangeUnsupportedException : Exception
        {
            public RangeUnsupportedException() : base("该下载源不支持分段") { }
        }

        internal const int SegmentCount = 16;
        private const long MinSegmented = 4L * 1024 * 1024;

        private static void DownloadAny(string[] urls, string dest, long minBytes, long expectedSize,
            string sha256, string label, IProgress<InstallProgress> progress, CancellationToken ct)
        {
            long total = expectedSize;
            bool hfSource = urls[0].IndexOf("hf-mirror.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            urls[0].IndexOf("huggingface.co", StringComparison.OrdinalIgnoreCase) >= 0;
            // GitHub 的 release CDN 对多连接很敏感（会直接 403），只对 HF 走分段。
            if (!hfSource) total = 0;
            else if (total <= 0) total = ProbeTotal(urls[0]);
            if (total >= MinSegmented)
            {
                try
                {
                    DownloadSegmented(urls, dest, total, sha256, label, progress, ct);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (RangeUnsupportedException) { }
                catch (Exception ex)
                {
                    throw new Exception(label + " 下载失败（" + ex.Message + "），已下载的分段会保留，点重试可继续。", ex);
                }
            }

            Exception last = null;
            foreach (string url in urls)
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        DownloadOne(url, dest, minBytes, expectedSize, sha256, label, progress, ct);
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (IntegrityException ex)
                    {
                        // 内容错了不能续传，删掉换下一个地址。
                        last = ex;
                        try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                    }
                    catch (Exception ex)
                    {
                        // 网络中断保留半截文件，下次从断点继续。
                        last = ex;
                        if (attempt < 2) Thread.Sleep(1500);
                    }
                }
            }
            throw new Exception(label + " 下载失败（" + (last == null ? "未知错误" : last.Message) + "）", last);
        }

        private static long ProbeTotal(string url)
        {
            try
            {
                HttpWebRequest req = Create(url);
                req.AddRange(0, 0);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode != 206) return -1;
                    string cr = resp.Headers["Content-Range"];
                    if (cr == null) return -1;
                    int slash = cr.LastIndexOf('/');
                    long total;
                    if (slash > 0 && long.TryParse(cr.Substring(slash + 1), out total)) return total;
                }
            }
            catch { }
            return -1;
        }

        // 单连接在这些源上只有几十 KB/s，拆成多段并发能快 5~10 倍；
        // 每段独立落盘，断网后重开只补没下完的段。
        private static void DownloadSegmented(string[] urls, string dest, long total, string sha256,
            string label, IProgress<InstallProgress> progress, CancellationToken ct)
        {
            string partsDir = dest + ".parts";
            Directory.CreateDirectory(partsDir);

            int n = (int)Math.Min(SegmentCount, total / (2L * 1024 * 1024));
            if (n < 2) n = 2;
            long[] starts = new long[n + 1];
            for (int i = 0; i <= n; i++) starts[i] = total * i / n;

            long[] done = new long[n];
            long initial = 0;
            for (int i = 0; i < n; i++)
            {
                long have = PartLength(partsDir, i);
                long want = starts[i + 1] - starts[i];
                if (have > want)
                {
                    try { File.Delete(PartPath(partsDir, i)); } catch { }
                    have = 0;
                }
                done[i] = have;
                initial += have;
            }

            Exception[] errors = new Exception[n];
            Thread[] workers = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                Thread t = new Thread(delegate()
                {
                    try
                    {
                        DownloadSegment(urls, partsDir, idx, starts[idx], starts[idx + 1] - 1, done, ct);
                    }
                    catch (Exception ex) { errors[idx] = ex; }
                });
                t.IsBackground = true;
                workers[i] = t;
                t.Start();
            }

            long lastDone = initial;
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                bool alive = false;
                for (int i = 0; i < n; i++) if (workers[i].IsAlive) { alive = true; break; }

                long cur = 0;
                for (int i = 0; i < n; i++) cur += Interlocked.Read(ref done[i]);
                double speed = (cur - lastDone) / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                lastDone = cur;
                sw.Restart();

                InstallProgress p = new InstallProgress();
                p.Stage = label;
                p.Speed = speed;
                p.Percent = total > 0 ? Math.Min(100.0, cur * 100.0 / total) : -1;
                p.Eta = speed > 1024 ? (total - cur) / speed : 0;
                p.Note = "分段下载 " + n + " 路";
                progress.Report(p);

                if (!alive) break;
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(400);
            }

            for (int i = 0; i < n; i++) workers[i].Join();
            ct.ThrowIfCancellationRequested();

            for (int i = 0; i < n; i++)
            {
                if (errors[i] is RangeUnsupportedException)
                {
                    try { Directory.Delete(partsDir, true); } catch { }
                    throw new RangeUnsupportedException();
                }
                if (errors[i] != null) throw new Exception(errors[i].Message, errors[i]);
            }

            for (int i = 0; i < n; i++)
            {
                long want = starts[i + 1] - starts[i];
                if (PartLength(partsDir, i) != want) throw new IOException("分段 " + (i + 1) + " 不完整");
            }

            string tmp = dest + ".joining";
            try
            {
                using (FileStream outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buf = new byte[262144];
                    for (int i = 0; i < n; i++)
                    {
                        using (FileStream pf = new FileStream(PartPath(partsDir, i), FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            int read;
                            while ((read = pf.Read(buf, 0, buf.Length)) > 0) outFs.Write(buf, 0, read);
                        }
                    }
                }
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }

            long length = new FileInfo(dest).Length;
            if (length != total) throw new IntegrityException("文件大小不符（" + length + " / " + total + " 字节）");
            if (sha256 != null && !MatchesSha256(dest, sha256)) throw new IntegrityException("sha256 校验失败");

            try { Directory.Delete(partsDir, true); } catch { }
            progress.Report(new InstallProgress { Stage = label, Percent = 100, Speed = 0, Note = "完成" });
        }

        private static string PartPath(string dir, int i)
        {
            return Path.Combine(dir, i.ToString("D2") + ".part");
        }

        private static long PartLength(string dir, int i)
        {
            string p = PartPath(dir, i);
            try { return File.Exists(p) ? new FileInfo(p).Length : 0; }
            catch { return 0; }
        }

        private static void DownloadSegment(string[] urls, string partsDir, int idx, long start, long end,
            long[] done, CancellationToken ct)
        {
            string part = PartPath(partsDir, idx);
            long want = end - start + 1;
            long have = PartLength(partsDir, idx);
            if (have >= want)
            {
                Interlocked.Exchange(ref done[idx], want);
                return;
            }

            Exception last = null;
            for (int u = 0; u < urls.Length; u++)
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        HttpWebRequest req = Create(urls[u]);
                        req.AddRange(start + have, end);
                        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                        {
                            if ((int)resp.StatusCode == 200) throw new RangeUnsupportedException();
                            if ((int)resp.StatusCode != 206) throw new IOException("HTTP " + (int)resp.StatusCode);

                            using (FileStream fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                            {
                                fs.Seek(have, SeekOrigin.Begin);
                                byte[] buf = new byte[131072];
                                long got = have;
                                int n;
                                Stream rs = resp.GetResponseStream();
                                while (got < want && (n = rs.Read(buf, 0, buf.Length)) > 0)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    long take = Math.Min(n, want - got);
                                    fs.Write(buf, 0, (int)take);
                                    got += take;
                                    Interlocked.Exchange(ref done[idx], got);
                                }
                                if (got < want) throw new IOException("分段提前结束");
                            }
                        }
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (RangeUnsupportedException) { throw; }
                    catch (Exception ex)
                    {
                        last = ex;
                        have = PartLength(partsDir, idx);
                        Interlocked.Exchange(ref done[idx], have);
                        if (have >= want) return;
                        Thread.Sleep(800 * attempt);
                    }
                }
            }
            throw new IOException("分段 " + (idx + 1) + " 下载失败", last);
        }

        private static void DownloadOne(string url, string dest, long minBytes, long expectedSize,
            string sha256, string label, IProgress<InstallProgress> progress, CancellationToken ct)
        {
            long existing = File.Exists(dest) ? new FileInfo(dest).Length : 0;
            if (expectedSize > 0)
            {
                if (existing == expectedSize && (sha256 == null || MatchesSha256(dest, sha256)))
                {
                    progress.Report(new InstallProgress { Stage = label, Percent = 100, Note = "已存在，跳过" });
                    return;
                }
                if (existing >= expectedSize)
                {
                    File.Delete(dest);
                    existing = 0;
                }
            }
            else if (existing >= minBytes)
            {
                progress.Report(new InstallProgress { Stage = label, Percent = 100, Note = "已存在，跳过" });
                return;
            }

            using (FileStream fs = new FileStream(dest, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                if (fs.Length > 0) fs.Seek(0, SeekOrigin.End);
                HttpWebRequest req = Create(url);
                if (fs.Length > 0) req.AddRange(fs.Length);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (fs.Length > 0 && (int)resp.StatusCode == 200)
                    {
                        fs.SetLength(0);
                    }

                    long content = resp.ContentLength;
                    long total = fs.Length + content;
                    long done = fs.Length;
                    byte[] buf = new byte[131072];
                    Stopwatch sw = Stopwatch.StartNew();
                    long markBytes = done;

                    int n;
                    Stream rs = resp.GetResponseStream();
                    while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        fs.Write(buf, 0, n);
                        done += n;

                        if (sw.ElapsedMilliseconds >= 250)
                        {
                            double speed = (done - markBytes) / sw.Elapsed.TotalSeconds;
                            sw.Restart();
                            markBytes = done;
                            InstallProgress p = new InstallProgress();
                            p.Stage = label;
                            p.Speed = speed;
                            p.Percent = total > 0 ? Math.Min(100.0, done * 100.0 / total) : -1;
                            p.Eta = speed > 1024 ? (total - done) / speed : 0;
                            progress.Report(p);
                        }
                    }

                    if (content > 0 && done < total)
                        throw new IOException("连接提前结束");
                }
            }

            long length = new FileInfo(dest).Length;
            if (expectedSize > 0 && length != expectedSize)
                throw new IntegrityException("文件大小不符（" + length + " / " + expectedSize + " 字节）");
            if (length < minBytes)
                throw new IOException("文件不完整（" + length + " 字节）");
            if (sha256 != null && !MatchesSha256(dest, sha256))
                throw new IntegrityException("sha256 校验失败");

            progress.Report(new InstallProgress { Stage = label, Percent = 100, Speed = 0, Note = "完成" });
        }

        private static bool MatchesSha256(string path, string expected)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return string.Equals(sb.ToString(), expected, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        internal static void CreateShortcut(string exePath)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnk = Path.Combine(desktop, "MOSS-Image Studio.lnk");
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type sct = sc.GetType();
                sct.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exePath });
                sct.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc,
                    new object[] { Path.GetDirectoryName(exePath) });
                sct.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "MOSS-Image 本地生图" });
                sct.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            }
            catch { }
        }

        private sealed class ModelSpec
        {
            internal readonly string Name;
            internal readonly long MinBytes;
            internal readonly string HfPath;
            internal readonly string Path;

            internal ModelSpec(string name, long minBytes, string hfPath, string path)
            {
                Name = name;
                MinBytes = minBytes;
                HfPath = hfPath;
                Path = path;
            }
        }
    }

    internal static class Config
    {
        internal static int Port = 1234;
        internal static string Listen = "127.0.0.1";
        internal static string Server;
        internal static string Html;
        internal static string ModelDir;
        internal static string Diffusion;
        internal static string TextEncoder;
        internal static string Vae;
        internal static string Tokenizer;
        internal static string ExtraArgs = "";
        internal static bool Offload = true;
        internal static bool StopOnExit = true;

        private static readonly string[] EnvKeys = new string[]
        {
            "MOSS_PORT", "MOSS_LISTEN", "MOSS_SERVER", "MOSS_HTML", "MOSS_MODEL_DIR",
            "MOSS_DIFFUSION_MODEL", "MOSS_TEXT_ENCODER", "MOSS_VAE", "MOSS_TOKENIZER",
            "MOSS_OFFLOAD_TO_CPU", "MOSS_STOP_ON_EXIT", "MOSS_EXTRA_ARGS"
        };

        internal static void Load(string root)
        {
            Dictionary<string, string> v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string file = Path.Combine(root, "moss.env");
            if (File.Exists(file))
            {
                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    v[line.Substring(0, eq).Trim()] = Unquote(line.Substring(eq + 1).Trim());
                }
            }
            foreach (string key in EnvKeys)
            {
                string e = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrEmpty(e)) v[key] = e;
            }

            int port;
            if (int.TryParse(Get(v, "MOSS_PORT", "1234"), out port) && port > 0 && port < 65536)
                Port = port;

            Listen = Get(v, "MOSS_LISTEN", "127.0.0.1");
            Server = Resolve(root, Get(v, "MOSS_SERVER", @"bin\sd-server.exe"));
            Html = Resolve(root, Get(v, "MOSS_HTML", "web/index.html"));
            ModelDir = Resolve(root, Get(v, "MOSS_MODEL_DIR", "models"));
            Diffusion = Resolve(root, Get(v, "MOSS_DIFFUSION_MODEL", "models/MOSS-Image-SFT-Q4_K.gguf"));
            TextEncoder = Resolve(root, Get(v, "MOSS_TEXT_ENCODER", "models/text_encoders/Qwen3.5-9B-Q4_K_M.gguf"));
            Vae = Resolve(root, Get(v, "MOSS_VAE", "models/vae/flux2-vae.safetensors"));
            Tokenizer = Resolve(root, Get(v, "MOSS_TOKENIZER", "models/tokenizers/tokenizer.json"));
            ExtraArgs = Get(v, "MOSS_EXTRA_ARGS", "");
            Offload = Flag(Get(v, "MOSS_OFFLOAD_TO_CPU", "1"));
            StopOnExit = Flag(Get(v, "MOSS_STOP_ON_EXIT", "1"));
        }

        private static string Get(Dictionary<string, string> v, string key, string def)
        {
            string s;
            return v.TryGetValue(key, out s) && s.Length > 0 ? s : def;
        }

        private static bool Flag(string s)
        {
            s = s.Trim().ToLowerInvariant();
            return s == "1" || s == "true" || s == "yes" || s == "on";
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
                return s.Substring(1, s.Length - 2);
            return s;
        }

        internal static string Resolve(string root, string p)
        {
            p = p.Trim();
            if (p.Length == 0) return p;
            if (p.StartsWith("~/"))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.GetFullPath(Path.Combine(home, p.Substring(2)));
            }
            return Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(root, p));
        }

        internal static string[] SplitArgs(string s)
        {
            List<string> list = new List<string>();
            if (s == null) return list.ToArray();
            StringBuilder cur = new StringBuilder();
            bool quote = false;
            foreach (char c in s)
            {
                if (c == '"') { quote = !quote; continue; }
                if (!quote && (c == ' ' || c == '\t'))
                {
                    if (cur.Length > 0) { list.Add(cur.ToString()); cur.Length = 0; }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list.ToArray();
        }

        internal static string Quote(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }
    }

    internal sealed class LauncherForm : Form
    {
        private readonly string _server;
        private readonly Label _status;
        private readonly Label _hint;
        private Process _serverProc;
        private bool _ready;
        private bool _failed;
        private bool _aborted;

        public Process ServerProcess { get { return _serverProc; } }
        public bool Failed { get { return _failed; } }

        public LauncherForm(string server)
        {
            _server = server;

            Text = "MOSS-Image Studio";
            ClientSize = new Size(420, 196);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(248, 250, 252);

            try { Icon = Icon.ExtractAssociatedIcon(server); } catch { }

            Label title = new Label();
            title.Text = "正在启动 MOSS-Image 应用...\r\n模型加载约需 30~90 秒，请稍候";
            title.Font = new Font("Microsoft YaHei UI", 11f);
            title.AutoSize = false;
            title.Size = new Size(384, 56);
            title.Location = new Point(18, 18);
            Controls.Add(title);

            _status = new Label();
            _status.Text = "正在启动服务...";
            _status.Font = new Font("Microsoft YaHei UI", 9f);
            _status.ForeColor = Color.FromArgb(100, 116, 139);
            _status.AutoSize = false;
            _status.Size = new Size(384, 22);
            _status.Location = new Point(18, 84);
            Controls.Add(_status);

            ProgressBar bar = new ProgressBar();
            bar.Style = ProgressBarStyle.Marquee;
            bar.MarqueeAnimationSpeed = 30;
            bar.Size = new Size(384, 14);
            bar.Location = new Point(18, 120);
            Controls.Add(bar);

            _hint = new Label();
            _hint.Text = "";
            _hint.Font = new Font("Microsoft YaHei UI", 8.5f);
            _hint.ForeColor = Color.FromArgb(22, 163, 74);
            _hint.AutoSize = false;
            _hint.Size = new Size(384, 30);
            _hint.Location = new Point(18, 144);
            Controls.Add(_hint);

            Shown += OnShown;
            FormClosing += OnClosing;
        }

        private async void OnShown(object sender, EventArgs e)
        {
            StartServer();
            if (_serverProc == null) { _failed = true; Close(); return; }

            Stopwatch watch = Stopwatch.StartNew();
            while (!_aborted && !IsDisposed && watch.Elapsed.TotalSeconds < 300)
            {
                if (_serverProc.HasExited)
                {
                    _failed = true;
                    MessageBox.Show("服务启动失败，详见 outputs\\server-err.log", "MOSS-Image Studio",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                    return;
                }
                if (Program.TestServer())
                {
                    _ready = true;
                    _status.Text = "加载完成，正在打开窗口...";
                    if (Program.IsLanMode())
                    {
                        string ip = Program.LanAddress();
                        if (ip != null)
                        {
                            _hint.Text = "局域网/手机访问: http://" + ip + ":" + Config.Port;
                            Program.WriteLanFile();
                            await Task.Delay(3000);
                        }
                    }
                    Close();
                    return;
                }
                _status.Text = "正在加载模型，已等待 " + (int)watch.Elapsed.TotalSeconds + " 秒...";
                await Task.Delay(700);
            }
            if (_aborted) return;

            _failed = true;
            MessageBox.Show("启动超时，请检查 outputs\\server-err.log", "MOSS-Image Studio",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }

        private void StartServer()
        {
            string outputs = Path.Combine(Program.Root, "outputs");
            Directory.CreateDirectory(outputs);
            StreamWriter logOut = new StreamWriter(Path.Combine(outputs, "server.log"), false, new UTF8Encoding(false));
            StreamWriter logErr = new StreamWriter(Path.Combine(outputs, "server-err.log"), false, new UTF8Encoding(false));
            logOut.AutoFlush = true;
            logErr.AutoFlush = true;
            object gate = new object();

            ProcessStartInfo psi = new ProcessStartInfo(_server);
            psi.WorkingDirectory = Program.Root;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.Arguments = BuildArguments();

            try
            {
                Process p = new Process();
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate) { logOut.WriteLine(e.Data); }
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate) { logErr.WriteLine(e.Data); }
                };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _serverProc = p;
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法启动服务：" + ex.Message, "MOSS-Image Studio",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _serverProc = null;
            }
        }

        private static string BuildArguments()
        {
            List<string> a = new List<string>();
            a.Add("--listen-ip"); a.Add(Config.Quote(Config.Listen));
            a.Add("--listen-port"); a.Add(Config.Port.ToString());
            a.Add("--serve-html-path"); a.Add(Config.Quote(Config.Html));
            a.Add("--model-dir"); a.Add(Config.Quote(Config.ModelDir));
            a.Add("--diffusion-model"); a.Add(Config.Quote(Config.Diffusion));
            a.Add("--llm"); a.Add(Config.Quote(Config.TextEncoder));
            a.Add("--vae"); a.Add(Config.Quote(Config.Vae));
            a.Add("--tokenizer"); a.Add(Config.Quote(Config.Tokenizer));
            if (Config.Offload) a.Add("--offload-to-cpu");
            foreach (string extra in Config.SplitArgs(Config.ExtraArgs)) a.Add(Config.Quote(extra));
            return string.Join(" ", a.ToArray());
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!_ready)
            {
                _aborted = true;
                _failed = true;
                if (_serverProc != null)
                {
                    try { if (!_serverProc.HasExited) _serverProc.Kill(); } catch { }
                }
            }
        }
    }
}
