using System;
using System.IO.Pipes;
using System.Threading;
using System.Windows.Forms;

namespace whisperer
{
    static class Program
    {
        public static bool iswatch = false;
        static Mutex gmutex = null;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            bool createdNew;
            gmutex = new Mutex(true, "whisperermutex", out createdNew);
            iswatch = args.Length > 0 && args[0].ToLower().Trim() == "/watch";

            if (createdNew)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Form1());
            }
            else if (iswatch)
            {
                using (var pipe = new NamedPipeClientStream(".", "whispererwatchpipe", PipeDirection.InOut))
                    pipe.Connect(1000);
            }
            else
                BringTofront();
        }

        static void BringTofront()
        {
            using (var stream = new NamedPipeClientStream(".", "whispererlaunchpipe", PipeDirection.InOut))
                stream.Connect(1000);
        }
    }
}
