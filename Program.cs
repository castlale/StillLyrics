using System;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace StillLyrics
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            bool first;
            using (var instance = new Mutex(true, @"Local\StillLyrics.Desktop", out first))
            {
                if (!first) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new Island()); }
                catch (System.IO.IOException ex) { MessageBox.Show(ex.Message,"定格词岛 · 读写设置失败"); }
                catch (InvalidOperationException ex) { MessageBox.Show(ex.Message,"定格词岛 · 启动失败"); }
            }
        }
    }
}
