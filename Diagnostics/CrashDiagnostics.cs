using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

internal static class CrashDiagnostics
{
    private const string LifecycleLogFileName = "process-lifecycle.log";
    private const string CrashLogFileName = "crash.log";
    private const string ActiveSessionFileName = "active-session.txt";
    private const long MaxLifecycleLogBytes = 1024 * 1024;
    private const long MaxCrashLogBytes = 8L * 1024 * 1024;

    private static readonly object FileGate = new();
    private static readonly string SessionId = Guid.NewGuid().ToString("N");
    private static readonly DateTime StartedUtc = DateTime.UtcNow;
    private static int _initialized;
    private static int _processExitWritten;
    private static int _cleanExitRequested;
    private static string _cleanExitReason = "";

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        try
        {
            // Register this first so a later diagnostics-initialization failure
            // still leaves an exit marker when the runtime can raise ProcessExit.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => OnProcessExit();

            string startDetails = BuildProcessDetails();
            foreach (string directory in GetDiagnosticDirectories())
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    string activePath = Path.Combine(directory, ActiveSessionFileName);
                    if (File.Exists(activePath))
                    {
                        string previous = File.ReadAllText(activePath, Encoding.UTF8).Trim();
                        AppendLifecycleToDirectory(
                            directory,
                            "PREVIOUS_SESSION_UNCLEAN",
                            string.IsNullOrWhiteSpace(previous) ? "active marker existed without content" : previous);
                    }

                    File.WriteAllText(
                        activePath,
                        $"session={SessionId} pid={Environment.ProcessId} startedUtc={StartedUtc:O} {startDetails}{Environment.NewLine}",
                        Encoding.UTF8);
                }
                catch
                {
                    // The other diagnostics directory may still be writable.
                }
            }

            WriteLifecycleEvent("START", startDetails);
        }
        catch (Exception ex)
        {
            // Crash reporting must never become a second unhandled exception.
            // Keep this path deliberately small and independent of reflection,
            // log rotation, and the normal lifecycle formatter.
            TryAppendMinimalFallback("CrashDiagnostics.Initialize", ex, terminating: false);
        }
    }

    internal static void MarkCleanExit(string reason)
    {
        _cleanExitReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();
        Interlocked.Exchange(ref _cleanExitRequested, 1);
        WriteLifecycleEvent("CLEAN_EXIT_REQUESTED", $"reason={_cleanExitReason}");
    }

    internal static void WriteLifecycleEvent(string eventName, string details)
    {
        if (Volatile.Read(ref _initialized) == 0)
            return;

        foreach (string directory in GetDiagnosticDirectories())
        {
            try
            {
                Directory.CreateDirectory(directory);
                AppendLifecycleToDirectory(directory, eventName, details);
            }
            catch
            {
                // Best effort: never let diagnostics affect the application.
            }
        }
    }

    internal static void WriteCrash(string source, Exception? exception, bool terminating)
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string heading = terminating ? "FATAL CRASH" : "NONFATAL EXCEPTION";
            string body =
                $"{Environment.NewLine}===== {heading} {stamp} =====" +
                $"{Environment.NewLine}session={SessionId} pid={Environment.ProcessId}" +
                $"{Environment.NewLine}source={source} terminating={terminating}" +
                $"{Environment.NewLine}{BuildProcessDetails()}" +
                $"{Environment.NewLine}{exception?.ToString() ?? "(no exception object)"}{Environment.NewLine}";

            foreach (string directory in GetDiagnosticDirectories())
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    string crashPath = Path.Combine(directory, CrashLogFileName);
                    lock (FileGate)
                    {
                        RotateIfNeeded(crashPath, MaxCrashLogBytes);
                        File.AppendAllText(crashPath, body, Encoding.UTF8);
                    }
                }
                catch
                {
                    // Try the other directory.
                }
            }

            WriteLifecycleEvent(
                terminating ? "FATAL_EXCEPTION" : "NONFATAL_EXCEPTION",
                $"source={source} exception={exception?.GetType().FullName ?? "unknown"}");
        }
        catch (Exception diagnosticsException)
        {
            TryAppendMinimalFallback(
                string.IsNullOrWhiteSpace(source) ? "unknown" : source,
                exception ?? diagnosticsException,
                terminating);
        }
    }

    internal static string GetLocalDiagnosticsDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChessKit",
            "Logs");

    private static void OnProcessExit()
    {
        if (Volatile.Read(ref _initialized) == 0 ||
            Interlocked.Exchange(ref _processExitWritten, 1) != 0)
        {
            return;
        }

        bool clean = Volatile.Read(ref _cleanExitRequested) != 0;
        TimeSpan uptime = DateTime.UtcNow - StartedUtc;
        WriteLifecycleEvent(
            "PROCESS_EXIT",
            $"exitCode={Environment.ExitCode} exitHex=0x{unchecked((uint)Environment.ExitCode):X8} " +
            $"clean={clean} reason={_cleanExitReason} uptimeMs={(long)uptime.TotalMilliseconds}");

        if (!clean)
            return;

        foreach (string directory in GetDiagnosticDirectories())
        {
            try { File.Delete(Path.Combine(directory, ActiveSessionFileName)); } catch { }
        }
    }

    private static void AppendLifecycleToDirectory(string directory, string eventName, string details)
    {
        string path = Path.Combine(directory, LifecycleLogFileName);
        string normalizedDetails = (details ?? "").Replace('\r', ' ').Replace('\n', ' ');
        string line =
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} session={SessionId} pid={Environment.ProcessId} " +
            $"event={eventName} {normalizedDetails}{Environment.NewLine}";

        lock (FileGate)
        {
            RotateIfNeeded(path, MaxLifecycleLogBytes);
            File.AppendAllText(path, line, Encoding.UTF8);
        }
    }

    private static string BuildProcessDetails()
    {
        string version =
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ??
            "unknown";
        return
            $"version={version} processPath={Environment.ProcessPath ?? "unknown"} " +
            $"baseDirectory={AppContext.BaseDirectory} os={Environment.OSVersion} " +
            $"runtime={Environment.Version} arch={RuntimeInformation.ProcessArchitecture}";
    }

    private static IReadOnlyList<string> GetDiagnosticDirectories()
    {
        var directories = new List<string>(2);
        try
        {
            string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            if (!string.IsNullOrWhiteSpace(baseDirectory))
                directories.Add(baseDirectory);
        }
        catch { }

        try
        {
            string localDirectory = Path.GetFullPath(GetLocalDiagnosticsDirectory());
            if (!directories.Contains(localDirectory, StringComparer.OrdinalIgnoreCase))
                directories.Add(localDirectory);
        }
        catch { }

        return directories;
    }

    private static void RotateIfNeeded(string path, long maxBytes)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= maxBytes)
                return;

            string previousPath = path + ".previous";
            if (File.Exists(previousPath))
                File.Delete(previousPath);
            File.Move(path, previousPath);
        }
        catch
        {
            // Keep appending if rotation fails.
        }
    }

    private static void TryAppendMinimalFallback(string source, Exception? exception, bool terminating)
    {
        try
        {
            string exceptionType;
            try { exceptionType = exception?.GetType().FullName ?? "unknown"; }
            catch { exceptionType = "unknown"; }

            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                $"session={SessionId} pid={Environment.ProcessId} " +
                $"event=CRASH_DIAGNOSTICS_FALLBACK source={source} " +
                $"terminating={terminating} exception={exceptionType}{Environment.NewLine}";

            foreach (string directory in GetDiagnosticDirectories())
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(Path.Combine(directory, CrashLogFileName), line, Encoding.UTF8);
                }
                catch
                {
                    // The fallback itself is strictly best effort.
                }
            }
        }
        catch
        {
            // Never throw from diagnostics, especially from an exception callback.
        }
    }
}
