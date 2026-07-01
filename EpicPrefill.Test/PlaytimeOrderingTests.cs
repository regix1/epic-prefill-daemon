using EpicPrefill;
using EpicPrefill.Models;
using EpicPrefill.Models.ApiResponses;

namespace EpicPrefill.Test
{
    public sealed class PlaytimeOrderingTests
    {
        private static AppInfo Game(string appId, string? title = null) =>
            new AppInfo { AppId = appId, Title = title ?? appId };

        private static PlaytimeEntry Played(string artifactId, long totalTime) =>
            new PlaytimeEntry { ArtifactId = artifactId, TotalTime = totalTime };

        [Fact]
        public void OrdersOwnedGamesByTotalPlaytimeDescending()
        {
            var owned = new List<AppInfo> { Game("a"), Game("b"), Game("c") };
            var playtimes = new List<PlaytimeEntry>
            {
                Played("a", 100),
                Played("b", 900),
                Played("c", 500)
            };

            var result = PlaytimeOrdering.OrderOwnedGamesByMostPlayed(owned, playtimes);

            Assert.Equal(new List<string> { "b", "c", "a" }, result);
        }

        [Fact]
        public void ExcludesOwnedGamesWithNoOrZeroPlaytime()
        {
            var owned = new List<AppInfo> { Game("a"), Game("b"), Game("c") };
            var playtimes = new List<PlaytimeEntry>
            {
                Played("a", 300),
                Played("b", 0)
                // "c" has no playtime entry at all
            };

            var result = PlaytimeOrdering.OrderOwnedGamesByMostPlayed(owned, playtimes);

            Assert.Equal(new List<string> { "a" }, result);
        }

        [Fact]
        public void IgnoresPlaytimeForTitlesTheAccountDoesNotOwn()
        {
            var owned = new List<AppInfo> { Game("a") };
            var playtimes = new List<PlaytimeEntry>
            {
                Played("a", 100),
                Played("not-owned", 9999)
            };

            var result = PlaytimeOrdering.OrderOwnedGamesByMostPlayed(owned, playtimes);

            Assert.Equal(new List<string> { "a" }, result);
        }

        [Fact]
        public void MatchesArtifactIdToAppNameCaseInsensitively()
        {
            var owned = new List<AppInfo> { Game("Fortnite") };
            var playtimes = new List<PlaytimeEntry> { Played("fortnite", 42) };

            var result = PlaytimeOrdering.OrderOwnedGamesByMostPlayed(owned, playtimes);

            Assert.Equal(new List<string> { "Fortnite" }, result);
        }

        [Fact]
        public void ReturnsEmptyWhenNoOwnedGameHasPlaytimeSoCallerCanFallBack()
        {
            var owned = new List<AppInfo> { Game("a"), Game("b") };
            var playtimes = new List<PlaytimeEntry>();

            var result = PlaytimeOrdering.OrderOwnedGamesByMostPlayed(owned, playtimes);

            Assert.Empty(result);
        }
    }
}
