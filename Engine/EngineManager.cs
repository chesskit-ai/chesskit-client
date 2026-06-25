using System.Text.Json;

namespace ChessKit
{
    /// <summary>
    /// Manages multiple UCI chess engines
    /// </summary>
    public class EngineManager
    {
        private readonly string _enginesPath;
        private readonly List<EngineInfo> _availableEngines = new();
        private EngineInfo? _currentEngine;
        // Persisted customizations, keyed by EngineInfo.Key.
        private readonly Dictionary<string, string> _nicknames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);

        public event Action<EngineInfo>? EngineChanged;

        public IReadOnlyList<EngineInfo> AvailableEngines => _availableEngines;
        public IEnumerable<EngineInfo> LocalEngines => _availableEngines.Where(e => e.Source == EngineSource.Local);
        public IEnumerable<EngineInfo> RemoteEngines => _availableEngines.Where(e => e.Source == EngineSource.Remote);
        public EngineInfo? CurrentEngine => _currentEngine;

        private readonly bool _includeRemote;

        public EngineManager(string enginesPath, bool includeRemote = true)
        {
            _enginesPath = enginesPath;
            _includeRemote = includeRemote;
            ScanForEngines();
        }

        /// <summary>
        /// Scans the engines directory for UCI-compatible engines
        /// </summary>
        public void ScanForEngines()
        {
            _availableEngines.Clear();

            if (!Directory.Exists(_enginesPath))
            {
                Directory.CreateDirectory(_enginesPath);
            }

            // Look for executable files
            var exeFiles = Directory.GetFiles(_enginesPath, "*.exe", SearchOption.AllDirectories);
            var bundledLc0Path = Path.Combine(_enginesPath, "lc0", "lc0.exe");
            bool hasBundledLc0 = File.Exists(bundledLc0Path);

            var candidateEngines = new Dictionary<string, EngineInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var exePath in exeFiles)
            {
                if (hasBundledLc0 &&
                    Path.GetFileName(exePath).Equals("lc0.exe", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetDirectoryName(exePath)?.TrimEnd(Path.DirectorySeparatorChar)
                        .Equals(_enginesPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Prefer the bundled LC0 executable that lives beside its runtimes/weights.
                    continue;
                }

                var fileName = Path.GetFileName(exePath);
                var name = Path.GetFileNameWithoutExtension(exePath);
                var lowerFileName = fileName.ToLower();

                // Skip files that are clearly not chess engines
                if (lowerFileName.Contains("uninstall") ||
                    lowerFileName.Contains("setup") ||
                    lowerFileName.Contains("install") ||
                    lowerFileName.Contains("update") ||
                    lowerFileName.Contains("config") ||
                    lowerFileName.Contains("launcher") ||
                    lowerFileName.Contains("helper") ||
                    lowerFileName.Contains("updater") ||
                    lowerFileName.Contains("crash") ||
                    lowerFileName.Contains("report") ||
                    lowerFileName.Contains("test"))
                {
                    continue;
                }

                // Try to identify common engines
                EngineType type = IdentifyEngineType(fileName);

                // For LC0, we specifically look for lc0.exe
                bool isDefinitelyEngine = false;
                if (lowerFileName == "lc0.exe" || IsKnownEngine(lowerFileName))
                {
                    isDefinitelyEngine = true;
                }

                // If it's not a known engine and it's in a subdirectory, 
                // skip it unless it's the main executable for that folder
                if (!isDefinitelyEngine && type == EngineType.Other)
                {
                    var directoryName = Path.GetFileName(Path.GetDirectoryName(exePath))?.ToLower() ?? "";

                    // Check if the exe name matches or contains the folder name
                    // This helps identify the main engine executable in a folder
                    if (!string.IsNullOrEmpty(directoryName))
                    {
                        // If the exe doesn't match the folder name, skip it
                        // Unless it's in the root engines folder
                        if (!lowerFileName.Contains(directoryName) &&
                            Path.GetDirectoryName(exePath) != _enginesPath)
                        {
                            continue;
                        }
                    }
                    else if (Path.GetDirectoryName(exePath) != _enginesPath)
                    {
                        // If it's in a subdirectory and we can't identify it, skip
                        continue;
                    }
                }

                // Create engine info
                var engineInfo = new EngineInfo
                {
                    Name = FormatEngineName(name),
                    ExecutablePath = exePath,
                    Type = type,
                    FileName = fileName,
                    IsDefault = fileName.ToLower().Contains("stockfish")
                };

                string dedupeKey = $"{engineInfo.Type}|{engineInfo.Name}";
                if (candidateEngines.TryGetValue(dedupeKey, out var existing))
                {
                    if (IsBetterEngineCandidate(engineInfo, existing))
                    {
                        candidateEngines[dedupeKey] = engineInfo;
                    }
                }
                else
                {
                    candidateEngines[dedupeKey] = engineInfo;
                }
            }

            // Local entries scanned above.
            foreach (var local in candidateEngines.Values)
                local.Source = EngineSource.Local;
            _availableEngines.AddRange(candidateEngines.Values);

            // Remote category: every engine the broker serves, as its own
            // selectable entry (so Stockfish can be chosen remote AND local).
            AddRemoteEngineEntries();

            // Apply persisted nicknames, then drop hidden ("removed") entries.
            foreach (var e in _availableEngines)
            {
                if (_nicknames.TryGetValue(e.Key, out var nick) && !string.IsNullOrWhiteSpace(nick))
                    e.Nickname = nick;
            }
            _availableEngines.RemoveAll(e => _hidden.Contains(e.Key));

            // Sort: local before remote, Stockfish first within a group, then
            // alphabetically by display name.
            _availableEngines.Sort((a, b) =>
            {
                if (a.Source != b.Source) return a.Source == EngineSource.Local ? -1 : 1;
                if (a.IsDefault && !b.IsDefault) return -1;
                if (!a.IsDefault && b.IsDefault) return 1;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            // Default to a visible local Stockfish, else first available.
            _currentEngine = _availableEngines.FirstOrDefault(e => e.IsDefault && e.Source == EngineSource.Local)
                ?? _availableEngines.FirstOrDefault(e => e.Source == EngineSource.Local)
                ?? _availableEngines.FirstOrDefault();
        }

        private static string RemoteEngineDisplayName(string engineName) => engineName switch
        {
            "stockfish" => "Stockfish (server)",
            "lc0" => "Lc0 (server)",
            "humanuci" => "Human (server)",
            _ => char.ToUpperInvariant(engineName[0]) + engineName.Substring(1) + " (server)",
        };

        private static EngineType RemoteEngineType(string engineName) => engineName switch
        {
            "stockfish" => EngineType.Stockfish,
            "lc0" => EngineType.LeelaChessZero,
            _ => EngineType.Other,
        };

        /// <summary>
        /// Adds a Remote-source entry for each engine the broker serves. The
        /// ExecutablePath carries the "remote://" prefix so the selection routes
        /// to the server (and needs no local binary).
        /// </summary>
        private void AddRemoteEngineEntries()
        {
            if (!_includeRemote)
                return;
            foreach (var name in RemoteEngineClient.GetRemoteEngineNames())
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                _availableEngines.Add(new EngineInfo
                {
                    Name = RemoteEngineDisplayName(name),
                    ExecutablePath = RemoteEngineClient.RemoteEnginePathPrefix + name,
                    FileName = name,
                    Type = RemoteEngineType(name),
                    Source = EngineSource.Remote,
                });
            }
        }

        /// <summary>Set or clear a per-engine nickname (persisted).</summary>
        public void SetNickname(EngineInfo engine, string? nickname)
        {
            if (engine == null) return;
            nickname = nickname?.Trim();
            if (string.IsNullOrWhiteSpace(nickname))
                _nicknames.Remove(engine.Key);
            else
                _nicknames[engine.Key] = nickname;
            engine.Nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname;
            SaveSettings();
        }

        /// <summary>Hide an engine from the list (does NOT delete the file).</summary>
        public void RemoveEngine(EngineInfo engine)
        {
            if (engine == null) return;
            _hidden.Add(engine.Key);
            _availableEngines.RemoveAll(e => e.Key == engine.Key);
            if (_currentEngine != null && _currentEngine.Key == engine.Key)
                _currentEngine = _availableEngines.FirstOrDefault(e => e.Source == EngineSource.Local)
                    ?? _availableEngines.FirstOrDefault();
            SaveSettings();
        }

        /// <summary>Clean/reset: restore hidden engines, clear nicknames, rescan.</summary>
        public void ResetEngines()
        {
            _hidden.Clear();
            _nicknames.Clear();
            try { File.Delete(Path.Combine(_enginesPath, "engine_settings.json")); } catch { }
            ScanForEngines();
        }

        /// <summary>
        /// Checks if a filename is a known chess engine
        /// </summary>
        private bool IsKnownEngine(string lowerFileName)
        {
            var knownEngines = new[]
            {
                "stockfish", "komodo", "leela", "lc0", "houdini",
                "fritz", "rybka", "fire", "ethereal", "berserk",
                "koivisto", "clover", "igel", "rubichess", "seer",
                "velvet", "caissa", "obsidian", "viridithas", "arasan",
                "crafty", "toga", "fruit", "glaurung", "scorpio",
                "spike", "sjeng", "phalanx", "booot", "rodent",
                "texel", "laser", "andscacs", "xiphos", "defenchess",
                "pedone", "winter", "minic", "marvin", "wasp",
                "nemorino", "demolito", "topple", "weiss", "bit-genie"
            };

            return knownEngines.Any(engine => lowerFileName.Contains(engine));
        }

        /// <summary>
        /// Adds a custom engine from file path
        /// </summary>
        public bool AddCustomEngine(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            var fileName = Path.GetFileName(filePath);
            var targetPath = Path.Combine(_enginesPath, fileName);
            var sourceDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;

            if (LooksLikeBundledEngineDirectory(sourceDirectory))
            {
                var sourceDirFull = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
                var enginesDirFull = Path.GetFullPath(_enginesPath).TrimEnd(Path.DirectorySeparatorChar);

                if (!sourceDirFull.StartsWith(enginesDirFull, StringComparison.OrdinalIgnoreCase))
                {
                    var folderName = Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(folderName))
                        return false;

                    var targetDir = Path.Combine(_enginesPath, folderName);
                    try
                    {
                        CopyDirectory(sourceDirectory, targetDir);
                    }
                    catch
                    {
                        return false;
                    }

                    filePath = Path.Combine(targetDir, fileName);
                }
            }
            else if (!filePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Copy(filePath, targetPath, overwrite: true);
                    filePath = targetPath;
                }
                catch
                {
                    return false;
                }
            }

            // Rescan for engines
            ScanForEngines();

            // Select the newly added engine
            var newEngine = _availableEngines.FirstOrDefault(e =>
                e.ExecutablePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                ?? _availableEngines.FirstOrDefault(e => e.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (newEngine != null)
            {
                SetCurrentEngine(newEngine);
            }

            return true;
        }

        private static bool LooksLikeBundledEngineDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                return false;

            bool hasWeights = Directory.GetFiles(directoryPath, "*.pb.gz").Any() ||
                              Directory.GetFiles(directoryPath, "*.pb").Any();
            bool hasBundledRuntime = File.Exists(Path.Combine(directoryPath, "onnxruntime.dll")) ||
                                     File.Exists(Path.Combine(directoryPath, "dnnl.dll")) ||
                                     File.Exists(Path.Combine(directoryPath, "mimalloc-override.dll")) ||
                                     File.Exists(Path.Combine(directoryPath, "mimalloc-redirect.dll"));

            return hasWeights || hasBundledRuntime;
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var targetFile = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, targetFile, overwrite: true);
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                var targetSubDir = Path.Combine(destinationDir, Path.GetFileName(subDir));
                CopyDirectory(subDir, targetSubDir);
            }
        }

        /// <summary>
        /// Sets the current active engine
        /// </summary>
        public void SetCurrentEngine(EngineInfo engine)
        {
            if (_availableEngines.Contains(engine))
            {
                _currentEngine = engine;
                EngineChanged?.Invoke(engine);
                SaveSettings();
            }
        }

        /// <summary>
        /// Sets engine by name
        /// </summary>
        public void SetCurrentEngine(string engineName)
        {
            var engine = _availableEngines.FirstOrDefault(e => e.Name.Equals(engineName, StringComparison.OrdinalIgnoreCase));
            if (engine != null)
            {
                SetCurrentEngine(engine);
            }
        }

        /// <summary>
        /// Gets the path to the current engine executable
        /// </summary>
        public string? GetCurrentEnginePath()
        {
            return _currentEngine?.ExecutablePath;
        }

        /// <summary>
        /// Saves engine settings to a JSON file
        /// </summary>
        private void SaveSettings()
        {
            try
            {
                var settingsPath = Path.Combine(_enginesPath, "engine_settings.json");
                var settings = new EngineSettings
                {
                    SelectedEngine = _currentEngine?.FileName,
                    SelectedEnginePath = _currentEngine?.ExecutablePath,
                    SelectedKey = _currentEngine?.Key,
                    Nicknames = new Dictionary<string, string>(_nicknames),
                    Hidden = _hidden.ToList(),
                    LastUpdated = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(settingsPath, json);
            }
            catch
            {
                // Silently fail if can't save settings
            }
        }

        /// <summary>
        /// Loads engine settings from JSON file
        /// </summary>
        public void LoadSettings()
        {
            try
            {
                var settingsPath = Path.Combine(_enginesPath, "engine_settings.json");
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    var settings = JsonSerializer.Deserialize<EngineSettings>(json);

                    // Load customizations first, then rescan so they apply.
                    _nicknames.Clear();
                    _hidden.Clear();
                    if (settings?.Nicknames != null)
                        foreach (var kv in settings.Nicknames)
                            if (!string.IsNullOrWhiteSpace(kv.Value))
                                _nicknames[kv.Key] = kv.Value;
                    if (settings?.Hidden != null)
                        foreach (var h in settings.Hidden)
                            _hidden.Add(h);

                    ScanForEngines();

                    // Restore the selected engine by key, then path, then name.
                    EngineInfo? sel = null;
                    if (!string.IsNullOrWhiteSpace(settings?.SelectedKey))
                        sel = _availableEngines.FirstOrDefault(e => e.Key == settings!.SelectedKey);
                    if (sel == null && !string.IsNullOrWhiteSpace(settings?.SelectedEnginePath))
                        sel = _availableEngines.FirstOrDefault(e =>
                            e.ExecutablePath.Equals(settings!.SelectedEnginePath, StringComparison.OrdinalIgnoreCase));
                    if (sel == null && !string.IsNullOrWhiteSpace(settings?.SelectedEngine))
                        sel = _availableEngines.FirstOrDefault(e =>
                            e.FileName.Equals(settings!.SelectedEngine, StringComparison.OrdinalIgnoreCase));
                    if (sel != null)
                        _currentEngine = sel;
                }
            }
            catch
            {
                // Silently fail if can't load settings
            }
        }

        /// <summary>
        /// Identifies engine type based on filename
        /// </summary>
        private EngineType IdentifyEngineType(string fileName)
        {
            var lowerName = fileName.ToLower();

            if (lowerName.Contains("stockfish")) return EngineType.Stockfish;
            if (lowerName.Contains("komodo")) return EngineType.Komodo;
            if (lowerName.Contains("leela") || lowerName.Contains("lc0")) return EngineType.LeelaChessZero;
            if (lowerName.Contains("houdini")) return EngineType.Houdini;
            if (lowerName.Contains("fritz")) return EngineType.Fritz;
            if (lowerName.Contains("rybka")) return EngineType.Rybka;
            if (lowerName.Contains("fire")) return EngineType.Fire;
            if (lowerName.Contains("ethereal")) return EngineType.Ethereal;

            return EngineType.Other;
        }

        /// <summary>
        /// Formats engine name for display
        /// </summary>
        private string FormatEngineName(string name)
        {
            // Remove common suffixes
            name = name.Replace("-windows", "", StringComparison.OrdinalIgnoreCase);
            name = name.Replace("-x86-64", "", StringComparison.OrdinalIgnoreCase);
            name = name.Replace("-x86_64", "", StringComparison.OrdinalIgnoreCase);
            name = name.Replace("-avx2", "", StringComparison.OrdinalIgnoreCase);
            name = name.Replace("-bmi2", "", StringComparison.OrdinalIgnoreCase);
            name = name.Replace("-popcnt", "", StringComparison.OrdinalIgnoreCase);
            name = name.Replace("_", " ");
            name = name.Replace("-", " ");

            // Special case for lc0
            if (name.Equals("lc0", StringComparison.OrdinalIgnoreCase))
            {
                return "Leela Chess Zero";
            }

            // Capitalize first letter of each word
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].Length > 0)
                {
                    words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1).ToLower();
                }
            }

            return string.Join(" ", words);
        }

        private bool IsBetterEngineCandidate(EngineInfo candidate, EngineInfo existing)
        {
            int candidateScore = ScoreEngineCandidate(candidate);
            int existingScore = ScoreEngineCandidate(existing);
            if (candidateScore != existingScore)
                return candidateScore > existingScore;

            return candidate.ExecutablePath.Length < existing.ExecutablePath.Length;
        }

        private int ScoreEngineCandidate(EngineInfo engine)
        {
            int score = 0;
            string directory = Path.GetDirectoryName(engine.ExecutablePath) ?? "";
            string rootDirectory = Path.GetFullPath(_enginesPath).TrimEnd(Path.DirectorySeparatorChar);
            string engineDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            bool inRoot = string.Equals(engineDirectory, rootDirectory, StringComparison.OrdinalIgnoreCase);

            if (engine.IsDefault)
                score += 100;

            if (!inRoot)
                score += 10;

            if (engine.Type == EngineType.LeelaChessZero)
            {
                if (Directory.Exists(directory))
                {
                    if (Directory.GetFiles(directory, "*.pb.gz", SearchOption.TopDirectoryOnly).Length > 0 ||
                        Directory.GetFiles(directory, "*.pb", SearchOption.TopDirectoryOnly).Length > 0)
                    {
                        score += 1000;
                    }

                    if (File.Exists(Path.Combine(directory, "onnxruntime.dll")))
                        score += 200;
                }

                if (string.Equals(Path.GetFileName(engine.ExecutablePath), "lc0.exe", StringComparison.OrdinalIgnoreCase) && !inRoot)
                    score += 100;
            }

            return score;
        }
    }

    /// <summary>
    /// Information about a chess engine
    /// </summary>
    public class EngineInfo
    {
        public string Name { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public EngineType Type { get; set; }
        public bool IsDefault { get; set; }
        // Where this engine runs: a local binary or the remote broker. The same
        // engine (e.g. Stockfish) can appear as both a Local and a Remote entry.
        public EngineSource Source { get; set; } = EngineSource.Local;
        // User-assigned nickname; when set, shown instead of Name.
        public string? Nickname { get; set; }

        public bool IsRemoteOnly => Source == EngineSource.Remote;
        // What the dropdown shows.
        public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? Name : Nickname!;
        // Stable identity for nicknames / hidden / selection persistence.
        public string Key => Source == EngineSource.Remote
            ? "R:" + Name
            : "L:" + ExecutablePath;

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Types of chess engines
    /// </summary>
    public enum EngineType
    {
        Stockfish,
        Komodo,
        LeelaChessZero,
        Houdini,
        Fritz,
        Rybka,
        Fire,
        Ethereal,
        Other
    }

    public enum EngineSource
    {
        Local,
        Remote
    }

    /// <summary>
    /// Settings for engine selection
    /// </summary>
    internal class EngineSettings
    {
        public string? SelectedEngine { get; set; }
        public string? SelectedEnginePath { get; set; }
        public string? SelectedKey { get; set; }
        public Dictionary<string, string>? Nicknames { get; set; }
        public List<string>? Hidden { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
