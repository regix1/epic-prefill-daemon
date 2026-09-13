#nullable enable

using System.Text.Json;
using System.Threading;

namespace EpicPrefill.Api;

/// <summary>
/// Command interface that uses Unix Domain Socket or TCP for IPC.
/// Handles all socket commands for the Epic Games prefill daemon.
/// </summary>
public sealed class SocketCommandInterface : IDisposable
{
    private readonly SocketServer _socketServer;
    private readonly SocketAuthProvider _authProvider;
    private readonly SocketProgress _progress;
    private readonly CancellationTokenSource _cts = new();
    private readonly PrefillProtocol _protocol = PrefillProtocol.FromEnvironment(AppConfig.MaxConcurrentRequests);
    private readonly OwnedOperationCoordinator _prefillOperation;
    private readonly RequestBudget _budget;
    private readonly ItemClaims _claims = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PrefillRun> _runs = new(StringComparer.Ordinal);
    private readonly Func<PrefillOptions, PrefillRun?, CancellationToken, Task<PrefillResult>>? _execute;
    private CancellationTokenSource? _loginCts;
    private EpicPrefillApi? _api;
    private Task? _loginTask;
    private bool _isLoggedIn;
    private bool _isLoggingIn;
    private bool _disposed;
    private int _authLost;

    // Bumped by logout (and cancel-login) so a login task that is still unwinding (or already
    // orphaned by a cancellation that never got observed) can tell it has been superseded and must
    // not resurrect _isLoggedIn/_api for whatever now owns them. Written from the socket command
    // loop, read from thread-pool login-task continuations after an await - always accessed via
    // Interlocked, never a plain read/increment.
    private long _loginGeneration;

    // How long logout waits for an in-flight login task to unwind before force-cleaning up
    // anyway. Logout must never hang on a stuck login.
    private static readonly TimeSpan LogoutLoginTaskTimeout = TimeSpan.FromSeconds(8);
    private static readonly string[] Presets = { "all", "recent", "top" };

