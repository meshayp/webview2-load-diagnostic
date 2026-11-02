// WebView2 Load-Time Diagnostic Tool
//
// Self-running mode (CLI args via RunConfig):
//   --url <url>        loads automatically on startup
//   --repeat <n>       loads n times and shows averages
//   --output <file>    saves summary text when done
//   --exit             closes the window when done
//
// Interactive mode: type a URL in the box and press Load (or Enter).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;

namespace WebView2TestApp
{
    // ─────────────────────────────────────────────────────────────────────
    // One load run's raw timing data
    // ─────────────────────────────────────────────────────────────────────
    internal class RunResult
    {
        public int    RunIndex;
        public long   TNavCall, TNavStarting, TContentLoading, TDomLoaded, TNavCompleted;
        public bool   Ok;
        public int    HttpStatus;
        public string ErrorStatus;
        public string NavTimingJson;
        public string ResourceListJson;
        public string PageTitle;

        /// <summary>Time from Navigate() call to NavigationCompleted.</summary>
        public long TotalMs => TNavCompleted - TNavCall;

        /// <summary>True if total exceeded the configured timeout.</summary>
        public bool TimedOut(int limitMs) => limitMs > 0 && TotalMs > limitMs;
    }

    // ─────────────────────────────────────────────────────────────────────
    public class Form1 : Form
    {
        // ── Config ────────────────────────────────────────────────────────
        private readonly RunConfig _cfg;

        // ── Controls ──────────────────────────────────────────────────────
        private TextBox      _txtUrl;
        private Button       _btnLoad, _btnCopy;
        private WebView2     _webView;
        private DataGridView _gridPhases, _gridResources;
        private RichTextBox  _rtbSummary;
        private TabControl   _tabs;
        private Label        _lblStatus;
        private ProgressBar  _progressBar;

        // ── Single-run state (reset before each run) ─────────────────────
        private Stopwatch  _sw;
        private TaskCompletionSource<bool> _runTcs;   // completed in OnNavigationCompleted

        // Intermediate timestamps captured on WV2 thread
        private long _tNavStarting, _tContentLoading, _tDomLoaded;

        // JS strings captured after NavigationCompleted
        private string _navTimingJson, _resourceListJson, _pageTitle;
        private bool   _navOk;
        private int    _httpStatus;
        private string _errorStatus;

        private bool _ready;          // WebView2 initialised
        private bool _batchRunning;   // prevents re-entrant clicks

        // ══════════════════════════════════════════════════════════════════
        public Form1() : this(new RunConfig()) { }

        public Form1(RunConfig cfg)
        {
            _cfg = cfg;
            BuildUI();
            if (cfg.Minimized) WindowState = FormWindowState.Minimized;
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            await InitWebViewAsync();
        }

        // ══════════════════════════════════════════════════════════════════
        // UI CONSTRUCTION
        // ══════════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            Text        = "WebView2 Load-Time Diagnostic";
            Size        = new Size(1100, 760);
            MinimumSize = new Size(800, 580);

            // ── URL bar ──────────────────────────────────────────────────
            var urlBar = new Panel { Height = 38, Dock = DockStyle.Top, Padding = new Padding(4, 4, 4, 0) };
            var tbl    = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

