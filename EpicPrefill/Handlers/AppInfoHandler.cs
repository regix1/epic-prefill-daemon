namespace EpicPrefill.Handlers
{
    //TODO rename
    public sealed class AppInfoHandler
    {
        private readonly IAnsiConsole _ansiConsole;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _path;
        private readonly Action<string, string> _commit;

        //TODO document
        private Dictionary<string, HashSet<string>> _previouslyDownloadedApps = new Dictionary<string, HashSet<string>>();

        public AppInfoHandler(IAnsiConsole ansiConsole)
            : this(ansiConsole, AppConfig.SuccessfullyDownloadedAppsPath, (source, target) => File.Move(source, target, true))
        {
        }

        internal AppInfoHandler(IAnsiConsole ansiConsole, string path, Action<string, string> commit)
        {
            _path = Path.GetFullPath(path);
            _commit = commit;
            _ansiConsole = ansiConsole;
            if (File.Exists(_path))
            {
                var fileContents = File.ReadAllText(_path);
                _previouslyDownloadedApps = JsonSerializer.Deserialize(fileContents, SerializationContext.Default.DictionaryStringHashSetString);
            }
        }

        public bool MarkDownloadAsSuccessful(AppInfo appInfo, PrefillRun run = null, long totalBytes = 0)
        {
            lock (Gates.GetOrAdd(_path, _ => new object()))
            {
                var merged = File.Exists(_path)
                    ? JsonSerializer.Deserialize(File.ReadAllText(_path), SerializationContext.Default.DictionaryStringHashSetString)
                    : new Dictionary<string, HashSet<string>>();
                merged ??= new Dictionary<string, HashSet<string>>();
                if (!merged.TryGetValue(appInfo.AppId, out var versions))
                {
                    versions = new HashSet<string>();
                    merged.Add(appInfo.AppId, versions);
                }
                versions.Add(appInfo.BuildVersion);
                var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(merged, SerializationContext.Default.DictionaryStringHashSetString));
                    void Commit()
                    {
                        _commit(temporary, _path);
                        _previouslyDownloadedApps = merged;
                    }
                    if (run != null)
                    {
                        return run.TryCommitItem(new AppDownloadInfo
                        {
                            AppId = appInfo.AppId,
                            Name = appInfo.Title,
                            TotalBytes = totalBytes
                        }, Commit);
                    }
                    Commit();
                    return true;
                }
                finally
                {
                    if (File.Exists(temporary)) { File.Delete(temporary); }
                }
            }
        }

        /// <summary>
        /// An app will be considered up to date if it's current build version has been previously downloaded.
        /// </summary>
        public bool AppIsUpToDate(AppInfo appInfo)
        {
            lock (Gates.GetOrAdd(_path, _ => new object()))
            {
                return _previouslyDownloadedApps.TryGetValue(appInfo.AppId, out var versions)
                    && versions.Contains(appInfo.BuildVersion);
            }
        }
    }
}
