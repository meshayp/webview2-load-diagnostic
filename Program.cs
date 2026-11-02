using System;
using System.Windows.Forms;

namespace WebView2TestApp
{
    // ─────────────────────────────────────────────────────────────────────
    // Config populated from command-line arguments.
    // ─────────────────────────────────────────────────────────────────────
    public class RunConfig
    {
        /// <summary>URL to load automatically on startup.  If null the user types one.</summary>
        public string StartUrl    { get; set; }

        /// <summary>Write the text summary to this file after every run batch.</summary>
        public string OutputFile  { get; set; }

        /// <summary>Close the window automatically when the run batch finishes.</summary>
        public bool   AutoExit    { get; set; }

        /// <summary>How many times to load the URL and average the results.</summary>
        public int    Repeat      { get; set; } = 1;

        /// <summary>Start the window minimized (useful for scripted runs).</summary>
        public bool   Minimized   { get; set; }

        /// <summary>Fail a run if it takes longer than this many ms (0 = no limit).</summary>
        public int    TimeoutMs   { get; set; } = 0;

        // ── Usage text ──────────────────────────────────────────────────
        public static string Usage => @"
WebView2 Load-Time Diagnostic
Usage:
  WebView2TestApp.exe [--url <url>] [--output <file>] [--exit]
                      [--repeat <n>] [--minimized] [--timeout <ms>]

Options:
  --url <url>        URL to load automatically on startup
  --output <file>    Save the text summary to a file after the run
  --exit             Close the window when the run finishes
  --repeat <n>       Load the URL n times and show averages  (default 1)
  --minimized        Start the window minimized
  --timeout <ms>     Warn if any single load exceeds this limit  (default 0 = none)

Examples:
  WebView2TestApp.exe --url https://myapp/login --exit --output c:\logs\timing.txt
  WebView2TestApp.exe --url https://myapp/ --repeat 5 --minimized
";
    }

    // ─────────────────────────────────────────────────────────────────────
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            var cfg = ParseArgs(args);
            if (cfg == null) return;   // --help was printed

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1(cfg));
        }

        // ── Argument parser ──────────────────────────────────────────────
        private static RunConfig ParseArgs(string[] args)
        {
            var cfg = new RunConfig();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        Console.WriteLine(RunConfig.Usage);
                        return null;

                    case "--url":
                        cfg.StartUrl = NextArg(args, ref i, "--url");
                        break;

                    case "--output":
                        cfg.OutputFile = NextArg(args, ref i, "--output");
                        break;

                    case "--exit":
                        cfg.AutoExit = true;
                        break;

                    case "--minimized":
                        cfg.Minimized = true;
                        break;

                    case "--repeat":
                        if (int.TryParse(NextArg(args, ref i, "--repeat"), out int r) && r > 0)
                            cfg.Repeat = r;
                        break;

                    case "--timeout":
                        if (int.TryParse(NextArg(args, ref i, "--timeout"), out int t) && t > 0)
                            cfg.TimeoutMs = t;
                        break;

                    default:
                        // Treat bare arguments as a URL
                        if (!args[i].StartsWith("-") && cfg.StartUrl == null)
                            cfg.StartUrl = args[i];
                        break;
                }
            }

            return cfg;
        }

        private static string NextArg(string[] args, ref int i, string flag)
        {
            if (i + 1 < args.Length) return args[++i];
            Console.Error.WriteLine($"Missing value for {flag}");
            return "";
        }
    }
}
