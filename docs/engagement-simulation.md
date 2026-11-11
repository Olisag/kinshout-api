# Production engagement simulation

Production enables the simulation extension to the existing discussion seeder. Targets are approximately 500 comments and 4,000 likes per UTC day, with a smooth time-of-day distribution peaking near 18:00 Kinshasa. Targets are not guarantees: moderation, abstention, distinct-like saturation, eligible post supply, genuine-reply cutoff, budget, downtime and source quality can reduce output. There is no campaign expiration, 28-day timer, automatic stop, automatic rollback or automatic deletion. Missed days are not backfilled. Late jobs are subject to today's publication allowance and global spacing; publication timestamps are never backdated.

Each active run reuses up to 5,000 persisted profiles, including existing run personas. Profiles have pseudonymous community handles, have no password, and cannot receive user JWTs. The stored run identifier tracks their simulated origin. Reply DTOs also expose `isSimulated`. Likes are stored only in `DiscussionSimulatedLikes`, never in `LikedDiscussions` or `Discussions.LikeCount`. The backend adds a read-only `/api/discussions/{id}/simulation` disclosure, and `/api/discussions/engagement-display/simulation` status. Both retain normal client authentication. Existing discussion endpoints include visible simulated interactions in the displayed totals. Use the separate simulation counts for reporting genuine traction.

Source likes/views and genuine Kinoiserie likes/replies contribute to a freshness-decayed weighted selection across the most recent 500 eligible imports, sampled among the top 100. Simulated replies do not increase the ranking score; simulated likes never touch the genuine counters. Genuine native posts, blocked/political posts, private or inactive communities, and posts at the existing genuine-reply cutoff remain excluded. Originality, abstention, production moderation at generation and publication, and rechecking eligibility under the shared database lock remain. The extension allows additional distinct comments on an unchanged source snapshot, subject to daily quotas and duplicate checks; the legacy revision-only seeder remains available with `SimulationEnabled=false`.

Public communities have topic names and a description disclosing automated activity. Starter threads carry a `Simulation ·` label. The API resolves legacy community URLs to the current slugs. Up to two of six curated themes open per UTC week. The worker rotates moderated French discussion prompts across those communities and may comment and like their threads. The starter prompts use curated text without a generation call.

## Configuration and cost

`DiscussionSeeding` settings (Azure values override JSON):

| Setting | Default |
| --- | ---: |
| SimulationEnabled | false globally; true in Production |
| ProfileCount | 1500 globally, 5000 in Production (maximum 5000) |
| DailyComments | 150 globally, 500 in Production |
| DailyLikes | 1200 globally, 4000 in Production |
| DailyDiscussionPosts | 0 globally, 10 in Production |
| WeeklyPublicCommunities | 0 globally, 2 in Production |
| MaxLikesPerTick | 4 globally, 6 in Production |
| TickSeconds | 60 |
| MinDelaySeconds / MaxDelaySeconds | 60 / 600 |
| MaxDailyGenerationAttempts | 300 globally, 800 in Production |
| EstimatedCostPerAttemptUsd | 0.01 |
| DailyCostLimitUsd | 3.00 globally, 8.00 in Production |

A daily reservation is committed before each inference call, including failed or ambiguous attempts. One attempt globally per minimum interval and the attempt/budget ceilings bound retries. This is a conservative **estimated budget**, not an invoice or hard provider-dollar cap: calibrate the per-attempt estimate against the configured model/pricing. Inference logs record actual provider token usage and model; daily database rows retain attempts/reserved estimate and heartbeats. Output is bounded to 200 completion tokens, context is truncated, and later ticks retry failures within the same budget. SQL execution strategies retry transactional mutations with fresh contexts. Unique persona/post likes, idempotency keys, deterministic reply IDs and a shared serializable control-row lock protect publication and rollback across replicas.

`OpenAI__ModerationEnabled=true` and an API key are mandatory. `OpenAI__DiscussionModerationEnabled` independently controls ordinary discussion submissions and is **never changed by the new deployment/operations workflows**. The existing global switch workflow still requires seeding moderation. Always On is required for the in-process scheduler; the deployment verifier enables it and verifies an advancing durable heartbeat. Do not run a second external scheduler for the same work.

## Independent operator controls

Use **Production engagement simulation controls and reporting** in GitHub Actions on **Master**, or the `tools/SeedingControl` console with a securely supplied `ConnectionStrings__DefaultConnection`. The workflow's database operations need network access to production SQL; it never prints credentials or changes SQL firewall rules. `verify` uses the authenticated API and requires no SQL connection from the runner.

| Action | Result |
| --- | --- |
| report | Separate genuine users/comments/likes from simulated counts, including UTC daily totals and estimated budget history. Never use aggregate `ReplyCount` as genuine traction. |
| verify | Check deployed SHA, moderation/global configuration, Always On and advancing scheduler heartbeat. |
| pause | Disable generation/publication durably; retain pending jobs, published records and active run. |
| resume | Explicitly enable durable controls; reuse the active run, or deliberately create a new run after a terminal stop. Global configuration must also be enabled. |
| stop | Terminally stop runs and cancel pending jobs; retain published comments, likes and profiles. |
| preview | Count records affected by an explicit rollback. |
| stop-and-hide | Explicit rollback: stop runs/cancel jobs, hide tagged comments, profiles and simulation likes. Archive simulated threads with no genuine participation and simulated communities with no genuine members or remaining discussions. Retain provenance and all genuine records. |

Pause/stop/rollback survive process restarts. Initialization bootstraps only a fresh installation with no operator action; it never resumes paused/stopped runs. Rollback uses the same shared write lock as publishing and subtracts only newly hidden comments from reply counters, so retries cannot double-subtract or restore an outdated snapshot. Genuine like counters are never changed. A newly resumed terminal run creates its own profiles; old rolled-back records stay hidden.

The production workflow first validates rollback/concurrency/schema behavior against an isolated SQL Server service, then runs the complete API regression suite before publishing the API project. It verifies the running binary's Git commit in `/api/health` and an advancing heartbeat after deployment. A completed Azure upload alone is not deployment verification. First interactions normally appear on the first eligible minute tick (likes) and within roughly 2–12 minutes for approved comments, subject to the cumulative daily allowance and safeguards.
