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
    private CancellationTokenSource? _loginCts;
    private CancellationTokenSource? _prefillCts;
    private EpicPrefillApi? _api;
    private Task? _loginTask;
    private bool _isLoggedIn;
    private bool _isLoggingIn;
    private bool _isPrefilling;
    private bool _disposed;

    // Bumped by logout (and cancel-login) so a login task that is still unwinding (or already
    // orphaned by a cancellation that never got observed) can tell it has been superseded and must
    // not resurrect _isLoggedIn/_api for whatever now owns them. Written from the socket command
    // loop, read from thread-pool login-task continuations after an await - always accessed via
    // Interlocked, never a plain read/increment.
    private long _loginGeneration;

    // How long logout waits for an in-flight login task to unwind before force-cleaning up
    // anyway. Logout must never hang on a stuck login.
    private static readonly TimeSpan LogoutLoginTaskTimeout = TimeSpan.FromSeconds(8);

    private static readonly HashSet<string> PreLoginCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "login",
        "logout",
        "status",
        "cancel-login",
        "provide-credential",
        "provide-auto-login"
    };

    public SocketCommandInterface(string socketPath)
    {
        _progress = new SocketProgress();
        _socketServer = new SocketServer(socketPath, _progress);
        _authProvider = new SocketAuthProvider(_socketServer, _progress);
        _socketServer.OnCommand = HandleCommandAsync;

        _progress.SocketServer = _socketServer;
    }

    public SocketCommandInterface(int tcpPort)
    {
        _progress = new SocketProgress();
        _socketServer = new SocketServer(tcpPort, _progress);
        _authProvider = new SocketAuthProvider(_socketServer, _progress);
        _socketServer.OnCommand = HandleCommandAsync;

        _progress.SocketServer = _socketServer;
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
        _cts.Cancel();
        await _socketServer.StopAsync();
        _progress.OnLog(LogLevel.Info, "Socket command interface stopped");
    }

    private async Task<CommandResponse> HandleCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        _progress.OnLog(LogLevel.Info, $"Processing command: {request.Type} (ID: {request.Id})");

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
                "logout" => await HandleLogoutAsync(request),
                "cancel-login" => await HandleCancelLoginAsync(request),
                "cancel-prefill" => HandleCancelPrefill(request),
                "provide-credential" => HandleProvideCredential(request),
                "status" => HandleStatus(request),
                "get-owned-games" => await HandleGetOwnedGamesAsync(request, cancellationToken),
                "get-cdn-info" => await HandleGetCdnInfoAsync(request, cancellationToken),
                "get-selected-apps" => HandleGetSelectedApps(request),
                "set-selected-apps" => HandleSetSelectedApps(request),
                "get-selected-apps-status" => await HandleGetSelectedAppsStatusAsync(request, cancellationToken),
                "prefill" => await HandlePrefillAsync(request, cancellationToken),
                "clear-cache" => HandleClearCache(request),
                "get-cache-info" => HandleGetCacheInfo(request),
                "check-cache-status" => await HandleCheckCacheStatusAsync(request, cancellationToken),
                "shutdown" => HandleShutdown(request),
                _ => new CommandResponse
                {
                    Id = request.Id,
                    Success = false,
                    Error = $"Unknown command type: {request.Type}",
                    CompletedAt = DateTime.UtcNow
                }
            };
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

    private Task<CommandResponse> HandleLoginAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (_isLoggedIn)
        {
            _progress.OnLog(LogLevel.Info, "Already logged in");
            return Task.FromResult(new CommandResponse
            {
                Id = request.Id, Success = true, Message = "Already logged in", CompletedAt = DateTime.UtcNow
            });
        }

        if (_isLoggingIn)
        {
            _progress.OnLog(LogLevel.Info, "Login already in progress");
            return Task.FromResult(new CommandResponse
            {
                Id = request.Id, Success = true, Message = "Login already in progress", CompletedAt = DateTime.UtcNow
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

                _isLoggedIn = true;
                _isLoggingIn = false;
                _progress.OnLog(LogLevel.Info, "Login successful - commands now available");

                await BroadcastStatusAsync("logged-in", "Authenticated and ready for commands", api.DisplayName);
            }
            catch (OperationCanceledException)
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
            Id = request.Id, Success = true, Message = "Login started - awaiting credentials", CompletedAt = DateTime.UtcNow
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
        if (_isLoggedIn)
        {
            return new CommandResponse
            {
                Id = request.Id, Success = true, Message = "Already logged in", CompletedAt = DateTime.UtcNow
            };
        }

        if (_isLoggingIn)
        {
            return new CommandResponse
            {
                Id = request.Id, Success = true, Message = "Login already in progress", CompletedAt = DateTime.UtcNow
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

                _isLoggedIn = true;
                _isLoggingIn = false;
                _progress.OnLog(LogLevel.Info, "Headless login successful - commands now available");

                await BroadcastStatusAsync("logged-in", "Authenticated and ready for commands", api.DisplayName);
            }
            catch (OperationCanceledException)
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
            Id = request.Id, Success = true, Message = "Headless login started - awaiting refresh token", CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleLogoutAsync(CommandRequest request)
    {
        // Bump the generation so any login task still unwinding (or one that never observes
        // the cancellation below - Epic's OAuth exchange doesn't check the token mid-flight)
        // cannot resurrect _isLoggedIn/_api once it finally settles.
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
                await Task.WhenAny(loginTask, Task.Delay(LogoutLoginTaskTimeout));
            }
        }

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
            Id = request.Id, Success = true, Message = "Logged out successfully", CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleCancelLoginAsync(CommandRequest request)
    {
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
            Id = request.Id, Success = true, Message = "Login cancelled", CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleCancelPrefill(CommandRequest request)
    {
        if (!_isPrefilling)
        {
            return new CommandResponse
            {
                Id = request.Id, Success = true, Message = "No prefill in progress", CompletedAt = DateTime.UtcNow
            };
        }

        _progress.OnLog(LogLevel.Info, "Cancelling prefill...");
        try { _prefillCts?.Cancel(); }
        catch (Exception ex) { _progress.OnLog(LogLevel.Debug, $"Error cancelling prefill CTS: {ex.Message}"); }

        return new CommandResponse
        {
            Id = request.Id, Success = true, Message = "Prefill cancellation requested", CompletedAt = DateTime.UtcNow
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
                Id = request.Id, Success = false, Error = "Missing required credential parameters", CompletedAt = DateTime.UtcNow
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
            Id = request.Id, Success = true, Message = "Credential received", CompletedAt = DateTime.UtcNow
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
                IsLoggedIn = _isLoggedIn,
                IsInitialized = _api?.IsInitialized ?? false,
                AuthExpiryUtc = ToUtcIso(storedToken?.RefreshTokenExpiresAt),
                AccessExpiryUtc = ToUtcIso(storedToken?.ExpiresAt),
                AccountDisplayName = storedToken?.DisplayName
            },
            CompletedAt = DateTime.UtcNow
        };
    }

    private static string? ToUtcIso(DateTime? value)
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
            Id = request.Id, Success = true, Data = games, CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleGetCdnInfoAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();

        // Optional: filter by specific appIds
        List<string>? appIds = null;
        if (request.Parameters?.TryGetValue("appIds", out var appIdsJson) == true)
        {
            appIds = JsonSerializer.Deserialize<List<string>>(appIdsJson);
        }

        var result = await _api!.GetCdnInfoAsync(appIds, cancellationToken);
        return new CommandResponse
        {
            Id = request.Id, Success = true, Data = result, CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleGetSelectedApps(CommandRequest request)
    {
        EnsureLoggedIn();
        var selected = _api!.GetSelectedApps();

        return new CommandResponse
        {
            Id = request.Id, Success = true, Data = selected, CompletedAt = DateTime.UtcNow
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
                Id = request.Id, Success = false, Error = "appIds parameter required", CompletedAt = DateTime.UtcNow
            };
        }

        var appIds = JsonSerializer.Deserialize(appIdsJson, DaemonSerializationContext.Default.ListString);
        if (appIds != null && appIds.Count > 0)
        {
            _api!.SetSelectedApps(appIds);
            _progress.OnLog(LogLevel.Info, $"Set {appIds.Count} selected apps");
        }

        return new CommandResponse
        {
            Id = request.Id, Success = true, Message = "Apps selected", CompletedAt = DateTime.UtcNow
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

    private Task<CommandResponse> HandlePrefillAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();

        if (_isPrefilling)
        {
            return Task.FromResult(new CommandResponse
            {
                Id = request.Id, Success = false, Error = "A prefill is already in progress", CompletedAt = DateTime.UtcNow
            });
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

        _prefillCts?.Dispose();
        _prefillCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _isPrefilling = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _api!.PrefillAsync(options, _prefillCts.Token);

                if (result.Success)
                    _progress.OnLog(LogLevel.Info, "Prefill completed successfully");
                else
                    _progress.OnLog(LogLevel.Warning, $"Prefill completed with errors: {result.ErrorMessage}");
            }
            catch (OperationCanceledException)
            {
                _progress.OnLog(LogLevel.Info, "Prefill cancelled by user");
                // Emit a terminal socket event so the backend always clears IsPrefilling.
                // Reuse the existing terminal "error" state; the backend terminal funnel is idempotent.
                _progress.OnError("Prefill cancelled by user");
            }
            catch (Exception ex)
            {
                _progress.OnLog(LogLevel.Error, $"Prefill failed: {ex.Message}");
                // Emit a terminal socket event so the backend always clears IsPrefilling
                // even when PrefillAsync throws without emitting one itself.
                _progress.OnError($"Prefill failed: {ex.Message}", ex);
            }
            finally
            {
                _isPrefilling = false;
                _prefillCts?.Dispose();
                _prefillCts = null;
            }
        }, _prefillCts.Token);

        return Task.FromResult(new CommandResponse
        {
            Id = request.Id, Success = true, Message = "Prefill started", CompletedAt = DateTime.UtcNow
        });
    }

    private CommandResponse HandleClearCache(CommandRequest request)
    {
        var result = EpicPrefillApi.ClearCache();

        return new CommandResponse
        {
            Id = request.Id, Success = result.Success, Data = result, Message = result.Message, CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleGetCacheInfo(CommandRequest request)
    {
        var info = EpicPrefillApi.GetCacheInfo();

        return new CommandResponse
        {
            Id = request.Id, Success = info.Success, Data = info, Message = info.Message, CompletedAt = DateTime.UtcNow
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

    private CommandResponse HandleShutdown(CommandRequest request)
    {
        CleanupApiInstance();

        return new CommandResponse
        {
            Id = request.Id, Success = true, Message = "Shutdown complete", CompletedAt = DateTime.UtcNow
        };
    }

    private void EnsureLoggedIn()
    {
        if (!_isLoggedIn || _api == null || !_api.IsInitialized)
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

    public void Dispose()
    {
        if (_disposed) return;

        _cts.Cancel();
        _loginCts?.Dispose();
        _prefillCts?.Dispose();
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
    private sealed class SocketProgress : IPrefillProgress
    {
        public SocketServer? SocketServer { get; set; }
        private DateTime _lastProgressBroadcast = DateTime.MinValue;
        private static readonly TimeSpan BroadcastThrottle = TimeSpan.FromMilliseconds(250);

        public void OnLog(LogLevel level, string message)
        {
            var prefix = level switch
            {
                LogLevel.Debug => "[DEBUG]",
                LogLevel.Info => "[INFO]",
                LogLevel.Warning => "[WARN]",
                LogLevel.Error => "[ERROR]",
                _ => "[LOG]"
            };
            Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {prefix} {message}");
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
            OnLog(LogLevel.Info, $"{progress.AppName}: {progress.PercentComplete:F1}% - {speedStr} - {downloadedStr} / {totalStr}");

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

        private void BroadcastProgress(PrefillProgressUpdate update)
        {
            if (SocketServer == null) return;

            var progressEvent = new ProgressEvent(update);
            _ = SocketServer.BroadcastProgressAsync(progressEvent);
        }
    }
}
