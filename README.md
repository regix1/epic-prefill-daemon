# EpicPrefill

[![License: MIT](https://img.shields.io/badge/License-MIT-blue?style=for-the-badge)](LICENSE)
[![Discord](https://dcbadge.vercel.app/api/server/BKnBS4u?style=for-the-badge)](https://discord.com/invite/BKnBS4u)

Epic Games prefill daemon for [LANCache](https://lancache.net/) — a companion to [**LANCache Manager**](https://github.com/regix1/lancache-manager), which coordinates the prefill providers.

It pre-downloads your owned Epic Games content **through your lancache** so the cache is warm before installing — the real install then comes from your LAN at full speed. No data is written to disk; bytes are streamed through the cache and discarded.

## How it works

Sign in first via Epic's OAuth flow:

1. **Authenticate** — log in with your Epic Games account.
2. **Select titles** — choose which owned apps to prefill via an interactive menu.
3. **Resolve** — each selected app's manifest and CDN URLs are resolved from Epic's content system.
4. **Prefill** — each app's content is fetched through the lancache and discarded, warming the cache.

Previously prefilled apps are tracked by version; only changed or new content is re-downloaded on subsequent runs.

## Requirements

- A running [LANCache](https://lancache.net/) with the **`epicgames`** cache-domain group enabled (from [uklans/cache-domains](https://github.com/uklans/cache-domains)).
- An Epic Games account that owns the games you want to prefill.
- Docker, or the [.NET 8 SDK](https://dotnet.microsoft.com/) to build from source.

## Running it

EpicPrefill runs as a **daemon** driven by [**LANCache Manager**](https://github.com/regix1/lancache-manager) — set it up there alongside the other prefill providers.

To build and run standalone:

```bash
dotnet build EpicPrefill/EpicPrefill.csproj -c Release
dotnet run  --project EpicPrefill/EpicPrefill.csproj -c Release
```

## Support & License

Questions or issues? [Open an issue](https://github.com/regix1/epic-prefill-daemon/issues), or find the LANCache community on the [LanCache.NET Discord](https://discord.com/invite/BKnBS4u).

Licensed under the MIT License (see [LICENSE](LICENSE)); a fork of the lancache-prefill tools by Tim Pilius ([@tpill90](https://github.com/tpill90)).

If these tools have been useful, you can support the original author on [ko-fi](https://ko-fi.com/Y8Y5DWGZN) or support this fork via [buy me a coffee](https://www.buymeacoffee.com/regix).
