# Source-driven discussion seeding

Implemented on top of the reversible foundation. Nothing is enabled or deployed
by this change, and no live OpenAI generation or social API requests were made
during validation. The API worker requires both the deployment gate and the
database switches. A missing OpenAI key or disabled moderation prevents generation.

## Data flow

1. A feed import or YouTube metric refresh stores actual source likes, views and
   any supplied reaction text separately from Kinoiserie's counters.
2. A stable revision hash changes when those source values change; identical
   snapshots do not create a second generation job. Existing source rows are
   reread under the database lock, so an older observation cannot overwrite a
   newer one even when the importer is tracking stale data.
3. The default-off worker uses the newest non-stopped run. It selects eligible
   public discussions with source reactions and a new revision.
4. The model receives bounded post context and reaction text as untrusted JSON.
   It generates an original contribution; exact/near copies, blocked topics,
   duplicate contributions and moderation rejections are discarded.
5. After inference, a transaction rechecks switches, run, revision, daily and
   post limits. It assigns a persistent invented handle and a future queue time.
6. Publication checks moderation, then reacquires the same database control lock
   used by rollback. It rechecks the job, run, persona, discussion visibility and
   organic participation before committing the reply and counter together.

The job ID is also the published reply ID. Two workers or a retry cannot create
two public comments from one job. Inference happens outside the lock, so two
workers can perform redundant inference; the second result cannot create a
duplicate job. API errors retry on a later tick; there is no copied-text fallback.

## Source coverage

- **YouTube:** existing video import and metric refresh carry actual likes/views.
  Set `KINSHOUT_IMPORTER_FETCH_SOURCE_REACTIONS=true` on the importer to fetch up
  to 20 relevant top-level comment texts using the official `commentThreads.list`
  endpoint. Its default is false. Disabled comments or unavailable access do not
  abort importing the post. Comment author identities are not retained.
- **Facebook, X and TikTok:** adapters carry available raw counts and reaction
  text actually present in their existing provider payload. They do not launch
  additional paid scraping actors. If the configured actor supplies counts only,
  there is no reaction sample and generation is skipped for that post.
- **JSON/partner feeds:** provide `viewCount`, `likeCount`, and an optional
  `sourceReactions` array of actual comment text. The API import contract accepts
  `sourceViewCount`, `sourceLikeCount`, and `sourceReactions`.

Source reaction samples are bounded, deduplicated and retained privately for
generation. An omitted sample preserves the last supplied sample; an explicit
empty array clears it. No inference is made from a comment *count* alone.

Source likes are replaced with the latest supplied total, including decreases
or zero; they are never added again on each refresh. Missing counts stay unknown.
Organic counts and existing reactions are not overwritten.

## Defaults and controls

| Setting under `DiscussionSeeding` | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Deployment gate for all generation/publication |
| `TickSeconds` | `60` | Delay between worker passes |
| `MaxJobsPerTick` | `4` | Bounded number of candidates per pass |
| `MaxCommentsPerPost` | `8` | Maximum queued contributions per run/post |
| `MaxCommentsPerDay` | `100` | Global daily queue budget, UTC |
| `StopAfterOrganicReplies` | `3` | Stop seeding once a thread has this many human replies |
| `MinDelaySeconds` | `60` | Minimum gap between scheduled comments on one thread |
| `MaxDelaySeconds` | `600` | Maximum added scheduling delay |

Limits have server-side bounds. Persisted schedules on a thread are distinct;
`CreatedAt` and `PublishedAt` record the actual publication instant. There are no
fabricated historical timestamps. Publication after downtime uses the real time
at which each pending comment is committed, not its earlier scheduled time.
The minimum gap is rechecked against actual publication times under the lock,
so an overdue backlog is not dumped into a thread in one batch.

Existing `@kinshout.system` animator comments do not count toward the organic
participation threshold. They are not retroactively retagged or hidden by this
feature. Handles use invented French/Lingala combinations and a run suffix, not
copied accounts. Generated profiles have no password, confirmed email or login.

