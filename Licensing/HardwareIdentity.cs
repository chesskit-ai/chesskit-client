using Microsoft.Win32;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace ChessKit
{
    internal static class HardwareIdentity
    {
        private static string? _cachedHardwareId;

        public static string GetHardwareId()
        {
            if (!string.IsNullOrWhiteSpace(_cachedHardwareId))
                return _cachedHardwareId;

            TryDeleteLegacyHardwareIdCache();
            _cachedHardwareId = GenerateHardwareId();
            return _cachedHardwareId;
        }

        private static string GenerateHardwareId()
        {
            var parts = new List<string>
            {
                ReadRegistryValue(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid"),
                ReadWmiValue("Win32_BaseBoard", "SerialNumber"),
                ReadWmiValue("Win32_BIOS", "SerialNumber"),
                ReadWmiValue("Win32_ComputerSystemProduct", "UUID"),
                ReadWmiValue("Win32_Processor", "ProcessorId")
            }
            .Select(NormalizeIdentifierPart)
            .Where(IsUsefulIdentifierPart)
            .Distinct(StringComparer.Ordinal)
            .ToList();

            if (parts.Count == 0)
            {
                parts.Add(NormalizeIdentifierPart(Environment.MachineName));
                parts.Add(NormalizeIdentifierPart(Environment.UserDomainName));
            }

            string material = string.Join("|", parts);
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("ChessKit.HWID.v1|" + material));
            return Convert.ToHexString(digest).Substring(0, 16).ToLowerInvariant();
        }

        private static void TryDeleteLegacyHardwareIdCache()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "hwid.txt");
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static string ReadRegistryValue(string subKey, string valueName)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
                return key?.GetValue(valueName)?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string ReadWmiValue(string className, string propertyName)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT {propertyName} FROM {className}");
                foreach (ManagementBaseObject obj in searcher.Get())
                {
                    string value = obj[propertyName]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
            catch
            {
            }

            return "";
        }

        private static string NormalizeIdentifierPart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var builder = new StringBuilder(value.Length);
            foreach (char ch in value.Trim().ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                    builder.Append(ch);
            }

            return builder.ToString();
        }

        private static bool IsUsefulIdentifierPart(string value)
        {
            if (value.Length < 4)
                return false;

            string[] placeholders =
            {
                "NONE",
                "NULL",
                "UNKNOWN",
                "DEFAULTSTRING",
                "TOBEFILLEDBYOEM",
                "SYSTEMSERIALNUMBER",
                "BASEBOARDSERIALNUMBER",
                "FFFFFFFFFFFFFFFF",
                "0000000000000000"
            };

            return !placeholders.Contains(value, StringComparer.Ordinal);
        }
    }
}
