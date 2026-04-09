using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NVevaAce
{
    public partial class MainForm : Form
    {
        private Button btnStartStop;
        private TextBox txtPort;
        private Label lblPort;
        private RichTextBox txtLog;
        private bool isRunning = false;
        private TunnelManager _tunnelManager;
        private AppConfig _config;

        public MainForm()
        {
            InitializeComponent();
            InitializeUI();
            _config = AppConfig.Load();
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.ClientSize = new System.Drawing.Size(400, 300);
            this.Name = "MainForm";
            this.Text = "NVevaAce - 内网穿透工具";
            this.ResumeLayout(false);
        }

        private void InitializeUI()
        {
            // 端口标签
            lblPort = new Label();
            lblPort.Text = "本地端口:";
            lblPort.Location = new Point(20, 20);
            lblPort.AutoSize = true;
            this.Controls.Add(lblPort);

            // 端口输入框
            txtPort = new TextBox();
            txtPort.Text = "8080";
            txtPort.Location = new Point(100, 17);
            txtPort.Width = 100;
            this.Controls.Add(txtPort);

            // 启动/停止按钮
            btnStartStop = new Button();
            btnStartStop.Text = "启动内网穿透";
            btnStartStop.Location = new Point(220, 15);
            btnStartStop.Width = 120;
            btnStartStop.Click += BtnStartStop_Click;
            this.Controls.Add(btnStartStop);

            // 日志显示框
            txtLog = new RichTextBox();
            txtLog.Location = new Point(20, 50);
            txtLog.Size = new Size(360, 200);
            txtLog.ReadOnly = true;
            this.Controls.Add(txtLog);
            
            // 显示配置信息
            Log($"加载配置：服务器 {_config.ServerAddr}:{_config.ServerPort}");
            Log($"隧道数量：{_config.Tunnels.Count}");
            foreach (var tunnel in _config.Tunnels)
            {
                Log($"  - {tunnel.Protocol?.ToUpper() ?? "TCP"} {tunnel.LocalPort} -> {tunnel.RemotePort}");
            }
        }

        private void BtnStartStop_Click(object sender, EventArgs e)
        {
            if (!isRunning)
            {
                StartTunnel();
            }
            else
            {
                StopTunnel();
            }
        }

        private void StartTunnel()
        {
            if (!int.TryParse(txtPort.Text, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("请输入有效的端口号 (1-65535)", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Log($"准备启动内网穿透，本地端口：{port}");

            try
            {
                _tunnelManager = new TunnelManager(new FormLogger(this), _config);
                _tunnelManager.StartTunnel();
                isRunning = true;
                btnStartStop.Text = "停止内网穿透";
                Log("内网穿透已启动");
            }
            catch (Exception ex)
            {
                Log($"启动隧道失败：{ex.Message}");
                MessageBox.Show($"启动隧道失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopTunnel()
        {
            Log("正在停止内网穿透...");
            try
            {
                _tunnelManager?.StopTunnel();
                isRunning = false;
                btnStartStop.Text = "启动内网穿透";
                Log("内网穿透已停止");
            }
            catch (Exception ex)
            {
                Log($"停止隧道时出错：{ex.Message}");
            }
        }

        private void Log(string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => Log(message)));
                return;
            }
            
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            txtLog.AppendText($"[{timestamp}] {message}{Environment.NewLine}");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        private class FormLogger : ILogger
        {
            private readonly MainForm _form;

            public FormLogger(MainForm form)
            {
                _form = form;
            }

            public void Log(string message)
            {
                _form.Log(message);
            }
        }
    }
}
