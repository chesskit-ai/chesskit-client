using System.Diagnostics;
using System.Management;

namespace ChessKit
{
    internal static class SystemUsageTelemetry
    {
        private static readonly object LockObject = new();
        private static DateTime _lastCpuSampleUtc = DateTime.MinValue;
        private static TimeSpan _lastProcessCpu = TimeSpan.Zero;
        private static double? _lastProcessCpuPercent;

        public static SystemUsageSnapshot Capture()
        {
            return new SystemUsageSnapshot
            {
                ProcessCpuPercent = CaptureProcessCpuPercent(),
                SystemCpuPercent = TryReadWmiDouble("Win32_PerfFormattedData_PerfOS_Processor", "PercentProcessorTime", "Name='_Total'"),
                GpuPercent = CaptureGpuPercent()
            };
        }

        private static double? CaptureProcessCpuPercent()
        {
            try
            {
                using Process process = Process.GetCurrentProcess();
                DateTime now = DateTime.UtcNow;
                TimeSpan cpu = process.TotalProcessorTime;

                lock (LockObject)
                {
                    if (_lastCpuSampleUtc == DateTime.MinValue)
                    {
                        _lastCpuSampleUtc = now;
                        _lastProcessCpu = cpu;
                        return _lastProcessCpuPercent;
                    }

                    double elapsedMs = (now - _lastCpuSampleUtc).TotalMilliseconds;
                    double cpuMs = (cpu - _lastProcessCpu).TotalMilliseconds;
                    _lastCpuSampleUtc = now;
                    _lastProcessCpu = cpu;

                    if (elapsedMs <= 0)
                        return _lastProcessCpuPercent;

                    double percent = cpuMs / (elapsedMs * Math.Max(1, Environment.ProcessorCount)) * 100.0;
                    _lastProcessCpuPercent = Math.Clamp(percent, 0.0, 100.0);
                    return _lastProcessCpuPercent;
                }
            }
            catch
            {
                return null;
            }
        }

        private static double? CaptureGpuPercent()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");

                double total = 0;
                bool found = false;
                foreach (ManagementBaseObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    if (name.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("engtype_Compute", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("engtype_Copy", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("engtype_VideoDecode", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("engtype_VideoEncode", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    if (double.TryParse(obj["UtilizationPercentage"]?.ToString(), out double value))
                    {
                        total += value;
                        found = true;
                    }
                }

                return found ? Math.Clamp(total, 0.0, 100.0) : null;
            }
            catch
            {
                return null;
            }
        }

        private static double? TryReadWmiDouble(string className, string propertyName, string where)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    $"SELECT {propertyName} FROM {className} WHERE {where}");
                foreach (ManagementBaseObject obj in searcher.Get())
                {
                    if (double.TryParse(obj[propertyName]?.ToString(), out double value))
                        return Math.Clamp(value, 0.0, 100.0);
                }
            }
            catch
            {
            }

            return null;
        }
    }

    internal sealed class SystemUsageSnapshot
    {
        public double? ProcessCpuPercent { get; init; }
        public double? SystemCpuPercent { get; init; }
        public double? GpuPercent { get; init; }
    }
}
