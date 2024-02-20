using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TaskScheduler;

namespace whisperer
{
    public partial class Form1 : Form, IMessageFilter
    {
        long totmem = 0, freemem = 0;
        bool cancel = false;
        ArrayList glbarray = new ArrayList();
        string glbmodel = "";
        int completed = 0;
        string glboutdir, glblang, glbprompt;
        int glbwaittime = 0;
        Dictionary<string, string> langs = new Dictionary<string, string>();
        bool glbsamefolder = false;
        List<PerformanceCounter> gpuCountersDedicated = new List<PerformanceCounter>();
        ConcurrentQueue<Action> whisperq = new ConcurrentQueue<Action>();
        bool quitq = false;
        Stopwatch sw = new Stopwatch();
        string rootdir = Path.GetDirectoryName(Environment.GetCommandLineArgs()[0]);
        static ScheduledTasks schedtask = null;
        static TaskScheduler.Task tasksched = null;

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

        void Form1_Load(object sender, EventArgs e)
        {
            if (!IsAtLeastWindows10())
            {
                ShowError(@"Unsupported Windows version, will now exit.");
                FormClosing -= new FormClosingEventHandler(Form1_FormClosing);
                Application.Exit();
                return;
            }

            Thread thr = new Thread(initperfcounter);
            thr.IsBackground = true;
            thr.Start();

            Thread watchthr = new Thread(watchwait);
            watchthr.IsBackground = true;
            watchthr.Start();

            Thread waitlaunch = new Thread(wait4launch);
            waitlaunch.IsBackground = true;
            waitlaunch.Start();

            getwhispersize(true);

            if (File.Exists("languageCodez.tsv"))
            {
                foreach (string line in File.ReadLines("languageCodez.tsv"))
                {
                    string[] lang = line.Split('\t');
                    string proper = toproper(lang[2]);
                    langs.Add(proper, lang[0]);
                    comboBox1.Items.Add(proper);
                }
            }
            else
            {
                ShowError("languageCodez.tsv missing!");
                langs.Add("English", "en");
                comboBox1.Items.Add("English");
            }

            loadsettings();

            checkBox4.CheckedChanged += outputtype_CheckedChanged;
            checkBox5.CheckedChanged += outputtype_CheckedChanged;
            checkBox6.CheckedChanged += outputtype_CheckedChanged;

            if (totmem == 0)
            {
                ShowError("Unsupprted GPU, will now exit.");
                FormClosing -= new FormClosingEventHandler(Form1_FormClosing);
                Application.Exit();
            }
            else if (Program.iswatch)
                button3_Click(null, null);
        }

        bool IsAtLeastWindows10()
        {
            try
            {
                string productName = (string)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "");
                return productName.Contains("Windows 1") || productName.Contains("Windows 2") || productName.Contains("Windows 3"); 
            }
            catch { }

            return false;
        }

        void loadfilelist()
        {
            string s = readreg("files", "");
            string[] files = s.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            fastObjectListView1.BeginUpdate();
            foreach (string file in files)
                fastObjectListView1.AddObject(new filenameline(file));
            fastObjectListView1.EndUpdate();
            fastObjectListView1.SelectAll();
            setcount();
        }

        void loadsettings()
        {
            Cursor = Cursors.WaitCursor;
            textBox1.Text = readreg("outputdir", textBox1.Text);
            textBox2.Text = readreg("modelpath", textBox2.Text);
            comboBox1.Text = toproper(readreg("language", "English"));
            checkBox4.Checked = Convert.ToBoolean(readreg("srt", "True"));
            checkBox5.Checked = Convert.ToBoolean(readreg("txt", "False"));
            checkBox6.Checked = Convert.ToBoolean(readreg("vtt", "False"));
            numericUpDown1.Value = Convert.ToDecimal(readreg("maxatonce", "10"));
            comboBox2.Text = readreg("whendone", "Do nothing");
            checkBox3.Checked = Convert.ToBoolean(readreg("sameasinputfolder", "False"));
            checkBox1.Checked = Convert.ToBoolean(readreg("skipifexists", "True"));
            checkBox2.Checked = Convert.ToBoolean(readreg("translate", "False"));
            textBox3.Text = readreg("watchfolders", textBox3.Text);
            textBox4.Text = readreg("prompt", textBox4.Text);
            loadfilelist();
            Cursor = Cursors.Default;
        }

