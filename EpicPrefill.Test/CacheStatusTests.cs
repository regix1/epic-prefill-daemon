using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using EpicPrefill.Api;
using EpicPrefill.Models;
using EpicPrefill.Models.ApiResponses;
using LancachePrefill.Common;

namespace EpicPrefill.Test;

[Collection("ProcessEnvironment")]
public sealed class CacheStatusTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 9, 21, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LegacyAndV2ResultsMatchGoldenJson()
    {
        var legacy = new CacheStatusResult
        {
            Apps =
            [
                new AppCacheStatus
                {
                    AppId = "alpha",
                    Name = "Alpha",
                    IsUpToDate = true
                }
            ],
            Message = "Checked 1 apps"
        };
        var version2 = new CacheStatusResult
        {
            Apps =
            [
                new AppCacheStatus
                {
                    AppId = "alpha",
                    Name = "alpha",
                    IsUpToDate = true,
                    Outcome = CacheOutcome.Current
                }
            ],
            Version = 2
        };

        AssertJsonEqual(
            """
            {
              "apps": [
                { "appId": "alpha", "name": "Alpha", "isUpToDate": true }
              ],
              "message": "Checked 1 apps"
            }
            """,
            JsonSerializer.Serialize(legacy, DaemonSerializationContext.Default.CacheStatusResult));
        AssertJsonEqual(
            """
            {
              "apps": [
                {
                  "appId": "alpha",
                  "name": "alpha",
                  "isUpToDate": true,
                  "outcome": "Current"
                }
              ],
              "version": 2
            }
            """,
            JsonSerializer.Serialize(version2, DaemonSerializationContext.Default.CacheStatusResult));
    }

    [Fact]
    public async Task LegacyRequestKeepsDetailsAndOmitsUnknownRows()
    {
        var clock = new CacheStatusClock(StartUtc);
        bool? includeDetails = null;
        var result = await EpicPrefillApi.CheckCacheStatusAsync(
            [
                new CachedAppInput { AppId = "alpha" },
                new CachedAppInput { AppId = "beta" }
            ],
            (details, _) =>
            {
                includeDetails = details;
                return Task.FromResult(new List<AppInfo>
                {
                    App("alpha", "build-a", "Alpha"),
                    App("beta", "build-b", "Beta")
                });
            },
            (game, _) => ValueTask.FromResult<bool?>(game.AppId == "alpha" ? true : null),
            clock);

        Assert.True(includeDetails);
        var app = Assert.Single(result.Apps);
        Assert.Equal("Alpha", app.Name);
        Assert.True(app.IsUpToDate);
        Assert.Null(app.Outcome);
        Assert.Null(app.Reason);
        Assert.Null(result.Version);
        Assert.Equal("Checked 1 apps", result.Message);
    }

    [Fact]
    public async Task V2RequestUsesStatusOnlyAssetsAndTypedOutcomes()
    {
        var clock = new CacheStatusClock(StartUtc);
        bool? includeDetails = null;
        var inspectCalls = 0;
        var result = await EpicPrefillApi.CheckCacheStatusAsync(
            [
                new CachedAppInput { AppId = "alpha", Revision = "build-a" },
                new CachedAppInput { AppId = "beta", Revision = "old-build" }
            ],
            (details, _) =>
            {
                includeDetails = details;
                return Task.FromResult(new List<AppInfo>
                {
                    App("alpha", "build-a", "alpha"),
                    App("beta", "build-b", "beta")
                });
            },
            (_, _) =>
            {
                Interlocked.Increment(ref inspectCalls);
                return ValueTask.FromResult<bool?>(null);
            },
            clock,
            expiresAtUtc: StartUtc.AddSeconds(30),
            cacheStatusVersion: 2);

        Assert.False(includeDetails);
        Assert.Equal(0, inspectCalls);
        Assert.Equal(2, result.Version);
        Assert.Null(result.Message);
        Assert.Collection(
            result.Apps,
            app =>
            {
                Assert.Equal("alpha", app.Name);
                Assert.True(app.IsUpToDate);
                Assert.Equal(CacheOutcome.Current, app.Outcome);
                Assert.Null(app.Reason);
            },
            app =>
            {
                Assert.Equal("beta", app.Name);
                Assert.False(app.IsUpToDate);
                Assert.Equal(CacheOutcome.Outdated, app.Outcome);
                Assert.Null(app.Reason);
            });
    }

    [Fact]
    public async Task V2AtResponseReserveReturnsOrderedDeadlineRowsWithoutDependencies()
    {
        var clock = new CacheStatusClock(StartUtc);
        var dependencyCalls = 0;
        var result = await EpicPrefillApi.CheckCacheStatusAsync(
            [
                new CachedAppInput { AppId = "alpha" },
                new CachedAppInput { AppId = "beta" }
            ],
            (_, _) =>
            {
                Interlocked.Increment(ref dependencyCalls);
                return Task.FromResult(new List<AppInfo>());
            },
            (_, _) => ValueTask.FromResult<bool?>(true),
            clock,
            expiresAtUtc: StartUtc.AddSeconds(2),
            cacheStatusVersion: 2);

        Assert.Equal(0, dependencyCalls);
        Assert.Null(result.Message);
        Assert.Equal(new[] { "alpha", "beta" }, result.Apps.Select(app => app.AppId));
        Assert.All(result.Apps, app =>
        {
            Assert.False(app.IsUpToDate);
            Assert.Equal(CacheOutcome.Unknown, app.Outcome);
            Assert.Equal(CacheReason.DeadlineReached, app.Reason);
        });
    }

    [Fact]
    public async Task LegacyExpiryUsesTheSameResponseReserve()
    {
        var clock = new CacheStatusClock(StartUtc);
        var dependencyCalls = 0;
        var result = await EpicPrefillApi.CheckCacheStatusAsync(
            [new CachedAppInput { AppId = "alpha" }],
            (_, _) =>
            {
                Interlocked.Increment(ref dependencyCalls);
                return Task.FromResult(new List<AppInfo>());
            },
            (_, _) => ValueTask.FromResult<bool?>(true),
            clock,
            expiresAtUtc: StartUtc.AddSeconds(2));

        Assert.Equal(0, dependencyCalls);
        Assert.Empty(result.Apps);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task DeadlineKeepsCompletedRowsAndAwaitsInspectionCleanup()
    {
        var clock = new CacheStatusClock(StartUtc);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = EpicPrefillApi.CheckCacheStatusAsync(
            [
                new CachedAppInput { AppId = "alpha" },
                new CachedAppInput { AppId = "beta" }
            ],
            (_, _) => Task.FromResult(new List<AppInfo>
            {
                App("alpha", "build-a", "alpha"),
                App("beta", "build-b", "beta")
            }),
            async (game, token) =>
            {
                if (game.AppId == "alpha")
                {
                    return true;
                }

                held.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return null;
                }
                finally
                {
                    cleaned.TrySetResult();
                }
            },
            clock,
            expiresAtUtc: StartUtc.AddSeconds(10),
            cacheStatusVersion: 2);

        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(8));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cleaned.Task.IsCompleted);
        Assert.Collection(
            result.Apps,
            app => Assert.Equal(CacheOutcome.Current, app.Outcome),
            app => Assert.Equal(CacheReason.DeadlineReached, app.Reason));
    }

    [Fact]
    public async Task CallerCancellationWinsADeadlineRaceAndAwaitsCleanup()
    {
        var clock = new CacheStatusClock(StartUtc);
        using var callerCancellation = new CancellationTokenSource();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = EpicPrefillApi.CheckCacheStatusAsync(
            [new CachedAppInput { AppId = "alpha" }],
            (_, _) => Task.FromResult(new List<AppInfo> { App("alpha", "build-a", "alpha") }),
            async (_, token) =>
            {
                held.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return null;
                }
                finally
                {
                    cleaned.TrySetResult();
                }
            },
            clock,
            callerCancellation.Token,
            StartUtc.AddSeconds(10),
            2);

        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await callerCancellation.CancelAsync();
        clock.Advance(TimeSpan.FromSeconds(8));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.True(cleaned.Task.IsCompleted);
    }

    [Fact]
    public async Task ItemFailureDoesNotBlockALaterResult()
    {
        var clock = new CacheStatusClock(StartUtc);
        var result = await EpicPrefillApi.CheckCacheStatusAsync(
            [
                new CachedAppInput { AppId = "alpha" },
                new CachedAppInput { AppId = "beta" }
            ],
            (_, _) => Task.FromResult(new List<AppInfo>
            {
                App("alpha", "build-a", "alpha"),
                App("beta", "build-b", "beta")
            }),
            (game, _) => game.AppId == "alpha"
                ? ValueTask.FromException<bool?>(new InvalidOperationException("inspection failed"))
                : ValueTask.FromResult<bool?>(false),
            clock,
            expiresAtUtc: StartUtc.AddSeconds(30),
            cacheStatusVersion: 2);

        Assert.Collection(
            result.Apps,
            app => Assert.Equal(CacheReason.InspectionFailed, app.Reason),
            app => Assert.Equal(CacheOutcome.Outdated, app.Outcome));
    }

    [Fact]
    public async Task V2ClassifiesMissingManifestAndCacheEvidence()
    {
        var clock = new CacheStatusClock(StartUtc);
        var result = await EpicPrefillApi.CheckCacheStatusAsync(
            [
                new CachedAppInput { AppId = "missing" },
                new CachedAppInput { AppId = "blank" },
                new CachedAppInput { AppId = "uncached" }
            ],
            (_, _) => Task.FromResult(new List<AppInfo>
            {
                App("blank", "", "blank"),
                App("uncached", "build", "uncached")
            }),
            (_, _) => ValueTask.FromResult<bool?>(null),
            clock,
            expiresAtUtc: StartUtc.AddSeconds(30),
            cacheStatusVersion: 2);

        Assert.Equal(
            new CacheReason?[] { CacheReason.MissingApp, CacheReason.ManifestUnavailable, CacheReason.NoCacheEvidence },
            result.Apps.Select(app => app.Reason).ToArray());
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task MalformedV2RequestFailsBeforeDependencies()
    {
        var clock = new CacheStatusClock(StartUtc);
        var dependencyCalls = 0;
        Task<List<AppInfo>> Load(bool _, CancellationToken __)
        {
            Interlocked.Increment(ref dependencyCalls);
            return Task.FromResult(new List<AppInfo>());
        }

        await Assert.ThrowsAsync<ArgumentException>(() => EpicPrefillApi.CheckCacheStatusAsync(
            [new CachedAppInput { AppId = "alpha" }],
            Load,
            (_, _) => ValueTask.FromResult<bool?>(true),
            clock,
            cacheStatusVersion: 2));
        await Assert.ThrowsAsync<ArgumentException>(() => EpicPrefillApi.CheckCacheStatusAsync(
            [
                new CachedAppInput { AppId = "alpha" },
                new CachedAppInput { AppId = "ALPHA" }
            ],
            Load,
            (_, _) => ValueTask.FromResult<bool?>(true),
            clock,
            expiresAtUtc: StartUtc.AddSeconds(30),
            cacheStatusVersion: 2));

        Assert.Equal(0, dependencyCalls);
    }

    [Fact]
    public async Task FramedSocketReturnsPartialV2BeforeTransportCancellation()
    {
        const string socketSecretVariable = "PREFILL_SOCKET_SECRET";
        var originalSecret = Environment.GetEnvironmentVariable(socketSecretVariable);
        Environment.SetEnvironmentVariable(socketSecretVariable, null);
        var clock = new CacheStatusClock(StartUtc);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = GetUnusedTcpPort();
        using var commands = new SocketCommandInterface(
            port,
            (apps, token, expiresAtUtc, cacheStatusVersion) => EpicPrefillApi.CheckCacheStatusAsync(
                apps,
                (_, _) => Task.FromResult(new List<AppInfo>
                {
                    App("alpha", "build-a", "alpha"),
                    App("beta", "build-b", "beta")
                }),
                async (game, inspectionToken) =>
                {
                    if (game.AppId == "alpha")
                    {
                        return true;
                    }

                    held.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, inspectionToken);
                        return null;
                    }
                    finally
                    {
                        cleaned.TrySetResult();
                    }
                },
                clock,
                token,
                expiresAtUtc,
                cacheStatusVersion),
            clock);

        try
        {
            await commands.StartAsync();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await WriteCommandAsync(stream, new CommandRequest
            {
                Id = "cache-status",
                Type = "check-cache-status",
                Parameters = new Dictionary<string, string>
                {
                    ["cacheStatusVersion"] = "2",
                    ["expiresAtUtc"] = StartUtc.AddSeconds(10).ToString("O"),
                    ["cachedApps"] = JsonSerializer.Serialize(
                        new List<CachedAppInput>
                        {
                            new() { AppId = "alpha" },
                            new() { AppId = "beta" }
                        },
                        DaemonSerializationContext.Default.ListCachedAppInput)
                }
            });

            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(8));
            using var response = await ReadJsonFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));

            var root = response.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("cache-status", root.GetProperty("id").GetString());
            Assert.Equal(2, root.GetProperty("data").GetProperty("version").GetInt32());
            Assert.False(root.TryGetProperty("message", out _));
            Assert.False(root.GetProperty("data").TryGetProperty("message", out _));
            Assert.Equal(
                "DeadlineReached",
                root.GetProperty("data").GetProperty("apps")[1].GetProperty("reason").GetString());
            Assert.True(cleaned.Task.IsCompleted);
        }
        finally
        {
            await commands.StopAsync();
            Environment.SetEnvironmentVariable(socketSecretVariable, originalSecret);
        }
    }

    [Fact]
    public async Task SocketRejectsMalformedV2FieldsWithoutDispatch()
    {
        var calls = 0;
        using var commands = new SocketCommandInterface(
            0,
            (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new CacheStatusResult());
            },
            new CacheStatusClock(StartUtc));

        var response = await SendAsync(commands, new CommandRequest
        {
            Id = "missing-expiry",
            Type = "check-cache-status",
            Parameters = new Dictionary<string, string>
            {
                ["cacheStatusVersion"] = "2",
                ["cachedApps"] = "[]"
            }
        });
        Assert.False(response.Success);
        Assert.Contains("expiresAtUtc", response.Error);

        response = await SendAsync(commands, new CommandRequest
        {
            Id = "bad-shape",
            Type = "check-cache-status",
            Parameters = new Dictionary<string, string>
            {
                ["cacheStatusVersion"] = "2",
                ["expiresAtUtc"] = StartUtc.AddSeconds(30).ToString("O"),
                ["cachedApps"] = "{}"
            }
        });
        Assert.False(response.Success);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SocketCleanV2OmitsResultAndResponseMessages()
    {
        using var commands = new SocketCommandInterface(
            0,
            (_, _, _, _) => Task.FromResult(new CacheStatusResult
            {
                Apps =
                [
                    new AppCacheStatus
                    {
                        AppId = "alpha",
                        Name = "alpha",
                        IsUpToDate = true,
                        Outcome = CacheOutcome.Current
                    }
                ],
                Version = 2
            }),
            new CacheStatusClock(StartUtc));

        var response = await SendAsync(commands, new CommandRequest
        {
            Id = "clean-v2",
            Type = "check-cache-status",
            Parameters = new Dictionary<string, string>
            {
                ["cacheStatusVersion"] = "2",
                ["expiresAtUtc"] = StartUtc.AddSeconds(30).ToString("O"),
                ["cachedApps"] = "[{\"appId\":\"alpha\"}]"
            }
        });

        Assert.True(response.Success);
        Assert.Null(response.Message);
        Assert.Null(Assert.IsType<CacheStatusResult>(response.Data).Message);
        var json = JsonSerializer.Serialize(response, DaemonSerializationContext.Default.CommandResponse);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("message", out _));
        Assert.False(document.RootElement.GetProperty("data").TryGetProperty("message", out _));
    }

    [Fact]
    public async Task SocketRejectsMalformedLegacyExpiryAndAcceptsItsAbsence()
    {
        var calls = 0;
        using var commands = new SocketCommandInterface(
            0,
            (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new CacheStatusResult
                {
                    Apps = new List<AppCacheStatus>(),
                    Message = "Checked 0 apps"
                });
            },
            new CacheStatusClock(StartUtc));

        var malformed = await SendAsync(commands, new CommandRequest
        {
            Id = "malformed-legacy-expiry",
            Type = "check-cache-status",
            Parameters = new Dictionary<string, string>
            {
                ["expiresAtUtc"] = "not-a-timestamp",
                ["cachedApps"] = "[]"
            }
        });
        Assert.False(malformed.Success);
        Assert.Contains("expiresAtUtc", malformed.Error);
        Assert.Equal(0, calls);

        var absent = await SendAsync(commands, new CommandRequest
        {
            Id = "absent-legacy-expiry",
            Type = "check-cache-status",
            Parameters = new Dictionary<string, string>
            {
                ["cachedApps"] = "[]"
            }
        });
        Assert.True(absent.Success);
        Assert.Equal("Checked 0 apps", absent.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task StatusAdvertisesCacheStatusV2Locally()
    {
        using var commands = new SocketCommandInterface(0, new CacheStatusClock(StartUtc));
        var response = await SendAsync(commands, new CommandRequest { Id = "status", Type = "status" });
        var status = Assert.IsType<StatusData>(response.Data);
        Assert.Contains("cacheStatusV2", status.Features);
    }

    private static AppInfo App(string appId, string buildVersion, string title) => new()
    {
        AppId = appId,
        BuildVersion = buildVersion,
        CatalogItemId = appId,
        Namespace = appId,
        Title = title,
        KeyImages = new List<KeyImage>()
    };

    private static void AssertJsonEqual(string expected, string actual)
    {
        var expectedJson = JsonNode.Parse(expected);
        var actualJson = JsonNode.Parse(actual);
        Assert.True(JsonNode.DeepEquals(expectedJson, actualJson), actual);
    }

    private static Task<CommandResponse> SendAsync(SocketCommandInterface commands, CommandRequest request)
        => (Task<CommandResponse>)typeof(SocketCommandInterface)
            .GetMethod("HandleCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(commands, new object[] { request, CancellationToken.None })!;

    private static int GetUnusedTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WriteCommandAsync(NetworkStream stream, CommandRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            DaemonSerializationContext.Default.CommandRequest);
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<JsonDocument> ReadJsonFrameAsync(NetworkStream stream)
    {
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes);
        var bytes = new byte[BitConverter.ToInt32(lengthBytes, 0)];
        await stream.ReadExactlyAsync(bytes);
        return JsonDocument.Parse(bytes);
    }
}
