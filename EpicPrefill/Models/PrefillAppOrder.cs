namespace EpicPrefill.Models
{
    /// <summary>
    /// Selection/ordering strategy for a prefill run, mapped from the socket preset parameters
    /// (all / recent / top). Keeps the wire-level booleans out of the manager and download layers.
    /// </summary>
    public enum PrefillAppOrder
    {
        /// <summary>Prefill the previously-selected apps list. Default when no preset is requested.</summary>
        Selected = 0,

        /// <summary>Prefill every owned game (the "All" preset).</summary>
        AllOwned,

        /// <summary>
        /// The "Recent" preset. Epic's public API exposes no per-title last-played timestamp, so a
        /// genuine recently-played ordering is impossible. This mode is treated as gracefully
        /// unsupported: the daemon logs the reason and falls back to prefilling all owned games
        /// rather than shipping a misleading ordering.
        /// </summary>
        Recent,

        /// <summary>
        /// The "Top" preset: owned games ordered by cumulative account playtime, most-played first,
        /// sourced from Epic's library-service playtime endpoint.
        /// </summary>
        Top
    }
}
