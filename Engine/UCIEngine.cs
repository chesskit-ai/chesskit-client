using System.Diagnostics;
using System.Text;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ChessKit
{
    public class UCIEngine : IDisposable
    {
        private Process? _engineProcess;
        private readonly string _enginePath;
        private readonly StringBuilder _outputBuffer = new StringBuilder(8192);
        private readonly object _outputBufferLock = new object();
        private readonly object _processLock = new object();
        private static readonly ChildProcessJob _childProcessJob = ChildProcessJob.Create();
        private bool _disposed = false;
        private volatile TaskCompletionSource<string>? _currentAnalysis;
        private readonly RemoteEngineClient? _remoteEngine;
        // A POOL of independent remote connections used only for speculative
        // prefetch. They must not share _remoteEngine's send gate (a prefetch
        // in flight would block the next real request), and the pool lets the
        // top-N candidate replies prefetch in PARALLEL - each on its own socket
        // - instead of serializing on one connection.
        // Reverted to a single prefetch connection after a top-3/3-connection
        // build correlated with a flaky session. Parallel top-N is re-enabled
        // by raising this and SpeculativePrefetchTopMoves together once the
        // predicted-side vs analyzed-side key mismatch is addressed.
        private const int PrefetchConnectionCount = 1;
        private readonly RemoteEngineClient?[] _prefetchEngines = new RemoteEngineClient?[PrefetchConnectionCount];
        private volatile bool _prefetchEngineReady;
        public int PrefetchSlotCount => PrefetchConnectionCount;
        private readonly bool _isRemoteOnly;
        private volatile bool _remoteEngineActive = false;
        private CancellationTokenSource? _remoteAnalysisCts;
        // How long to wait for the broker's graceful-stop final packet before
        // hard-cancelling (which tears the socket down and forces the next
        // request to cold-dial ~+200ms). The broker normally returns its
        // final within ~250ms of a stop, so this is generous headroom: it
        // keeps the connection warm across rapid move bursts, where a torn
        // socket was the source of the p90/max latency spikes. The rare true
        // broker hang still recovers via this fallback.
        private const int RemoteStopFallbackGraceMs = 1500;
        private DateTime _remoteRetryAfterUtc = DateTime.MinValue;

        // Cache with analysis depth tracking
        private readonly ConcurrentDictionary<string, AnalysisCache> _cache = new();
        private volatile bool _analyzing = false;

        // Iterative deepening settings
        public int InitialDepth { get; set; } = 6;
        public int MaxDepth { get; set; } = 12;
        public bool InfiniteAnalysis { get; set; } = false;
        public int DepthIncrement { get; set; } = 2;
        public int InitialThinkTime { get; set; } = 10;
        public int MaxThinkTime { get; set; } = 200;
        public int TimeIncrement { get; set; } = 50;

        // ELO limiting settings
        public bool EloLimitEnabled { get; set; } = false;
        public int MaxEloRating { get; set; } = 2000;
        public int SkillLevel { get; set; } = 20;
        public bool AdaptiveHuman { get; set; } = true;
        public HumanPlayProfile HumanPlayProfile { get; set; } = HumanPlayProfile.Balanced;

        private DateTime _positionStartTime = DateTime.UtcNow;
        private int _currentPositionDepth = 0;

        private readonly ConcurrentDictionary<string, int> _positionMaxDepth = new();
        private DateTime _lastBoardPositionTime = DateTime.UtcNow;

        // Most recent value of each setoption. Replayed after StartAsync
        // when the engine restarts (e.g. after a crash) so the new process
        // comes up with the same configuration the caller previously
        // applied (Threads, Hash, MultiPV, etc.) instead of falling back
        // to whatever defaults StartAsync hardcodes.
        private readonly ConcurrentDictionary<string, string> _persistedOptions = new();
        private volatile int _optionMultiPv = 3;
        private volatile int _optionThreads = 1;
        private volatile int _optionHashMb = 32;

#if DEBUG
        // Direct file logging for engine forensics. Mirrors the LogDiag
        // helper in Program.cs; both write to fps-diag.log next to the
        // executable so engine-side and main-loop events appear in the
        // same timeline. Debug-only so Release publishes stay quiet.
        private static readonly object _diagFileLock = new object();
#endif
        private static void LogDiag(string tag, string message)
        {
#if DEBUG
            try
            {
                string logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "fps-diag.log");
                string line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}{Environment.NewLine}";
                lock (_diagFileLock)
                {
                    System.IO.File.AppendAllText(logPath, line);
                }
            }
            catch { }
#else
            _ = tag;
            _ = message;
#endif
        }

        private static readonly object _depthLock = new object();
        private static int _globalMaxDepthAchieved = 8;
        private int _currentPositionMaxDepth = 8;
        private DateTime _positionFirstSeen = DateTime.UtcNow;

        private DateTime _processStartTime = DateTime.UtcNow;
        private int _processRestartCount = 0;
        private readonly object _processHealthLock = new object();
        private volatile int _lastUnexpectedExitCode = int.MinValue;
        private long _lastUnexpectedExitTicks = 0;
        private readonly Queue<DateTime> _recentUnexpectedExitTimes = new();
        private DateTime _restartBlockedUntilUtc = DateTime.MinValue;
        private string? _lastFailureSummary = null;
        private long _lastFailureSummaryTicks = 0;
        private const string EngineLicenseFailurePrefix = "info string ChessKit engine license failed:";
        private const int CrashLoopWindowSeconds = 20;
        private const int CrashLoopExitLimit = 3;
        private const int CrashLoopCooldownSeconds = 15;

        private static readonly ConcurrentDictionary<string, int> _boardPositionDepths = new();

        private static readonly ConcurrentDictionary<string, DateTime> _boardPositionLastSeen = new();

        private readonly ConcurrentDictionary<string, IterativeAnalysis> _iterativeAnalyses = new();

        private readonly ConcurrentDictionary<string, int> _positionHighestDepth = new();
        private readonly ConcurrentDictionary<string, BestMoveResult> _positionBestResults = new();

        private bool _infiniteAnalysisRunning = false;
        private string _currentAnalysisPosition = "";
        private int _lastReportedDepth = 0;
        private readonly object _infiniteLock = new object();
        private string _streamingAnalysisFen = "";
        private int _lastStreamedDepth = -1;
        private DateTime _lastStreamedAtUtc = DateTime.MinValue;

        public event Action<BestMoveResult>? AnalysisUpdated;

        public UCIEngine(string enginePath)
        {
            _enginePath = enginePath;
            _remoteEngine = RemoteEngineClient.TryCreate(_enginePath);
            for (int i = 0; i < _prefetchEngines.Length; i++)
                _prefetchEngines[i] = RemoteEngineClient.TryCreate(_enginePath);

            // A remote-only engine (the server-hosted human model) has no local
            // binary. Only require a local file when nothing remote can serve
            // this engine - otherwise rely entirely on the remote broker.
            _isRemoteOnly = _remoteEngine != null && !File.Exists(_enginePath);
            if (_remoteEngine == null && !File.Exists(_enginePath))
            {
                throw new FileNotFoundException($"Engine not found at: {_enginePath}");
            }
        }

        private string SafeExitCode()
        {
            try
            {
                if (_engineProcess != null && _engineProcess.HasExited)
                    return _engineProcess.ExitCode.ToString();
            }
            catch { }
            return "?";
        }

        private void DisposeLocalEngineProcess()
        {
            lock (_processLock)
            {
                if (_engineProcess == null)
                    return;

                try
                {
                    try { _engineProcess.Exited -= OnEngineProcessExited; } catch { }
                    if (!_engineProcess.HasExited)
                    {
                        try { _engineProcess.StandardInput?.WriteLine("quit"); } catch { }
                        if (!_engineProcess.WaitForExit(100))
                            _engineProcess.Kill();
                    }
                }
                catch
                {
                }
                finally
                {
                    try { _engineProcess.Dispose(); } catch { }
                    _engineProcess = null;
                }
            }
        }

        private bool HasLc0Weights()
        {
            try
            {
                string? engineDir = Path.GetDirectoryName(_enginePath);
                if (string.IsNullOrWhiteSpace(engineDir) || !Directory.Exists(engineDir))
                    return false;

                return Directory.EnumerateFiles(engineDir, "*.pb.gz", SearchOption.AllDirectories).Any()
                    || Directory.EnumerateFiles(engineDir, "*.pb", SearchOption.AllDirectories).Any();
            }
            catch
            {
                return false;
            }
        }

        private string? FindLc0WeightsPath()
        {
            try
            {
                string? engineDir = Path.GetDirectoryName(_enginePath);
                if (string.IsNullOrWhiteSpace(engineDir) || !Directory.Exists(engineDir))
                    return null;

                return Directory.EnumerateFiles(engineDir, "*.pb.gz", SearchOption.AllDirectories).FirstOrDefault()
                    ?? Directory.EnumerateFiles(engineDir, "*.pb", SearchOption.AllDirectories).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        public string GetEnginePath() => _enginePath;

        private void RememberFailureSummary(string summary)
        {
            _lastFailureSummary = summary;
            _lastFailureSummaryTicks = DateTime.UtcNow.Ticks;
        }

        public string? GetRecentCrashDescription()
        {
            int exitCode = _lastUnexpectedExitCode;
            long ticks = _lastUnexpectedExitTicks;
            if (exitCode == int.MinValue || ticks == 0)
                return null;

            var when = new DateTime(ticks, DateTimeKind.Utc);
            if ((DateTime.UtcNow - when).TotalSeconds > 15)
                return null;

            uint unsignedExitCode = unchecked((uint)exitCode);
            return $"Engine process crashed (exit code {exitCode}, 0x{unsignedExitCode:X8}).";
        }

        public string? GetRecentFailureSummary()
        {
            string? summary = _lastFailureSummary;
            long ticks = _lastFailureSummaryTicks;
            if (string.IsNullOrWhiteSpace(summary) || ticks == 0)
                return null;

            var when = new DateTime(ticks, DateTimeKind.Utc);
            if ((DateTime.UtcNow - when).TotalSeconds > 30)
                return null;

            return summary;
        }

        public bool IsRemotePrefetchAvailable =>
            _remoteEngineActive && _prefetchEngineReady;

        /// <summary>
        /// Runs a one-shot (non-streaming) analysis on prefetch connection
        /// <paramref name="slot"/> (mod pool size), so several candidate
        /// replies can prefetch in parallel. Never touches the live analysis
        /// connection, the overlay, or shared engine state. Returns a failed
        /// result when prefetch is offline.
        /// </summary>
        public async Task<BestMoveResult> PrefetchAnalyzeAsync(string fen, int depth, int thinkTimeMs, int slot, CancellationToken cancellationToken)
        {
            if (!_prefetchEngineReady)
                return new BestMoveResult { Success = false, Error = "prefetch unavailable", AnalysisFen = fen };

            var pf = _prefetchEngines[((slot % _prefetchEngines.Length) + _prefetchEngines.Length) % _prefetchEngines.Length];
            if (pf == null)
                return new BestMoveResult { Success = false, Error = "prefetch slot unavailable", AnalysisFen = fen };

            int multiPv = _optionMultiPv <= 0 ? 3 : _optionMultiPv;
            int threads = _optionThreads <= 0 ? 1 : _optionThreads;
            int hash = _optionHashMb <= 0 ? 32 : _optionHashMb;
            return await pf.AnalyzeAsync(
                fen,
                thinkTimeMs,
                depth,
                multiPv,
                threads,
                hash,
                fixedDepthOnly: false,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> StartAsync()
        {
            if (_disposed) return false;

            try
            {
                lock (_processHealthLock)
                {
                    if (_restartBlockedUntilUtc != DateTime.MinValue && DateTime.UtcNow < _restartBlockedUntilUtc)
                    {
                        int waitSeconds = Math.Max(1, (int)Math.Ceiling((_restartBlockedUntilUtc - DateTime.UtcNow).TotalSeconds));
                        string summary = $"Engine restart paused for {waitSeconds}s after repeated crashes.";
                        RememberFailureSummary(summary);
                        LogDiag("ENGINE", summary);
                        return false;
                    }
                }

                if (_remoteEngine != null && DateTime.UtcNow >= _remoteRetryAfterUtc)
                {
                    LogDiag("REMOTE_ENGINE", $"ping {_remoteEngine.EngineName} at {_remoteEngine.Endpoint}");
                    if (await _remoteEngine.PingAsync())
                    {
                        _remoteEngineActive = true;
                        DisposeLocalEngineProcess();
                        RememberFailureSummary("");
                        _lastFailureSummary = null;
                        _lastFailureSummaryTicks = 0;
                        DebugRuntime.WriteLine($"[RemoteEngine] Using {_remoteEngine.EngineName} at {_remoteEngine.Endpoint}");
                        LogDiag("REMOTE_ENGINE", $"active {_remoteEngine.EngineName} endpoint={_remoteEngine.Endpoint}");
                        // Pay the analyze-socket dial, broker worker setup, and
                        // cold-engine costs now, before the user's first move,
                        // using the same options real requests will send.
                        int warmMultiPv = _optionMultiPv <= 0 ? 3 : _optionMultiPv;
                        int warmThreads = _optionThreads <= 0 ? 1 : _optionThreads;
                        int warmHash = _optionHashMb <= 0 ? 32 : _optionHashMb;
                        _remoteEngine.BeginBackgroundWarmup(warmMultiPv, warmThreads, warmHash);

                        // Bring the prefetch pool up in the background so the
                        // connections are dialed and engine-warm before the
                        // first speculative request. Failures are non-fatal -
                        // prefetch stays unavailable and analysis runs entirely
                        // on the main connection.
                        foreach (var prefetch in _prefetchEngines)
                        {
                            if (prefetch == null)
                                continue;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    if (await prefetch.PingAsync().ConfigureAwait(false))
                                    {
                                        _prefetchEngineReady = true;
                                        prefetch.BeginBackgroundWarmup(warmMultiPv, warmThreads, warmHash);
                                    }
                                }
                                catch
                                {
                                    // One bad connection doesn't disable the pool.
                                }
                            });
                        }
                        return true;
                    }

                    _remoteEngineActive = false;
                    LogDiag("REMOTE_ENGINE", $"unavailable for {_remoteEngine.EngineName}; falling back to local process");
                }

                // A remote-only engine has no local binary to fall back to. If
                // we got here it means the remote path did not activate (ping
                // failed, or in a retry-cooldown window) - don't attempt a
                // local process that cannot exist; report unavailable instead.
                if (_isRemoteOnly && !_remoteEngineActive)
                {
                    RememberFailureSummary("The server engine is currently unavailable. Please try again shortly.");
                    LogDiag("REMOTE_ENGINE", "remote-only engine unavailable; no local fallback");
                    return false;
                }

                string engineFileName = Path.GetFileNameWithoutExtension(_enginePath).ToLowerInvariant();
                bool isLc0Engine = engineFileName.Contains("lc0") || engineFileName.Contains("leela");
                if (isLc0Engine && !HasLc0Weights())
                {
                    LogDiag("ENGINE", $"LC0 startup blocked: no weights file found near {_enginePath}");
                    RememberFailureSummary("LC0 requires a network weights file (*.pb.gz or *.pb) in the engines folder.");
                    DebugRuntime.WriteLine("[UCIEngine] LC0 requires a network weights file (*.pb.gz or *.pb) in the engines folder.");
                    return false;
                }

                bool replacingExisting;
                bool existingHadExited;
                lock (_processLock)
                {
                    replacingExisting = _engineProcess != null;
                    existingHadExited = _engineProcess?.HasExited ?? false;
                }
                if (replacingExisting)
                {
                    LogDiag("ENGINE", existingHadExited
                        ? $"StartAsync: replacing CRASHED engine (exit code {SafeExitCode()}) -> {Path.GetFileName(_enginePath)}"
                        : $"StartAsync: replacing live engine -> {Path.GetFileName(_enginePath)}");
                }
                else
                {
                    LogDiag("ENGINE", $"StartAsync: initial start -> {Path.GetFileName(_enginePath)}");
                }

                lock (_processLock)
                {
                    // Clean up any existing process
                    if (_engineProcess != null)
                    {
                        try
                        {
                            // Detach our Exited handler before killing,
                            // so the intentional shutdown doesn't appear
                            // in diag logs as a crash signal. Only
                            // unexpected exits should reach the handler.
                            try { _engineProcess.Exited -= OnEngineProcessExited; } catch { }

                            if (!_engineProcess.HasExited)
                            {
                                _engineProcess.StandardInput?.WriteLine("quit");
                                _engineProcess.WaitForExit(100);
                                if (!_engineProcess.HasExited)
                                {
                                    _engineProcess.Kill();
                                }
                            }
                        }
                        catch { }
                        _engineProcess?.Dispose();
                        _engineProcess = null;
                    }

                    _engineProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = _enginePath,
                            UseShellExecute = false,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = false,
                            CreateNoWindow = true,
                            StandardOutputEncoding = Encoding.UTF8
                        }
                    };

                    _engineProcess.OutputDataReceived += OnOutputDataReceived;
                    _engineProcess.EnableRaisingEvents = true;
                    _engineProcess.Exited += OnEngineProcessExited;
                    _engineProcess.Start();
                    _childProcessJob.TryAssign(_engineProcess);
                    _engineProcess.BeginOutputReadLine();

                    _processStartTime = DateTime.UtcNow;
                    _processRestartCount++;

                    try
                    {
                        _engineProcess.PriorityClass = ProcessPriorityClass.Normal;
                        DebugRuntime.WriteLine($"[UCIEngine] Running {Path.GetFileName(_enginePath)} in Normal priority");
                    }
                    catch { }
                }

                // Clear buffer before starting
                ClearOutputBuffer();

                string engineName = Path.GetFileNameWithoutExtension(_enginePath).ToLower();
                bool isHumanEngine = engineName.Contains("humanuci") || engineName.Contains("humanpolicy") || engineName.Contains("human");

                // Protected single-file HumanUciEngine builds can spend a few
                // seconds extracting, loading weights, and checking the server
                // license before answering the initial UCI handshake.
                int uciHandshakeTimeoutMs = isHumanEngine ? 15000 : 3000;

                // Send UCI and wait for response
                await QuickSendAsync("uci");
                if (!await WaitForResponse("uciok", uciHandshakeTimeoutMs))
                {
                    string failureSummary = GetRecentFailureSummary()
                        ?? $"UCI handshake failed for {Path.GetFileName(_enginePath)}.";
                    RememberFailureSummary(failureSummary);
                    DebugRuntime.WriteLine($"[UCIEngine] {failureSummary}");
                    return false;
                }

                // Detect engine type and apply appropriate settings
                if (engineName.Contains("lc0") || engineName.Contains("leela"))
                {
                    DebugRuntime.WriteLine($"[UCIEngine] Configuring Leela Chess Zero");

                    string? weightsPath = FindLc0WeightsPath();
                    if (!string.IsNullOrWhiteSpace(weightsPath))
                    {
                        await QuickSendAsync($"setoption name WeightsFile value {weightsPath}");
                        DebugRuntime.WriteLine($"[UCIEngine] LC0 weights: {Path.GetFileName(weightsPath)}");
                    }

                    if (EloLimitEnabled)
                    {
                        DebugRuntime.WriteLine($"[UCIEngine] LC0 will be limited to approximate {MaxEloRating} ELO via node limits");
                    }

                    // LC0 doesn't support MultiPV well with node limits, use single PV
                    await QuickSendAsync("setoption name MultiPV value 1");
                }
                else if (engineName.Contains("stockfish"))
                {
                    DebugRuntime.WriteLine($"[UCIEngine] Configuring Stockfish");

                    // CRITICAL: Set ELO limiting FIRST, before any other options
                    if (EloLimitEnabled)
                    {
                        // Calculate skill level from ELO
                        int skillLevel = CalculateStockfishSkillLevel(MaxEloRating);

                        // IMPORTANT: When using Skill Level, MultiPV might not work properly
                        // We need to set MultiPV BEFORE skill level settings
                        await QuickSendAsync("setoption name MultiPV value 1");
                        await Task.Delay(100);

                        // Must set UCI_LimitStrength FIRST
                        await QuickSendAsync("setoption name UCI_LimitStrength value true");
                        await Task.Delay(100);

                        // Then set the ELO value
                        await QuickSendAsync($"setoption name UCI_Elo value {MaxEloRating}");
                        await Task.Delay(100);

                        // Skill Level as backup
                        await QuickSendAsync($"setoption name Skill Level value {skillLevel}");
                        await Task.Delay(100);

                        DebugRuntime.WriteLine($"[UCIEngine] *** ELO LIMIT ACTIVE ***");
                        DebugRuntime.WriteLine($"[UCIEngine] UCI_LimitStrength: true");
                        DebugRuntime.WriteLine($"[UCIEngine] UCI_Elo: {MaxEloRating}");
                        DebugRuntime.WriteLine($"[UCIEngine] Skill Level: {skillLevel}");
                        DebugRuntime.WriteLine($"[UCIEngine] MultiPV: 1 (limited for ELO mode)");
                    }
                    else
                    {
                        // Explicitly disable limiting
                        await QuickSendAsync("setoption name UCI_LimitStrength value false");
                        await Task.Delay(50);
                        await QuickSendAsync("setoption name Skill Level value 20");
                        await Task.Delay(50);
                        // Full strength can use MultiPV
                        await QuickSendAsync("setoption name MultiPV value 5");
                        await Task.Delay(50);

                        DebugRuntime.WriteLine($"[UCIEngine] *** FULL STRENGTH MODE ***");
                        DebugRuntime.WriteLine($"[UCIEngine] UCI_LimitStrength: false");
                        DebugRuntime.WriteLine($"[UCIEngine] Skill Level: 20");
                        DebugRuntime.WriteLine($"[UCIEngine] MultiPV: 5");
                    }

                    // NOW set other options
                    await QuickSendAsync("setoption name Threads value 8");
                    await QuickSendAsync("setoption name Hash value 128");
                    await QuickSendAsync("setoption name Ponder value false");
                    await QuickSendAsync("setoption name UCI_AnalyseMode value true");
                }
                else if (engineName.Contains("komodo"))
                {
                    DebugRuntime.WriteLine($"[UCIEngine] Configuring Komodo");

                    if (EloLimitEnabled)
                    {
                        int skillLevel = CalculateStockfishSkillLevel(MaxEloRating);
                        await QuickSendAsync($"setoption name Skill value {skillLevel}");
                        await QuickSendAsync("setoption name MultiPV value 1");
                        DebugRuntime.WriteLine($"[UCIEngine] Komodo limited to ~{MaxEloRating} ELO (Skill {skillLevel})");
                    }
                    else
                    {
                        await QuickSendAsync("setoption name MultiPV value 5");
                    }

                    await QuickSendAsync("setoption name Threads value 8");
                    await QuickSendAsync("setoption name Hash value 128");

                    if (isHumanEngine)
                    {
                        await QuickSendAsync($"setoption name AdaptiveHuman value {(AdaptiveHuman ? "true" : "false")}");
                        await QuickSendAsync($"setoption name PlayProfile value {HumanPlayProfile.ToString().ToLowerInvariant()}");
                        DebugRuntime.WriteLine($"[UCIEngine] Human profile: {HumanPlayProfile}, adaptive: {AdaptiveHuman}");
                    }
                }
                else
                {
                    DebugRuntime.WriteLine($"[UCIEngine] Configuring generic UCI engine");

                    if (EloLimitEnabled && !isHumanEngine)
                    {
                        int skillLevel = CalculateStockfishSkillLevel(MaxEloRating);
                        await QuickSendAsync($"setoption name Skill Level value {skillLevel}");
                        await QuickSendAsync($"setoption name Skill value {skillLevel}");
                        await QuickSendAsync("setoption name UCI_LimitStrength value true");
                        await QuickSendAsync($"setoption name UCI_Elo value {MaxEloRating}");
                        await QuickSendAsync("setoption name MultiPV value 5");

                        DebugRuntime.WriteLine($"[UCIEngine] Attempting to limit to ~{MaxEloRating} ELO");
                    }
                    else
                    {
                        if (EloLimitEnabled && isHumanEngine)
                        {
                            await QuickSendAsync($"setoption name UCI_Elo value {MaxEloRating}");
                            DebugRuntime.WriteLine($"[UCIEngine] Human engine keeps MultiPV enabled while using UCI_Elo {MaxEloRating}");
                        }

                        await QuickSendAsync("setoption name MultiPV value 5");
                    }

                    await QuickSendAsync("setoption name Threads value 8");
                    await QuickSendAsync("setoption name Hash value 128");
                }

                // Replay any setoption commands that the caller previously
                // applied (Threads, Hash, MultiPV, etc.). Without this, a
                // recovered engine starts with whatever default values the
                // engine-specific block above hardcoded, which can produce
                // a quality regression after a crash (default 1 thread,
                // small hash, MultiPV=1 = single arrow). Replaying after
                // the per-engine block means caller's choices override
                // the per-engine defaults, matching original startup order.
                foreach (var kvp in _persistedOptions)
                {
                    await QuickSendAsync(kvp.Value);
                }

                // NOW send isready after ALL options are set
                await QuickSendAsync("isready");
                if (!await WaitForResponse("readyok", 3000))
                {
                    RememberFailureSummary($"Ready check failed for {Path.GetFileName(_enginePath)}.");
                    DebugRuntime.WriteLine($"[UCIEngine] Ready check failed for {Path.GetFileName(_enginePath)}");
                    return false;
                }

                _lastUnexpectedExitCode = int.MinValue;
                _lastUnexpectedExitTicks = 0;
                lock (_processHealthLock)
                {
                    _recentUnexpectedExitTimes.Clear();
                    _restartBlockedUntilUtc = DateTime.MinValue;
                }
                _lastFailureSummary = null;
                _lastFailureSummaryTicks = 0;
                DebugRuntime.WriteLine($"[UCIEngine] {Path.GetFileName(_enginePath)} started successfully (restart #{_processRestartCount})");

                // Clear any analysis state after restart
                _infiniteAnalysisRunning = false;
                _currentAnalysisPosition = "";
                _lastReportedDepth = 0;

                return true;
            }
            catch (Exception ex)
            {
                RememberFailureSummary($"Failed to start {Path.GetFileName(_enginePath)}: {ex.Message}");
                DebugRuntime.WriteLine($"[UCIEngine] Failed to start {Path.GetFileName(_enginePath)}: {ex.Message}");
                return false;
            }
        }

        private bool IsProcessHealthy()
        {
            if (_remoteEngineActive)
                return true;

            lock (_processHealthLock)
            {
                if (_engineProcess == null || _engineProcess.HasExited)
                    return false;

                if ((DateTime.UtcNow - _processStartTime).TotalMinutes > 30)
                {
                    DebugRuntime.WriteLine("[UCIEngine] Process running too long, should restart");
                    return false;
                }

                return true;
            }
        }

        private void ClearOutputBuffer()
        {
            lock (_outputBufferLock)
            {
                _outputBuffer.Clear();
            }
        }

        private void AppendOutputLine(string line)
        {
            lock (_outputBufferLock)
            {
                _outputBuffer.AppendLine(line);
            }
        }

        private string GetOutputBufferText()
        {
            lock (_outputBufferLock)
            {
                return _outputBuffer.ToString();
            }
        }

        private static string GetInfiniteAnalysisKey(string fen)
        {
            if (string.IsNullOrWhiteSpace(fen))
                return string.Empty;

            var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return string.Empty;

            string board = parts[0];
            string side = parts.Length > 1 ? parts[1] : "w";
            string castling = parts.Length > 2 ? parts[2] : "-";
            string enPassant = parts.Length > 3 ? parts[3] : "-";
            return $"{board} {side} {castling} {enPassant}";
        }

        private async Task<bool> WaitForResponse(string expectedResponse, int timeoutMs)
        {
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                if (!IsProcessHealthy())
                {
                    await Task.Delay(150);
                    return false;
                }

                if (GetOutputBufferText().Contains(expectedResponse))
                {
                    return true;
                }
                await Task.Delay(10);
            }
            return false;
        }

        public async Task<bool> EnsureReadyAsync(int timeoutMs = 1500)
        {
            if (_remoteEngineActive)
                return true;

            if (_disposed || !IsProcessHealthy())
                return false;

            ClearOutputBuffer();
            await QuickSendAsync("isready");
            return await WaitForResponse("readyok", timeoutMs);
        }

        public async Task<BestMoveResult> StartInfiniteAnalysis(string fen, int targetDepth = 30)
        {
            if (_disposed)
            {
                return new BestMoveResult { Success = false, Error = "Engine disposed" };
            }

            if (_remoteEngineActive)
            {
                int remoteThinkTime = Math.Max(MaxThinkTime, Math.Max(1000, targetDepth * 90));
                return await GetBestMoveAsync(fen, remoteThinkTime, targetDepth, fixedDepthOnly: true);
            }

            bool needToStop = false;
            bool alreadyRunning = false;

            lock (_infiniteLock)
            {
                string analysisKey = GetInfiniteAnalysisKey(fen);
                if (_infiniteAnalysisRunning && _currentAnalysisPosition == analysisKey)
                {
                    alreadyRunning = true;
                }
                else if (_infiniteAnalysisRunning)
                {
                    needToStop = true;
                }
            }

            if (alreadyRunning)
            {
                await Task.Delay(100);
                string currentOutput = GetOutputBufferText();
                var currentResult = ParseInfiniteOutput(currentOutput, fen, 0);
                var currentVar = currentResult.Variations.FirstOrDefault();
                if (currentVar != null)
                {
                    _lastReportedDepth = Math.Max(_lastReportedDepth, currentVar.Depth);
                    currentResult.AnalysisDepth = _lastReportedDepth;
                }
                DebugRuntime.WriteLine($"[Infinite] Already running at depth {_lastReportedDepth}");
                return currentResult;
            }

            if (needToStop)
            {
                await StopInfiniteAnalysis();
                await Task.Delay(50);
                DebugRuntime.WriteLine("[Infinite] Stopped previous analysis");
            }

            _infiniteAnalysisRunning = true;
            _currentAnalysisPosition = GetInfiniteAnalysisKey(fen);
            _lastReportedDepth = 0;
            BeginStreamingAnalysis(fen);

            try
            {
                if (!IsFenStructurallySane(fen, out string fenRejectReason))
                {
                    LogDiag("ENGINE", $"REJECTED corrupt FEN in StartInfiniteAnalysis ({fenRejectReason}): {fen}");
                    return new BestMoveResult { Success = false, Error = $"Invalid FEN: {fenRejectReason}" };
                }

                if (!IsProcessHealthy())
                {
                    DebugRuntime.WriteLine("[Infinite] Starting fresh engine process");
                    if (!await StartAsync())
                    {
                        return new BestMoveResult { Success = false, Error = "Failed to start engine" };
                    }
                    await Task.Delay(500);
                }

                ClearOutputBuffer();

                LogDiag("ENGINE", $"position fen {fen} (infinite)");
                await QuickSendAsync($"position fen {fen}");
                await QuickSendAsync("go infinite");
                DebugRuntime.WriteLine($"[Infinite] Started infinite analysis");

                var result = new BestMoveResult { Success = false };
                var startTime = DateTime.UtcNow;
                int retryCount = 0;

                while ((DateTime.UtcNow - startTime).TotalMilliseconds < 900 && retryCount < 3)
                {
                    await Task.Delay(60);

                    string currentOutput = GetOutputBufferText();

                    if (currentOutput.Length < 100 && (DateTime.UtcNow - startTime).TotalSeconds > 1)
                    {
                        DebugRuntime.WriteLine("[Infinite] No output detected, checking process...");
                        if (!IsProcessHealthy())
                        {
                            DebugRuntime.WriteLine("[Infinite] Process died, restarting...");
                            retryCount++;
                            if (!await StartAsync())
                            {
                                return new BestMoveResult { Success = false, Error = "Engine keeps crashing" };
                            }
                            await Task.Delay(500);
                            ClearOutputBuffer();
                            LogDiag("ENGINE", $"resending position fen after restart: {fen}");
                            await QuickSendAsync($"position fen {fen}");
                            await QuickSendAsync("go infinite");
                            continue;
                        }
                    }

                    var tempResult = ParseInfiniteOutput(currentOutput, fen, 0);

                    if (tempResult.Variations.Any())
                    {
                        result = tempResult;
                        var firstVar = result.Variations.FirstOrDefault();
                        if (firstVar != null)
                        {
                            _lastReportedDepth = firstVar.Depth;
                            DebugRuntime.WriteLine($"[Infinite] Got results at depth {_lastReportedDepth}");
                            result.Success = true;
                            break;
                        }
                    }
                }

                if (!result.Variations.Any())
                {
                    DebugRuntime.WriteLine("[Infinite] No variations found during warmup");
                    result.Success = false;
                    result.Error = IsProcessHealthy()
                        ? "Waiting for engine lines..."
                        : "No variations found - engine may have crashed";

                    if (!IsProcessHealthy())
                    {
                        _infiniteAnalysisRunning = false;
                    }
                }
                else
                {
                    result.Success = true;
                    result.AnalysisDepth = _lastReportedDepth;
                    DebugRuntime.WriteLine($"[Infinite] Returning {result.Variations.Count} variations at depth {_lastReportedDepth}");
                }

                return result;
            }
            catch (Exception ex)
            {
                DebugRuntime.WriteLine($"[Infinite] Exception: {ex.Message}");
                _infiniteAnalysisRunning = false;
                return new BestMoveResult { Success = false, Error = ex.Message };
            }
        }

        public async Task StopInfiniteAnalysis()
        {
            if (_infiniteAnalysisRunning)
            {
                DebugRuntime.WriteLine($"[Infinite] Stopping analysis at depth {_lastReportedDepth}");
                _infiniteAnalysisRunning = false;
                _currentAnalysisPosition = "";
                _lastReportedDepth = 0;
                EndStreamingAnalysis();

                ClearOutputBuffer();
                await QuickSendAsync("stop");
                await QuickSendAsync("isready");
                await WaitForResponse("readyok", 1500);
                ClearOutputBuffer();
            }
        }

        private BestMoveResult ParseInfiniteOutput(string output, string fen, int minDepth)
        {
            var result = new BestMoveResult { Success = true, AnalysisFen = fen };
            var variations = new List<MoveVariation>();

            var lines = output.Split('\n');

            var latestLinesByPv = new Dictionary<int, (int Depth, string Line)>();
            foreach (var line in lines)
            {
                if (!line.Contains("info depth") || !line.Contains(" pv "))
                    continue;

                var depthMatch = System.Text.RegularExpressions.Regex.Match(line, @"depth\s+(\d+)");
                if (!depthMatch.Success)
                    continue;

                int depth = int.Parse(depthMatch.Groups[1].Value);
                if (depth < minDepth)
                    continue;

                int pv = 1;
                var pvMatch = System.Text.RegularExpressions.Regex.Match(line, @"multipv\s+(\d+)");
                if (pvMatch.Success)
                    pv = int.Parse(pvMatch.Groups[1].Value);

                if (pv is < 1 or > 5)
                    continue;

                if (!latestLinesByPv.TryGetValue(pv, out var existing) || depth >= existing.Depth)
                    latestLinesByPv[pv] = (depth, line);
            }

            if (latestLinesByPv.Count > 0)
            {

                foreach (var kvp in latestLinesByPv.OrderBy(kvp => kvp.Key))
                {
                    int pv = kvp.Key;
                    int depth = kvp.Value.Depth;
                    string bestLine = kvp.Value.Line;
                    double score = 0;
                    string scoreType = "cp";
                    int? mateIn = null;

                    if (bestLine.Contains(" score mate "))
                    {
                        var mateMatch = System.Text.RegularExpressions.Regex.Match(bestLine, @"score mate\s+(-?\d+)");
                        if (mateMatch.Success)
                        {
                            mateIn = int.Parse(mateMatch.Groups[1].Value);
                            scoreType = "mate";
                            score = mateIn.Value > 0 ? 999 : -999;
                        }
                    }
                    else if (bestLine.Contains(" score cp "))
                    {
                        var cpMatch = System.Text.RegularExpressions.Regex.Match(bestLine, @"score cp\s+(-?\d+)");
                        if (cpMatch.Success)
                        {
                            score = int.Parse(cpMatch.Groups[1].Value) / 100.0;
                        }
                    }

                    int pvIndex = bestLine.IndexOf(" pv ");
                    if (pvIndex > 0)
                    {
                        var moveString = bestLine.Substring(pvIndex + 4).Trim();
                        var moves = new List<string>();
                        var parts = moveString.Split(' ');

                        foreach (var part in parts)
                        {
                            if (part.Length >= 4 && part[0] >= 'a' && part[0] <= 'h' &&
                                part[1] >= '1' && part[1] <= '8' && part[2] >= 'a' && part[2] <= 'h')
                            {
                                moves.Add(part);
                            }
                            else
                            {
                                break;
                            }
                        }

                        if (moves.Any())
                        {
                            variations.Add(new MoveVariation
                            {
                                Rank = pv,
                                Depth = depth,
                                Score = score,
                                ScoreType = scoreType,
                                MateIn = mateIn,
                                Moves = moves
                            });
                        }
                    }
                }
            }

            result.Variations = variations.OrderBy(v => v.Rank).ToList();
            result.AnalysisDepth = variations.Count > 0 ? variations.Max(v => v.Depth) : 0;

            return result;
        }

        public async Task<BestMoveResult> GetBestMoveIterativeInfinite(string fen, CancellationToken cancellationToken = default)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return new BestMoveResult { Success = false, Error = _disposed ? "Engine disposed" : "Superseded" };
            }

            string analysisKey = GetInfiniteAnalysisKey(fen);

            if (_currentAnalysisPosition != analysisKey)
            {
                // A superseded request (a newer position is now being analyzed) must NOT
                // switch the engine back to this stale position — that back-and-forth
                // thrash is what made analysis-board self-play lag grow per move. Bail
                // before touching the engine.
                if (cancellationToken.IsCancellationRequested)
                    return new BestMoveResult { Success = false, Error = "Superseded" };

                if (_infiniteAnalysisRunning)
                {
                    await StopInfiniteAnalysis();
                    await Task.Delay(100);
                }

                DebugRuntime.WriteLine($"[Infinite] Starting analysis for new position");
                var result = await StartInfiniteAnalysis(fen, MaxDepth);

                if (result.Success && result.Variations.Any())
                {
                    DebugRuntime.WriteLine($"[Infinite] Returning {result.Variations.Count} variations at depth {result.AnalysisDepth}");
                }
                return result;
            }
            else if (_infiniteAnalysisRunning)
            {
                DebugRuntime.WriteLine($"[Infinite] Continuing analysis at depth {_lastReportedDepth}");

                await Task.Delay(200);
                if (cancellationToken.IsCancellationRequested)
                    return new BestMoveResult { Success = false, Error = "Superseded" };

                string currentOutput = GetOutputBufferText();
                var result = ParseInfiniteOutput(currentOutput, fen, 0);
                result.Success = result.Variations.Any();
                var currentVar = result.Variations.FirstOrDefault();
                if (currentVar != null)
                {
                    _lastReportedDepth = Math.Max(_lastReportedDepth, currentVar.Depth);
                }
                result.AnalysisDepth = _lastReportedDepth;

                if (result.Variations.Any())
                {
                    DebugRuntime.WriteLine($"[Infinite] Returning {result.Variations.Count} variations at depth {_lastReportedDepth}");
                }

                return result;
            }
            else
            {
                var result = await StartInfiniteAnalysis(fen, MaxDepth);
                if (result.Success && result.Variations.Any())
                {
                    DebugRuntime.WriteLine($"[Infinite] Returning {result.Variations.Count} variations at depth {result.AnalysisDepth}");
                }
                return result;
            }
        }

        public async Task<BestMoveResult> GetBestMoveIterativeAsync(string fen, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                return new BestMoveResult { Success = false, Error = "Engine disposed" };
            }

            string boardPosition = GetBoardPosition(fen);

            // For ELO limited mode, don't use iterative deepening
            if (EloLimitEnabled)
            {
                int eloThinkTime = 200;
                var eloResult = await GetBestMoveAsync(fen, eloThinkTime, 10, cancellationToken);
                DebugRuntime.WriteLine($"[UCIEngine] ELO limited result: {eloResult.Variations.Count} variations, success: {eloResult.Success}");
                return eloResult;
            }

            // Check if we have a cached result that genuinely satisfies the
            // selected fixed depth. Previously MaxDepth - 2 was treated as
            // "close enough", which made a depth-12 setting plateau at 10.
            if (_positionBestResults.TryGetValue(boardPosition, out var cachedResult))
            {
                if (cachedResult.AnalysisDepth >= MaxDepth)
                {
                    DebugRuntime.WriteLine($"[UCIEngine] Using cached result at depth {cachedResult.AnalysisDepth}");
                    return cachedResult;
                }
            }

            // Get the highest depth we've achieved for this position
            int highestAchieved = _positionHighestDepth.GetOrAdd(boardPosition, InitialDepth);
            int currentDepth = _boardPositionDepths.GetOrAdd(boardPosition, InitialDepth);

            // Never go below the highest achieved depth
            currentDepth = Math.Max(currentDepth, highestAchieved);

            int targetDepth;
            if (currentDepth < 18)
            {
                targetDepth = currentDepth + 2;
            }
            else if (currentDepth < 20)
            {
                targetDepth = currentDepth + 1;
            }
            else if (currentDepth < 24)
            {
                targetDepth = currentDepth + 1;
            }
            else
            {
                if (_processRestartCount == 0 || DateTime.UtcNow.Second % 5 == 0)
                {
                    targetDepth = currentDepth + 1;
                    DebugRuntime.WriteLine($"[EXTREME] Attempting to go beyond {currentDepth}: targeting {currentDepth + 1}");
                }
                else
                {
                    targetDepth = currentDepth;
                }
            }

            targetDepth = Math.Min(targetDepth, MaxDepth);
            targetDepth = Math.Max(targetDepth, currentDepth);

            DebugRuntime.WriteLine($"[UCIEngine] Position highest: {highestAchieved}, current: {currentDepth}, target: {targetDepth}");

            int thinkTime;
            if (targetDepth <= 16)
            {
                thinkTime = 100;
            }
            else if (targetDepth <= 18)
            {
                thinkTime = 1000;
            }
            else if (targetDepth <= 20)
            {
                thinkTime = 2000;
            }
            else if (targetDepth <= 22)
            {
                thinkTime = 4000;  // Increased from 1500ms
            }
            else if (targetDepth <= 24)
            {
                thinkTime = 8000;  // Increased from 3000ms
            }
            else if (targetDepth <= 26)
            {
                thinkTime = 15000; // Increased from 5000ms - 15 seconds
            }
            else if (targetDepth <= 28)
            {
                thinkTime = 30000; // 30 seconds for depth 27-28
            }
            else
            {
                thinkTime = 60000; // 1 minute for extreme depths
            }

            var result = await GetBestMoveAsync(fen, thinkTime, targetDepth, cancellationToken);

            if (result.Success && result.Variations.Any())
            {
                int achievedDepth = result.Variations.First().Depth;

                // Only update and return if we achieved at least our highest known depth
                if (achievedDepth >= highestAchieved)
                {
                    // Update highest achieved depth
                    _positionHighestDepth[boardPosition] = achievedDepth;
                    _boardPositionDepths[boardPosition] = achievedDepth;

                    // Cache this result
                    _positionBestResults[boardPosition] = result;

                    DebugRuntime.WriteLine($"[UCIEngine] Depth progressed: {highestAchieved} -> {achievedDepth}");

                    return result;
                }
                else
                {
                    DebugRuntime.WriteLine($"[UCIEngine] Depth regressed ({achievedDepth} < {highestAchieved}), using cached result");

                    // Return the cached higher-depth result if available
                    if (_positionBestResults.TryGetValue(boardPosition, out var betterResult))
                    {
                        return betterResult;
                    }

                    // If no cached result, still return current but don't update depth tracking
                    return result;
                }
            }
            else
            {
                DebugRuntime.WriteLine($"[UCIEngine] Analysis failed: {result.Error}");

                // On failure, return cached result if available
                if (_positionBestResults.TryGetValue(boardPosition, out var cachedFallback))
                {
                    DebugRuntime.WriteLine($"[UCIEngine] Using cached result after failure");
                    return cachedFallback;
                }
            }

            return result;
        }

        public void LogSettings()
        {
            DebugRuntime.WriteLine($"[UCIEngine Settings] InitialDepth: {InitialDepth}, MaxDepth: {MaxDepth}");
            DebugRuntime.WriteLine($"[UCIEngine Settings] InitialThinkTime: {InitialThinkTime}, MaxThinkTime: {MaxThinkTime}");
        }

        public async void ClearAllDepthTracking()
        {
            await QuickSendAsync("ucinewgame");

            _boardPositionDepths.Clear();
            _positionHighestDepth.Clear();
            _positionBestResults.Clear();
            DebugRuntime.WriteLine("[DEBUG] All depth tracking and caches cleared");
        }

        public void ClearPositionCache(string fen)
        {
            string boardPosition = GetBoardPosition(fen);
            _positionBestResults.TryRemove(boardPosition, out _);
            _positionHighestDepth.TryRemove(boardPosition, out _);
            DebugRuntime.WriteLine($"[UCIEngine] Cleared cache for position");
        }

        private string GetBoardPosition(string fen)
        {
            var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
                return string.Join(" ", parts.Take(4));
            if (parts.Length >= 2)
                return $"{parts[0]} {parts[1]}";
            return parts.Length > 0 ? parts[0] : fen;
        }

        public void ResetDepthTracking()
        {
            lock (_depthLock)
            {
                _globalMaxDepthAchieved = InitialDepth;
                _currentPositionMaxDepth = InitialDepth;
                _positionFirstSeen = DateTime.UtcNow;
                DebugRuntime.WriteLine("[UCIEngine] Depth tracking reset to initial values");
            }
        }

        private void CleanupOldCache()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-5);
            var toRemove = _cache
                .Where(kvp => kvp.Value.Timestamp < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _cache.TryRemove(key, out _);
            }
        }

        public async Task<BestMoveResult> GetBestMoveAsync(
            string fen,
            int thinkTimeMs = 10,
            int depth = 6,
            CancellationToken cancellationToken = default,
            bool fixedDepthOnly = false)
        {
            if (_disposed)
            {
                return new BestMoveResult { Success = false, Error = "Engine disposed" };
            }

            if (_analyzing)
            {
                LogDiag("ENGINE", $"GetBestMoveAsync rejected: busy (depth={depth})");
                return new BestMoveResult { Success = false, Error = "Busy" };
            }

            _analyzing = true;
            TaskCompletionSource<string>? analysisTcs = null;
            try
            {
                if (!IsFenStructurallySane(fen, out string fenRejectReason))
                {
                    LogDiag("ENGINE", $"REJECTED corrupt FEN ({fenRejectReason}): {fen}");
                    return new BestMoveResult { Success = false, Error = $"Invalid FEN: {fenRejectReason}" };
                }

                if (_remoteEngineActive && _remoteEngine != null)
                {
                    BestMoveResult remoteResult = await GetRemoteBestMoveAsync(
                        fen,
                        thinkTimeMs,
                        depth,
                        fixedDepthOnly,
                        cancellationToken);

                    if (remoteResult.Success || RemoteEngineClient.IsAuthoritativeFailure(remoteResult.Error))
                    {
                        return remoteResult;
                    }

                    LogDiag("REMOTE_ENGINE", $"request failed; falling back local: {remoteResult.Error}");
                    DebugRuntime.WriteLine($"[RemoteEngine] Request failed; falling back local: {remoteResult.Error}");
                    _remoteEngineActive = false;
                    _remoteRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
                }

                if (!IsProcessHealthy())
                {
                    LogDiag("ENGINE", $"unhealthy detected before analysis (HasExited={_engineProcess?.HasExited}, exitCode={SafeExitCode()}) -> restarting");
                    DebugRuntime.WriteLine("[UCIEngine] Process unhealthy - restarting...");
                    if (!await StartAsync())
                    {
                        LogDiag("ENGINE", "restart FAILED in GetBestMoveAsync");
                        return new BestMoveResult { Success = false, Error = "Failed to restart" };
                    }
                }

                ClearOutputBuffer();
                analysisTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _currentAnalysis = analysisTcs;
                BeginStreamingAnalysis(fen);

                LogDiag("ENGINE", $"position fen {fen}");
                await QuickSendAsync($"position fen {fen}");

                string engineName = Path.GetFileNameWithoutExtension(_enginePath).ToLower();
                bool isLc0 = engineName.Contains("lc0") || engineName.Contains("leela");

                // IMPORTANT: When ELO is limited, use different search parameters
                bool isHumanEngine = engineName.Contains("humanuci") || engineName.Contains("humanpolicy") || engineName.Contains("human");

                if (EloLimitEnabled && !isLc0 && !isHumanEngine)
                {
                    // For ELO-limited mode, use only time-based search
                    int adjustedTime = Math.Max(500, thinkTimeMs * 3); // Even more time
                    await QuickSendAsync($"go movetime {adjustedTime}");
                    DebugRuntime.WriteLine($"[UCIEngine] ELO-limited analysis with {adjustedTime}ms");
                }
                else if (isLc0 && EloLimitEnabled)
                {
                    int maxNodes = CalculateLc0Nodes(MaxEloRating);
                    await QuickSendAsync($"go nodes {maxNodes}");
                    DebugRuntime.WriteLine($"[UCIEngine] LC0 limited to {maxNodes} nodes");
                }
                else if (isLc0)
                {
                    int lc0ThinkTime = Math.Max(1000, thinkTimeMs);
                    await QuickSendAsync($"go movetime {lc0ThinkTime}");
                    DebugRuntime.WriteLine($"[UCIEngine] LC0 movetime {lc0ThinkTime}ms");
                }
                else if (fixedDepthOnly && depth > 0 && !isLc0)
                {
                    await QuickSendAsync($"go depth {depth}");
                    DebugRuntime.WriteLine($"[UCIEngine] Fixed depth {depth}");
                }
                else if (depth <= 0)
                {
                    await QuickSendAsync($"go movetime {thinkTimeMs}");
                    DebugRuntime.WriteLine($"[UCIEngine] Pure movetime {thinkTimeMs}ms");
                }
                else
                {
                    // Normal operation without ELO limiting
                    if (isHumanEngine && depth > 0 && depth <= 18)
                    {
                        // HumanUciEngine is fast enough at normal overlay depths
                        // that fixed-depth requests should be real depth requests.
                        // Adding movetime here lets the engine stop early, which
                        // made the overlay plateau around the quick preview depth.
                        await QuickSendAsync($"go depth {depth}");
                    }
                    else if (depth >= 26)
                    {
                        // Keep high-depth preview searches bounded. The overlay uses
                        // progressive depth steps, so a deep request must never block
                        // waiting for a full depth completion before arrows refresh.
                        int boundedTime = Math.Max(thinkTimeMs, 500);
                        await QuickSendAsync($"go depth {depth} movetime {boundedTime}");
                        DebugRuntime.WriteLine($"[UCIEngine] Depth {depth} with {boundedTime}ms time limit");
                    }
                    else if (depth >= 22)
                    {
                        // For high depths, give generous time
                        await QuickSendAsync($"go depth {depth} movetime {thinkTimeMs * 2}");
                        DebugRuntime.WriteLine($"[UCIEngine] Depth {depth} with {thinkTimeMs * 2}ms time limit");
                    }
                    else if (depth >= 20)
                    {
                        await QuickSendAsync($"go depth {depth} movetime {thinkTimeMs}");
                        DebugRuntime.WriteLine($"[UCIEngine] Depth {depth} with {thinkTimeMs}ms time limit");
                    }
                    else
                    {
                        await QuickSendAsync($"go depth {depth} movetime {thinkTimeMs}");
                    }
                }

                // Adjust timeout based on conditions
                int timeout;
                if (fixedDepthOnly && depth > 0 && !isLc0 && !EloLimitEnabled)
                {
                    timeout = Math.Max(3000, thinkTimeMs + depth * 250);
                    DebugRuntime.WriteLine($"[UCIEngine] Fixed-depth timeout: {timeout}ms");
                }
                else if (isHumanEngine && depth > 0 && depth <= 18)
                {
                    timeout = Math.Max(3000, Math.Max(thinkTimeMs * 12, depth * 350));
                    DebugRuntime.WriteLine($"[UCIEngine] Human fixed-depth timeout: {timeout}ms");
                }
                else if (isHumanEngine && depth <= 10)
                {
                    timeout = Math.Max(600, thinkTimeMs * 8);
                    DebugRuntime.WriteLine($"[UCIEngine] Human quick timeout: {timeout}ms");
                }
                else if (isLc0)
                {
                    timeout = Math.Max(8000, thinkTimeMs * 6);
                    DebugRuntime.WriteLine($"[UCIEngine] LC0 timeout: {timeout}ms");
                }
                else if (EloLimitEnabled)
                {
                    timeout = Math.Max(3000, thinkTimeMs * 4);
                    DebugRuntime.WriteLine($"[UCIEngine] ELO mode timeout: {timeout}ms");
                }
                else if (depth >= 28)
                {
                    timeout = thinkTimeMs + 30000; // Extra 30s buffer for extreme depths
                    DebugRuntime.WriteLine($"[UCIEngine] Extreme depth timeout: {timeout}ms");
                }
                else if (depth >= 24)
                {
                    timeout = thinkTimeMs + 10000; // Extra 10s buffer
                    DebugRuntime.WriteLine($"[UCIEngine] High depth timeout: {timeout}ms");
                }
                else if (depth >= 20)
                {
                    timeout = thinkTimeMs * 2; // Double the think time
                }
                else
                {
                    timeout = Math.Max(thinkTimeMs * 2, 2000); // At least 2 seconds
                }

                using (cancellationToken.Register(() => RequestAnalysisCancellationStop(analysisTcs, cancellationToken, depth)))
                {
                    var resultTask = analysisTcs.Task;
                    var timeoutTask = Task.Delay(timeout, cancellationToken);
                    var completedTask = await Task.WhenAny(resultTask, timeoutTask);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (completedTask == resultTask)
                    {
                        var output = await resultTask;

                        // EXTENSIVE DEBUG OUTPUT FOR ELO MODE
                        if (EloLimitEnabled)
                        {
                            DebugRuntime.WriteLine($"\n[UCIEngine] ===== ELO MODE RAW OUTPUT START =====");
                            DebugRuntime.WriteLine(output);
                            DebugRuntime.WriteLine($"[UCIEngine] ===== ELO MODE RAW OUTPUT END =====\n");
                        }

                        var result = ParseOutputSimplified(output, fen, depth);
                        result.AnalysisDepth = GetAchievedDepth(result, depth);
                        result.ThinkTime = thinkTimeMs;

                        DebugRuntime.WriteLine($"[UCIEngine] Parse result: Success={result.Success}, Variations={result.Variations.Count}, BestMove={result.BestMove}");

                        return result;
                    }
                    else
                    {
                        LogDiag("ENGINE", $"timeout {timeout}ms (depth={depth}, thinkTimeMs={thinkTimeMs}, processAlive={_engineProcess?.HasExited == false})");
                        DebugRuntime.WriteLine($"[UCIEngine] Timeout after {timeout}ms");

                        await QuickSendAsync("stop");
                        await Task.Delay(200);

                        var output = GetOutputBufferText();

                        if (isLc0)
                        {
                            DebugRuntime.WriteLine($"\n[UCIEngine] ===== LC0 TIMEOUT OUTPUT START =====");
                            DebugRuntime.WriteLine(output);
                            DebugRuntime.WriteLine($"[UCIEngine] ===== LC0 TIMEOUT OUTPUT END =====\n");
                        }

                        if (EloLimitEnabled)
                        {
                            DebugRuntime.WriteLine($"\n[UCIEngine] ===== TIMEOUT OUTPUT START =====");
                            DebugRuntime.WriteLine(output);
                            DebugRuntime.WriteLine($"[UCIEngine] ===== TIMEOUT OUTPUT END =====\n");
                        }

                        if (output.Contains("bestmove"))
                        {
                            var result = ParseOutputSimplified(output, fen, depth);
                            result.AnalysisDepth = GetAchievedDepth(result, depth);
                            result.ThinkTime = thinkTimeMs;

                            DebugRuntime.WriteLine($"[UCIEngine] Timeout parse: Success={result.Success}, Variations={result.Variations.Count}, BestMove={result.BestMove}");

                            return result;
                        }

                        return new BestMoveResult { Success = false, Error = $"Timeout - no bestmove" };
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LogDiag("ENGINE", $"analysis canceled (depth={depth})");
                throw;
            }
            catch (Exception ex)
            {
                LogDiag("ENGINE", $"analysis exception: {ex.GetType().Name}: {ex.Message}");
                DebugRuntime.WriteLine($"[UCIEngine] Analysis error: {ex.Message}");
                return new BestMoveResult { Success = false, Error = ex.Message };
            }
            finally
            {
                if (analysisTcs != null)
                    Interlocked.CompareExchange(ref _currentAnalysis, null, analysisTcs);
                EndStreamingAnalysis();
                _analyzing = false;
            }
        }

        // Simplified parser that ALWAYS creates at least one variation if we have a bestmove
        private BestMoveResult ParseOutputSimplified(string output, string fen, int requestedDepth)
        {
            var result = new BestMoveResult { Success = true, AnalysisFen = fen };

            // Extract best move FIRST
            int bestMoveIndex = output.IndexOf("bestmove ");
            if (bestMoveIndex >= 0)
            {
                int start = bestMoveIndex + 9;
                int end = output.IndexOf(' ', start);
                if (end < 0) end = output.IndexOf('\n', start);
                if (end < 0) end = output.Length;

                result.BestMove = output.Substring(start, end - start).Trim();
                DebugRuntime.WriteLine($"[ParseSimplified] Found bestmove: {result.BestMove}");
            }

            if (string.IsNullOrEmpty(result.BestMove))
            {
                DebugRuntime.WriteLine($"[ParseSimplified] ERROR: No bestmove found!");
                result.Success = false;
                return result;
            }

            // Try normal MultiPV parsing first. Only fall back to bestmove if the engine did not emit PV lines.
            var variations = new Dictionary<int, MoveVariation>();
            var outputLines = output.Split('\n');

            for (int i = outputLines.Length - 1; i >= 0; i--)
            {
                var line = outputLines[i];

                if (!line.Contains("multipv") || !line.Contains(" pv "))
                    continue;

                var pvMatch = System.Text.RegularExpressions.Regex.Match(line, @"multipv\s+(\d+)");
                if (!pvMatch.Success) continue;

                int pvNum = int.Parse(pvMatch.Groups[1].Value);
                if (pvNum > 5) continue;

                if (variations.ContainsKey(pvNum))
                    continue;

                var depthMatch = System.Text.RegularExpressions.Regex.Match(line, @"depth\s+(\d+)");
                int depth = depthMatch.Success ? int.Parse(depthMatch.Groups[1].Value) : requestedDepth;

                double score = 0;
                string scoreType = "cp";
                int? mateIn = null;

                if (line.Contains(" score mate "))
                {
                    var mateMatch = System.Text.RegularExpressions.Regex.Match(line, @"score mate\s+(-?\d+)");
                    if (mateMatch.Success)
                    {
                        mateIn = int.Parse(mateMatch.Groups[1].Value);
                        scoreType = "mate";
                        score = mateIn.Value > 0 ? 999 : -999;
                    }
                }
                else if (line.Contains(" score cp "))
                {
                    var cpMatch = System.Text.RegularExpressions.Regex.Match(line, @"score cp\s+(-?\d+)");
                    if (cpMatch.Success)
                    {
                        score = int.Parse(cpMatch.Groups[1].Value) / 100.0;
                    }
                }

                int pvIndex = line.IndexOf(" pv ");
                if (pvIndex > 0)
                {
                    pvIndex += 4;
                    var pvString = line.Substring(pvIndex).Trim();

                    var pvMoves = new List<string>();
                    var moveParts = pvString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var move in moveParts)
                    {
                        var trimmedMove = move.Trim();
                        if (IsValidMove(trimmedMove))
                        {
                            pvMoves.Add(trimmedMove);
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (pvMoves.Any())
                    {
                        variations[pvNum] = new MoveVariation
                        {
                            Rank = pvNum,
                            Depth = depth,
                            Score = score,
                            ScoreType = scoreType,
                            MateIn = mateIn,
                            Moves = pvMoves
                        };
                    }
                }
            }

            result.Variations = variations.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToList();

            // If no variations found, create one from bestmove
            if (!result.Variations.Any() && !string.IsNullOrEmpty(result.BestMove))
            {
                DebugRuntime.WriteLine($"[ParseSimplified] No variations found, creating from bestmove");
                result.Variations.Add(new MoveVariation
                {
                    Rank = 1,
                    Depth = requestedDepth,
                    Moves = new List<string> { result.BestMove },
                    Score = 0,
                    ScoreType = "cp"
                });
            }

            return result;
        }

        private static int GetAchievedDepth(BestMoveResult result, int requestedDepth)
        {
            int variationDepth = result.Variations.Any(v => v.Depth > 0)
                ? result.Variations.Max(v => v.Depth)
                : 0;

            if (variationDepth > 0)
                return variationDepth;

            return Math.Max(0, requestedDepth);
        }

        private async Task<BestMoveResult> GetRemoteBestMoveAsync(
            string fen,
            int thinkTimeMs,
            int depth,
            bool fixedDepthOnly,
            CancellationToken cancellationToken)
        {
            if (_remoteEngine == null)
                return new BestMoveResult { Success = false, Error = "Remote engine not configured" };

            // Deliberately NOT linked to the caller's token: every external
            // cancellation (position changed, mode switched, abort) first
            // asks the broker to stop gracefully - the stream then ends with
            // a final packet and the connection stays warm. The hard cancel
            // (which tears the socket down) only fires as a fallback when no
            // final arrives in time, or immediately when the broker lacks
            // the stop capability.
            using var linkedCts = new CancellationTokenSource();
            using var stopRegistration = cancellationToken.Register(() =>
            {
                var remote = _remoteEngine;
                if (remote != null && remote.TryRequestStop())
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(RemoteStopFallbackGraceMs);
                        try { linkedCts.Cancel(); } catch { }
                    });
                }
                else
                {
                    try { linkedCts.Cancel(); } catch { }
                }
            });
            _remoteAnalysisCts = linkedCts;
            try
            {
                int multiPv = _optionMultiPv <= 0 ? 3 : _optionMultiPv;
                int threads = _optionThreads <= 0 ? 1 : _optionThreads;
                int hash = _optionHashMb <= 0 ? 32 : _optionHashMb;
                LogDiag("REMOTE_ENGINE", $"analyze engine={_remoteEngine.EngineName} depth={depth} think={thinkTimeMs} multipv={multiPv} endpoint={_remoteEngine.Endpoint}");
                BeginStreamingAnalysis(fen);

                BestMoveResult result = await _remoteEngine.AnalyzeAsync(
                    fen,
                    thinkTimeMs,
                    depth,
                    multiPv,
                    threads,
                    hash,
                    fixedDepthOnly,
                    linkedCts.Token,
                    update => PublishRemoteAnalysisUpdate(update, fen));

                result.ThinkTime = thinkTimeMs;
                result.AnalysisFen = fen;
                if (result.AnalysisDepth <= 0)
                    result.AnalysisDepth = GetAchievedDepth(result, depth);

                LogDiag("REMOTE_ENGINE", result.Success
                    ? $"result best={result.BestMove} depth={result.AnalysisDepth} pv={result.Variations.Count}"
                    : $"failed: {result.Error}");
                return result;
            }
            finally
            {
                if (ReferenceEquals(_remoteAnalysisCts, linkedCts))
                    _remoteAnalysisCts = null;
            }
        }

        private void PublishRemoteAnalysisUpdate(BestMoveResult result, string fen)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_streamingAnalysisFen) ||
                    !string.Equals(_streamingAnalysisFen, fen, StringComparison.Ordinal) ||
                    !(result.Success && result.Variations.Any()))
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                int minDelayMs = _lastStreamedDepth < 1 ? 0 : 70;
                if ((now - _lastStreamedAtUtc).TotalMilliseconds < minDelayMs)
                    return;

                int variationDepth = result.Variations.Any(v => v.Depth > 0)
                    ? result.Variations.Max(v => v.Depth)
                    : 0;
                int depth = Math.Max(result.AnalysisDepth, variationDepth);
                if (depth <= _lastStreamedDepth && (now - _lastStreamedAtUtc).TotalMilliseconds < 180)
                    return;

                result.AnalysisFen = fen;
                result.AnalysisDepth = depth;
                _lastStreamedDepth = Math.Max(_lastStreamedDepth, depth);
                _lastStreamedAtUtc = now;
                LogDiag("REMOTE_ENGINE", $"stream update depth={depth} best={result.BestMove ?? "-"} pv={result.Variations.Count}");
                AnalysisUpdated?.Invoke(result);
            }
            catch
            {
                // Remote streaming updates are opportunistic; the final packet is still authoritative.
            }
        }

        private int CalculateStockfishSkillLevel(int targetElo)
        {
            if (targetElo <= 800) return 0;
            if (targetElo <= 900) return 1;
            if (targetElo <= 1000) return 2;
            if (targetElo <= 1100) return 3;
            if (targetElo <= 1200) return 4;
            if (targetElo <= 1300) return 5;
            if (targetElo <= 1400) return 6;
            if (targetElo <= 1500) return 7;
            if (targetElo <= 1600) return 8;
            if (targetElo <= 1700) return 9;
            if (targetElo <= 1800) return 10;
            if (targetElo <= 1900) return 11;
            if (targetElo <= 2000) return 12;
            if (targetElo <= 2100) return 13;
            if (targetElo <= 2200) return 14;
            if (targetElo <= 2300) return 15;
            if (targetElo <= 2400) return 16;
            if (targetElo <= 2500) return 17;
            if (targetElo <= 2600) return 18;
            if (targetElo <= 2700) return 19;

            return 20;
        }

        private int CalculateLc0Nodes(int targetElo)
        {
            if (targetElo <= 1000) return 10;
            if (targetElo <= 1500) return 100;
            if (targetElo <= 2000) return 1000;
            if (targetElo <= 2500) return 10000;
            return 100000;
        }

        public void ResetPosition(string fen)
        {
            DebugRuntime.WriteLine($"[UCIEngine] Position reset requested (depth tracking unchanged)");
        }

        public bool IsAnalyzing => _analyzing;
        public bool IsInfiniteAnalysisRunning => _infiniteAnalysisRunning;
        public bool IsRemoteEngineActive => _remoteEngineActive && _remoteEngine != null;

        public async Task AbortCurrentAnalysisAsync()
        {
            try
            {
                if (_remoteEngineActive)
                {
                    var remote = _remoteEngine;
                    if (remote != null && remote.TryRequestStop())
                    {
                        // Graceful stop: the broker ends the stream with a
                        // final packet and the connection stays warm. Only
                        // hard-cancel (which tears the socket down) if that
                        // final does not arrive promptly.
                        var pendingCts = _remoteAnalysisCts;
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(RemoteStopFallbackGraceMs);
                            try { pendingCts?.Cancel(); } catch { }
                        });
                    }
                    else
                    {
                        try { _remoteAnalysisCts?.Cancel(); } catch { }
                    }
                    _infiniteAnalysisRunning = false;
                    _currentAnalysisPosition = "";
                    _lastReportedDepth = 0;
                    EndStreamingAnalysis();
                    LogDiag("REMOTE_ENGINE", "abort requested");
                    return;
                }

                bool hadPending = _currentAnalysis != null && !_currentAnalysis.Task.IsCompleted;
                _currentAnalysis?.TrySetCanceled();
                _infiniteAnalysisRunning = false;
                _currentAnalysisPosition = "";
                _lastReportedDepth = 0;
                EndStreamingAnalysis();
                LogDiag("ENGINE", $"abort: sending stop (hadPending={hadPending}, analyzing={_analyzing}, processAlive={_engineProcess?.HasExited == false})");
                ClearOutputBuffer();
                await QuickSendAsync("stop");
                await QuickSendAsync("isready");
                await WaitForResponse("readyok", 1500);
                ClearOutputBuffer();
            }
            catch (Exception ex)
            {
                LogDiag("ENGINE", $"abort threw: {ex.GetType().Name}: {ex.Message}");
                DebugRuntime.WriteLine($"[UCIEngine] AbortCurrentAnalysisAsync error: {ex.Message}");
            }
            finally
            {
                _analyzing = false;
            }
        }

        private void RequestAnalysisCancellationStop(TaskCompletionSource<string> analysisTcs, CancellationToken cancellationToken, int depth)
        {
            analysisTcs.TrySetCanceled(cancellationToken);
            _infiniteAnalysisRunning = false;
            _currentAnalysisPosition = "";
            _lastReportedDepth = 0;
            EndStreamingAnalysis();

            if (TrySendCommandNow("stop"))
            {
                LogDiag("ENGINE", $"cancel: sent stop immediately (depth={depth})");
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    LogDiag("ENGINE", $"cancel: queued stop fallback (depth={depth})");
                    await QuickSendAsync("stop");
                }
                catch (Exception ex)
                {
                    LogDiag("ENGINE", $"cancel stop fallback threw: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        public int GetCurrentDepth(string fen)
        {
            string boardPosition = GetBoardPosition(fen);
            if (_positionMaxDepth.TryGetValue(boardPosition, out int depth))
            {
                return depth;
            }
            return InitialDepth;
        }

        public void ClearIterativeTracking()
        {
            _positionStartTime = DateTime.UtcNow;
            _currentPositionDepth = InitialDepth;
            _iterativeAnalyses.Clear();
            _cache.Clear();
            DebugRuntime.WriteLine("[UCIEngine] Cleared all iterative analysis tracking");
        }

        private void CleanupOldPositions()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-30);
            var toRemove = _iterativeAnalyses
                .Where(kvp => kvp.Value.LastSeen < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _iterativeAnalyses.TryRemove(key, out _);
                _cache.TryRemove(key, out _);
            }

            if (toRemove.Count > 0)
            {
                DebugRuntime.WriteLine($"[UCIEngine] Cleaned up {toRemove.Count} old positions");
            }
        }

        private async Task QuickSendAsync(string command)
        {
            if (_remoteEngineActive)
            {
                await Task.CompletedTask;
                return;
            }

            lock (_processLock)
            {
                if (_engineProcess?.StandardInput != null && !_engineProcess.HasExited)
                {
                    _engineProcess.StandardInput.WriteLine(command);
                    _engineProcess.StandardInput.Flush();
                }
            }
            await Task.CompletedTask;
        }

        private bool TrySendCommandNow(string command)
        {
            if (_remoteEngineActive)
                return true;

            bool lockTaken = false;
            try
            {
                if (!Monitor.TryEnter(_processLock, 10))
                    return false;

                lockTaken = true;
                if (_engineProcess?.StandardInput == null || _engineProcess.HasExited)
                    return false;

                _engineProcess.StandardInput.WriteLine(command);
                _engineProcess.StandardInput.Flush();
                return true;
            }
            catch (Exception ex)
            {
                LogDiag("ENGINE", $"urgent send failed ({command}): {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                if (lockTaken)
                    Monitor.Exit(_processLock);
            }
        }

        public async Task SendCommandAsync(string command)
        {
            CaptureRemoteSetOptionCommand(command);
            // Capture setoption commands so we can replay them after a
            // restart. Only commands sent through this public API get
            // captured - internal-StartAsync defaults stay encapsulated.
            // Format: "setoption name <Name> value <Value>"
            if (!string.IsNullOrEmpty(command) && command.StartsWith("setoption name ", StringComparison.OrdinalIgnoreCase))
            {
                int valueIdx = command.IndexOf(" value ", StringComparison.OrdinalIgnoreCase);
                if (valueIdx > 15) // "setoption name " is 15 chars
                {
                    string optionName = command.Substring(15, valueIdx - 15).Trim();
                    if (!string.IsNullOrEmpty(optionName))
                    {
                        _persistedOptions[optionName] = command;
                    }
                }
            }
            await QuickSendAsync(command);
        }

        private void CaptureRemoteSetOptionCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command) || !command.StartsWith("setoption name ", StringComparison.OrdinalIgnoreCase))
                return;

            int valueIdx = command.IndexOf(" value ", StringComparison.OrdinalIgnoreCase);
            if (valueIdx <= 15)
                return;

            string optionName = command.Substring(15, valueIdx - 15).Trim();
            string optionValue = command[(valueIdx + 7)..].Trim();
            if (!int.TryParse(optionValue, out int numericValue))
                return;

            if (optionName.Equals("MultiPV", StringComparison.OrdinalIgnoreCase))
            {
                _optionMultiPv = Math.Clamp(numericValue, 1, BuildLimits.MaxLines);
            }
            else if (optionName.Equals("Threads", StringComparison.OrdinalIgnoreCase))
            {
                _optionThreads = Math.Clamp(numericValue, 1, 64);
            }
            else if (optionName.Equals("Hash", StringComparison.OrdinalIgnoreCase))
            {
                _optionHashMb = Math.Clamp(numericValue, 1, 4096);
            }
        }

        private void OnOutputDataReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                AppendOutputLine(e.Data);
                if (e.Data.StartsWith(EngineLicenseFailurePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string reason = e.Data.Substring(EngineLicenseFailurePrefix.Length).Trim();
                    RememberFailureSummary(string.IsNullOrWhiteSpace(reason)
                        ? "Private engine license validation failed."
                        : $"Private engine license validation failed. {reason}");
                }

                TryPublishAnalysisUpdate(e.Data);

                if (e.Data.StartsWith("bestmove"))
                {
                    var tcs = _currentAnalysis;
                    if (tcs != null && !tcs.Task.IsCompleted)
                    {
                        tcs.TrySetResult(GetOutputBufferText());
                    }
                }
            }
        }

        private void BeginStreamingAnalysis(string fen)
        {
            _streamingAnalysisFen = fen;
            _lastStreamedDepth = -1;
            _lastStreamedAtUtc = DateTime.MinValue;
        }

        private void EndStreamingAnalysis()
        {
            _streamingAnalysisFen = "";
            _lastStreamedDepth = -1;
            _lastStreamedAtUtc = DateTime.MinValue;
        }

        private void TryPublishAnalysisUpdate(string line)
        {
            try
            {
                string fen = _streamingAnalysisFen;
                if (string.IsNullOrWhiteSpace(fen) ||
                    !line.StartsWith("info depth", StringComparison.Ordinal) ||
                    !line.Contains(" pv ", StringComparison.Ordinal))
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                int minDelayMs = _lastStreamedDepth < 1 ? 0 : 70;
                if ((now - _lastStreamedAtUtc).TotalMilliseconds < minDelayMs)
                {
                    return;
                }

                BestMoveResult result = ParseInfiniteOutput(GetOutputBufferText(), fen, 0);
                if (!(result.Success && result.Variations.Any()))
                {
                    return;
                }

                int variationDepth = result.Variations.Any(v => v.Depth > 0)
                    ? result.Variations.Max(v => v.Depth)
                    : 0;
                int depth = Math.Max(result.AnalysisDepth, variationDepth);
                if (depth <= _lastStreamedDepth && (now - _lastStreamedAtUtc).TotalMilliseconds < 180)
                {
                    return;
                }

                _lastStreamedDepth = Math.Max(_lastStreamedDepth, depth);
                _lastStreamedAtUtc = now;
                AnalysisUpdated?.Invoke(result);
            }
            catch
            {
                // Streaming updates are opportunistic; final bestmove parsing is still authoritative.
            }
        }

        private void OnEngineProcessExited(object? sender, EventArgs e)
        {
            // Fires the moment the engine process exits, whether we asked
            // for it (quit/Kill) or it died on its own (segfault, OOM,
            // OS termination). The interesting case is the second one:
            // catches Stockfish crashes that would otherwise only be
            // observed indirectly via failed analyses.
            int exitCode = -1;
            try
            {
                if (sender is Process p && p.HasExited)
                {
                    exitCode = p.ExitCode;
                }
            }
            catch { }

            bool wasAnalyzing = _analyzing;
            string positionInfo = string.IsNullOrEmpty(_currentAnalysisPosition)
                ? "no position"
                : $"position {_currentAnalysisPosition.Substring(0, Math.Min(20, _currentAnalysisPosition.Length))}";
            _lastUnexpectedExitCode = exitCode;
            _lastUnexpectedExitTicks = DateTime.UtcNow.Ticks;
            uint unsignedExitCode = unchecked((uint)exitCode);
            string phase = wasAnalyzing ? "while analyzing" : "during startup or idle";
            RememberFailureSummary($"Engine process exited {phase}. Exit code: {exitCode} (0x{unsignedExitCode:X8}).");
            lock (_processHealthLock)
            {
                DateTime now = DateTime.UtcNow;
                _recentUnexpectedExitTimes.Enqueue(now);
                while (_recentUnexpectedExitTimes.Count > 0 &&
                       (now - _recentUnexpectedExitTimes.Peek()).TotalSeconds > CrashLoopWindowSeconds)
                {
                    _recentUnexpectedExitTimes.Dequeue();
                }

                if (_recentUnexpectedExitTimes.Count >= CrashLoopExitLimit)
                {
                    _restartBlockedUntilUtc = now.AddSeconds(CrashLoopCooldownSeconds);
                    string summary = $"Engine restart paused for {CrashLoopCooldownSeconds}s after {_recentUnexpectedExitTimes.Count} crashes in {CrashLoopWindowSeconds}s.";
                    RememberFailureSummary(summary);
                    LogDiag("ENGINE", summary);
                }
            }
            LogDiag("ENGINE", $"PROCESS EXITED exitCode={exitCode} analyzing={wasAnalyzing} {positionInfo}");

            // Wake up any waiting analysis so it doesn't sit on the
            // timeout. Setting the result to "" causes the parser to
            // see no bestmove and return failure - caller's recovery
            // logic (IsProcessHealthy ? StartAsync) takes over.
            var tcs = _currentAnalysis;
            if (tcs != null && !tcs.Task.IsCompleted)
            {
                LogDiag("ENGINE", "waking pending analysis with empty result after exit");
                tcs.TrySetResult("");
            }
        }

        /// <summary>
        /// Lightweight validator for FENs we're about to feed to the
        /// engine. Stockfish crashes (access violation) on certain
        /// impossible positions - missing kings, pawns on rank 1/8,
        /// etc. YOLO can produce those when screen capture races with
        /// a window scroll. We don't try to verify full chess legality
        /// here (no check-detection, no castling-rights cross-check)
        /// - just enough structure to filter out the obvious garbage
        /// that segfaults the engine.
        /// </summary>
        public static bool IsFenStructurallySane(string fen, out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(fen))
            {
                reason = "empty";
                return false;
            }

            string[] parts = fen.Split(' ');
            if (parts.Length < 1 || string.IsNullOrEmpty(parts[0]))
            {
                reason = "no board field";
                return false;
            }

            string[] ranks = parts[0].Split('/');
            if (ranks.Length != 8)
            {
                reason = $"rank count {ranks.Length} (expected 8)";
                return false;
            }

            int whiteKings = 0, blackKings = 0;
            int whitePawns = 0, blackPawns = 0;
            int totalPieces = 0;

            for (int rankIdx = 0; rankIdx < 8; rankIdx++)
            {
                string rank = ranks[rankIdx];
                int squareCount = 0;
                foreach (char c in rank)
                {
                    if (c >= '1' && c <= '8')
                    {
                        squareCount += c - '0';
                    }
                    else if ("KQRBNPkqrbnp".IndexOf(c) >= 0)
                    {
                        squareCount++;
                        totalPieces++;
                        switch (c)
                        {
                            case 'K': whiteKings++; break;
                            case 'k': blackKings++; break;
                            case 'P':
                                whitePawns++;
                                // White pawn on rank 1 (rankIdx==7) or rank 8
                                // (rankIdx==0) is impossible.
                                if (rankIdx == 0 || rankIdx == 7)
                                {
                                    reason = $"white pawn on edge rank";
                                    return false;
                                }
                                break;
                            case 'p':
                                blackPawns++;
                                if (rankIdx == 0 || rankIdx == 7)
                                {
                                    reason = $"black pawn on edge rank";
                                    return false;
                                }
                                break;
                        }
                    }
                    else
                    {
                        reason = $"unknown char '{c}' in rank {rankIdx + 1}";
                        return false;
                    }
                }
                if (squareCount != 8)
                {
                    reason = $"rank {rankIdx + 1} has {squareCount} squares (expected 8)";
                    return false;
                }
            }

            // Must have exactly one king of each color.
            if (whiteKings != 1)
            {
                reason = $"white kings={whiteKings}";
                return false;
            }
            if (blackKings != 1)
            {
                reason = $"black kings={blackKings}";
                return false;
            }
            if (whitePawns > 8)
            {
                reason = $"white pawns={whitePawns}";
                return false;
            }
            if (blackPawns > 8)
            {
                reason = $"black pawns={blackPawns}";
                return false;
            }
            if (totalPieces > 32)
            {
                reason = $"total pieces={totalPieces}";
                return false;
            }

            return true;
        }

        private bool IsValidMove(string move)
        {
            if (string.IsNullOrWhiteSpace(move))
                return false;

            move = move.TrimEnd('.', ',', ';', '!', '?', '+', '#');

            if (move.Length < 4 || move.Length > 5)
                return false;

            if (move[0] < 'a' || move[0] > 'h') return false;
            if (move[1] < '1' || move[1] > '8') return false;
            if (move[2] < 'a' || move[2] > 'h') return false;
            if (move[3] < '1' || move[3] > '8') return false;

            if (move.Length == 5)
            {
                char promo = char.ToLower(move[4]);
                if (promo != 'q' && promo != 'r' && promo != 'b' && promo != 'n')
                    return false;
            }

            return true;
        }

        public static (int fromFile, int fromRank, int toFile, int toRank, char promotionPiece) ParseMove(string move)
        {
            if (move != null && move.Length >= 4)
            {
                int fromFile = move[0] - 'a';
                int fromRank = move[1] - '1';
                int toFile = move[2] - 'a';
                int toRank = move[3] - '1';
                char promotionPiece = move.Length >= 5 ? char.ToLowerInvariant(move[4]) : '\0';
                return (fromFile, fromRank, toFile, toRank, promotionPiece);
            }
            return (-1, -1, -1, -1, '\0');
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _remoteAnalysisCts?.Cancel(); } catch { }
            try { _remoteEngine?.Dispose(); } catch { }
            foreach (var pf in _prefetchEngines)
            {
                try { pf?.Dispose(); } catch { }
            }
            DisposeLocalEngineProcess();

            _cache.Clear();
            _iterativeAnalyses.Clear();
        }

        // Helper classes
        private class AnalysisCache
        {
            public BestMoveResult Result { get; set; } = new();
            public int Depth { get; set; }
            public int ThinkTime { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private class IterativeAnalysis
        {
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public DateTime LastAnalyzed { get; set; }
            public int CurrentDepth { get; set; }
            public int CurrentThinkTime { get; set; }
        }
    }

    internal sealed class ChildProcessJob
        {
            private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

            private readonly IntPtr _handle;

            private ChildProcessJob(IntPtr handle)
            {
                _handle = handle;
            }

            public static ChildProcessJob Create()
            {
                IntPtr handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                    return new ChildProcessJob(IntPtr.Zero);

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr ptr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(handle, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, ptr, (uint)length);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                return new ChildProcessJob(handle);
            }

            public void TryAssign(Process? process)
            {
                if (_handle == IntPtr.Zero || process == null)
                    return;

                try
                {
                    AssignProcessToJobObject(_handle, process.Handle);
                }
                catch { }
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            private enum JOBOBJECTINFOCLASS
            {
                JobObjectExtendedLimitInformation = 9
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public long Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IO_COUNTERS
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }
        }

    public class BestMoveResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? BestMove { get; set; }
        public string? PonderMove { get; set; }
        public List<MoveVariation> Variations { get; set; } = new List<MoveVariation>();
        public string? RawOutput { get; set; }
        public int AnalysisDepth { get; set; }
        public int ThinkTime { get; set; }
        public string? AnalysisFen { get; set; }
    }

    public class MoveVariation
    {
        public int Rank { get; set; }
        public int Depth { get; set; }
        public double Score { get; set; }
        public string ScoreType { get; set; } = "cp";
        public int? MateIn { get; set; }
        public List<string> Moves { get; set; } = new List<string>();

        public string GetScoreDisplay()
        {
            if (ScoreType == "mate")
            {
                return $"Mate in {Math.Abs(MateIn ?? 0)}";
            }
            return $"{Score:+0.00;-0.00}";
        }
    }
}
