# WebView2 Load-Time Diagnostic

A .NET 4.8 WinForms tool that measures exactly where a WebView2 page load spends its time.

## Prerequisites

- .NET Framework 4.8 SDK
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) installed

## Build

```
cd WebView2TestApp
dotnet build
```

The exe is at `bin\Debug\net48\WebView2TestApp.exe`.

---

## Modes

### Interactive (no args)

Open the window, type a URL, press **Load** (or Enter).

### Self-running (command-line)

```
WebView2TestApp.exe --url <url> [options]
```

| Option | Meaning |
|---|---|
| `--url <url>` | Load this URL automatically on startup |
| `--repeat <n>` | Load n times and show per-run data plus averages |
| `--output <file>` | Write the text summary to a file when done |
| `--exit` | Close the window automatically when done |
| `--minimized` | Start the window minimized |
| `--timeout <ms>` | Mark a run ⚠ TIMEOUT if it takes longer than this |

**Examples**

```bat
:: One-shot diagnostic, save and exit
WebView2TestApp.exe --url https://myapp/login --exit --output timing.txt

:: 5 runs to get stable averages
WebView2TestApp.exe --url https://myapp/ --repeat 5 --exit --output avg.txt

:: Headless-style from a CI script
WebView2TestApp.exe --url https://staging.myapp/ --minimized --repeat 3 --timeout 5000 --exit --output results\timing.txt
```

---

## What it measures

### Tab 1 — Load Phases (host-side C# `Stopwatch`)

| Phase | What a long delta means |
|---|---|
| Call → `NavigationStarting` | WebView2 init overhead or DNS start |
| `NavigationStarting` → `ContentLoading` | DNS + TCP + TLS + server TTFB |
| `ContentLoading` → `DOMContentLoaded` | Large HTML or many blocking `<script>` tags |
| `DOMContentLoaded` → `NavigationCompleted` | Images, async JS, CSS after the HTML |

Rows turn **yellow** at > 300 ms, **red** at > 1 s.

Also shows the **Browser Navigation Timing API** breakdown:
DNS lookup / TCP+TLS / Time-to-first-byte / Response download / DOM processing / Blocking scripts / load event.

With `--repeat n`, an **AVERAGES** section appears at the bottom.

### Tab 2 — Resources (browser `performance` API)

Reads `window.performance.getEntriesByType('resource')` after load.
Sorted slowest-first. Shows Duration, TTFB, Transfer size per resource.
One very slow row = one slow endpoint/CDN/image.

### Tab 3 — Summary

Plain-text report — copy button or `--output` to save to file.

---

## Reading the output

| Symptom | Likely cause |
|---|---|
| Big gap `NavigationStarting` → `ContentLoading` | DNS slow, or TCP/TLS negotiation slow |
| High TTFB on the main document | Server processing slow |
| Big gap `ContentLoading` → `DOMContentLoaded` | Large HTML, many render-blocking scripts |
| Big gap `DOMContentLoaded` → `NavigationCompleted` | Heavy images or many async resources |
| One resource dominates the Resources tab | That specific endpoint is the bottleneck |
| All host phases fast, but browser timing slow | JS execution after DOMContentLoaded |
