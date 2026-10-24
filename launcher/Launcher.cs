using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("MOSS-Image Studio")]
[assembly: AssemblyDescription("MOSS-Image 本地生图启动器")]
[assembly: AssemblyProduct("MOSS-Image Studio")]
[assembly: AssemblyVersion("0.1.0.0")]

namespace MossImageLauncher
{
    internal static class Program
    {
        internal static string Root;
        internal static string Url { get { return "http://127.0.0.1:" + Config.Port; } }

        [STAThread]
        private static int Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "MossImageLauncher_v1", out created))
            {
                if (!created) return 0;

                Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                try { Config.Load(Root); }
                catch (Exception ex)
                {
                    MessageBox.Show("读取 moss.env 失败：\r\n" + ex.Message, "MOSS-Image Studio",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                List<string> missing = Config.Validate();
                if (Array.IndexOf(args, "--check") >= 0)
                {
                    return RunCheck(missing);
                }
                if (missing.Count > 0)
                {
                    MessageBox.Show(Config.MissingMessage(missing), "MOSS-Image Studio",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

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
                if (edge != null)
                {
                    edgeProc = StartEdge(edge);
                }
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
            }
            return 0;
        }

        private static int RunCheck(List<string> missing)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("程序目录: " + Root);
            sb.AppendLine("服务地址: " + Url + (IsLanMode() ? "（含局域网）" : "（仅本机）"));
            sb.AppendLine("sd-server: " + Config.Server);
            sb.AppendLine("界面文件: " + Config.Html);
            sb.AppendLine("扩散模型: " + Config.Diffusion);
            sb.AppendLine("文本编码器: " + Config.TextEncoder);
            sb.AppendLine("VAE: " + Config.Vae);
            sb.AppendLine("Tokenizer: " + Config.Tokenizer);
            sb.AppendLine();
            sb.AppendLine(missing.Count == 0 ? "检查通过，可以启动。" : Config.MissingMessage(missing));

            try
            {
                Directory.CreateDirectory(Path.Combine(Root, "outputs"));
                File.WriteAllText(Path.Combine(Root, "outputs", "check.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }

            MessageBox.Show(sb.ToString(), "MOSS-Image Studio 环境检查",
                MessageBoxButtons.OK, missing.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return missing.Count == 0 ? 0 : 1;
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

        internal static List<string> Validate()
        {
            List<string> missing = new List<string>();
            Check(missing, "MOSS_SERVER", "sd-server 主程序", Server);
            Check(missing, "MOSS_HTML", "中文界面文件", Html);
            Check(missing, "MOSS_DIFFUSION_MODEL", "扩散模型 (DiT)", Diffusion);
            Check(missing, "MOSS_TEXT_ENCODER", "文本编码器", TextEncoder);
            Check(missing, "MOSS_VAE", "VAE", Vae);
            Check(missing, "MOSS_TOKENIZER", "Tokenizer", Tokenizer);
            if (!Directory.Exists(ModelDir)) missing.Add("MOSS_MODEL_DIR → " + ModelDir);
            return missing;
        }

        private static void Check(List<string> missing, string key, string label, string path)
        {
            if (!File.Exists(path)) missing.Add(key + "（" + label + "）→ " + path);
        }

        internal static string MissingMessage(List<string> missing)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("以下文件找不到：");
            sb.AppendLine();
            foreach (string m in missing) sb.AppendLine("· " + m);
            sb.AppendLine();
            sb.AppendLine("请把这些文件放到对应位置，或编辑程序目录下的 moss.env 指向实际路径。");
            sb.AppendLine("moss.env.example 里有全部可用配置项。");
            return sb.ToString();
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
