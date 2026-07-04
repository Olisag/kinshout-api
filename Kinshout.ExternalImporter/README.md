# Kinshout External Importer

Daily importer that pulls recent and popular external advert feeds, normalizes them, and posts them to:

```http
POST /api/imports/adverts
X-Kinshout-Import-Key: <Import:SecretKey>
```

## Scraping mode

The importer can scrape marketplace pages directly. Enable a scraper provider in `appsettings.json`:

| Type | Source | Notes |
|------|--------|-------|
| `mediacongo-scraper` | mediacongo.net | HTML listing + detail pages |
| `zwandako-scraper` | zwandako.com | RSS feed + search pages + JSON-LD detail enrichment |
| `jiji-scraper` | jiji.cd | **Cloudflare-protected** — requires browser `Cookie` via `${JIJI_COOKIE}` (see below) |
| `apify-facebook-scraper` | Facebook Marketplace via [Apify](https://apify.com/apify/facebook-marketplace-scraper) | Requires `apifyToken` / `${APIFY_TOKEN}` |

Scraper settings on each provider:

```json
{
  "maxPages": 3,
  "fetchDetails": true,
  "requestDelayMs": 400,
  "accessToken": "${FACEBOOK_ACCESS_TOKEN}"
}
```

Use `--dry-run` first to inspect counts without posting to Kinshout.

### Removed listing detection

When `schedule.detectRemovedListings` is `true` (default), each scraper reports every listing ID seen during its crawl. After fetch, the importer compares those IDs to Kinshout’s known external adverts for that provider and posts `status: "removed"` for any missing IDs so the API hard-deletes stale rows.

A safety gate skips mass deletion when the crawl looks incomplete (seen count is less than 25% of known count, when there are more than 10 known adverts). MediaCongo and Zwandako track IDs from listing pages before skipping known adverts for detail fetch.

### Facebook Marketplace via Apify

Get an API token from [Apify Console → Integrations](https://console.apify.com/account/integrations), then enable:

```json
{
  "name": "Facebook Marketplace Kinshasa (Apify)",
  "provider": "facebook_marketplace",
  "type": "apify-facebook-scraper",
  "enabled": true,
  "apifyToken": "${APIFY_TOKEN}",
  "apifyActorId": "apify/facebook-marketplace-scraper",
  "marketplaceLocation": "kinshasa",
  "searchQueries": ["appartement", "maison", "immobilier"],
  "resultsLimit": 75,
  "fetchDetails": true,
  "actorTimeoutSeconds": 300
}
```

The provider starts the Apify actor, waits for completion, and maps the dataset to Kinshout import DTOs. One actor run covers all `searchQueries` in a single batch. Pricing is pay-per-listing on Apify (~$2.60/1,000 on the official actor).

Run `dotnet run -- --once --dry-run` to inspect counts before posting.

### Jiji RDC (jiji.cd)

Jiji Congo is behind **Cloudflare**. Plain HTTP requests get a 403 challenge — the same blocker you see without a browser. Apify's [Jiji Listings Scraper](https://apify.com/piotrv1001/jiji-listings-scraper) only supports Nigeria, Kenya, Ghana, Tanzania, and Uganda today; a test against `jiji.cd` returned **0 listings**.

**Working approach:** copy a browser session cookie into `.env`:

1. Open [jiji.cd/kinshasa/immobilier](https://jiji.cd/kinshasa/immobilier) in Chrome.
2. Complete the Cloudflare check if shown (you do not need to log in).
3. Open DevTools → **Network** → reload → click the document request → **Headers** → copy the full **Cookie** value.
4. Add to repo-root `.env`:

```bash
JIJI_COOKIE="cf_clearance=...; __cf_bm=...; ..."
```

5. Enable the provider in `appsettings.json` (`"enabled": true` on the Jiji block).
6. Dry run:

```bash
dotnet run -- --once --dry-run
```

**Important:** `cf_clearance` cookies expire after a few hours to a few days. Refresh `JIJI_COOKIE` when Jiji imports start failing again. For unattended daily runs, you would need a headless browser (Playwright) or a paid proxy/scraper service once one supports `jiji.cd`.

Configured provider keys:

- `facebook_marketplace`
- `mediacongo`
- `zwandako`
- `jiji_rdc`
- `other`

Legacy feed types (`json-feed`, `rss`) remain supported for partner exports.

## Run locally

```bash
cd api/Kinshout.ExternalImporter
cp appsettings.example.json appsettings.json
# edit appsettings.json and enable/configure providers
dotnet run -- --once
```

Dry run without posting to Kinshout:

```bash
dotnet run -- --once --dry-run
```

Use another config file:

```bash
dotnet run -- --config appsettings.production.json --once
```

Environment overrides:

| Variable | Description |
|----------|-------------|
| `KINSHOUT_IMPORTER_API_BASE_URL` | Kinshout API base URL, e.g. `https://kinshout-api-dev.azurewebsites.net` |
| `KINSHOUT_IMPORTER_IMPORT_KEY` | Value sent as `X-Kinshout-Import-Key` |
| `KINSHOUT_IMPORTER_IMPORT_PATH` | Defaults to `/api/imports/adverts` |
| `KINSHOUT_IMPORTER_BATCH_SIZE` | Import batch size |
| `KINSHOUT_IMPORTER_MAX_AGE_DAYS` | Freshness window override |
| `APIFY_TOKEN` | Apify API token for Facebook Marketplace scraper |
| `JIJI_COOKIE` | Browser cookie for Jiji scraper when Cloudflare blocks requests |

## JSON feed format

JSON providers can return either an array, or an object with `adverts`, `items`, or `results`.

```json
{
  "adverts": [
    {
      "externalId": "1234567890123456",
      "externalUrl": "https://www.facebook.com/marketplace/item/1234567890123456/",
      "title": "Appartement meublé à Gombe",
      "description": "Entièrement meublé, climatisation, piscine.",
      "summary": "Appartement 2 chambres meublé à Gombe.",
      "category": "immobilier",
      "subcategory": "appartement_a_louer",
      "publishedAt": "2026-07-03T07:20:00Z",
      "price": {
        "amount": 2200,
        "currency": "USD",
        "formatted": "$2 200 / mois",
        "period": "monthly",
        "negotiable": true
      },
      "location": {
        "city": "Kinshasa",
        "commune": "Gombe",
        "formatted": "Gombe, Kinshasa"
      },
      "details": {
        "bedrooms": 2,
        "bathrooms": 2,
        "area": 95,
        "furnished": true,
        "propertyType": "apartment"
      },
      "images": [
        "https://source.example.com/image-1.jpg"
      ],
      "contact": {
        "sellerName": "John K.",
        "phone": "+243812345678",
        "whatsapp": "+243812345678",
        "preferredContact": "whatsapp",
        "isPubliclyListed": true
      },
      "tags": ["meublé", "Gombe", "piscine"],
      "intent": ["location", "long_term"],
      "modality": "rent"
    }
  ]
}
```

The worker adds the configured `provider`, `providerName`, timestamps, defaults, and posts the final Kinshout import payload.

## Freshness window

By default, the importer keeps only listings from the last 60 days:

```json
"schedule": {
  "maxAdvertAgeDays": 60
}
```

Listings with `publishedAt` older than this window are skipped before posting to Kinshout. Listings without `publishedAt` are allowed through because some partner feeds and RSS feeds do not expose a reliable published date.

## Scheduling

### GitHub Actions (dev)

Workflow: `.github/workflows/external-importer-dev-schedule.yml`

- Runs every **~15 days** at **03:00 Kinshasa** (`02:00 UTC` on the **1st and 16th** of each month)
- Also runnable manually via **Actions → External importer (dev, every 15 days) → Run workflow**
- Targets `https://kinshout-api-dev.azurewebsites.net` with `--once` and `skipExisting`

Add these repository secrets on `kinshout-api`:

| Secret | Description |
|--------|-------------|
| `KINSHOUT_IMPORTER_IMPORT_KEY` | Same value as Azure App Setting `Import__SecretKey` on `Kinshout-api-dev` |
| `APIFY_TOKEN` | Apify API token for Facebook Marketplace scraper |

```bash
gh secret set KINSHOUT_IMPORTER_IMPORT_KEY --repo Olisag/kinshout-api
gh secret set APIFY_TOKEN --repo Olisag/kinshout-api
```

### Local daemon

Set:

```json
"schedule": {
  "runOnce": false,
  "intervalHours": 360,
  "timeZoneId": "Africa/Kinshasa",
  "skipExisting": true,
  "detectRemovedListings": true,
  "maxAdvertAgeDays": 60
}
```

For a long-running local process, use `dotnet run` without `--once`. For cloud hosting, prefer GitHub Actions or Azure Container Apps Jobs with `--once`.