    private static readonly HashSet<string> PreLoginCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "login",
        "logout",
        "status",
        "get-operation",
        "cancel-prefill",
        "shutdown",
        "cancel-login",
        "provide-credential",
        "provide-auto-login"
    };

    public SocketCommandInterface(string socketPath)
    {
        _prefillOperation = new OwnedOperationCoordinator(_protocol.MaxConcurrentRuns);
        _budget = new RequestBudget(_protocol.MaxConcurrentRequests);
        _progress = new SocketProgress();
        _socketServer = new SocketServer(socketPath, _progress);
        _authProvider = new SocketAuthProvider(_socketServer, _progress);
        _socketServer.OnCommand = HandleCommandAsync;
        _socketServer.CommandLaneSelector = SelectCommandLane;

        _progress.SocketServer = _socketServer;
    }

    public SocketCommandInterface(int tcpPort)
    {
        _prefillOperation = new OwnedOperationCoordinator(_protocol.MaxConcurrentRuns);
        _budget = new RequestBudget(_protocol.MaxConcurrentRequests);
        _progress = new SocketProgress();
        _socketServer = new SocketServer(tcpPort, _progress);
        _authProvider = new SocketAuthProvider(_socketServer, _progress);
        _socketServer.OnCommand = HandleCommandAsync;
        _socketServer.CommandLaneSelector = SelectCommandLane;

        _progress.SocketServer = _socketServer;
    }

    internal SocketCommandInterface(int tcpPort, Func<PrefillOptions, PrefillRun?, CancellationToken, Task<PrefillResult>> execute)
        : this(tcpPort)
    {
        _execute = execute;
        _isLoggedIn = true;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _progress.OnLog(LogLevel.Info, "Starting socket command interface...");

        await _socketServer.StartAsync(cancellationToken);

        await BroadcastStatusAsync("awaiting-login", "Login required before other commands can be executed");

        _progress.OnLog(LogLevel.Info, "Socket command interface started - awaiting login");
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        await _prefillOperation.CancelAllAndWaitAsync();
        await _socketServer.StopAsync();
        _progress.OnLog(LogLevel.Info, "Socket command interface stopped");
    }

    private async Task<CommandResponse> HandleCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        _progress.OnLog(LogLevel.Debug, $"Processing command: {request.Type} (ID: {request.Id})");

        if (!_isLoggedIn && !PreLoginCommands.Contains(request.Type))
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "Authentication required. Please login first.",
                RequiresLogin = true,
                CompletedAt = DateTime.UtcNow
            };
        }

        try
        {
            return request.Type.ToLowerInvariant() switch
            {
                "login" => await HandleLoginAsync(request, cancellationToken),
                "provide-auto-login" => HandleProvideAutoLogin(request),
                "logout" => await HandleLogoutAsync(request, cancellationToken),
                "cancel-login" => await HandleCancelLoginAsync(request),
                "cancel-prefill" => await HandleCancelPrefillAsync(request, cancellationToken),
                "provide-credential" => HandleProvideCredential(request),
                "status" => HandleStatus(request),
                "get-operation" => HandleGetOperation(request),
                "get-owned-games" => await HandleGetOwnedGamesAsync(request, cancellationToken),
                "get-cdn-info" => await HandleGetCdnInfoAsync(request, cancellationToken),
                "get-selected-apps" => HandleGetSelectedApps(request),
                "set-selected-apps" => HandleSetSelectedApps(request),
                "get-selected-apps-status" => await HandleGetSelectedAppsStatusAsync(request, cancellationToken),
                "prefill" => await HandlePrefillAsync(request, cancellationToken),
                "clear-cache" => HandleClearCache(request),
                "get-cache-info" => HandleGetCacheInfo(request),
                "check-cache-status" => await HandleCheckCacheStatusAsync(request, cancellationToken),
                "shutdown" => await HandleShutdownAsync(request, cancellationToken),
                _ => new CommandResponse
                {
                    Id = request.Id,
                    Success = false,
                    Error = $"Unknown command type: {request.Type}",
                    CompletedAt = DateTime.UtcNow
                }
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnLog(LogLevel.Error, $"Error handling command {request.Type}: {ex.Message}");
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = ex.Message,
                CompletedAt = DateTime.UtcNow
            };
        }
    }

    private static DaemonCommandLane SelectCommandLane(CommandRequest request)
        => request.Type.ToLowerInvariant() switch
        {
            "cancel-login" or "cancel-prefill" or "status" or "shutdown" or "get-operation"
                => DaemonCommandLane.Control,
            "get-owned-games" or "get-cdn-info" or "get-selected-apps" or
            "get-selected-apps-status" or "get-cache-info" or "check-cache-status"
                => DaemonCommandLane.Concurrent,
            _ => DaemonCommandLane.Serialized
        };

    private Task<CommandResponse> HandleLoginAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (_prefillOperation.IsRunning && !_isLoggedIn)
        {
            return Task.FromResult(new CommandResponse { Id = request.Id, Success = false, Error = "Account work is still draining" });
        }
        if (_isLoggedIn)
        {
            _progress.OnLog(LogLevel.Info, "Already logged in");
            return Task.FromResult(new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Message = "Already logged in",
                CompletedAt = DateTime.UtcNow
            });
        }

        if (_isLoggingIn)
        {
            _progress.OnLog(LogLevel.Info, "Login already in progress");
            return Task.FromResult(new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Message = "Login already in progress",
                CompletedAt = DateTime.UtcNow
            });
        }

        _progress.OnLog(LogLevel.Info, "Starting secure login process via socket...");
        _isLoggingIn = true;

        _loginCts?.Dispose();
        _loginCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var loginCts = _loginCts;

        // Captured now: if logout runs before this task settles, it bumps _loginGeneration so a
        // late-completing (superseded) task can tell and must not touch shared login state.
        // Epic's InitializeAsync doesn't accept/propagate a cancellation token internally (the
        // OAuth exchange runs to completion once started), so this guard is the primary defense
        // against a logout-raced login resurrecting state, not just a belt-and-suspenders check.
        var loginGeneration = Interlocked.Increment(ref _loginGeneration);

        var api = new EpicPrefillApi(_authProvider, _progress);
        _api = api;

        _loginTask = Task.Run(async () =>
        {
            try
            {
                await api.InitializeAsync(loginCts.Token);

                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    _progress.OnLog(LogLevel.Info, "Login superseded by logout - discarding orphaned session");
                    DisposeOrphanedApi(api);
                    return;
                }

                Interlocked.Exchange(ref _authLost, 0);
                _isLoggedIn = true;
                _isLoggingIn = false;
                _progress.OnLog(LogLevel.Info, "Login successful - commands now available");

                await BroadcastStatusAsync("logged-in", "Authenticated and ready for commands", api.DisplayName);
            }
            catch (OperationCanceledException) when (loginCts.Token.IsCancellationRequested)
            {
                _progress.OnLog(LogLevel.Info, "Login cancelled");
                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    DisposeOrphanedApi(api);
                    return;
                }
                _isLoggingIn = false;
                CleanupApiInstance();
                await BroadcastStatusAsync("awaiting-login", "Login cancelled - ready for new attempt");
            }
            catch (Exception ex)
            {
                _progress.OnLog(LogLevel.Error, $"Login failed: {ex.Message}");
                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    DisposeOrphanedApi(api);
                    return;
                }
                _isLoggingIn = false;
                CleanupApiInstance();
                await BroadcastStatusAsync("awaiting-login", $"Login failed: {ex.Message}");
            }
            finally
            {
                if (loginGeneration == Interlocked.Read(ref _loginGeneration))
                {
                    _loginCts?.Dispose();
                    _loginCts = null;
                }
            }
        }, loginCts.Token);

        return Task.FromResult(new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Login started - awaiting credentials",
            CompletedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Non-interactive (headless) login. Mirrors the Steam daemon's 'provide-auto-login': requests an encrypted
    /// refresh token from the client over the SAME secure channel used by interactive 'provide-credential',
    /// performs the OAuth refresh grant, and persists the resulting full token to Config/userAccount.json.
    /// A subsequent (and here, automatic) login then reuses the persisted session without user interaction.
    /// </summary>
    private CommandResponse HandleProvideAutoLogin(CommandRequest request)
    {
        if (_prefillOperation.IsRunning && !_isLoggedIn)
        {
            return new CommandResponse { Id = request.Id, Success = false, Error = "Account work is still draining" };
        }
        if (_isLoggedIn)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Message = "Already logged in",
                CompletedAt = DateTime.UtcNow
            };
        }

        if (_isLoggingIn)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Message = "Login already in progress",
                CompletedAt = DateTime.UtcNow
            };
        }

        _progress.OnLog(LogLevel.Info, "Starting headless (auto) login via socket...");
        _isLoggingIn = true;

        _loginCts?.Dispose();
        _loginCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var loginCts = _loginCts;
        var loginGeneration = Interlocked.Increment(ref _loginGeneration);

        var api = new EpicPrefillApi(_authProvider, _progress);
        _api = api;

        _loginTask = Task.Run(async () =>
        {
            try
            {
                // Reuse the existing encrypted credential channel to receive the refresh token.
                var refreshToken = await _authProvider.GetRefreshTokenAsync(loginCts.Token);

                // Exchange the refresh token for a full token and persist it (encrypted) to disk.
                var accountManager = Handlers.UserAccountManager.LoadFromFile(new ApiConsoleAdapter(_authProvider, _progress), _authProvider);
                await accountManager.ImportRefreshTokenAsync(refreshToken, loginCts.Token);

                // With a valid persisted session, a normal login completes without further interaction.
                await api.InitializeAsync(loginCts.Token);

                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    _progress.OnLog(LogLevel.Info, "Headless login superseded by logout - discarding orphaned session");
                    DisposeOrphanedApi(api);
                    return;
                }

                Interlocked.Exchange(ref _authLost, 0);
                _isLoggedIn = true;
                _isLoggingIn = false;
                _progress.OnLog(LogLevel.Info, "Headless login successful - commands now available");

                await BroadcastStatusAsync("logged-in", "Authenticated and ready for commands", api.DisplayName);
            }
            catch (OperationCanceledException) when (loginCts.Token.IsCancellationRequested)
            {
                _progress.OnLog(LogLevel.Info, "Headless login cancelled");
                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    DisposeOrphanedApi(api);
                    return;
                }
                _isLoggingIn = false;
                CleanupApiInstance();
                await BroadcastStatusAsync("awaiting-login", "Login cancelled - ready for new attempt");
            }
            catch (Exception ex)
            {
                _progress.OnLog(LogLevel.Error, $"Headless login failed: {ex.Message}");
                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    DisposeOrphanedApi(api);
                    return;
                }
                _isLoggingIn = false;
                CleanupApiInstance();
                await BroadcastStatusAsync("awaiting-login", $"Login failed: {ex.Message}");
            }
            finally
            {
                if (loginGeneration == Interlocked.Read(ref _loginGeneration))
                {
                    _loginCts?.Dispose();
                    _loginCts = null;
                }
            }
        }, loginCts.Token);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Headless login started - awaiting refresh token",
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleLogoutAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        // Bump the generation so any login task still unwinding cannot resurrect
        // _isLoggedIn/_api once it finally settles.
        Interlocked.Increment(ref _loginGeneration);

        // Logout while a login is in progress: cancel it the same way cancel-login does, then
        // fall through to the same cleanup + credential wipe below (cancel-then-forget).
        if (_isLoggingIn)
        {
            _authProvider.CancelPendingRequest();

            try
            {
                if (_loginCts != null) await _loginCts.CancelAsync();
            }
            catch (Exception ex) { _progress.OnLog(LogLevel.Debug, $"Error cancelling login CTS: {ex.Message}"); }

            // Bounded wait for the login task to unwind. A stuck login must never hang logout -
            // if it doesn't finish in time we force-cleanup below anyway; the generation bump
            // above keeps a late finish from resurrecting state.
            var loginTask = _loginTask;
            if (loginTask != null)
            {
                await Task.WhenAny(loginTask, Task.Delay(LogoutLoginTaskTimeout, CancellationToken.None));
            }
        }

        await _prefillOperation.CancelAllAndWaitAsync(cancellationToken);
        CleanupApiInstance();

        // Wipe the persisted account file AND its storage.key so the refresh token cannot linger
        // (or be silently re-decrypted by a later login re-using the same key) after logout.
        // Session 20260703-221336-2070027597 (RC6): logout previously deleted only the account
        // file, leaving storage.key on disk - any subsequent login re-persisted a token the SAME
        // key could still decrypt, making the erase-on-stop/clear-logins policy incomplete.
        EraseAccountStore(_progress);

        _progress.OnLog(LogLevel.Info, "Logged out");

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Logged out successfully",
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleCancelLoginAsync(CommandRequest request)
    {
        if (!_isLoggingIn)
        {
            return new CommandResponse { Id = request.Id, Success = true, Message = "No login in progress" };
        }
        _progress.OnLog(LogLevel.Info, "Cancelling login...");

        // Bump the generation first, same as logout: a login task that races past this
        // cancellation (Epic's OAuth exchange doesn't check the token mid-flight) must not be able
        // to resurrect _isLoggedIn/_api once it finally settles, even though this handler isn't a logout.
        Interlocked.Increment(ref _loginGeneration);

        _authProvider.CancelPendingRequest();

        try
        {
            if (_loginCts != null) await _loginCts.CancelAsync();
        }
        catch (Exception ex) { _progress.OnLog(LogLevel.Debug, $"Error cancelling login CTS: {ex.Message}"); }

        CleanupApiInstance();
        await BroadcastStatusAsync("awaiting-login", "Login cancelled - ready for new attempt");

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Login cancelled",
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleCancelPrefillAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Parameters?.TryGetValue("operationId", out var operationId) == true)
        {
            _protocol.ValidateInstance(request.Parameters.GetValueOrDefault("daemonInstanceId") ?? "");
            var snapshot = _prefillOperation.Cancel(operationId, _protocol.DaemonInstanceId);
            return new CommandResponse
            {
                Id = request.Id,
                Success = snapshot != null,
                Data = snapshot,
                Error = snapshot == null ? "operation-not-found" : null
            };
        }
        if (!_prefillOperation.IsRunning)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Message = "No prefill in progress",
                CompletedAt = DateTime.UtcNow
            };
        }

        _progress.OnLog(LogLevel.Info, "Cancelling prefill...");
        await _prefillOperation.CancelAndWaitAsync(cancellationToken);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Prefill cancelled",
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleProvideCredential(CommandRequest request)
    {
        var challengeId = request.Parameters?.GetValueOrDefault("challengeId");
        var clientPublicKey = request.Parameters?.GetValueOrDefault("clientPublicKey");
        var encryptedCredential = request.Parameters?.GetValueOrDefault("encryptedCredential");
        var nonce = request.Parameters?.GetValueOrDefault("nonce");
        var tag = request.Parameters?.GetValueOrDefault("tag");

        if (string.IsNullOrEmpty(challengeId) || string.IsNullOrEmpty(clientPublicKey) ||
            string.IsNullOrEmpty(encryptedCredential) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(tag))
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "Missing required credential parameters",
                CompletedAt = DateTime.UtcNow
            };
        }

        var response = new EncryptedCredentialResponse
        {
            ChallengeId = challengeId,
            ClientPublicKey = clientPublicKey,
            EncryptedCredential = encryptedCredential,
            Nonce = nonce,
            Tag = tag
        };

        var accepted = _authProvider.ReceiveCredential(response);
        if (!accepted)
        {
            // RC4 (session 20260703-221336-2070027597): don't mask a dropped credential as
            // success - no pending challenge or a challenge-id mismatch means the manager's
            // session-scoping desynced from the daemon and this credential landed nowhere.
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "No matching login challenge is pending for this credential",
                CompletedAt = DateTime.UtcNow
            };
        }

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Credential received",
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleStatus(CommandRequest request)
    {
        // Read the persisted token (read-only) so expiry/account info is available even before/without an active login.
        var storedToken = Handlers.UserAccountManager.TryReadStoredToken();

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = new StatusData
            {
                ProtocolVersion = PrefillProtocol.Version,
                Features = PrefillProtocol.Features,
                DaemonInstanceId = _protocol.DaemonInstanceId,
                MaxConcurrentRuns = _protocol.MaxConcurrentRuns,
                MaxConcurrentRequests = _protocol.MaxConcurrentRequests,
                RetentionHours = PrefillProtocol.RetentionHours,
                RetentionOperations = PrefillProtocol.RetentionOperations,
                RetentionItems = PrefillProtocol.RetentionItems,
                ActiveOperations = _prefillOperation.GetActiveOperations(),
                RecentOperations = _prefillOperation.GetRecentOperations(),
                IsLoggedIn = _isLoggedIn,
                IsInitialized = _api?.IsInitialized ?? false,
                IsPrefilling = _prefillOperation.IsRunning,
                AuthExpiryUtc = ToUtcIso(storedToken?.RefreshTokenExpiresAt),
                AccessExpiryUtc = ToUtcIso(storedToken?.ExpiresAt),
                AccountDisplayName = storedToken?.DisplayName
            },
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleGetOperation(CommandRequest request)
    {
        var parameters = request.Parameters ?? throw new ArgumentException("operationId is required");
        _protocol.ValidateInstance(parameters.GetValueOrDefault("daemonInstanceId") ?? "");
        var operationId = parameters.GetValueOrDefault("operationId") ?? throw new ArgumentException("operationId is required");
        var offset = parameters.TryGetValue("offset", out var start) ? int.Parse(start, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var limit = parameters.TryGetValue("limit", out var count) ? int.Parse(count, System.Globalization.CultureInfo.InvariantCulture) : 100;
        var page = _prefillOperation.GetOperation(operationId, offset, limit);
        return new CommandResponse { Id = request.Id, Success = page != null, Data = page, Error = page == null ? "operation-not-found" : null };
    }

    private RunOptions CaptureOptions(Dictionary<string, string> parameters)
    {
        var presets = Presets.Where(key => bool.TryParse(parameters.GetValueOrDefault(key), out var enabled) && enabled).ToArray();
        if (presets.Length > 1) { throw new ArgumentException("Conflicting selection presets"); }
        var selection = parameters.GetValueOrDefault("selection") ?? presets.SingleOrDefault() ?? "selected";
        if (selection is not ("selected" or "all" or "recent" or "top")) { throw new ArgumentException("Unsupported selection preset"); }
        if (presets.Length > 0 && selection != presets[0]) { throw new ArgumentException("Conflicting selection presets"); }
        List<string>? ids = null;
        if (parameters.TryGetValue("appIds", out var json))
        {
            ids = JsonSerializer.Deserialize(json, DaemonSerializationContext.Default.ListString)
                ?? throw new ArgumentException("appIds must be an array");
            ids = ids.Select(id => id.ToUpperInvariant()).ToList();
        }
        var maximum = parameters.TryGetValue("maxConcurrency", out var rawMaximum)
            ? int.Parse(rawMaximum, System.Globalization.CultureInfo.InvariantCulture) : _protocol.MaxConcurrentRequests;
        int? topCount = parameters.TryGetValue("topCount", out var rawCount)
            ? int.Parse(rawCount, System.Globalization.CultureInfo.InvariantCulture) : null;
        if (topCount <= 0) { throw new ArgumentException("topCount must be positive"); }
        return _protocol.Capture(new RunOptions
        {
            AppIds = ids,
            Selection = selection,
            MaxConcurrency = maximum,
            TopCount = topCount,
            Force = bool.TryParse(parameters.GetValueOrDefault("force"), out var force) && force
        });
    }

    private static string? ToUtcIso(in DateTime? value)
    {
        if (value == null || value.Value == default)
        {
            return null;
        }

        return value.Value.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<CommandResponse> HandleGetOwnedGamesAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();
        var games = await _api!.GetOwnedGamesAsync(cancellationToken);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = games,
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleGetCdnInfoAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();

        // Optional: filter by specific appIds
        List<string>? appIds = null;
        if (request.Parameters?.TryGetValue("appIds", out var appIdsJson) == true)
        {
            appIds = JsonSerializer.Deserialize(appIdsJson, DaemonSerializationContext.Default.ListString);
        }

        var result = await _api!.GetCdnInfoAsync(appIds, cancellationToken);
        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = result,
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleGetSelectedApps(CommandRequest request)
    {
        EnsureLoggedIn();
        var selected = _api!.GetSelectedApps();

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = selected,
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleSetSelectedApps(CommandRequest request)
    {
        EnsureLoggedIn();

        var appIdsJson = request.Parameters?.GetValueOrDefault("appIds");
        if (string.IsNullOrEmpty(appIdsJson))
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "appIds parameter required",
                CompletedAt = DateTime.UtcNow
            };
        }

        var appIds = JsonSerializer.Deserialize(appIdsJson, DaemonSerializationContext.Default.ListString);
        if (appIds == null)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "appIds must be a JSON array",
                CompletedAt = DateTime.UtcNow
            };
        }

        // An empty array is a valid request: it clears the current selection.
        _api!.SetSelectedApps(appIds);
        _progress.OnLog(LogLevel.Info, $"Set {appIds.Count} selected apps");

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Apps selected",
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleGetSelectedAppsStatusAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();

        List<string>? operatingSystems = null;
        var osParam = request.Parameters?.GetValueOrDefault("os");
        if (!string.IsNullOrEmpty(osParam))
        {
            operatingSystems = osParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        var status = await _api!.GetSelectedAppsStatusAsync(operatingSystems, cancellationToken);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = status,
            Message = status.Message,
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandlePrefillAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        EnsureLoggedIn();

        if (request.Parameters?.GetValueOrDefault("protocolVersion") == "2")
        {
            if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128)
            {
                throw new ArgumentException("Invalid operationId");
            }
            _protocol.ValidateInstance(request.Parameters.GetValueOrDefault("daemonInstanceId") ?? "");
            var captured = CaptureOptions(request.Parameters);
            var run = new PrefillRun(request.Id, _protocol, captured, _budget, _claims, _progress,
                (snapshot, token) => _socketServer.BroadcastProgressAsync(new ProgressEvent(PrefillRun.ToUpdate(snapshot)), token));
            var api = _api!;
            var registered = _runs.TryAdd(request.Id, run);
            var admission = await _prefillOperation.StartAsync(request.Id, PrefillProtocol.Fingerprint(run.Options),
                run.Progress, token => run.ExecuteAsync(async runToken =>
                {
                    try
                    {
                        var execute = _execute ?? api.PrefillAsync;
                        var result = await execute(new PrefillOptions
                        {
                            Force = run.Options.Force,
                            DownloadAllOwnedGames = run.Options.Selection == "all",
                            Recent = run.Options.Selection == "recent",
                            Top = run.Options.Selection == "top"
                        }, run, runToken);
                        if (!result.Success) { run.Progress.TryChooseTerminal("failed", "prefill-failed"); }
                    }
                    catch (EpicLoginException)
                    {
                        _isLoggedIn = false;
                        if (Interlocked.Exchange(ref _authLost, 1) == 0)
                        {
                            foreach (var active in _runs.Values)
                            {
                                active.Progress.TryChooseTerminal("failed", "auth-lost");
                                _prefillOperation.Cancel(active.Progress.Snapshot.OperationId, _protocol.DaemonInstanceId);
                            }
                            await BroadcastStatusAsync("auth-required", "Epic authentication is required");
                        }
                        throw;
                    }
                    finally
                    {
                        _runs.TryRemove(request.Id, out _);
                    }
                }, token), _cts.Token);
            if (registered && (!admission.Accepted || admission.Replayed)) { _runs.TryRemove(request.Id, out _); }
            return new CommandResponse
            {
                Id = request.Id,
                Success = admission.Accepted,
                Error = admission.Error,
                Data = admission.Accepted ? new PrefillStart(true, request.Id, _protocol.DaemonInstanceId,
                    admission.Replayed ? admission.Operation!.State : "started") : null
            };
        }

        if (_prefillOperation.IsRunning)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "A prefill is already in progress",
                CompletedAt = DateTime.UtcNow
            };
        }

        var options = new PrefillOptions();

        if (request.Parameters != null)
        {
            if (bool.TryParse(request.Parameters.GetValueOrDefault("all"), out var all))
                options.DownloadAllOwnedGames = all;
            if (bool.TryParse(request.Parameters.GetValueOrDefault("recent"), out var recent))
                options.Recent = recent;
            if (bool.TryParse(request.Parameters.GetValueOrDefault("top"), out var top))
                options.Top = top;
            if (bool.TryParse(request.Parameters.GetValueOrDefault("force"), out var force))
                options.Force = force;
        }

        var legacy = new PrefillRun(request.Id, _protocol, new RunOptions
        {
            Selection = "legacy",
            Force = options.Force,
            MaxConcurrency = _protocol.MaxConcurrentRequests
        }, _budget, _claims, _progress);
        await _prefillOperation.StartAsync(operationToken => legacy.ExecuteAsync(async token =>
        {
            try
            {
                var result = _execute == null
                    ? await _api!.PrefillAsync(options, token)
                    : await _execute(options, null, token);
                token.ThrowIfCancellationRequested();

                if (result.Success)
                {
                    _progress.OnLog(LogLevel.Info, "Prefill completed successfully");
                }
                else
                {
                    _progress.OnLog(LogLevel.Warning, $"Prefill completed with errors: {result.ErrorMessage}");
                    throw new InvalidOperationException(result.ErrorMessage ?? "Prefill failed");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _progress.OnCancelled("Prefill cancelled by user");
                throw;
            }
        }, operationToken), _cts.Token);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Prefill started",
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleClearCache(CommandRequest request)
    {
        if (_prefillOperation.IsRunning)
        {
            return new CommandResponse { Id = request.Id, Success = false, Error = "A prefill is already in progress" };
        }
        var result = EpicPrefillApi.ClearCache();

        return new CommandResponse
        {
            Id = request.Id,
            Success = result.Success,
            Data = result,
            Message = result.Message,
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleGetCacheInfo(CommandRequest request)
    {
        var info = EpicPrefillApi.GetCacheInfo();

        return new CommandResponse
        {
            Id = request.Id,
            Success = info.Success,
            Data = info,
            Message = info.Message,
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleCheckCacheStatusAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();

        // Accept app IDs as a JSON string list in "appIds" parameter
        List<string> appIds;
        var appIdsJson = request.Parameters?.GetValueOrDefault("appIds");
        if (!string.IsNullOrEmpty(appIdsJson))
        {
            appIds = JsonSerializer.Deserialize(appIdsJson, DaemonSerializationContext.Default.ListString) ?? new List<string>();
        }
        else
        {
            // No app IDs provided
            return new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Data = new CacheStatusResult { Apps = new List<AppCacheStatus>(), Message = "No app IDs provided" },
                Message = "No app IDs provided",
                CompletedAt = DateTime.UtcNow
            };
        }

        var status = await _api!.CheckCacheStatusAsync(appIds, cancellationToken);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = status,
            Message = status.Message,
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleShutdownAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        await _prefillOperation.CancelAllAndWaitAsync(cancellationToken);
        CleanupApiInstance();

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Shutdown complete",
            CompletedAt = DateTime.UtcNow
        };
    }

    private void EnsureLoggedIn()
    {
        if (!_isLoggedIn || (_execute == null && (_api == null || !_api.IsInitialized)))
            throw new InvalidOperationException("Not logged in. Please login first.");
    }

    private void CleanupApiInstance()
    {
        try
        {
            _api?.Shutdown();
            _api?.Dispose();
        }
        catch (Exception ex) { _progress.OnLog(LogLevel.Debug, $"Error during API cleanup: {ex.Message}"); }
        _api = null;
        _isLoggedIn = false;
        _isLoggingIn = false;
    }

    /// <summary>
    /// Tears down an api instance that lost the generation race (superseded by a logout) without
    /// touching any of the shared fields, since a newer login/logout cycle may already own them.
    /// Also erases the account store: session 20260703-221336-2070027597 (RC6) confirmed that
    /// <see cref="Handlers.UserAccountManager.LoginAsync"/>/<c>ImportRefreshTokenAsync</c> call
    /// Save() BEFORE this generation check runs, so an orphaned login that raced past a logout can
    /// still persist a fresh token to disk. Erasing here closes that resurrection window; the erase
    /// is idempotent (both files may already be gone from the logout that superseded this task).
    /// </summary>
    private static void DisposeOrphanedApi(EpicPrefillApi api)
    {
        try
        {
            api.Shutdown();
            api.Dispose();
        }
        catch { /* ignore cleanup errors for a discarded orphan */ }

        EraseAccountStore();
    }

    /// <summary>
    /// Deletes the persisted account file and its storage.key. Shared by <see cref="HandleLogoutAsync"/>
    /// (explicit logout) and <see cref="DisposeOrphanedApi"/> (superseded-login resurrection guard) -
    /// session 20260703-221336-2070027597 (RC6). Best-effort: both files may already be absent.
    /// </summary>
    private static void EraseAccountStore(IPrefillProgress? progress = null)
    {
        TryDeleteAccountStoreFile(AppConfig.AccountSettingsStorePath, "Account credentials", progress);
        TryDeleteAccountStoreFile(Path.Combine(AppConfig.ConfigDir, "storage.key"), "Storage key", progress);
    }

    private static void TryDeleteAccountStoreFile(string path, string label, IPrefillProgress? progress)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                progress?.OnLog(LogLevel.Info, $"{label} removed from disk");
            }
        }
        catch (Exception ex)
        {
            progress?.OnLog(LogLevel.Warning, $"Could not remove {label.ToLowerInvariant()} from disk: {ex.Message}");
        }
    }

    private async Task BroadcastStatusAsync(string status, string message, string? displayName = null)
    {
        var statusEvent = new AuthStateEvent(status, message, displayName);
        await _socketServer.BroadcastAuthStateAsync(statusEvent);
    }

    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits", Justification = "The synchronous disposal contract drains process-owned tasks before releasing their resources.")]
    public void Dispose()
    {
        if (_disposed) return;

        _cts.Cancel();
        _loginCts?.Dispose();
        _prefillOperation.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _budget.Dispose();
        _cts.Dispose();
        _api?.Dispose();
        _authProvider.Dispose();
        _socketServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _disposed = true;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Progress implementation that broadcasts updates via socket.
    /// </summary>
    internal sealed class SocketProgress : IPrefillProgress
    {
        public SocketServer? SocketServer { get; set; }
        private readonly DaemonLogSink _logSink = new(
            Console.WriteLine,
            AppConfig.DebugLogs ? DaemonLogLevel.Debug : DaemonLogLevel.Info);
        private DateTime _lastProgressBroadcast = DateTime.MinValue;
        private static readonly TimeSpan BroadcastThrottle = TimeSpan.FromMilliseconds(250);

        public void OnLog(LogLevel level, string message)
        {
            var daemonLevel = level switch
            {
                LogLevel.Debug => DaemonLogLevel.Debug,
                LogLevel.Info => DaemonLogLevel.Info,
                LogLevel.Warning => DaemonLogLevel.Warning,
                LogLevel.Error => DaemonLogLevel.Error,
                _ => DaemonLogLevel.Info
            };
            var prefix = level switch
            {
                LogLevel.Debug => "[DEBUG]",
                LogLevel.Info => "[INFO]",
                LogLevel.Warning => "[WARN]",
                LogLevel.Error => "[ERROR]",
                _ => "[LOG]"
            };
            _logSink.Write(daemonLevel, $"{DateTime.UtcNow:HH:mm:ss} {prefix} {message}");
        }

        public void OnOperationStarted(string operationName)
            => OnLog(LogLevel.Info, $"Starting: {operationName}");

        public void OnOperationCompleted(string operationName, TimeSpan elapsed)
            => OnLog(LogLevel.Info, $"Completed: {operationName} ({elapsed.TotalSeconds:F2}s)");

        public void OnAppStarted(AppDownloadInfo app)
        {
            OnLog(LogLevel.Info, $"Downloading: {app.Name} ({app.AppId})");
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "downloading",
                CurrentAppId = app.AppId,
                CurrentAppName = app.Name,
                TotalBytes = app.TotalBytes,
                BytesDownloaded = 0,
                PercentComplete = 0,
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnDownloadProgress(DownloadProgressInfo progress)
        {
            var now = DateTime.UtcNow;
            if (now - _lastProgressBroadcast < BroadcastThrottle)
                return;

            _lastProgressBroadcast = now;

            var downloadedStr = FormatBytes(progress.BytesDownloaded);
            var totalStr = FormatBytes(progress.TotalBytes);
            var speedStr = FormatBytes((long)progress.BytesPerSecond) + "/s";
            OnLog(LogLevel.Debug, $"{progress.AppName}: {progress.PercentComplete:F1}% - {speedStr} - {downloadedStr} / {totalStr}");

            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "downloading",
                CurrentAppId = progress.AppId,
                CurrentAppName = progress.AppName,
                TotalBytes = progress.TotalBytes,
                BytesDownloaded = progress.BytesDownloaded,
                PercentComplete = progress.PercentComplete,
                BytesPerSecond = progress.BytesPerSecond,
                Elapsed = progress.Elapsed,
                UpdatedAt = DateTime.UtcNow
            });
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:F2} {sizes[order]}";
        }

        public void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result)
        {
            OnLog(LogLevel.Info, $"Completed: {app.Name} - {result}");
            var bytesDownloaded = result == AppDownloadResult.Success ? app.TotalBytes : 0;
            var state = result == AppDownloadResult.AlreadyUpToDate ? "already_cached" : "app_completed";

            BroadcastProgress(new PrefillProgressUpdate
            {
                State = state,
                CurrentAppId = app.AppId,
                CurrentAppName = app.Name,
                TotalBytes = app.TotalBytes,
                BytesDownloaded = bytesDownloaded,
                Result = result.ToString(),
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnPrefillCompleted(PrefillSummary summary)
        {
            OnLog(LogLevel.Info, $"Prefill complete: {summary.UpdatedApps} updated, {summary.AlreadyUpToDate} up-to-date, {summary.FailedApps} failed");
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "completed",
                TotalApps = summary.TotalApps,
                UpdatedApps = summary.UpdatedApps,
                AlreadyUpToDate = summary.AlreadyUpToDate,
                FailedApps = summary.FailedApps,
                TotalBytesTransferred = summary.TotalBytesTransferred,
                TotalTime = summary.TotalTime,
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnError(string message, Exception? exception = null)
        {
            OnLog(LogLevel.Error, message);
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "error",
                ErrorMessage = message,
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnCancelled(string message)
        {
            OnLog(LogLevel.Info, message);
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "cancelled",
                ErrorMessage = message,
                UpdatedAt = DateTime.UtcNow
            });
        }

        private void BroadcastProgress(PrefillProgressUpdate update)
        {
            if (SocketServer == null) return;

            var progressEvent = new ProgressEvent(update);
            _ = SocketServer.BroadcastProgressAsync(progressEvent);
        }
    }
}
