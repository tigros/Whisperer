using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
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
        string glboutdir, glblang;
        int glbwaittime = 0;
        Dictionary<string, string> langs = new Dictionary<string, string>();
        bool glbsamefolder = false;

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
            if (File.Exists("languageCodez.tsv"))
            {
                foreach (string line in File.ReadLines("languageCodez.tsv"))
                {
                    string[] lang = line.Split('\t');
                    langs.Add(lang[2], lang[0]);
                    comboBox1.Items.Add(lang[2]);
                }
            }
            else
            {
                MessageBox.Show("languageCodez.tsv missing!");
                langs.Add("english", "en");
                comboBox1.Items.Add("english");
            }
            comboBox1.Text = "english";
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
            //totmem = getvideomem();

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
            label5.Text = "0";
        }

        long getwhispersize()
        {
            long whispersize = 0;
            try
            {
                Process proc = new Process();
                proc.StartInfo.FileName = "GPUmembyproc.exe";
                proc.StartInfo.Arguments = "main.exe";
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

        bool srtexists(string filename)
        {
            return checkBox1.Checked && File.Exists(filename + ".srt");
        }

        void convertalltowav()
        {
            try
            {
                foreach (string filename in glbarray)
                {
                    string outname = Path.Combine(getfolder(filename), Path.GetFileName(filename));
                    int i = outname.LastIndexOf('.');
                    if (i == -1)
                        continue;
                    string tmp = outname.Remove(i);
                    outname = tmp + ".wav";
                    if (File.Exists(outname) || srtexists(tmp))
                        continue;
                    Process proc = new Process();
                    proc.StartInfo.FileName = "ffmpeg.exe";
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

        string getfolder(string filename)
        {
            return glbsamefolder ? Path.GetDirectoryName(filename) : glboutdir;
        }

        void execwhisper()
        {
            try
            {
                if (cancel)
                    return;

                foreach (string filename in glbarray)
                {
                    string inname = Path.Combine(getfolder(filename), Path.GetFileName(filename));
                    int i = inname.LastIndexOf('.');
                    if (i == -1)
                        continue;
                    string tmp = inname.Remove(i);
                    inname = tmp + ".wav";

                    if (srtexists(tmp))
                    {
                        Proc_Exited(null, null);
                        continue;
                    }
                    Process proc = new Process();
                    string translate = " ";
                    if (checkBox2.Checked)
                        translate = " -tr ";
                    proc.StartInfo.FileName = "main.exe";
                    proc.StartInfo.Arguments = "--language " + glblang + translate + "--output-srt --max-context 0 --model \"" + 
                        glbmodel + "\" \"" + inname + "\"";
                    proc.StartInfo.UseShellExecute = false;
                    proc.StartInfo.CreateNoWindow = true;

                    fillmemvars();
                    long neededmem = 400000000;
                    if (glbwaittime == 15000)
                        neededmem = 2400000000;
                    else if (glbwaittime == 20000)
                        neededmem = 4300000000;

                    while ((freemem < neededmem || Process.GetProcessesByName("main").Length >= numericUpDown1.Value) && !cancel)
                    {
                        Thread.Sleep(1000);
                        fillmemvars();
                    }

                    if (cancel)
                        break;

                    int wlen = Process.GetProcessesByName("main").Length;
                    proc.EnableRaisingEvents = true;
                    proc.Exited += Proc_Exited;
                    proc.Start();
                    
                    while (Process.GetProcessesByName("main").Length == wlen)
                        Thread.Sleep(10);

                    long whispersize = 0;

                    while (whispersize == 0 && Process.GetProcessesByName("main").Length > 0 && !cancel)
                    {
                        Thread.Sleep(1000);
                        whispersize = getwhispersize();
                        if (whispersize > 0)
                        {
                            Thread.Sleep(glbwaittime);
                            whispersize = getwhispersize();
                            fillmemvars();
                        }
                    }

                    while (freemem - 200000000 < whispersize && Process.GetProcessesByName("main").Length > 0 && !cancel)
                    {
                        Thread.Sleep(1000);
                        fillmemvars();
                        whispersize = getwhispersize();
                    }

                    if (cancel)
                        break;
                }

                while (Process.GetProcessesByName("main").Length > 0 && !cancel)
                    Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        private void Proc_Exited(object sender, EventArgs e)
        {
            try
            {
                if (sender != null)
                {
                    string args = ((Process)sender).StartInfo.Arguments;
                    args = args.TrimEnd('"');
                    args = args.Substring(args.LastIndexOf('"') + 1);
                    File.Delete(args);
                }
            }
            catch { }
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
                    glbmodel = textBox2.Text;
                    glbwaittime = 10000;
                    if (glbmodel.ToLower().Contains("medium"))
                        glbwaittime = 15000;
                    else if (glbmodel.ToLower().Contains("large"))
                        glbwaittime = 20000;
                    foreach (filenameline filename in fastObjectListView1.SelectedObjects)
                        glbarray.Add(filename.filename);
                    cancel = false;
                    button3.Text = "Cancel";
                    completed = 0;
                    label5.Text = "0";
                    glboutdir = textBox1.Text;
                    glbsamefolder = checkBox3.Checked;
                    glblang = "en";
                    try
                    {
                        glblang = langs[comboBox1.Text];
                    }
                    catch { }
                    Thread thr = new Thread(() =>
                    {
                        convertalltowav();
                        execwhisper();
                        Invoke(new Action(() =>
                        {
                            button3.Text = "Go";
                        }));
                    });
                    thr.IsBackground = true;
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

        private void fastObjectListView1_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private void fastObjectListView1_DragDrop(object sender, DragEventArgs e)
        {
            foreach (string file in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                if (!fexists(file))
                    fastObjectListView1.AddObject(new filenameline(file));
            }
            setcount();
        }

        private void checkBox3_CheckedChanged(object sender, EventArgs e)
        {
            textBox1.Enabled = !checkBox3.Checked;
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
