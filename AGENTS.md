# Afterglow — agent notes

Afterglow is a **Steam-like** desktop library for AVN Hub. If a change would make Steam feel worse (resetting the library view, decoding every cover at full resolution, dumping every tag into the sidebar), do not ship it.

## Steam-like rules (non-negotiable)

- **Persist the library session.** Card size, grid vs list, sort, play-status filter, and installed-only must round-trip through `ui_prefs` and apply on the next launch. Users should never re-set “Installed only” or card size after a restart.
- **Display settings are not filters.** Grid/list and card size live in a dedicated Display section that stays on screen. They must not sit under the tag cloud, and the user must not scroll the sidebar to change card size.
- **Collapse tags by default.** Sidebar tag filters show ~5 chips plus a `+N more` / Show less badge (same idea as card `TagBadges`). Selected tags stay visible while collapsed. Cards already limit tags — the filter rail must too.
- **Cache media on disk, not in RAM.** Covers live in `%AppData%/Afterglow/media-cache`. Library and browse decode **thumbnails** (`DecodeToWidth`), not native 1080p/AVIF bitmaps. Do not keep every GIF frame or hover screenshot decoded for the whole grid. Idle library RAM should stay in the hundreds of MB, not multiple GB.
- **Revalidate, don’t refetch blindly.** On library load, show the local thumb immediately when the cover URL is unchanged. If hub `cover_url` or `game.updated_at` changed (web custom cover, refresh metadata, etc.), discard that cache entry and pull the new file. Same URL + same version = disk hit, no download.
- **Paint the grid first.** Cards appear before covers finish. Load thumbs with a small concurrency cap. Hover galleries load on hover, not at refresh.
- **Hub sort is the source of truth.** Pass `playtime_desc` (and the other library sorts) to `GET /api/v1/library`. Do not invent Afterglow-only sorts the hub cannot honor.

## Hub API

Contract: sibling repo `../avn-hub/openapi/openapi.yaml`. Catalog pages return `CatalogPage`, not a bare array. Prefer screenshot `cached_url`.
