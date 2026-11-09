# Developer-controlled imported discussion seeding

`DiscussionSeeding__Enabled` is the single developer-owned switch in Azure App Service environment variables. `false` stops generation and publication; `true` starts the worker after the application restarts. Azure environment values override checked-in settings. The base default is false. Production configuration enables the feature as requested by the owner.

There is no moderator login, moderator-created campaign or per-feed approval requirement. The worker initializes its internal database switches and creates an internal run automatically on its first enabled tick. Replicas share the same database lock and reuse the active run. An automatic run records `Guid.Empty` as the configuration/system actor; it does not invent a moderator account.

All public imported discussions are eligible across existing source providers, including historical imports without source metrics or source comments. When actual source comments are available, generation uses their themes and checks for copying. Otherwise, the model uses only the title and post content. It must not claim to have watched or heard media from a text description. A stable initial revision prevents repeated generation for historical imports; subsequent genuine source-metric revisions can produce further comments.

Existing exclusions remain: native Kinoiserie posts, private/inactive communities, blocked content, politics and disallowed content are excluded or rejected. Seeding stops on a discussion after the configured organic-reply threshold. Generation may abstain when there is too little useful context. Eligibility is not a promise that every imported post will receive a comment.

Default limits: 100 queued comments/day, 8 per post per run, 4 jobs/tick, 60-second ticks, 60–600-second randomized delays, and stop after 3 organic replies. Actual publication times are checked again to prevent overdue jobs publishing as a batch. The daily queue limit is not an API-attempt spending cap. Accounts are created on demand from a 12-persona pool per run and reused; they cannot log in.

The database flags are internal durable coordination/rollback state, not moderator-managed product settings. They are initialized from the global setting on worker startup. The combined-like display is gated by both the global setting and internal state; source counts and organic likes remain separate in storage.

## Developer operation

- Enable: set `DiscussionSeeding__Enabled=true` in the production API App Service and restart/apply settings. Existing API OpenAI credentials and enabled moderation are required. No new moderator session or API request is required.
- Disable: set `DiscussionSeeding__Enabled=false` on every API instance and restart/apply settings. Existing generated content remains visible; queued jobs stop publishing.
- Remove generated activity: first disable the global setting on all instances, then run the operator tool described in discussion-seeding-rollback.md.
- Re-enable after a rollback: deliberately set the global switch true and restart. A new internal run is created if all earlier runs were stopped. Old cancelled jobs and hidden comments are not resurrected.

The former `/api/editorial/discussion-seeding/*` endpoints and moderator desk panel have been removed. No unauthenticated replacement mutation endpoint exists. Operational database access remains restricted to the developer's infrastructure credentials.

## Verification limits

The first live six-fixture batch failed politics abstention and showed overly verbose writing. The prompt and code-level political rejection were improved. The revised generator still needs a fresh live qualitative review, including Lingala and actual source material. Automated tests do not establish language quality. Reports are under review/ and contain no API secrets.
