using System.Text.Json;
using EpicPrefill.Api;
using EpicPrefill.Handlers;
using EpicPrefill.Models.ApiResponses;

namespace EpicPrefill.Test
{
    public sealed class OwnedGamesArtworkTests
    {
        private static Asset App(string appId) => new Asset { AppId = appId, BuildVersion = "1", CatalogItemId = "cat", Namespace = "ns" };

        private static KeyImage WideImage => new KeyImage { Type = "OfferImageWide", Url = "https://example.com/wide.jpg", Width = 1200, Height = 400 };

        [Fact]
        public void GetAppsMissingMetadata_AppNotInCache_IsMissing()
        {
            var apps = new List<Asset> { App("a") };
            var cache = new Dictionary<string, AppMetadataResponse>();

            var missing = EpicGamesApi.GetAppsMissingMetadata(apps, cache).ToList();

            Assert.Single(missing);
            Assert.Equal("a", missing[0].AppId);
        }

        [Fact]
        public void GetAppsMissingMetadata_CachedWithArtwork_IsNotMissing()
        {
            var apps = new List<Asset> { App("a") };
            var cache = new Dictionary<string, AppMetadataResponse>
            {
                ["a"] = new AppMetadataResponse { Title = "Game A", KeyImages = new List<KeyImage> { WideImage } }
            };

            var missing = EpicGamesApi.GetAppsMissingMetadata(apps, cache).ToList();

            Assert.Empty(missing);
        }

        /// <summary>
        /// This is the trap: a cache file written before artwork support existed deserializes with
        /// KeyImages == null even though the AppId key is present. Reproduces the silent-no-op bug -
        /// fails before the fix (the old lookup was ContainsKey only), passes after.
        /// </summary>
        [Fact]
        public void GetAppsMissingMetadata_CachedWithNullArtwork_IsTreatedAsMissing()
        {
            var apps = new List<Asset> { App("a") };
            var cache = new Dictionary<string, AppMetadataResponse>
            {
                ["a"] = new AppMetadataResponse { Title = "Game A", KeyImages = null }
            };

            var missing = EpicGamesApi.GetAppsMissingMetadata(apps, cache).ToList();

            Assert.Single(missing);
            Assert.Equal("a", missing[0].AppId);
        }

        [Fact]
        public void GetAppsMissingMetadata_CachedWithEmptyArtworkList_IsNotMissing()
        {
            // A genuinely image-less title (Epic returned an empty array, not null) should not be
            // re-requested on every run.
            var apps = new List<Asset> { App("a") };
            var cache = new Dictionary<string, AppMetadataResponse>
            {
                ["a"] = new AppMetadataResponse { Title = "Game A", KeyImages = new List<KeyImage>() }
            };

            var missing = EpicGamesApi.GetAppsMissingMetadata(apps, cache).ToList();

            Assert.Empty(missing);
        }

        /// <summary>
        /// An older build stored a null value whenever Epic answered with no usable entry for an app.
        /// Reading that back has to count as a miss, not dereference the null: before the fix the
        /// cached.KeyImages access threw and every owned-games call failed for good.
        /// </summary>
        [Fact]
        public void GetAppsMissingMetadata_CachedEntryIsNull_IsTreatedAsMissing()
        {
            var apps = new List<Asset> { App("a") };
            var cache = new Dictionary<string, AppMetadataResponse> { ["a"] = null! };

            var missing = EpicGamesApi.GetAppsMissingMetadata(apps, cache).ToList();

            Assert.Single(missing);
            Assert.Equal("a", missing[0].AppId);
        }

        /// <summary>
        /// A truncated write can leave metadataCache.json holding the literal text "null", which
        /// deserializes to a null dictionary rather than an empty one. Pins the fact the cache load
        /// guards against, since the file is only ever rewritten after a load succeeds.
        /// </summary>
        [Fact]
        public void MetadataCache_LiteralNullFile_DeserializesToNull()
        {
            var loaded = JsonSerializer.Deserialize("null", SerializationContext.Default.DictionaryStringAppMetadataResponse);

            Assert.Null(loaded);

            var apps = new List<Asset> { App("a") };
            var missing = EpicGamesApi.GetAppsMissingMetadata(apps, loaded ?? new Dictionary<string, AppMetadataResponse>()).ToList();

            Assert.Single(missing);
        }

        [Fact]
        public void CatalogMetadata_KeyImagesSurviveJsonRoundTrip()
        {
            // Proves the nested KeyImage type is reachable from the SerializationContext root
            // (Dictionary<string, AppMetadataResponse>) that LoadAppMetadataAsync actually uses -
            // a source-generation reachability gap here fails at runtime, not at compile time.
            var dictionary = new Dictionary<string, AppMetadataResponse>
            {
                ["app-1"] = new AppMetadataResponse { Title = "Game A", KeyImages = new List<KeyImage> { WideImage } }
            };

            var json = JsonSerializer.Serialize(dictionary, SerializationContext.Default.DictionaryStringAppMetadataResponse);
            var roundTripped = JsonSerializer.Deserialize(json, SerializationContext.Default.DictionaryStringAppMetadataResponse);

            var image = Assert.Single(roundTripped!["app-1"].KeyImages!);
            Assert.Equal("OfferImageWide", image.Type);
            Assert.Equal(1200, image.Width);
            Assert.Equal(400, image.Height);
        }

        [Fact]
        public void OwnedGame_KeyImagesSurviveSocketJsonRoundTrip()
        {
            // Proves the same nested type is reachable from the OTHER serializer context
            // (DaemonSerializationContext, List<OwnedGame> root) that carries the socket reply to
            // lancache-manager, and that the emitted property name matches the frozen contract.
            var games = new List<OwnedGame>
            {
                new OwnedGame { AppId = "app-1", Name = "Game A", KeyImages = new List<KeyImage> { WideImage } }
            };

            var json = JsonSerializer.Serialize(games, DaemonSerializationContext.Default.ListOwnedGame);

            Assert.Contains("\"keyImages\"", json);
            Assert.DoesNotContain("\"imageUrl\"", json);

            var roundTripped = JsonSerializer.Deserialize(json, DaemonSerializationContext.Default.ListOwnedGame);
            var image = Assert.Single(roundTripped![0].KeyImages!);
            Assert.Equal("https://example.com/wide.jpg", image.Url);
            Assert.Equal(1200, image.Width);
            Assert.Equal(400, image.Height);
        }
    }
}
