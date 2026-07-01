namespace EpicPrefill
{
    /// <summary>
    /// Pure ordering logic for the "Top" (most-played) preset, kept separate from
    /// <see cref="EpicGamesManager"/> so it can be unit-tested without any network or auth state.
    /// </summary>
    public static class PlaytimeOrdering
    {
        /// <summary>
        /// Orders owned games by cumulative playtime, most-played first, keeping only titles that
        /// Epic reports a positive playtime for. Returns an empty list when no owned game has any
        /// recorded playtime, which lets the caller fall back to a full prefill instead of prefilling
        /// nothing. Epic's artifactId matches the owned asset appName, so titles are joined on
        /// <see cref="AppInfo.AppId"/> (case-insensitively).
        /// </summary>
        public static List<string> OrderOwnedGamesByMostPlayed(
            IReadOnlyList<AppInfo> ownedGames,
            IReadOnlyList<PlaytimeEntry> playtimes)
        {
            var playedSeconds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in playtimes)
            {
                if (!string.IsNullOrEmpty(entry.ArtifactId) && entry.TotalTime > 0)
                {
                    playedSeconds[entry.ArtifactId] = entry.TotalTime;
                }
            }

            return ownedGames
                .Where(game => playedSeconds.ContainsKey(game.AppId))
                .OrderByDescending(game => playedSeconds[game.AppId])
                .ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
                .Select(game => game.AppId)
                .ToList();
        }
    }
}
