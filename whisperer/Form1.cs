using Microsoft.Win32;
using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace whisperer
{
    public partial class Form1 : Form, IMessageFilter
    {
        private NvDisplayHandle displayHandle;
        long totmem = 0, freemem = 0;
        bool cancel = false;
        ArrayList glbarray = new ArrayList();
        string glbmodel = "";
        int completed = 0;

        public Form1()
        {
            InitializeComponent();
            Application.AddMessageFilter(this);
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == 0x20a)
            {
                // WM_MOUSEWHEEL, find the control at screen position m.LParam
                Point pos = new Point(m.LParam.ToInt32() & 0xffff, m.LParam.ToInt32() >> 16);
                IntPtr hWnd = WindowFromPoint(pos);
                Control c = Control.FromHandle(hWnd);

                if (hWnd != IntPtr.Zero && c != null && hWnd != m.HWnd && this.Contains(c))
                {
                    SendMessage(hWnd, (uint)m.Msg, m.WParam, m.LParam);
                    return true;
                }
            }
            return false;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            NVAPI.NvAPI_EnumNvidiaDisplayHandle(0, ref displayHandle);            
        }

        long getvideomem()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000"))
                {
                    if (key != null)
                    {
                        object o = key.GetValue("HardwareInformation.qwMemorySize");
                        if (o != null)
                            return (long)o;
                    }
                }
            }
            catch { }
            return 0;
        }

        void fillmemvars()
        {
            totmem = getvideomem();

            NvDisplayDriverMemoryInfo memoryInfo = new NvDisplayDriverMemoryInfo();
            memoryInfo.Version = NVAPI.DISPLAY_DRIVER_MEMORY_INFO_VER;
            memoryInfo.Values = new uint[NVAPI.MAX_MEMORY_VALUES_PER_GPU];
            if (NVAPI.NvAPI_GetDisplayDriverMemoryInfo != null &&
                NVAPI.NvAPI_GetDisplayDriverMemoryInfo(displayHandle, ref memoryInfo) == NvStatus.OK)
                freemem = (long)memoryInfo.Values[4] * 1024;
        }

        bool fexists(string name)
        {
            for (int i = 0; i < fastObjectListView1.Items.Count; i++)
            {
                string s = fastObjectListView1.Items[i].Text;
                if (name == s)
                    return true;
            }
            return false;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                foreach (string filename in openFileDialog1.FileNames)
                {
                    if (!fexists(filename))
                        fastObjectListView1.AddObject(new filenameline(filename));
                }
                setcount();
            }
        }

        private void button2_Click(object sender, EventArgs e)
        {
            fastObjectListView1.ClearObjects();
            setcount();
            completed = 0;
        }

        long getwhispersize()
        {
            long whispersize = 0;
            try
            {
                Process proc = new Process();
                proc.StartInfo.FileName = "GPUmembyproc.exe";
                proc.StartInfo.Arguments = "python.exe";
                proc.StartInfo.RedirectStandardInput = true;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.Start();
                string[] vals = proc.StandardOutput.ReadToEnd().Trim().Replace(",", "").Replace("\r\n", "  ").Split(new string[] { "  " }, StringSplitOptions.RemoveEmptyEntries);
                proc.WaitForExit();
                if (vals.Length >= 4)
                    whispersize = Convert.ToInt64(vals[3]);
            }
            catch
            {
                MessageBox.Show("GPUmembyproc.exe not found!");
                Application.Exit();
            }
            return whispersize;
        }

        void convertalltowav()
        {
            try
            {
                foreach (string filename in glbarray)
                {
                    Process proc = new Process();
                    proc.StartInfo.FileName = "ffmpeg.exe";
                    string outname = Path.Combine(textBox1.Text, Path.GetFileName(filename));
                    int i = outname.LastIndexOf('.');
                    if (i == -1)
                        continue;
                    outname = outname.Remove(i) + ".wav";
                    if (File.Exists(outname))
                        continue;
                    proc.StartInfo.Arguments = "-y -i \"" + filename + "\" -vn -ar 16000 -ac 1 -ab 32k -af volume=1.75 -f wav \"" + outname + "\"";
                    proc.StartInfo.UseShellExecute = false;
                    proc.StartInfo.CreateNoWindow = true;
                    proc.Start();
                    while (Process.GetProcessesByName("ffmpeg").Length > 10 && !cancel)
                        Thread.Sleep(1000);
                    if (cancel)
                        break;
                }

                while (Process.GetProcessesByName("ffmpeg").Length > 0 && !cancel)
                    Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        void execwhisper()
        {
            try
            {
                if (cancel)
                    return;

                foreach (string filename in glbarray)
                {
                    Process proc = new Process();
                    proc.StartInfo.FileName = @"whisper.exe";
                    string inname = Path.Combine(textBox1.Text, Path.GetFileName(filename));
                    int i = inname.LastIndexOf('.');
                    if (i == -1)
                        continue;
                    inname = inname.Remove(i) + ".wav";
                    proc.StartInfo.Arguments = "--output_dir " + textBox1.Text + " --language en --device cuda --model \"" + glbmodel + "\" \"" + inname + "\"";
                    proc.StartInfo.UseShellExecute = false;
                    proc.StartInfo.CreateNoWindow = true;
                    int wlen = Process.GetProcessesByName("whisper").Length;
                    proc.EnableRaisingEvents = true;
                    proc.Exited += Proc_Exited;
                    proc.Start();
                    
                    while (Process.GetProcessesByName("whisper").Length == wlen)
                        Thread.Sleep(1000);

                    long whispersize = 0;

                    while (whispersize == 0 && Process.GetProcessesByName("whisper").Length > 0 && !cancel)
                    {
                        Thread.Sleep(1000);
                        whispersize = getwhispersize();
                        if (whispersize > 0)
                        {
                            Thread.Sleep(20000);
                            whispersize = getwhispersize();
                            fillmemvars();
                        }
                    }

                    while (freemem - 200000000 < whispersize && Process.GetProcessesByName("whisper").Length > 0 && !cancel)
                    {
                        Thread.Sleep(1000);
                        fillmemvars();
                        whispersize = getwhispersize();
                    }

                    if (cancel)
                        break;
                }

                while (Process.GetProcessesByName("whisper").Length > 0 && !cancel)
                    Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        private void Proc_Exited(object sender, EventArgs e)
        {
            completed++;
            Invoke(new Action(() =>
            {
                label5.Text = completed.ToString();
            }));            
        }

        bool checkdir()
        {
            if (!Directory.Exists(textBox1.Text))
            {
                try
                {
                    Directory.CreateDirectory(textBox1.Text);
                }
                catch
                {
                    MessageBox.Show("An error occured creating directory " + textBox1.Text);
                    return false;
                }
            }
            return true;
        }

        private void button3_Click(object sender, EventArgs e)
        {
            try
            {
                if (button3.Text == "Go")
                {
                    if (fastObjectListView1.SelectedObjects.Count == 0)
                    {
                        MessageBox.Show("No files selected!");
                        return;
                    }
                    if (!checkdir())
                        return;
                    glbarray.Clear();
                    glbmodel = comboBox1.Text;
                    foreach (filenameline filename in fastObjectListView1.SelectedObjects)
                        glbarray.Add(filename.filename);
                    cancel = false;
                    button3.Text = "Cancel";
                    completed = 0;
                    Thread thr = new Thread(() =>
                    {
                        convertalltowav();
                        execwhisper();
                        Invoke(new Action(() =>
                        {
                            button3.Text = "Go";
                        }));
                    });
                    thr.Start();
                }
                else
                    cancel = true;
            }
            catch { }
        }

        void setcount()
        {
            label3.Text = fastObjectListView1.SelectedObjects.Count.ToString("#,##0") + " / " +
                fastObjectListView1.Items.Count.ToString("#,##0");
        }

        private void fastObjectListView1_SelectionChanged(object sender, EventArgs e)
        {
            setcount();
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "WindowFromPoint", CharSet = CharSet.Auto, ExactSpelling = true)]
        public static extern IntPtr WindowFromPoint(Point pt);
    }

    public class filenameline
    {
        public string filename;

        public filenameline(string filename)
        {
            this.filename = filename;
        }
    }
}
