namespace EpicPrefill.Handlers
{
    //TODO document
    /// <summary>
    /// https://dev.epicgames.com/docs/web-api-ref/authentication
    /// </summary>
    //TODO fix this warning
    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Fix this.")]
    public sealed class UserAccountManager
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly IEpicAuthProvider _authProvider;
        private readonly HttpClient _client;

        //TODO I'm not sure where this link comes from.  Can I possibly setup my own?
        private const string LoginUrl = "https://legendary.gl/epiclogin";
        private const string OauthHost = "account-public-service-prod03.ol.epicgames.com";

        private const string BasicUsername = "34a02cf8f4414e29b15921876da36f9a";
        private const string BasicPassword = "daafbccc737745039dffe53d94fc76cf";

        private const int MaxRetries = 3;

        //TODO this should probably be private
        public OauthToken OauthToken { get; set; }

        private UserAccountManager(IAnsiConsole ansiConsole, IEpicAuthProvider authProvider)
        {
            _ansiConsole = ansiConsole;
            _authProvider = authProvider;
            _client = new HttpClient
            {
                Timeout = AppConfig.DefaultRequestTimeout
            };
            _client.DefaultRequestHeaders.Add("User-Agent", AppConfig.DefaultUserAgent);
        }

        public async Task LoginAsync()
        {
            if (!OauthTokenIsExpired())
            {
                _ansiConsole.LogMarkupLine("Reusing existing auth session...");
                return;
            }

            int retryCount = 0;
            while (OauthTokenIsExpired() && retryCount < MaxRetries)
            {
                try
                {
                    var requestParams = await BuildRequestParamsAsync();

                    var authUri = new Uri($"https://{OauthHost}/account/api/oauth/token");
                    using var request = new HttpRequestMessage(HttpMethod.Post, authUri);
                    request.Headers.Authorization = BasicAuthentication.ToAuthenticationHeader(BasicUsername, BasicPassword);
                    request.Content = new FormUrlEncodedContent(requestParams);

                    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    using var responseStream = await response.Content.ReadAsStreamAsync();
                    OauthToken = await JsonSerializer.DeserializeAsync(responseStream, SerializationContext.Default.OauthToken);

                    Save();
                }
                catch (Exception e)
                {
                    // If the login failed due to a bad request then we'll clear out the existing token and try again
                    if (e is HttpRequestException)
                    {
                        OauthToken = null;
                    }
                }

                retryCount++;
            }

            if (retryCount == 3)
            {
                throw new EpicLoginException("Unable to login to Epic!  Try again in a few moments...");
            }
        }

        private async Task<Dictionary<string, string>> BuildRequestParamsAsync()
        {
            // Handles the user logging in for the first time, as well as when the refresh token has expired, or when an unknown failure has occurred
            if (OauthToken == null || RefreshTokenIsExpired())
            {
                if (RefreshTokenIsExpired())
                {
                    _ansiConsole.LogMarkupLine(LightYellow("Refresh token has expired!  EpicPrefill will need to login again..."));
                }

                _ansiConsole.LogMarkupLine("Requesting authorization code via auth provider...");

                var authCode = await _authProvider.GetAuthorizationCodeAsync(LoginUrl);

                return new Dictionary<string, string>
                {
                    { "token_type", "eg1" },
                    { "grant_type", "authorization_code" },
                    { "code", authCode }
                };
            }

            // Handles a user being logged in, but the saved token has expired
            _ansiConsole.LogMarkupLine("Auth token expired.  Requesting refresh auth token...");
            return new Dictionary<string, string>
            {
                { "token_type", "eg1" },
                { "grant_type", "refresh_token" },
                { "refresh_token", OauthToken.RefreshToken }
            };
        }

        //TODO this should probably not be referenced externally
        public bool OauthTokenIsExpired()
        {
            if (OauthToken == null)
            {
                return true;
            }

            // Tokens are valid for 8 hours, but we're adding a buffer of 10 minutes to make sure that the token doesn't expire while we're using it.
            return DateTimeOffset.UtcNow.DateTime > OauthToken.ExpiresAt.AddMinutes(-10);
        }

        private bool RefreshTokenIsExpired()
        {
            if (OauthToken == null)
            {
                return true;
            }

            // Tokens are valid for 8 hours, but we're adding a buffer of 10 minutes to make sure that the token doesn't expire while we're using it.
            return DateTimeOffset.UtcNow.DateTime > OauthToken.RefreshTokenExpiresAt;
        }

        //TODO document
        public static UserAccountManager LoadFromFile(IAnsiConsole ansiConsole, IEpicAuthProvider authProvider)
        {
            if (!File.Exists(AppConfig.AccountSettingsStorePath))
            {
                return new UserAccountManager(ansiConsole, authProvider);
            }

            using var fileStream = File.Open(AppConfig.AccountSettingsStorePath, FileMode.Open, FileAccess.Read);

            var accountManager = new UserAccountManager(ansiConsole, authProvider);
            accountManager.OauthToken = JsonSerializer.Deserialize(fileStream, SerializationContext.Default.OauthToken);
            return accountManager;
        }

        private void Save()
        {
            using var fileStream = File.Open(AppConfig.AccountSettingsStorePath, FileMode.Create, FileAccess.Write);
            JsonSerializer.Serialize(fileStream, OauthToken, SerializationContext.Default.OauthToken);
        }

    }
}