        string toproper(string s)
        {
            try
            {
                return s.Substring(0, 1).ToUpper() + s.Substring(1);
            }
            catch { }
            return "English";
        }

        void outputtype_CheckedChanged(object sender, EventArgs e)
        {
            CheckBox clickedCheckBox = sender as CheckBox;
            if (!clickedCheckBox.Checked && !checkBox4.Checked && !checkBox5.Checked && !checkBox6.Checked)
            {
                clickedCheckBox.CheckedChanged -= outputtype_CheckedChanged;
                clickedCheckBox.Checked = true;
                clickedCheckBox.CheckedChanged += outputtype_CheckedChanged;
            }
        }

        void writereg(string name, string value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\tigros\whisperer"))
                {
                    if (key != null)
                        key.SetValue(name, value);
                }
            }
            catch { }
        }

        string readreg(string name, string deflt)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\tigros\whisperer"))
                {
                    if (key != null)
                    {
                        object o = key.GetValue(name);
                        if (o != null)
                            return (string)o;
                    }
                }
            }
            catch { }
            return deflt;
        }

        void initperfcounter()
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Adapter Memory");
                var counterNames = category.GetInstanceNames();
                foreach (string counterName in counterNames)
                {
                    foreach (var counter in category.GetCounters(counterName))
                    {
                        if (counter.CounterName == "Dedicated Usage")
                            gpuCountersDedicated.Add(counter);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError(@"Possibly corrupt perf counters, try C:\Windows\SysWOW64\LODCTR /R from elevated cmd prompt, if an error occurs, run it again. Will now exit.");
                FormClosing -= new FormClosingEventHandler(Form1_FormClosing);
                Application.Exit();
            }
        }

        long getfreegpumem()
        {
            var result = 0f;
            gpuCountersDedicated.ForEach(x =>
            {
                result += x.NextValue();
            });
            return Convert.ToInt64(totmem - result);
        }

        void fillmemvars()
        {
            freemem = getfreegpumem();
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

        void button1_Click(object sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                Cursor = Cursors.WaitCursor;
                fastObjectListView1.BeginUpdate();
                foreach (string filename in openFileDialog1.FileNames)
                {
                    if (!fexists(filename))
                        fastObjectListView1.AddObject(new filenameline(filename));
                }
                fastObjectListView1.EndUpdate();
                fastObjectListView1.SelectAll();
                setcount();
                Cursor = Cursors.Default;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Delete && fastObjectListView1.Focused)
            {
                fastObjectListView1.RemoveObjects(fastObjectListView1.SelectedObjects);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void button2_Click(object sender, EventArgs e)
        {
            fastObjectListView1.ClearObjects();
            setcount();
            completed = 0;
            label5.Text = "0";
        }

        long getwhispersize(bool filltotmem = false)
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
                if (filltotmem)
                    totmem = Convert.ToInt64(vals[vals.Length - 1]);
            }
            catch
            {
                ShowError("GPUmembyproc.exe not found!");
                FormClosing -= new FormClosingEventHandler(Form1_FormClosing);
                Application.Exit();
            }
            return whispersize;
        }

        bool outputexists(string filename)
        {
            if (!checkBox1.Checked && !Program.iswatch)
                return false;
            filename = filename.Remove(filename.LastIndexOf('.'));

            if (filename.EndsWith(".wav"))
                filename = filename.Remove(filename.LastIndexOf('.'));

            bool res = true;
            if (checkBox4.Checked)
                res = File.Exists(filename + ".srt");
            if (checkBox5.Checked)
                res &= File.Exists(filename + ".txt");
            if (checkBox6.Checked)
                res &= File.Exists(filename + ".vtt");
            return res;
        }

        void convertandwhisper(string filename)
        {
            try
            {
                while ((Process.GetProcessesByName("ffmpeg").Length >= numericUpDown1.Value ||
                    whisperq.Count >= numericUpDown1.Value) && !cancel)
                    Thread.Sleep(100);
                string outname = Path.Combine(getfolder(filename), Path.GetFileName(filename));
                int i = outname.LastIndexOf('.');
                if (i == -1 || cancel)
                    return;
                outname = outname.Remove(i) + ".wav";
                if (Path.GetExtension(filename).ToLower() == ".wav")
                    outname += ".wav";
                Process proc = new Process();
                proc.StartInfo.FileName = "ffmpeg.exe";
                proc.StartInfo.Arguments = "-y -i \"" + filename + "\" -vn -ar 16000 -ac 1 -ab 32k -af volume=1.75 -f wav \"" + outname + "\"";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;

                if (File.Exists(outname) || outputexists(outname))
                {
                    ffmpeg_Exited(proc, null);
                    return;
                }

                proc.EnableRaisingEvents = true;
                proc.Exited += ffmpeg_Exited;
                proc.Start();
            }
            catch (Exception ex)
            {
                cancel = true;
                ShowError("ffmpeg.exe not found, make sure it is on your path or same folder as Whisperer");
            }
        }

        void ffmpeg_Exited(object sender, EventArgs e)
        {
            qwhisper(getfilename((Process)sender));
        }

        string getfolder(string filename)
        {
            return glbsamefolder ? Path.GetDirectoryName(filename) : glboutdir;
        }

        void wait4it(string filename)
        {
            int div = 1;
            try
            {
                FileInfo fi = new FileInfo(filename);
                div = fi.Length < 10000000 ? 10 : fi.Length < 20000000 ? 3 : 1;
            }
            catch { }

            long whispersize = 0;

            while (whispersize == 0 && Process.GetProcessesByName("main").Length > 0 && !cancel)
            {
                Thread.Sleep(1000 / div);
                whispersize = getwhispersize();
                if (whispersize > 0)
                {
                    for (int i = 0; i < glbwaittime / div && !cancel; i += 1000)
                        Thread.Sleep(1000);
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
        }

        void qwhisper(string filename)
        {
            whisperq.Enqueue(new Action(() =>
            {
                try
                {
                    while (Process.GetProcessesByName("main").Length >= numericUpDown1.Value && !cancel)
                        Thread.Sleep(1000);
                    Process proc = new Process();
                    string translate = " ";
                    if (checkBox2.Checked)
                        translate = " -tr ";
                    proc.StartInfo.FileName = "main.exe";

                    string outtypes = "";
                    if (checkBox4.Checked)
                        outtypes = "--output-srt ";
                    if (checkBox5.Checked)
                        outtypes += "--output-txt ";
                    if (checkBox6.Checked)
                        outtypes += "--output-vtt ";

                    string prompt = " ";
                    if (glbprompt != "")
                        prompt = " --prompt \"" + glbprompt + "\" ";

                    proc.StartInfo.Arguments = "--language " + glblang + translate + outtypes + "--no-timestamps --max-context 0 --model \"" +
                        glbmodel + "\"" + prompt + "\"" + filename + "\"";
                    proc.StartInfo.UseShellExecute = false;
                    proc.StartInfo.CreateNoWindow = true;

                    if (outputexists(filename))
                    {
                        whisper_Exited(proc, null);
                        return;
                    }
                    if (!File.Exists(filename))
                        return;

                    fillmemvars();
                    long neededmem = 400000000;
                    if (glbwaittime == 15000)
                        neededmem = 2400000000;
                    else if (glbwaittime == 20000)
                        neededmem = 4300000000;

                    while (freemem < neededmem && !cancel)
                    {
                        Thread.Sleep(1000);
                        fillmemvars();
                    }

                    if (cancel)
                        return;

                    int wlen = Process.GetProcessesByName("main").Length;
                    proc.EnableRaisingEvents = true;
                    proc.Exited += whisper_Exited;
                    proc.Start();

                    while (Process.GetProcessesByName("main").Length == wlen)
                        Thread.Sleep(10);

                    wait4it(filename);
                }
                catch (Exception ex)
                {
                    cancel = true;
                    ShowError(ex.ToString());
                }
            }));
        }

        void tryrename(string filename, string ext)
        {
            try
            {
                string oldname = filename + ".wav" + ext;
                if (File.Exists(oldname))
                {
                    string newname = filename + ext;
                    if (File.Exists(newname))
                        File.Delete(newname);
                    File.Move(oldname, newname);
                }
            }
            catch { }
        }

        readonly string[] exts = { ".srt", ".txt", ".vtt" };

        void renamewaves(string filename)
        {
            if (filename.EndsWith(".wav.wav"))
            {
                filename = filename.Remove(filename.Length - 8);
                foreach (string ext in exts)
                    tryrename(filename, ext);
            }
        }

        string getfilename(Process p)
        {
            string filename = p.StartInfo.Arguments;
            filename = filename.TrimEnd('"');
            return filename.Substring(filename.LastIndexOf('"') + 1);
        }

        void whisper_Exited(object sender, EventArgs e)
        {
            try
            {
                string filename = getfilename((Process)sender);
                if (File.Exists(filename))
                {
                    completed++;
                    File.Delete(filename);
                }
                renamewaves(filename);
            }
            catch { }

            Invoke(new Action(() =>
            {
                label5.Text = completed.ToString("#,##0");
            }));
        }

        bool checkdir()
        {
            if (!checkBox3.Checked && !Directory.Exists(glboutdir))
            {
                try
                {
                    Directory.CreateDirectory(glboutdir);
                }
                catch
                {
                    ShowError("An error occured creating directory " + glboutdir);
                    return false;
                }
            }
            return true;
        }

        void whendone()
        {
            if (cancel || Program.iswatch)
                return;
            if (comboBox2.Text == "Shutdown")
                Process.Start("shutdown", "/s /t 1");
            else if (comboBox2.Text == "Sleep")
                Application.SetSuspendState(PowerState.Suspend, true, true);
            else if (comboBox2.Text == "Hibernate")
                Application.SetSuspendState(PowerState.Hibernate, true, true);
            else if (comboBox2.Text == "Lock")
                LockWorkStation();
            else if (comboBox2.Text == "Log off")
                ExitWindowsEx(0, 0);
            else if (comboBox2.Text == "Play sound")
                new SoundPlayer(Properties.Resources.tada).Play();
        }

        bool notdone()
        {
            return Process.GetProcessesByName("ffmpeg").Length > 0 || Process.GetProcessesByName("main").Length > 0 || whisperq.Count > 0;
        }

        void waitilldone()
        {
            while (!cancel)
            {
                if (notdone())
                    Thread.Sleep(1000);
                else
                {
                    Thread.Sleep(3000);
                    if (!notdone())
                        break;
                }
            }
        }

        void processarray()
        {
            foreach (string filename in glbarray)
            {
                if (cancel)
                    break;
                convertandwhisper(filename);
            }
        }

        void checkwatchfolders()
        {
            if (!Program.iswatch)
            {
                Program.iswatch = true;
                glbarray.Clear();
                loadwatchfilelist();
                processarray();
                if (glbarray.Count > 0)
                    waitilldone();
                Program.iswatch = false;
            }
        }

        void execwhisper()
        {
            processarray();
            waitilldone();
            checkwatchfolders();
        }

        void consumeq()
        {
            Action act = null;
            while (!quitq)
            {
                while (!quitq && whisperq.TryDequeue(out act))
                    act.Invoke();
                Thread.Sleep(100);
            }
        }

        bool isagen(string[] exts, ref string fname)
        {
            try
            {
                string ext = Path.GetExtension(fname).ToUpper();
                foreach (string s in exts)
                {
                    if (s == ext)
                        return true;
                }
            }
            catch { }
            return false;
        }

        readonly string[] audioext = {".3GA", ".669", ".A52", ".AAC", ".AAX", ".AC3", ".ADT", ".ADTS", ".AIF", ".AIFC",
            ".AIFF", ".AMB", ".AMR", ".AOB", ".APE", ".AU", ".AWB", ".CAF", ".DTS", ".FLAC", // ".DSF", ".DFF",
            ".IT", ".KAR", ".M4A", ".M4B", ".M4P", ".M4R", ".M5P", ".MID", ".MKA", ".MLP", ".MOD", ".MPA", ".MP1", ".MP2",
            ".MP3", ".MPC", ".MPGA", ".MUS", ".OGA", ".OGG", ".OMA", ".OPUS", ".QCP", ".RA", ".RMI", ".S3M", ".SID",
            ".SPX", ".TAK", ".THD", ".TTA", ".VOC", ".VOX", ".VQF", ".W64", ".WAV", ".WMA", ".WV", ".XA", ".XM" };
       
        readonly string[] videoext = {".3G2", ".3GP", ".3GP2", ".3GPP", ".AMV", ".ASF", ".AVI", ".BIK", ".BIN", ".CRF",
            ".DIVX", ".DRC", ".DV", ".DVR-MS", ".EVO", ".F4V", ".FLV", ".GVI", ".GXF", ".ISO", ".M1V", ".M2V",
            ".M2T", ".M2TS", ".M4V", ".MKV", ".MOV", ".MP2", ".MP2V", ".MP4", ".MP4V", ".MPE", ".MPEG", ".MPEG1",
            ".MPEG2", ".MPEG4", ".MPG", ".MPV2", ".MTS", ".MTV", ".MXF", ".MXG", ".NSV", ".NUV", ".OGM",
            ".OGV", ".OGX", ".RAM", ".REC", ".RM", ".RMVB", ".RPL", ".THP", ".TOD", ".TP", ".TS", ".TTS", ".TXD",
            ".VOB", ".VRO", ".WEBM", ".WM", ".WMV", ".WTV", ".XESC"};

        bool issoundtype(string fname)
        {
            return isagen(audioext, ref fname) || isagen(videoext, ref fname);
        }

        void loadwatchfilelist()
        {
            string[] folders = textBox3.Text.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string folder in folders)
            {
                try
                {
                    string[] files = Directory.GetFiles(folder);
                    foreach (string file in files)
                        if (issoundtype(file) && !glbarray.Contains(file))
                            glbarray.Add(file);
                }
                catch { }
            }
        }

        void button3_Click(object sender, EventArgs e)
        {
            try
            {
                if (button3.Text == "Go")
                {
                    if (fastObjectListView1.SelectedObjects.Count == 0 && textBox3.Text.Trim() == "")
                    {
                        ShowError("No files selected!");
                        return;
                    }
                    glboutdir = textBox1.Text.Trim();
                    glbprompt = textBox4.Text.Trim();
                    if (!checkdir())
                        return;
                    glbarray.Clear();
                    glbmodel = textBox2.Text;
                    if (!File.Exists(glbmodel))
                    {
                        ShowError(glbmodel + " not found!");
                        return;
                    }

                    glbwaittime = 10000;
                    if (glbmodel.ToLower().Contains("medium"))
                        glbwaittime = 15000;
                    else if (glbmodel.ToLower().Contains("large"))
                        glbwaittime = 20000;

                    if ((glbwaittime == 15000 && totmem < 2400000000) ||
                        (glbwaittime == 20000 && totmem < 4300000000))
                    {
                        ShowError("Insufficient graphics memory for this model!");
                        return;
                    }

                    if (Program.iswatch)
                    {
                        loadwatchfilelist();
                        if (glbarray.Count == 0)
                        {
                            Program.iswatch = false;
                            return;
                        }
                    }
                    else
                        foreach (filenameline filename in fastObjectListView1.SelectedObjects)
                            glbarray.Add(filename.filename);
                    cancel = false;
                    button3.Text = "Cancel";
                    completed = 0;
                    label5.Text = "0";
                    glbsamefolder = checkBox3.Checked;
                    glblang = "en";
                    Action act = null;
                    while (whisperq.Count > 0)
                    {
                        while (whisperq.TryDequeue(out act))
                            ;
                        Thread.Sleep(10);
                    }
                    quitq = false;
                    try
                    {
                        glblang = langs[comboBox1.Text];
                    }
                    catch { }
                    Thread thr = new Thread(() =>
                    {
                        execwhisper();
                        quitq = true;
                        Invoke(new Action(() =>
                        {
                            button3.Text = "Go";
                            timer1.Enabled = false;
                            whendone();
                        }));
                        Program.iswatch = false;
                    });
                    thr.IsBackground = true;
                    thr.Start();

                    Thread cq = new Thread(consumeq);
                    cq.IsBackground = true;
                    cq.Start();

                    sw.Restart();
                    timer1.Enabled = true;
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

        void fastObjectListView1_SelectionChanged(object sender, EventArgs e)
        {
            setcount();
        }

        void fastObjectListView1_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        void fastObjectListView1_DragDrop(object sender, DragEventArgs e)
        {
            Cursor = Cursors.WaitCursor;
            fastObjectListView1.BeginUpdate();
            foreach (string file in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                if (!fexists(file))
                    fastObjectListView1.AddObject(new filenameline(file));
            }
            fastObjectListView1.EndUpdate();
            fastObjectListView1.SelectAll();
            setcount();
            Cursor = Cursors.Default;
        }

        void checkBox3_CheckedChanged(object sender, EventArgs e)
        {
            textBox1.Enabled = !checkBox3.Checked;
        }

        void button4_Click(object sender, EventArgs e)
        {
            if (openFileDialog2.ShowDialog() == DialogResult.OK)
                textBox2.Text = openFileDialog2.FileName;
        }

        void button5_Click(object sender, EventArgs e)
        {
            if (folderBrowserDialog1.ShowDialog() == DialogResult.OK)
                textBox1.Text = folderBrowserDialog1.SelectedPath;
        }

        void timer1_Tick(object sender, EventArgs e)
        {
            label10.Text = sw.Elapsed.Hours.ToString("0") + ":" + sw.Elapsed.Minutes.ToString("00") + ":" +
                sw.Elapsed.Seconds.ToString("00");
        }

        void savefilelist()
        {
            string s = "";
            for (int i = 0; i < fastObjectListView1.Items.Count; i++)
                s += fastObjectListView1.Items[i].Text + ";";
            writereg("files", s.TrimEnd(';'));
        }

        void savesettings()
        {
            writereg("modelpath", textBox2.Text);
            writereg("outputdir", textBox1.Text);
            writereg("language", comboBox1.Text);
            writereg("srt", checkBox4.Checked.ToString());
            writereg("txt", checkBox5.Checked.ToString());
            writereg("vtt", checkBox6.Checked.ToString());
            writereg("maxatonce", numericUpDown1.Value.ToString());
            writereg("whendone", comboBox2.Text);
            writereg("sameasinputfolder", checkBox3.Checked.ToString());
            writereg("skipifexists", checkBox1.Checked.ToString());
            writereg("translate", checkBox2.Checked.ToString());
            writereg("watchfolders", textBox3.Text);
            writereg("prompt", textBox4.Text);
            savefilelist();
        }

        void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            savesettings();
        }

        void button6_Click(object sender, EventArgs e)
        {
            if (folderBrowserDialog1.ShowDialog() == DialogResult.OK)
            {
                string inputf = folderBrowserDialog1.SelectedPath;
                string[] folders = textBox3.Text.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                string s = "";
                foreach (string f in folders)
                {
                    if (string.Equals(inputf, f, StringComparison.InvariantCultureIgnoreCase))
                        return;
                    s += f + ";";
                }
                textBox3.Text = s + inputf;
            }
        }

        void watchwait()
        {
            while (true)
            {
                using (var stream = new NamedPipeServerStream("whispererwatchpipe", PipeDirection.InOut))
                    stream.WaitForConnection();

                if (button3.Text == "Go")
                {
                    Program.iswatch = true;
                    Invoke(new Action(() =>
                    {
                        button3_Click(null, null);
                    }));
                }
            }
        }

        void wait4launch()
        {
            while (true)
            {
                try
                {
                    using (var stream = new NamedPipeServerStream("whispererlaunchpipe", PipeDirection.InOut))
                        stream.WaitForConnection();
                    Invoke(new Action(() =>
                    {
                        SetForegroundWindow(Handle);
                    }));
                }
                catch (Exception ex)
                {
                    ShowError(ex.ToString());
                }
            }
        }

        string getapp()
        {
            string app = Environment.GetCommandLineArgs()[0];
            if (app[0] != '"')
                app = '"' + app + '"';
            return app;
        }

        string getqlaunch()
        {
            return '"' + Path.Combine(rootdir, "qlaunch.exe") + '"';
        }

        void loosets()
        {
            try
            {
                if (tasksched != null)
                {
                    tasksched.Triggers.Clear();
                    tasksched.Dispose();
                    tasksched = null;
                }
            }
            catch { }
        }

        TaskScheduler.Task CreateTask(string name)
        {
            try
            {
                tasksched = schedtask.CreateTask(name);
            }
            catch (ArgumentException)
            {
                Console.WriteLine("Task already exists");
                return null;
            }
            try
            {
                tasksched.ApplicationName = getqlaunch();
                tasksched.Parameters = getapp() + " /watch";
                tasksched.Comment = "Whisperer folder watcher";
                tasksched.Creator = Environment.UserName;
                tasksched.WorkingDirectory = rootdir;
                tasksched.Flags = TaskFlags.RunOnlyIfLoggedOn;
                tasksched.SetAccountInformation(Environment.UserName, (string)null);
                tasksched.MaxRunTimeLimited = false;
                tasksched.Priority = System.Diagnostics.ProcessPriorityClass.Normal;
                tasksched.Triggers.Add(new DailyTrigger(3, 0, 1));
            }
            catch (Exception ex)
            {
                ShowError(ex.ToString());
            }
            return tasksched;
        }

        bool setuptask()
        {
            bool had1 = false;
            if (schedtask == null)
                schedtask = new ScheduledTasks();
            loosets();
            tasksched = schedtask.OpenTask("Whisperer");
            if (tasksched == null)
                CreateTask("Whisperer");
            else
            {
                tasksched.ApplicationName = getqlaunch();
                tasksched.WorkingDirectory = rootdir;
                had1 = true;
            }
            return had1;
        }

        void saveworkaround()
        {
            try
            {
                tasksched.Save();
                setuptask();
                schedtask.DeleteTask("Whisperer");

                Trigger[] tgs = new Trigger[tasksched.Triggers.Count];
                tasksched.Triggers.CopyTo(tgs, 0);
                setuptask();
                tasksched.Triggers.Clear();

                foreach (DailyTrigger t in tgs)
                    tasksched.Triggers.Add(t);
            }
            catch (Exception ex)
            {
                ShowError(ex.ToString());
            }
        }

        DialogResult ShowMsgProc(string msg, bool iserr, string caption = "Whisperer", MessageBoxButtons mbb = MessageBoxButtons.OK)
        {
            MessageBoxIcon mbi;
            DialogResult dr = DialogResult.OK;
            if (mbb == MessageBoxButtons.OK)
                mbi = iserr ? MessageBoxIcon.Error : MessageBoxIcon.Information;
            else
                mbi = MessageBoxIcon.Question;
            dr = MessageBox.Show(msg, caption, mbb, mbi);
            return dr;
        }

        void ShowMsg(string msg)
        {
            ShowMsgProc(msg, false);
        }

        void ShowError(string msg)
        {
            ShowMsgProc(msg, true);
        }

        DialogResult AskQuestion(string msg, string caption, MessageBoxButtons buttons)
        {
            return ShowMsgProc(msg, false, caption, buttons);
        }

        void DoTask()
        {
            try
            {
                bool had1 = setuptask();
                if (tasksched == null)
                    return;

                if (tasksched.DisplayPropertySheet(TaskScheduler.Task.PropPages.Schedule))
                {
                    try
                    {
                        tasksched.Parameters = getapp() + " /watch";
                        saveworkaround();
                        tasksched.Save();
                    }
                    catch (Exception ex)
                    {
                        ShowError(ex.ToString());
                    }
                }
                else if (had1)
                {
                    string message = "Delete the scheduled task?";
                    string caption = "Delete Schedule";
                    MessageBoxButtons buttons = MessageBoxButtons.YesNo;
                    DialogResult result = AskQuestion(message, caption, buttons);
                    if (result == DialogResult.Yes)
                        schedtask.DeleteTask("Whisperer");
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.ToString());
            }

            loosets();
        }

        void button7_Click(object sender, EventArgs e)
        {
            DoTask();
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "WindowFromPoint", CharSet = CharSet.Auto, ExactSpelling = true)]
        public static extern IntPtr WindowFromPoint(Point pt);

        [DllImport("user32.dll")]
        public static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        [DllImport("user32.dll")]
        public static extern void LockWorkStation();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);
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