            var lbl = new Label { Text = "URL:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI", 10) };
            _txtUrl = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10), Text = _cfg.StartUrl ?? "https://www.bing.com" };
            _txtUrl.KeyDown += (s, ea) => { if (ea.KeyCode == Keys.Enter) { ea.SuppressKeyPress = true; ClickLoad(); } };

            var repeatLabel = new Label
            {
                Text      = _cfg.Repeat > 1 ? $"×{_cfg.Repeat} runs" : "",
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI", 9, FontStyle.Italic),
                ForeColor = Color.DimGray
            };

            _btnLoad = new Button { Text = "▶  Load", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10), FlatStyle = FlatStyle.System };
            _btnLoad.Click += (s, ea) => ClickLoad();

            foreach (Control c in new Control[] { lbl, _txtUrl, repeatLabel, _btnLoad })
                c.Margin = new Padding(2);

            tbl.Controls.Add(lbl,          0, 0);
            tbl.Controls.Add(_txtUrl,       1, 0);
            tbl.Controls.Add(repeatLabel,   2, 0);
            tbl.Controls.Add(_btnLoad,      3, 0);
            urlBar.Controls.Add(tbl);

            // ── Progress + status bar ─────────────────────────────────────
            var bottom = new Panel { Height = 26, Dock = DockStyle.Bottom, Padding = new Padding(4, 2, 4, 2) };
            _progressBar = new ProgressBar { Dock = DockStyle.Left, Width = 160, Visible = false, Style = ProgressBarStyle.Marquee };
            _lblStatus   = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9), TextAlign = ContentAlignment.MiddleLeft };
            bottom.Controls.Add(_lblStatus);
            bottom.Controls.Add(_progressBar);

            // ── Hidden WebView2 (1px — must be hosted to work) ───────────
            var wvPanel = new Panel { Height = 1, Dock = DockStyle.Bottom };
            _webView = new WebView2 { Dock = DockStyle.Fill };
            wvPanel.Controls.Add(_webView);

            // ── Result tabs ───────────────────────────────────────────────
            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(BuildPhasesTab());
            _tabs.TabPages.Add(BuildResourcesTab());
            _tabs.TabPages.Add(BuildSummaryTab());

            Controls.Add(_tabs);
            Controls.Add(wvPanel);
            Controls.Add(bottom);
            Controls.Add(urlBar);
        }

        private TabPage BuildPhasesTab()
        {
            var page = new TabPage("Load Phases");
            _gridPhases = MakeGrid(page);
            _gridPhases.Columns.Add("Run",     "Run");
            _gridPhases.Columns.Add("Phase",   "Phase");
            _gridPhases.Columns.Add("Abs",     "Abs (ms)");
            _gridPhases.Columns.Add("Delta",   "Δ prev (ms)");
            _gridPhases.Columns.Add("Note",    "Note");
            _gridPhases.Columns["Run"].FillWeight   = 30;
            _gridPhases.Columns["Abs"].FillWeight   = 50;
            _gridPhases.Columns["Delta"].FillWeight = 60;
            _gridPhases.Columns["Note"].FillWeight  = 200;
            _gridPhases.Columns["Abs"].DefaultCellStyle.Alignment   = DataGridViewContentAlignment.MiddleRight;
            _gridPhases.Columns["Delta"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            return page;
        }

        private TabPage BuildResourcesTab()
        {
            var page = new TabPage("Resources");
            _gridResources = MakeGrid(page);
            _gridResources.Columns.Add("Name",     "Resource URL");
            _gridResources.Columns.Add("Type",     "Type");
            _gridResources.Columns.Add("Start",    "Start (ms)");
            _gridResources.Columns.Add("Duration", "Duration (ms)");
            _gridResources.Columns.Add("TTFB",     "TTFB (ms)");
            _gridResources.Columns.Add("Transfer", "Transfer");
            _gridResources.Columns["Name"].FillWeight     = 350;
            _gridResources.Columns["Start"].DefaultCellStyle.Alignment    = DataGridViewContentAlignment.MiddleRight;
            _gridResources.Columns["Duration"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _gridResources.Columns["TTFB"].DefaultCellStyle.Alignment     = DataGridViewContentAlignment.MiddleRight;
            _gridResources.Columns["Transfer"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

            var note = new Label
            {
                Text      = "ⓘ  From window.performance.getEntriesByType('resource') — last run only, sorted slowest-first.",
                Dock      = DockStyle.Bottom, Height = 22,
                Font      = new Font("Segoe UI", 8, FontStyle.Italic),
                ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0)
            };
            page.Controls.Add(note);
            return page;
        }

        private TabPage BuildSummaryTab()
        {
            var page = new TabPage("Summary");
            _btnCopy = new Button { Text = "Copy to Clipboard", Dock = DockStyle.Bottom, Height = 28, FlatStyle = FlatStyle.System };
            _btnCopy.Click += (s, e) => { if (_rtbSummary.Text.Length > 0) Clipboard.SetText(_rtbSummary.Text); };
            _rtbSummary = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 10), WordWrap = false };
            page.Controls.Add(_rtbSummary);
            page.Controls.Add(_btnCopy);
            return page;
        }

        private static DataGridView MakeGrid(TabPage parent)
        {
            var g = new DataGridView
            {
                Dock                  = DockStyle.Fill,
                ReadOnly              = true,
                AllowUserToAddRows    = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible     = false,
                AutoSizeColumnsMode   = DataGridViewAutoSizeColumnsMode.Fill,
                Font                  = new Font("Consolas", 9),
                BackgroundColor       = Color.White,
                BorderStyle           = BorderStyle.None,
                SelectionMode         = DataGridViewSelectionMode.FullRowSelect
            };
            parent.Controls.Add(g);
            return g;
        }

        // ══════════════════════════════════════════════════════════════════
        // WEBVIEW2 INIT
        // ══════════════════════════════════════════════════════════════════

        private async Task InitWebViewAsync()
        {
            try
            {
                SetStatus("Initializing WebView2…");
                await _webView.EnsureCoreWebView2Async(null);
                _ready = true;

                var cw = _webView.CoreWebView2;
                cw.NavigationStarting  += OnNavigationStarting;
                cw.ContentLoading      += OnContentLoading;
                cw.DOMContentLoaded    += OnDomContentLoaded;
                cw.NavigationCompleted += OnNavigationCompleted;

                SetStatus($"Ready  —  WebView2 {cw.Environment.BrowserVersionString}");

                // Auto-start if a URL was given on the command line
                if (!string.IsNullOrWhiteSpace(_cfg.StartUrl))
                    await RunBatchAsync(_cfg.StartUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "WebView2 failed to initialize:\n\n" + ex.Message +
                    "\n\nInstall the Microsoft Edge WebView2 Runtime:\nhttps://developer.microsoft.com/en-us/microsoft-edge/webview2/",
                    "Init Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("Init failed");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // RUN BATCH
        // ══════════════════════════════════════════════════════════════════

        private void ClickLoad()
        {
            if (!_ready || _batchRunning) return;
            // Fire-and-forget (async void pattern for UI event)
            _ = RunBatchAsync(_txtUrl.Text.Trim());
        }

        /// <summary>
        /// Loads the URL _cfg.Repeat times, displays per-run and average results,
        /// saves to file, and optionally exits.
        /// </summary>
        private async Task RunBatchAsync(string url)
        {
            if (!_ready || _batchRunning) return;
            _batchRunning    = true;
            _btnLoad.Enabled = false;

            if (!url.Contains("://")) url = "https://" + url;
            _txtUrl.Text = url;

            _gridPhases.Rows.Clear();
            _gridResources.Rows.Clear();
            _rtbSummary.Clear();

            var results = new List<RunResult>(_cfg.Repeat);

            for (int run = 1; run <= _cfg.Repeat; run++)
            {
                SetStatus($"Run {run}/{_cfg.Repeat}  —  {url}");
                _progressBar.Visible = true;

                var result = await RunOnceAsync(url, run);
                results.Add(result);

                string flag = result.Ok
                    ? $"✔  HTTP {result.HttpStatus}  total {result.TotalMs} ms"
                    : $"✗  {result.ErrorStatus}  HTTP {result.HttpStatus}";

                if (result.TimedOut(_cfg.TimeoutMs))
                    flag += $"  ⚠ TIMEOUT (limit {_cfg.TimeoutMs} ms)";

                SetStatus($"Run {run}/{_cfg.Repeat} done  —  {flag}");

                // Small pause between repeated runs so the server isn't hit instantly
                if (run < _cfg.Repeat)
                    await Task.Delay(800);
            }

            _progressBar.Visible = false;

            // Display results
            PresentResults(results);

            // Save to file if requested
            if (!string.IsNullOrWhiteSpace(_cfg.OutputFile))
            {
                try
                {
                    File.WriteAllText(_cfg.OutputFile, _rtbSummary.Text, Encoding.UTF8);
                    SetStatus(SetStatus_Last + $"  →  saved to {_cfg.OutputFile}");
                }
                catch (Exception ex)
                {
                    SetStatus($"Save failed: {ex.Message}");
                }
            }

            _btnLoad.Enabled = true;
            _batchRunning    = false;

            if (_cfg.AutoExit)
                Close();
        }

        // ── Single-run execution ──────────────────────────────────────────

        private async Task<RunResult> RunOnceAsync(string url, int index)
        {
            // Reset single-run state
            _runTcs         = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tNavStarting   = -1;
            _tContentLoading = -1;
            _tDomLoaded     = -1;
            _navTimingJson  = "";
            _resourceListJson = "";
            _pageTitle      = "";
            _navOk          = false;
            _httpStatus     = 0;
            _errorStatus    = "";

            _sw = Stopwatch.StartNew();
            long tNavCall = _sw.ElapsedMilliseconds;

            _webView.CoreWebView2.Navigate(url);

            // Wait for NavigationCompleted (which also collects JS timing)
            using var cts = _cfg.TimeoutMs > 0
                ? new CancellationTokenSource(_cfg.TimeoutMs + 5000)
                : new CancellationTokenSource();

            try
            {
                await _runTcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _errorStatus = "TIMEOUT (no NavigationCompleted)";
            }

            _sw.Stop();

            return new RunResult
            {
                RunIndex        = index,
                TNavCall        = tNavCall,
                TNavStarting    = _tNavStarting,
                TContentLoading = _tContentLoading,
                TDomLoaded      = _tDomLoaded,
                TNavCompleted   = _sw.ElapsedMilliseconds,
                Ok              = _navOk,
                HttpStatus      = _httpStatus,
                ErrorStatus     = _errorStatus,
                NavTimingJson   = _navTimingJson,
                ResourceListJson = _resourceListJson,
                PageTitle       = _pageTitle
            };
        }

        // ══════════════════════════════════════════════════════════════════
        // WEBVIEW2 EVENT HANDLERS
        // ══════════════════════════════════════════════════════════════════

        private void OnNavigationStarting(object s, CoreWebView2NavigationStartingEventArgs e)
        {
            _tNavStarting = _sw?.ElapsedMilliseconds ?? -1;
        }

        private void OnContentLoading(object s, CoreWebView2ContentLoadingEventArgs e)
        {
            _tContentLoading = _sw?.ElapsedMilliseconds ?? -1;
        }

        private void OnDomContentLoaded(object s, CoreWebView2DOMContentLoadedEventArgs e)
        {
            _tDomLoaded = _sw?.ElapsedMilliseconds ?? -1;
        }

        private async void OnNavigationCompleted(object s, CoreWebView2NavigationCompletedEventArgs e)
        {
            _navOk      = e.IsSuccess;
            _httpStatus = e.HttpStatusCode;
            _errorStatus = e.IsSuccess ? "" : e.WebErrorStatus.ToString();

            // Read browser-side timing while still on the WV2 callback
            try
            {
                _navTimingJson    = await _webView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(performance.timing)");
                _resourceListJson = await _webView.CoreWebView2.ExecuteScriptAsync(@"
JSON.stringify(
  performance.getEntriesByType('resource').map(r => ({
    name:     r.name,
    type:     r.initiatorType,
    start:    Math.round(r.startTime),
    duration: Math.round(r.duration),
    ttfb:     Math.round(r.responseStart - r.requestStart),
    transfer: r.transferSize
  }))
)");
                _pageTitle = await _webView.CoreWebView2.ExecuteScriptAsync("document.title");
            }
            catch { /* partial data is OK */ }

            _runTcs?.TrySetResult(true);
        }

        // ══════════════════════════════════════════════════════════════════
        // RESULTS PRESENTATION
        // ══════════════════════════════════════════════════════════════════

        private void PresentResults(List<RunResult> results)
        {
            _gridPhases.Rows.Clear();
            _gridResources.Rows.Clear();

            // ── Phase grid ──
            foreach (var r in results)
                AppendPhaseRows(r, results.Count > 1);

            if (results.Count > 1)
                AppendAveragePhaseRows(results);

            // ── Resource grid (last run only) ──
            AppendResourceRows(results.Last());

            // ── Summary text ──
            _rtbSummary.Text = BuildSummary(results);

            _tabs.SelectedIndex = 0;
        }

        // ── Per-run phase rows ────────────────────────────────────────────

        private void AppendPhaseRows(RunResult r, bool showRunNum)
        {
            if (results_count_shown++ > 0)
                _gridPhases.Rows.Add("", "─────────────────────────", "", "", "");

            string runLabel = showRunNum ? $"#{r.RunIndex}" : "";

            var phases = new[]
            {
                (name: "Navigate() called",   t: r.TNavCall,         note: "baseline"),
                (name: "NavigationStarting",  t: r.TNavStarting,     note: "browser accepted request"),
                (name: "ContentLoading",      t: r.TContentLoading,  note: "first bytes arrived"),
                (name: "DOMContentLoaded",    t: r.TDomLoaded,       note: "HTML parsed"),
                (name: "NavigationCompleted", t: r.TNavCompleted,    note: $"HTTP {r.HttpStatus}  {r.ErrorStatus}  total {r.TotalMs} ms")
            };

            long prev = r.TNavCall;
            bool first = true;
            foreach (var (name, t, note) in phases)
            {
                if (t < 0) continue;
                long delta = t - prev;
                var row = _gridPhases.Rows[_gridPhases.Rows.Add(
                    first ? runLabel : "", name,
                    t.ToString(), first ? "—" : delta.ToString(), note)];
                ColorRow(row, first ? 0 : delta);
                prev  = t;
                first = false;
            }

            // Navigation Timing API breakdown (browser-side)
            if (!string.IsNullOrEmpty(r.NavTimingJson))
                AppendBrowserTimingRows(r.NavTimingJson, runLabel);
        }

        // field to track separator lines
        private int results_count_shown = 0;

        private void AppendBrowserTimingRows(string json, string runLabel)
        {
            try
            {
                var nt = JObject.Parse(json);
                long ns = nt["navigationStart"]?.Value<long>() ?? 0;
                if (ns == 0) return;

                _gridPhases.Rows.Add("", "  ── Browser Timing API ──", "", "", "");

                void BT(string phase, string sk, string ek, string desc)
                {
                    long sv = nt[sk]?.Value<long>() ?? 0;
                    long ev = nt[ek]?.Value<long>() ?? 0;
                    if (sv == 0 && ev == 0) return;
                    long dur = (sv > 0 && ev > sv) ? ev - sv : 0;
                    long abs = sv > 0 ? sv - ns : (ev > 0 ? ev - ns : 0);
                    var row = _gridPhases.Rows[_gridPhases.Rows.Add("", "  " + phase, abs.ToString(), dur > 0 ? dur.ToString() : "—", desc)];
                    ColorRow(row, dur);
                }

                BT("DNS lookup",         "domainLookupStart", "domainLookupEnd", "DNS resolution");
                BT("TCP + TLS connect",  "connectStart",      "connectEnd",      "TCP handshake + TLS");
                BT("Time-to-first-byte", "requestStart",      "responseStart",   "⭐ server processing + network RTT");
                BT("Response download",  "responseStart",     "responseEnd",     "body transfer");
                BT("DOM processing",     "domLoading",        "domInteractive",  "parse HTML, load subresources");
                BT("Blocking scripts",   "domInteractive",    "domContentLoaded","synchronous <script> execution");
                BT("DOM complete",       "domContentLoaded",  "domComplete",     "async resources + images");
                BT("load event",         "loadEventStart",    "loadEventEnd",    "window.onload handlers");
            }
            catch { }
        }

        private void AppendAveragePhaseRows(List<RunResult> results)
        {
            _gridPhases.Rows.Add("", "═════════ AVERAGES ═════════════", "", "", "");

            long Avg(Func<RunResult, long> sel) =>
                (long)results.Where(r => sel(r) >= 0).Select(r => (double)sel(r)).DefaultIfEmpty(0).Average();

            var avgs = new[]
            {
                ("Navigate() called",   Avg(r => r.TNavCall),         ""),
                ("NavigationStarting",  Avg(r => r.TNavStarting),     ""),
                ("ContentLoading",      Avg(r => r.TContentLoading),  ""),
                ("DOMContentLoaded",    Avg(r => r.TDomLoaded),       ""),
                ("NavigationCompleted", Avg(r => r.TNavCompleted),    $"avg total {Avg(r => r.TotalMs)} ms")
            };

            long prev = avgs[0].Item2;
            bool first = true;
            foreach (var (name, t, note) in avgs)
            {
                if (t < 0) continue;
                long delta = t - prev;
                var row = _gridPhases.Rows[_gridPhases.Rows.Add("avg", name, t.ToString(), first ? "—" : delta.ToString(), note)];
                row.DefaultCellStyle.Font      = new Font("Consolas", 9, FontStyle.Bold);
                row.DefaultCellStyle.BackColor = Color.FromArgb(230, 240, 255);
                ColorRow(row, first ? 0 : delta);
                prev  = t;
                first = false;
            }
        }

        private void AppendResourceRows(RunResult r)
        {
            if (string.IsNullOrEmpty(r.ResourceListJson)) return;
            try
            {
                var list = JArray.Parse(r.ResourceListJson);
                foreach (var res in list.OrderByDescending(x => x["duration"]?.Value<double>() ?? 0))
                {
                    string name = res["name"]?.ToString() ?? "";
                    long   dur  = res["duration"]?.Value<long>() ?? 0;
                    long   ttfb = res["ttfb"]?.Value<long>()     ?? 0;
                    long   xfer = res["transfer"]?.Value<long>() ?? 0;

                    var row = _gridResources.Rows[_gridResources.Rows.Add(
                        name,
                        res["type"]?.ToString() ?? "",
                        (res["start"]?.Value<long>() ?? 0).ToString(),
                        dur.ToString(),
                        ttfb.ToString(),
                        xfer > 0 ? $"{xfer / 1024.0:F1} KB" : "cached")];

                    if (dur > 1000)      row.DefaultCellStyle.BackColor = Color.FromArgb(255, 210, 210);
                    else if (dur > 300)  row.DefaultCellStyle.BackColor = Color.FromArgb(255, 255, 200);
                }
            }
            catch { }
        }

        private static void ColorRow(DataGridViewRow row, long delta)
        {
            if (delta > 1000)     row.DefaultCellStyle.BackColor = Color.FromArgb(255, 210, 210);
            else if (delta > 300) row.DefaultCellStyle.BackColor = Color.FromArgb(255, 255, 200);
        }

        // ── Text summary ─────────────────────────────────────────────────

        private string BuildSummary(List<RunResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=================================================");
            sb.AppendLine(" WebView2 Load-Time Diagnostic Report");
            sb.AppendLine($" {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("=================================================");
            sb.AppendLine($" URL:     {_txtUrl.Text}");
            sb.AppendLine($" Runs:    {results.Count}");
            sb.AppendLine($" Runtime: {_webView.CoreWebView2?.Environment.BrowserVersionString ?? "?"}");
            sb.AppendLine();

            foreach (var r in results)
            {
                sb.AppendLine($"── Run {r.RunIndex}  {(r.Ok ? "✔" : "✗")}  HTTP {r.HttpStatus}  {r.ErrorStatus}" +
                              (r.TimedOut(_cfg.TimeoutMs) ? $"  ⚠ TIMEOUT>{_cfg.TimeoutMs}ms" : ""));
                sb.AppendLine($"   Page: {r.PageTitle?.Trim('"') ?? ""}");

                void Ph(string label, long t)
                {
                    if (t >= 0)
                        sb.AppendLine($"   {label,-28} {t,7} ms   (+{t - r.TNavCall,6} ms from start)");
                }
                Ph("Navigate() called",   r.TNavCall);
                Ph("NavigationStarting",  r.TNavStarting);
                Ph("ContentLoading",      r.TContentLoading);
                Ph("DOMContentLoaded",    r.TDomLoaded);
                Ph("NavigationCompleted", r.TNavCompleted);

                // Browser timing breakdown
                if (!string.IsNullOrEmpty(r.NavTimingJson))
                {
                    try
                    {
                        var nt = JObject.Parse(r.NavTimingJson);
                        long ns = nt["navigationStart"]?.Value<long>() ?? 0;
                        if (ns > 0)
                        {
                            sb.AppendLine("   ── Browser Timing ──");
                            void BT(string label, string sk, string ek)
                            {
                                long sv = nt[sk]?.Value<long>() ?? 0;
                                long ev = nt[ek]?.Value<long>() ?? 0;
                                if (sv > 0 && ev > sv)
                                    sb.AppendLine($"   {label,-28} {ev - sv,7} ms");
                            }
                            BT("  DNS lookup",         "domainLookupStart", "domainLookupEnd");
                            BT("  TCP + TLS",          "connectStart",      "connectEnd");
                            BT("  Time-to-first-byte", "requestStart",      "responseStart");
                            BT("  Response download",  "responseStart",     "responseEnd");
                            BT("  DOM processing",     "domLoading",        "domInteractive");
                            BT("  Blocking scripts",   "domInteractive",    "domContentLoaded");
                            BT("  DOM complete",       "domContentLoaded",  "domComplete");
                            BT("  load event",         "loadEventStart",    "loadEventEnd");
                        }
                    }
                    catch { }
                }

                // Top 5 slowest resources
                if (!string.IsNullOrEmpty(r.ResourceListJson))
                {
                    try
                    {
                        var list = JArray.Parse(r.ResourceListJson);
                        var top  = list.OrderByDescending(x => x["duration"]?.Value<double>() ?? 0).Take(5).ToList();
                        if (top.Count > 0)
                        {
                            sb.AppendLine($"   ── Top {top.Count} Slowest Resources ──");
                            foreach (var res in top)
                            {
                                var dur  = res["duration"]?.Value<long>() ?? 0;
                                var name = res["name"]?.ToString() ?? "";
                                if (name.Length > 90) name = "…" + name.Substring(name.Length - 89);
                                sb.AppendLine($"   {dur,6} ms  {name}");
                            }
                        }
                    }
                    catch { }
                }

                sb.AppendLine();
            }

            if (results.Count > 1)
            {
                sb.AppendLine("── Averages across all runs ──────────────────────");
                long Avg(Func<RunResult, long> f) =>
                    (long)results.Where(r => f(r) >= 0).Select(r => (double)f(r)).DefaultIfEmpty(0).Average();

                sb.AppendLine($"   {"Total load time",-28} {Avg(r => r.TotalMs),7} ms");
                sb.AppendLine($"   {"→ NavigationStarting",-28} {Avg(r => r.TNavStarting  - r.TNavCall),7} ms");
                sb.AppendLine($"   {"→ ContentLoading",-28} {Avg(r => r.TContentLoading - r.TNavCall),7} ms");
                sb.AppendLine($"   {"→ DOMContentLoaded",-28} {Avg(r => r.TDomLoaded     - r.TNavCall),7} ms");
                sb.AppendLine($"   {"→ NavigationCompleted",-28} {Avg(r => r.TNavCompleted  - r.TNavCall),7} ms");
            }

            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════════════════

        private string SetStatus_Last = "";
        private void SetStatus(string text) { SetStatus_Last = text; _lblStatus.Text = text; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Extension: Task.WaitAsync polyfill for .NET 4.8
    // ─────────────────────────────────────────────────────────────────────
    internal static class TaskExtensions
    {
        /// <summary>Cancellable await compatible with .NET 4.8 (no built-in WaitAsync).</summary>
        public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
                if (completed == tcs.Task)
                    throw new OperationCanceledException(ct);
                return await task.ConfigureAwait(false);
            }
        }

        public static async Task WaitAsync(this Task task, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
                if (completed == tcs.Task)
                    throw new OperationCanceledException(ct);
                await task.ConfigureAwait(false);
            }
        }
    }
}