## UAT procedure

1. Apply both backend commits and the frontend patch to the actual repositories.
   Keep `DiscussionSeeding:Enabled=false` and all database switches false during
   schema deployment. The startup repair creates new columns before entity backfills.
2. In UAT only, configure `DiscussionSeeding__Enabled=true`, an existing OpenAI
   key and enabled moderation. Opt in to YouTube source reactions on the importer
   if that is the chosen source. No production values are changed by this patch.
3. As a general-community editorial moderator, create a run via
   `POST /api/editorial/discussion-seeding/runs` with `{ "name": "UAT pilot" }`.
   Create runs with generation off: the newest open run is the worker's target.
4. PUT `/api/editorial/discussion-seeding/flags` with
   `{ "generationEnabled": true, "publishingEnabled": false, "combinedLikesEnabled": false }`.
5. Refresh a small set of source posts, then review the bounded job list at
   `GET /api/editorial/discussion-seeding/jobs?limit=50`. Inspect text, handles,
   schedules, statuses and source discussions before allowing publication.
6. Enable publication for the pilot; verify the timestamp and count behavior,
   real replies/likes, repeated refreshes, moderation and the organic stop threshold.
7. Exercise **Stop and hide** from `/mod/general/editorial`, including while an
   inference request is in flight. Confirm no new comment appears after the stop
   commits, pending jobs are cancelled and organic contributions remain.

The Next.js BFF prefixes the API examples with `/api/proxy` and supplies the
existing client/user authentication. These controls remain moderator-only.

## Combined-like display

The backend keeps `LikeCount` organic and exposes raw source counts separately
under `source`. With the database `combinedLikesEnabled` switch on, feed and
detail buttons show **Combined** with a source/Kinoiserie breakdown in the
tooltip. Like/unlike still modifies only Kinoiserie's own count.

The display switch is fetched through a public read-only endpoint and shared
across components, with one request in flight and a 30-second refresh interval.
Errors fail back to organic-only display. A stop in the same browser disables
combined display immediately; other open sessions update on their next refresh.

## Recovery and validation

Keep [the recovery guide](discussion-seeding-rollback.md). The foundation commit
`d221f36` retains the visibility filters needed to hide generated rows. Reverting
below that foundation would expose hidden rows again. Keep the additive schema
when reverting application code; the pipeline migration refuses a destructive
schema downgrade.

Normal human reply creation/deletion and remaining-reply account deletion use
atomic counter deltas under the same control lock, so stale tracked values cannot
overwrite a generated reply count or double-subtract hidden replies.

Automated tests cover source contract round trips, raw metric replacement,
default-off behavior, source samples, deduplication, schedule ordering, limits,
stop during inference/publication, competing publishers, organic adoption,
moderation rejection, stale human reply counters, schema repair and rollback.
SQLite exercises real transactions. SQL Server UAT and live source/model quality
checks are still required; automated tests use fake HTTP/model responses.

Validated locally: **551 passed, 0 failed, 21 existing skips** (572 total).
The importer builds, and the frontend production build and TypeScript check pass.
Focused lint on new modules passes. The existing discussion detail view still has
an unrelated `react-hooks/set-state-in-effect` error; no new lint suppressions
were added. Browser visual QA was unavailable because the cloud browser could
not reach the local development server. This does not replace UAT.

YouTube ingestion reads `snippet.textDisplay` with `textFormat=plainText`; the
raw `textOriginal` field is restricted to the authenticated comment author.
See the official [comment resource](https://developers.google.com/youtube/data/reference/rest/v3/comments)
and [commentThreads.list](https://developers.google.com/youtube/data/reference/rest/v3/commentThreads/list).

The full patches include the earlier recovery work. Apply them to the original
backend/frontend baselines. If that earlier work is already applied, use the
`seeding-backend-next-phase.patch` and `seeding-frontend-next-phase.patch` files
instead to avoid applying the foundation twice.
