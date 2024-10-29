using BrightIdeasSoftware;
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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        long largereq = 0;
        Dictionary<string, durationrec> durations = new Dictionary<string, durationrec>();
        TimeSpan tottime;
        CustomProgressBar progressBar = new CustomProgressBar();
        DateTime starttime;

        public Form1()
        {
            InitializeComponent();
            progressBar.Location = new Point(timeelapsed.Location.X, label14.Location.Y - 3);
            progressBar.Size = new Size(comboBox1.Width, 18);
            progressBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            progressBar.DisplayStyle = ProgressBarDisplayText.CustomText;
            progressBar.CustomText = "--:--:--";
            Controls.Add(progressBar);
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

            if (Execute("ffmpeg.exe") == 2)
                ShowError(ffmpegnotfound);

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

            string tsvfile = Path.Combine(rootdir, "languageCodez.tsv");
            if (File.Exists(tsvfile))
            {
                foreach (string line in File.ReadLines(tsvfile))
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

            srtCheckBox.CheckedChanged += outputtype_CheckedChanged;
            txtCheckBox.CheckedChanged += outputtype_CheckedChanged;
            vttCheckBox.CheckedChanged += outputtype_CheckedChanged;

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
            s = readreg("langs", "");
            string[] reglangs = s.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            s = readreg("translates", "");
            string[] translates = s.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            fastObjectListView1.BeginUpdate();
            List<filenameline> items = new List<filenameline>();
            try
            {
                for (int i = 0; i < files.Length; i++)
                    items.Add(new filenameline(files[i], reglangs.Length > 0 ? reglangs[i] : "Default",
                        translates.Length > 0 ? (translates[i] == "1" ? true : false) : checkBox2.Checked));
            }
            catch { }
            fastObjectListView1.AddObjects(items);
            fastObjectListView1.EndUpdate();
            fastObjectListView1.SelectAll();
        }

        void loadsettings()
        {
            Cursor = Cursors.WaitCursor;
            textBox1.Text = readreg("outputdir", textBox1.Text);
            modelPathTextBox.Text = readreg("modelpath", modelPathTextBox.Text);
            comboBox1.Text = toproper(readreg("language", "English"));
            srtCheckBox.Checked = Convert.ToBoolean(readreg("srt", "True"));
            txtCheckBox.Checked = Convert.ToBoolean(readreg("txt", "False"));
            vttCheckBox.Checked = Convert.ToBoolean(readreg("vtt", "False"));
            numericUpDown1.Value = Convert.ToDecimal(readreg("maxatonce", "10"));
            comboBox2.Text = readreg("whendone", "Do nothing");
            checkBox3.Checked = Convert.ToBoolean(readreg("sameasinputfolder", "False"));
            skipIfExistCheckBox.Checked = Convert.ToBoolean(readreg("skipifexists", "True"));
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
            if (!clickedCheckBox.Checked && !srtCheckBox.Checked && !txtCheckBox.Checked && !vttCheckBox.Checked)
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
                Debug.WriteLine("Initializing GPU counters...");

                var category = new PerformanceCounterCategory("GPU Adapter Memory");
                var counterNames = category.GetInstanceNames();
                foreach (string counterName in counterNames)
                {
                    foreach (var counter in category.GetCounters(counterName))
                    {
                        if (counter.CounterName == "Dedicated Usage")
                        {
                            Debug.WriteLine($"  {counter.InstanceName}");
                            gpuCountersDedicated.Add(counter);
                        }
                    }
                }

                Debug.WriteLine($"GPU counters have been initialized. Counters count: {gpuCountersDedicated.Count}");

                if (gpuCountersDedicated.Count == 0)
                    ShowError("Failed to initialize GPU performance counters");

                goButton.BeginInvoke((Action)delegate { goButton.Enabled = true; });
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
            var usedMem = 0f;
            Debug.WriteLine("Calculating free VRAM...");
            gpuCountersDedicated.ForEach(x =>
            {
                float value = x.NextValue();
                Debug.WriteLine($"  {x.InstanceName}: {value}");
                usedMem += value;
            });
            if (totmem < usedMem)
                throw new Exception($"Failed to calculate free GPU memory. Used memory: {usedMem / 1024 / 1024} MB, Total memory: {totmem / 1024 / 1024} MB");
            return Convert.ToInt64(totmem - usedMem);
        }

        void fillmemvars()
        {
            freemem = getfreegpumem();
        }

        void button1_Click(object sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                Cursor = Cursors.WaitCursor;
                fastObjectListView1.BeginUpdate();
                filllookup();
                foreach (string filename in openFileDialog1.FileNames)
                {
                    if (!filelookup.ContainsKey(filename))
                    {
                        fastObjectListView1.AddObject(new filenameline(filename, checkBox2.Checked));
                        filelookup.TryAdd(filename, true);
                    }
                }
                filelookup.Clear();
                fastObjectListView1.EndUpdate();
                fastObjectListView1.SelectAll();
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

        bool outputexists(string filename, bool ignorecb = false)
        {
            if (!ignorecb && !skipIfExistCheckBox.Checked && !Program.iswatch)
                return false;
            filename = filename.Remove(filename.LastIndexOf('.'));

            if (filename.EndsWith(".wav"))
                filename = filename.Remove(filename.LastIndexOf('.'));

            bool res = true;
            if (srtCheckBox.Checked)
                res = File.Exists(filename + ".srt");
            if (txtCheckBox.Checked)
                res &= File.Exists(filename + ".txt");
            if (vttCheckBox.Checked)
                res &= File.Exists(filename + ".vtt");
            return res;
        }

        string wavname(string filename)
        {
            string outname = Path.Combine(getfolder(filename), Path.GetFileName(filename));
            int i = outname.LastIndexOf('.');
            if (i == -1)
                return "";
            outname = outname.Remove(i) + ".wav";
            if (Path.GetExtension(filename).ToLower() == ".wav")
                outname += ".wav";
            return outname;
        }

        void convertandwhisper(string filename)
        {
            try
            {
                while ((Process.GetProcessesByName("ffmpeg").Length >= numericUpDown1.Value ||
                    whisperq.Count >= numericUpDown1.Value) && !cancel)
                    Thread.Sleep(100);
                filename = filename.Substring(0, filename.IndexOf(';'));
                string outname = wavname(filename);
                if (outname == "" || cancel)
                    return;
                Process proc = new Process();
                proc.StartInfo.FileName = "ffmpeg.exe";
                proc.StartInfo.Arguments = "-y -i \"" + filename + "\" -vn -ar 16000 -ac 1 -ab 32k -af volume=1.75 -f wav \"" + outname + "\"";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.Domain = filename;
                try
                {
                    durationrec dr = durations[filename];
                    dr.starttime = DateTime.Now;
                }
                catch { }

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
                ShowError(ffmpegnotfound);
            }
        }

        void ffmpeg_Exited(object sender, EventArgs e)
        {
            qwhisper((Process)sender);
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

        void qwhisper(Process p)
        {
            string filename = getfilename(p);

            whisperq.Enqueue(new Action(() =>
            {
                try
                {
                    while (Process.GetProcessesByName("main").Length >= numericUpDown1.Value && !cancel)
                        Thread.Sleep(1000);
                    Process proc = new Process();
                    string errorOutput = "";
                    proc.StartInfo.FileName = "main.exe";
                    proc.StartInfo.Domain = p.StartInfo.Domain;
                    string[] splitrow = glbarray.FindRow(p.StartInfo.Domain).Split(';');
                    string langcode = splitrow[1];
                    string translate = " ";
                    if (splitrow[2] == "1")
                        translate = " -tr ";

                    string outtypes = "";
                    if (srtCheckBox.Checked)
                        outtypes = "--output-srt ";
                    if (txtCheckBox.Checked)
                        outtypes += "--output-txt ";
                    if (vttCheckBox.Checked)
                        outtypes += "--output-vtt ";

                    string prompt = " ";
                    if (glbprompt != "")
                        prompt = " --prompt \"" + glbprompt + "\" ";

                    proc.StartInfo.Arguments = "--language " + langcode + translate + outtypes + "--no-timestamps --max-context 0 --model \"" +
                        glbmodel + "\"" + prompt + "\"" + filename + "\"";
                    proc.StartInfo.UseShellExecute = false;
                    proc.StartInfo.CreateNoWindow = true;

                    if (outputexists(filename))
                    {
                        whisper_Exited(proc, null, null);
                        return;
                    }
                    if (!File.Exists(filename))
                        return;

                    fillmemvars();
                    long neededmem = 400000000;
                    if (glbwaittime == 15000)
                        neededmem = 2400000000;
                    else if (glbwaittime == 20000)
                        neededmem = largereq;

                    while (freemem < neededmem && !cancel)
                    {
                        Thread.Sleep(1000);
                        fillmemvars();
                    }

                    if (cancel)
                        return;

                    proc.StartInfo.RedirectStandardError = true;
                    proc.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            Debug.WriteLine(e.Data);
                            errorOutput += e.Data;
                            errorOutput += "\n";
                        }
                    };
                    int wlen = Process.GetProcessesByName("main").Length;
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (sender, e) => { whisper_Exited(sender, e, errorOutput); };

                    Debug.WriteLine($"Starting main.exe. Arguments: {proc.StartInfo.Arguments}");
                    proc.Start();
                    proc.BeginErrorReadLine();

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

        int Execute(string path)
        {
            string dir = "";
            try
            {
                dir = Path.GetDirectoryName(path);
            }
            catch { }
            try
            {
                IntPtr result = ShellExecute(0, "", path, "", dir, path == "ffmpeg.exe" ? SW_HIDE : SW_SHOWNORMAL);
                return result.ToInt32();
            }
            catch { }
            return -1;
        }

        void openinplayer(object o)
        {
            filenameline f = (filenameline)o;
            if (File.Exists(f.filename))
                Execute(f.filename);
        }

        private void fastObjectListView1_CellClick(object sender, BrightIdeasSoftware.CellClickEventArgs e)
        {
            try
            {
                if (e.ClickCount == 2 && e.HitTest.HitTestLocation == HitTestLocation.Text)
                    openinplayer(fastObjectListView1.SelectedObject);
            }
            catch { }
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

        void settimeremaining(TimeSpan t)
        {
            int totalHours = (int)t.TotalHours;
            progressBar.CustomText = $"{totalHours}:{t:mm\\:ss}";
            TimeSpan totalDuration = DateTime.Now - starttime + t;
            TimeSpan elapsedTime = DateTime.Now - starttime;
            int progress = (int)((elapsedTime.TotalMilliseconds / totalDuration.TotalMilliseconds) * 100);
            int value = Math.Min(Math.Max(progress, 0), 100);
            if (value > progressBar.Value)
                progressBar.Value = value;
        }

        void setelapsed()
        {
            int totalHours = (int)sw.Elapsed.TotalHours;
            timeelapsed.Text = $"{totalHours}:{sw.Elapsed:mm\\:ss}";
        }

        int maxmains = 0;
        void updatetimeremaining()
        {
            TimeSpan exectime = TimeSpan.Zero;
            TimeSpan duration = TimeSpan.Zero;

            foreach (KeyValuePair<string, durationrec> r in durations)
            {
                if (r.Value.exectime != TimeSpan.Zero)
                {
                    exectime += r.Value.exectime;
                    duration += r.Value.duration;
                }
            }

            if (exectime <= TimeSpan.Zero || duration <= TimeSpan.Zero)
                return;

            int mains = Process.GetProcessesByName("main").Length;
            if (mains > maxmains)
                maxmains = mains;
            TimeSpan timetodo = tottime - duration;
            double timepersec = exectime.TotalSeconds / duration.TotalSeconds / (maxmains + 1);
            int todo = (int)(timetodo.TotalSeconds * timepersec);
            TimeSpan ttodo = new TimeSpan(0, 0, todo);
            Invoke(new Action(() =>
            {
                settimeremaining(ttodo);
            }));
        }

        void whisper_Exited(object sender, EventArgs e, string errorOutput)
        {
            try
            {
                var proc = sender as Process;
                string filename = getfilename(proc);
                try
                {
                    durationrec dr = durations[proc.StartInfo.Domain];
                    dr.exectime = DateTime.Now - dr.starttime;
                }
                catch { }
                try
                {
                    if (proc.ExitCode != 0)
                        ShowError($"main.exe has finished with error. Exit code: {proc.ExitCode}\n\n{errorOutput}\n\nFile name: {filename}");
                    else if (!outputexists(filename, !Program.iswatch))
                        ShowError($"Something went wrong, no output produced!\n\nFile name: {filename}");
                }
                catch { }

                if (File.Exists(filename))
                    File.Delete(filename);
                completed++;
                renamewaves(filename);
                updatetimeremaining();
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

        void playsound()
        {
            string soundfile = Path.Combine(rootdir, "sound.wav");
            if (File.Exists(soundfile))
                new SoundPlayer(soundfile).Play();
            else
                new SoundPlayer(Properties.Resources.tada).Play();
        }

        void whendone()
        {
            progressBar.Value = 0;
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
                playsound();
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
            if (!Program.iswatch && !cancel)
            {
                Program.iswatch = true;
                glbarray.Clear();
                loadwatchfilelist();
                getdurations();
                tottime = gettottime();
                starttime = DateTime.Now;
                Invoke(new Action(() =>
                {
                    progressBar.Value = 0;
                }));
                processarray();
                if (tottime > TimeSpan.Zero)
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
                    act();
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
                        if (issoundtype(file) && !glbarray.MyContains(file))
                            Invoke(new Action(() =>
                            {
                                glbarray.Add(file + ";" + langs[comboBox1.Text] + ";" + (checkBox2.Checked ? "1" : "0"));
                            }));
                }
                catch { }
            }
        }

        TimeSpan getduration(string filename)
        {
            try
            {
                Process proc = new Process();
                proc.StartInfo.FileName = "ffmpeg.exe";
                proc.StartInfo.Arguments = "-i \"" + filename + '"';
                proc.StartInfo.RedirectStandardInput = true;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.Start();
                string output = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                int pos = output.IndexOf("Duration:");

                if (pos == -1)
                    return TimeSpan.Zero;

                pos += 10;
                int comma = output.IndexOf(',', pos);
                string s = output.Substring(pos, comma - pos);
                string[] units = s.Split(new char[] { ':', '.' });
                return new TimeSpan(Convert.ToInt32(units[0]), Convert.ToInt32(units[1]), Convert.ToInt32(units[2]));
            }
            catch (Exception ex)
            {
                if (!cancel)
                {
                    cancel = true;
                    ShowError(ffmpegnotfound);
                }
            }
            return TimeSpan.Zero;
        }

        void getdurations()
        {
            List<System.Threading.Tasks.Task> tasks = new List<System.Threading.Tasks.Task>();
            durations.Clear();
            foreach (string filename in glbarray)
            {
                string fname = filename.Substring(0, filename.IndexOf(';'));
                string outname = wavname(fname);

                if (outname == "" || outputexists(outname))
                    continue;

                System.Threading.Tasks.Task t = System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    if (cancel)
                        return;
                    durationrec r = new durationrec(getduration(fname));
                    if (r.duration != TimeSpan.Zero)
                        lock (durations)
                            durations.Add(fname, r);
                });
                tasks.Add(t);
            }
            System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
        }

        TimeSpan gettottime()
        {
            TimeSpan tot = TimeSpan.Zero;
            foreach (KeyValuePair<string, durationrec> r in durations)
                tot += r.Value.duration;
            return tot;
        }

        string getlangcode(string lang)
        {
            if (lang == "Default")
                lang = comboBox1.Text;
            return langs[lang];
        }

        void button3_Click(object sender, EventArgs e)
        {
            try
            {
                if (goButton.Text == "Go")
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
                    glbmodel = modelPathTextBox.Text;
                    if (!File.Exists(glbmodel))
                    {
                        ShowError(glbmodel + " not found!");
                        return;
                    }

                    glbwaittime = 10000;
                    largereq = 4300000000;

                    if (glbmodel.ToLower().Contains("medium"))
                        glbwaittime = 15000;
                    else if (glbmodel.ToLower().Contains("large"))
                    {
                        glbwaittime = 20000;
                        if (totmem < 5000000000)
                            largereq = 3000000000;
                    }

                    if ((glbwaittime == 15000 && totmem < 2400000000) ||
                        (glbwaittime == 20000 && totmem < largereq))
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
                            glbarray.Add(filename.filename + ";" + getlangcode(filename.lang) + ";" + (filename.translate ? "1" : "0"));
                    cancel = false;
                    goButton.Text = "Cancel";
                    completed = 0;
                    label5.Text = "0";
                    glbsamefolder = checkBox3.Checked;
                    glblang = "en";
                    maxmains = 0;
                    Action act = null;
                    while (whisperq.Count > 0)
                    {
                        while (whisperq.TryDequeue(out act))
                            ;
                        Thread.Sleep(10);
                    }
                    quitq = false;
                    glblang = langs[comboBox1.Text];
                    Thread thr = new Thread(() =>
                    {
                        getdurations();
                        tottime = gettottime();
                        execwhisper();
                        quitq = true;
                        Invoke(new Action(() =>
                        {
                            goButton.Text = "Go";
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

                    progressBar.CustomText = "--:--:--";
                    progressBar.Value = 0;
                    starttime = DateTime.Now;
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

        ConcurrentBag<string> tmpbag = new ConcurrentBag<string>();
        void ReadFileList(string rootFolderPath, ParallelLoopState state1 = null)
        {
            try
            {
                if ((File.GetAttributes(rootFolderPath) & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
                {
                    var files = Directory.GetFiles(rootFolderPath);
                    Parallel.ForEach(files, (string afile, ParallelLoopState state) =>
                    {
                        Thread.CurrentThread.Priority = ThreadPriority.Highest;
                        if (issoundtype(afile) && !filelookup.ContainsKey(afile))
                        {
                            tmpbag.Add(afile);
                            filelookup.TryAdd(afile, true);
                        }
                    });

                    var directories = Directory.GetDirectories(rootFolderPath);
                    Parallel.ForEach(directories, ReadFileList);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }

        void fastObjectListView1_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        void loadbag()
        {
            List<filenameline> items = new List<filenameline>();
            string file;
            while (tmpbag.TryTake(out file))
                items.Add(new filenameline(file, checkBox2.Checked));
            fastObjectListView1.AddObjects(items);
        }

        ConcurrentDictionary<string, bool> filelookup = new ConcurrentDictionary<string, bool>();
        void filllookup()
        {
            filelookup.Clear();
            foreach (filenameline obj in fastObjectListView1.Objects)
                filelookup.TryAdd(obj.filename, true);
        }

        void fastObjectListView1_DragDrop(object sender, DragEventArgs e)
        {
            Cursor = Cursors.WaitCursor;
            fastObjectListView1.BeginUpdate();
            filllookup();
            foreach (string file in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                if ((File.GetAttributes(file) & FileAttributes.Directory) == FileAttributes.Directory)
                    ReadFileList(file);
                else if (!filelookup.ContainsKey(file))
                {
                    fastObjectListView1.AddObject(new filenameline(file, checkBox2.Checked));
                    filelookup.TryAdd(file, true);
                }
            }
            loadbag();
            filelookup.Clear();
            fastObjectListView1.EndUpdate();
            fastObjectListView1.SelectAll();
            Cursor = Cursors.Default;
        }

        void checkBox3_CheckedChanged(object sender, EventArgs e)
        {
            textBox1.Enabled = !checkBox3.Checked;
        }

        void button4_Click(object sender, EventArgs e)
        {
            if (openFileDialog2.ShowDialog() == DialogResult.OK)
                modelPathTextBox.Text = openFileDialog2.FileName;
        }

        void button5_Click(object sender, EventArgs e)
        {
            if (folderBrowserDialog1.ShowDialog() == DialogResult.OK)
                textBox1.Text = folderBrowserDialog1.SelectedPath;
        }

        void timer1_Tick(object sender, EventArgs e)
        {
            setelapsed();
            if (progressBar.CustomText[0] == '-')
                return;
            string[] rems = progressBar.CustomText.Split(':');
            TimeSpan t = new TimeSpan(Convert.ToInt32(rems[0]), Convert.ToInt32(rems[1]), Convert.ToInt32(rems[2]));
            t -= new TimeSpan(0, 0, 1);
            if (t >= TimeSpan.Zero)
                settimeremaining(t);
        }

        void savefilelist()
        {
            StringBuilder sb = new StringBuilder();
            StringBuilder langssb = new StringBuilder();
            StringBuilder translatesb = new StringBuilder();
            foreach (filenameline obj in fastObjectListView1.Objects)
            {
                sb.Append(obj.filename + ";");
                langssb.Append(obj.lang + ";");
                translatesb.Append((obj.translate ? "1" : "0") + ";");
            }
            writereg("files", sb.ToString().TrimEnd(';'));
            writereg("langs", langssb.ToString().TrimEnd(';'));
            writereg("translates", translatesb.ToString().TrimEnd(';'));
        }

        void savesettings()
        {
            writereg("modelpath", modelPathTextBox.Text);
            writereg("outputdir", textBox1.Text);
            writereg("language", comboBox1.Text);
            writereg("srt", srtCheckBox.Checked.ToString());
            writereg("txt", txtCheckBox.Checked.ToString());
            writereg("vtt", vttCheckBox.Checked.ToString());
            writereg("maxatonce", numericUpDown1.Value.ToString());
            writereg("whendone", comboBox2.Text);
            writereg("sameasinputfolder", checkBox3.Checked.ToString());
            writereg("skipifexists", skipIfExistCheckBox.Checked.ToString());
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

                if (goButton.Text == "Go")
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

        void losets()
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
            losets();
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
            Invoke(new Action(() =>
            {
                dr = MessageBox.Show(this, msg, caption, mbb, mbi);
            }));
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
                    DialogResult result = AskQuestion("Delete the scheduled task?", "Delete Schedule", MessageBoxButtons.YesNo);
                    if (result == DialogResult.Yes)
                        schedtask.DeleteTask("Whisperer");
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.ToString());
            }

            losets();
        }

        void button7_Click(object sender, EventArgs e)
        {
            DoTask();
        }

        private void fastObjectListView1_CellRightClick(object sender, CellRightClickEventArgs e)
        {
            if (e.ColumnIndex == 0)
            {
                filenameline f = e.Model as filenameline;
                if (f != null)
                {
                    contextMenuStrip1.Tag = f;
                    e.MenuStrip = this.contextMenuStrip1;
                }
            }
            else
                e.MenuStrip = this.contextMenuStrip2;
        }

        private void resetAllToDefaultToolStripMenuItem_Click(object sender, EventArgs e)
        {
            fastObjectListView1.BeginUpdate();
            foreach (filenameline obj in fastObjectListView1.Objects)
            {
                obj.lang = "Default";
                obj.translate = checkBox2.Checked;
            }
            fastObjectListView1.EndUpdate();
        }

        private void toolStripMenuItem1_Click(object sender, EventArgs e)
        {
            openinplayer(contextMenuStrip1.Tag);
        }

        private void openinexplorer(object o)
        {
            filenameline f = (filenameline)o;
            string path = f.filename;
            if (!File.Exists(path))
            {
                string folder = Path.GetDirectoryName(path);
                if (Directory.Exists(folder))
                    path = Path.Combine(folder, ".");
                else
                    return;
            }
            string argument = "/select, \"" + path + "\"";
            Process.Start("explorer.exe", argument);
        }

        private void toolStripMenuItem2_Click(object sender, EventArgs e)
        {
            openinexplorer(contextMenuStrip1.Tag);
        }

        private void toolStripMenuItem3_Click(object sender, EventArgs e)
        {
            fastObjectListView1.RemoveObject(contextMenuStrip1.Tag);
        }

        private void toolStripMenuItem4_Click(object sender, EventArgs e)
        {
            fastObjectListView1.RemoveObjects(fastObjectListView1.SelectedObjects);
        }

        private void fastObjectListView1_CellEditStarting(object sender, CellEditEventArgs e)
        {
            if (e.Column.AspectName == "lang")
            {
                ComboBox comboBox = new ComboBox();
                comboBox.Bounds = e.CellBounds;
                comboBox.Font = ((ObjectListView)sender).Font;
                comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
                string[] languageNames = new string[langs.Count];
                langs.Keys.CopyTo(languageNames, 0);
                Array.Sort(languageNames);
                List<string> list = new List<string>(languageNames);
                list.Insert(0, "Default");
                languageNames = list.ToArray();
                comboBox.Items.AddRange(languageNames);
                filenameline fileItem = (filenameline)e.RowObject;
                comboBox.SelectedItem = fileItem.lang == comboBox1.SelectedItem.ToString() ? "Default" : fileItem.lang;
                e.Control = comboBox;
            }
        }

        private void fastObjectListView1_CellEditFinishing(object sender, CellEditEventArgs e)
        {
            if (e.Column.AspectName == "lang")
            {
                ComboBox comboBox = e.Control as ComboBox;
                if (comboBox != null)
                    e.NewValue = comboBox.SelectedItem.ToString() == comboBox1.SelectedItem.ToString() ? "Default" : comboBox.SelectedItem.ToString();
            }
        }

        private void checkBox2_CheckedChanged(object sender, EventArgs e)
        {
            fastObjectListView1.BeginUpdate();
            foreach (filenameline obj in fastObjectListView1.Objects)
                obj.translate = checkBox2.Checked;
            fastObjectListView1.EndUpdate();
        }

        const string ffmpegnotfound = "ffmpeg.exe not found, make sure it is on your path or same folder as Whisperer";
        const int SW_HIDE = 0;
        const int SW_SHOWNORMAL = 1;

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

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr ShellExecute(int hwnd, string lpOperation, string lpFile,
        string lpParameters, string lpDirectory, int nShowCmd);
    }

    public class filenameline
    {
        public string filename;
        public string lang;
        public bool translate;

        public filenameline(string filename)
        {
            this.filename = filename;
            this.lang = "Default";
            this.translate = false;
        }

        public filenameline(string filename, bool translate)
        {
            this.filename = filename;
            this.lang = "Default";
            this.translate = translate;
        }

        public filenameline(string filename, string lang, bool translate)
        {
            this.filename = filename;
            this.lang = lang;
            this.translate = translate;
        }
    }

    public class durationrec
    {
        public TimeSpan duration;
        public DateTime starttime;
        public TimeSpan exectime;

        public durationrec(TimeSpan duration)
        {
            this.duration = duration;
        }
    }

    public enum ProgressBarDisplayText
    {
        Percentage,
        CustomText
    }

    public class CustomProgressBar : ProgressBar
    {
        private string _customText;
        public string CustomText
        {
            get => _customText;
            set
            {
                _customText = value;
                Invalidate();
            }
        }
        public ProgressBarDisplayText DisplayStyle { get; set; }

        public CustomProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            SetWindowTheme(this.Handle, "", "");
            base.OnHandleCreated(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle rect = ClientRectangle;
            Graphics g = e.Graphics;
            ProgressBarRenderer.DrawHorizontalBar(g, rect);
            rect.Inflate(-3, -3);
            if (Value > 0)
            {
                Rectangle clip = new System.Drawing.Rectangle(rect.X, rect.Y, (int)Math.Round(((float)Value / Maximum) * rect.Width), rect.Height);
                ProgressBarRenderer.DrawHorizontalChunks(g, clip);
            }

            string text = DisplayStyle == ProgressBarDisplayText.Percentage ? Value.ToString() + '%' : CustomText;

            using (System.Drawing.Font f = new System.Drawing.Font("Calibri", 7.8f))
            {
                SizeF len = g.MeasureString(text, f);
                Point location = new Point(Convert.ToInt32((Width / 2) - len.Width / 2), Convert.ToInt32((Height / 2) - len.Height / 2));
                g.DrawString(text, f, Brushes.Black, location);
            }
        }

        [DllImportAttribute("uxtheme.dll")]
        private static extern int SetWindowTheme(IntPtr hWnd, string appname, string idlist);
    }

    public static class Extensions
    {
        public static bool MyContains(this ArrayList arrayList, string filename)
        {
            filename += ';';
            foreach (string s in arrayList)
                if (s.StartsWith(filename))
                    return true;
            return false;
        }

        public static string FindRow(this ArrayList arrayList, string filename)
        {
            filename += ';';
            foreach (string s in arrayList)
                if (s.StartsWith(filename))
                    return s;
            return "";
        }
    }
}
