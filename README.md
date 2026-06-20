# Kinshout API

ASP.NET Core **.NET 10** Web API for [kinshout.vercel.app](https://kinshout.vercel.app).

## Stack

- **C# / .NET 10**
- **SQL Server** + Entity Framework Core
- **JWT** authentication
- **Google Sign-In** and **Apple Sign-In**
- **OpenAI** for advert categorization, dynamic categories, and semantic search

## Prerequisites

1. [.NET 10 SDK](https://dotnet.microsoft.com/download)
2. Docker (for local SQL Server) or Azure SQL / SQL Server instance

## Quick start

```bash
# 1. Start SQL Server
cd api
docker compose up -d

# 2. Configure secrets (copy and edit)
#    api/Kinshout.Api/appsettings.Development.json
#    - ConnectionStrings:DefaultConnection
#    - Jwt:SecretKey (min 32 chars)
#    - OpenAI:ApiKey
#    - OAuth:Google:ClientId / ClientSecret
#    - OAuth:Apple:ClientId / TeamId / KeyId / PrivateKey

# 3. Run API
cd Kinshout.Api
dotnet restore
dotnet run
```

API runs at `https://localhost:7xxx` (see launchSettings). Swagger UI: `/swagger` in Development.

## Tests

```bash
cd api
dotnet test
```

Unit tests live in `Kinshout.Api.Tests` (xUnit + Moq + EF Core InMemory).

## Environment variables (production)

| Key | Description |
|-----|-------------|
| `ConnectionStrings__DefaultConnection` | SQL Server connection string |
| `Jwt__SecretKey` | JWT signing key (32+ chars) |
| `OpenAI__ApiKey` | OpenAI API key |
| `OAuth__Google__ClientId` | Google OAuth client ID |
| `OAuth__Google__ClientSecret` | Google OAuth client secret |
| `OAuth__Apple__ClientId` | Apple Services ID |
| `OAuth__Apple__TeamId` | Apple Team ID |
| `OAuth__Apple__KeyId` | Apple Sign In key ID |
| `OAuth__Apple__PrivateKey` | Apple `.p8` private key (use `\n` for newlines) |
| `Cors__AllowedOrigins__0` | `https://kinshout.vercel.app` |

## Two-layer security

Kinshout uses **two separate auth layers**:

| Layer | Who | Purpose | Token |
|-------|-----|---------|-------|
| **1. Frontend client** | Your web app (`kinshout-web`) | Only authorized frontends can call the API | `X-Kinshout-Client-Token` |
| **2. End user** | App visitors | Google/Apple sign-in for posting, profile, etc. | `Authorization: Bearer` |

Random Google/Apple users **cannot** call the API directly — they must go through your registered frontend first.

### Layer 1 — Frontend client auth

1. App loads → `POST /api/auth/client` with `clientId` + `clientSecret`
2. API returns client JWT → browser stores in `sessionStorage`
3. Browser calls Kinshout API with `X-Kinshout-Client-Token` on every request

**Frontend config** (must match `ClientAuth__KinshoutWebSecret` on the API):

| Variable | Example |
|----------|---------|
| `KINSHOUT_API` / `VITE_KINSHOUT_API` | `https://your-api.azurewebsites.net` |
| `KINSHOUT_CLIENT_ID` / `VITE_KINSHOUT_CLIENT_ID` | `kinshout-web` |
| `KINSHOUT_CLIENT_SECRET` / `VITE_KINSHOUT_CLIENT_SECRET` | same as `ClientAuth__KinshoutWebSecret` |

**API server env:**

| Variable | Example |
|----------|---------|
| `ClientAuth__KinshoutWebSecret` | strong random string |

**Local dev:** run `server.py` on `:5173` + API on `:5280`. Dev secret defaults in `appsettings.Development.json` and `api-client.js`.

See `.env.example`.

### Layer 2 — User auth (after layer 1)

1. User taps Google/Apple on your app
2. Frontend sends ID token to `POST /api/auth/google` or `/apple` **with client token header**
3. API returns **user JWT** bound to that frontend client
4. Protected routes require user JWT with `typ: user`

### Registered clients (`ApiClients` table)

Seeded client: `kinshout-web`  
Allowed origins: `https://kinshout.vercel.app`, `http://localhost:5173`

### Frontend headers

```http
X-Kinshout-Client-Token: <client-jwt>     # every /api/* call
Authorization: Bearer <user-jwt>          # logged-in user actions
```

See `api-client.js` — calls `ensureClientAuth()` automatically before each request.

## API endpoints

### Auth
| Method | Path | Client token | User JWT | Description |
|--------|------|:------------:|:--------:|-------------|
| POST | `/api/auth/client` | — | — | Authorize frontend app |
| POST | `/api/auth/google` | ✓ | — | User sign-in (Google) |
| POST | `/api/auth/apple` | ✓ | — | User sign-in (Apple) |
| GET | `/api/auth/me` | ✓ | ✓ | Current user profile |

### Adverts
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/adverts` | — | List adverts |
| GET | `/api/adverts/{id}` | — | Advert detail |
| POST | `/api/adverts` | JWT | Create advert (OpenAI categorizes + may create category) |

### Categories
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/categories` | — | List all categories (including AI-created) |

### Search & AI
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| POST | `/api/search` | — | Semantic search (adverts + discussions) |
| GET | `/api/search?q=...&tab=all` | — | Search via query string |
| POST | `/api/categorize` | — | Preview categorization without posting |

### Discussions
| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/discussions` | — | List discussions |
| GET | `/api/discussions/{id}` | — | Discussion with replies |
| POST | `/api/discussions` | JWT | Create discussion |
| POST | `/api/discussions/{id}/replies` | JWT | Add reply |

## OAuth setup

### Google
1. [Google Cloud Console](https://console.cloud.google.com/) → APIs & Services → Credentials
2. Create OAuth 2.0 Client ID (Web application)
3. Authorized JavaScript origins: `https://kinshout.vercel.app`, `http://localhost:5173`
4. Frontend obtains ID token via Google Identity Services, sends to `POST /api/auth/google`

### Apple
1. [Apple Developer](https://developer.apple.com/) → Sign in with Apple
2. Create Services ID, configure domains and return URLs
3. Create Sign In key (.p8)
4. Frontend obtains identity token, sends to `POST /api/auth/apple`

## OpenAI behavior

**On advert publish** (`POST /api/adverts`):
- OpenAI **moderation** blocks sexual/adult text and non-genuine web-sourced photos
- Analyzes text and assigns an existing category or creates a new one dynamically
- Extracts title, price, location, tags, intent (offre/demande)

**On photo upload** (`POST /api/uploads/images`):
- Each image is analyzed by OpenAI vision before being stored
- Stock photos, watermarked images, and sexual content are rejected

**On search** (`POST /api/search`):
- Returns relevant adverts **and** discussions based on semantic meaning of the query
- Falls back to keyword matching if OpenAI is unavailable

## Frontend integration

Set the API URL and client credentials in the web app:

```html
<script>
  window.KINSHOUT_API = "https://your-api.azurewebsites.net";
  window.KINSHOUT_CLIENT_ID = "kinshout-web";
  window.KINSHOUT_CLIENT_SECRET = "your-client-secret";
</script>
```

Or use `api-client.js` (see project root) with `VITE_*` env vars at build time.

## Deploy (Azure example)

```bash
az sql server create ...
az webapp create --runtime "DOTNET:10"
az webapp config appsettings set --settings ConnectionStrings__DefaultConnection="..." OpenAI__ApiKey="..."
dotnet publish -c Release
```

## CORS

Production origin `https://kinshout.vercel.app` is preconfigured in `appsettings.json`.
