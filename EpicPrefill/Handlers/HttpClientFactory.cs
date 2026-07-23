namespace EpicPrefill.Handlers
{
    //TODO document
    public class HttpClientFactory
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly UserAccountManager _userAccountManager;
        private readonly HttpMessageHandler? _handler;

        public HttpClientFactory(IAnsiConsole ansiConsole, UserAccountManager userAccountManager)
        {
            _ansiConsole = ansiConsole;
            _userAccountManager = userAccountManager;
        }

        internal HttpClientFactory(
            IAnsiConsole ansiConsole,
            UserAccountManager userAccountManager,
            HttpMessageHandler handler)
            : this(ansiConsole, userAccountManager)
        {
            _handler = handler;
        }

        //TODO document
        public async Task<HttpClient> GetHttpClientAsync(CancellationToken cancellationToken = default)
        {
            if (_userAccountManager.OauthTokenIsExpired())
            {
                await _userAccountManager.LoginAsync(cancellationToken);
            }

            var client = _handler == null
                ? new HttpClient()
                : new HttpClient(_handler, disposeHandler: false);
            client.Timeout = AppConfig.DefaultRequestTimeout;
            client.DefaultRequestHeaders.Add("Authorization", $"bearer {_userAccountManager.OauthToken.AccessToken}");
            client.DefaultRequestHeaders.Add("User-Agent", AppConfig.DefaultUserAgent);
            return client;
        }
    }
}
