# Discussion seeding: rollback foundation

The recovery foundation now supports the [source-driven pipeline](discussion-seeding-pipeline.md).
All switches and the deployment gate still default to **off**. Nothing has been deployed.
The foundation alone does not run generation; the separate pipeline implements it.

## Recovery target

The original backend baseline is `9a4f4b9ccf2baa15a23da950774ce4f758d81439`,
tagged locally as `baseline/discussion-seeding-20261108`.

**Once generated records exist, the application recovery target must retain this
foundation's visibility filters.** Reverting to an older application which does
not understand `SeedHiddenAt` would show hidden records again. Tag the reviewed,
tested foundation commit as the recovery target before enabling any later
generation feature. A Git revert alone cannot undo database writes.

Keep the additive migration when rolling application code back to that recovery
target. Its `Down` removes provenance/history and is only for disposable or
prelaunch databases, not emergency production rollback.

## Operator workflow

The frontend adds a panel to `/mod/general/editorial`. The existing editorial
moderator permission is checked on every backend request; hiding the panel is
not the authorization boundary.

1. Open the panel and refresh the preview.
2. Review active runs, pending jobs, visible generated comments and profiles.
3. Choose **Stop and hide**, then confirm.
4. Verify the switches are off and the affected records no longer appear.

The action atomically disables all switches, stops runs, cancels pending jobs,
hides tagged comments/profiles, and subtracts only newly hidden comments from
reply counters. Existing organic replies (including imported editorial replies),
likes, views, comment bodies and original timestamps are retained. Repeated
execution does not decrement counters again. Re-enabling flags does not unhide
records or revive stopped runs.

Profiles use a separate hidden timestamp because the legacy SQLite schema
repair can reset `IsProfilePublic`; recovery visibility must survive a restart.
Account deletion still includes hidden replies and does not subtract them twice.

## API

All paths below require the existing client authentication and a user JWT for
a moderator accepted by `IEditorialDeskService.EnsureCanEditAsync`.

| Method | Path under `/api/editorial/discussion-seeding` | Purpose |
| --- | --- | --- |
| GET | `/flags` | Current database-backed switches |
| PUT | `/flags` | Replace switches; body has `generationEnabled`, `publishingEnabled`, `combinedLikesEnabled` |
| POST | `/runs` | Prepare a named run; body `{ "name": "UAT pilot" }` |
| GET | `/rollback-preview?runId=<optional UUID>` | Read-only estimate of affected records |
| POST | `/stop-and-hide` | Body `{}` for all runs, or `{ "runId": "<UUID>" }` for one run |

A scoped stop still disables the global pipeline, but hides/cancels only that
run's records. Other runs remain stopped by the switches until an operator
explicitly re-enables them. An unknown run returns 404 without modifying flags.
The UI exposes recovery only; it does not expose an enable action.

## Before enabling the publisher

Use the staged UAT procedure and source requirements in the
[pipeline guide](discussion-seeding-pipeline.md). The publisher now uses the
same database lock as rollback, persists provenance at creation, moderates
contributions and rechecks the run before publication. Normal human reply
writers use atomic counter deltas. The deployment gate remains false.

SQL Server UAT and live quality checks remain necessary. Keep source engagement
separate from organic metrics, and retain this foundation's visibility filters
when rolling application code back.

## Validation

`DiscussionSeedingRollbackTests` covers default-off switches, repeat-safe schema
repair, idempotent and scoped rollback, preservation of organic data/history,
transaction rollback on a database failure, missing controls, and authorization.

```sh
./scripts/test-backend.sh
```

.NET SDK **10.0.100** is installed in this workspace at
`/tmp/kinoiserie-dotnet`. Microsoft's official blob download feed worked when
its primary build download host was blocked. To use that installation explicitly:

```sh
KINSHOUT_DOTNET=/tmp/kinoiserie-dotnet/dotnet ./scripts/test-backend.sh
```

The test script disables MSBuild node reuse and shared compilation, and runs
MSBuild with one node. This avoids the Unix socket background servers which
are unavailable in this workspace; normal VSTest execution still runs the real
test assembly. CI uses the same script with the SDK installed by setup-dotnet.

The first build exposed duplicate SDK content for `Data/source-registry.json`;
its project entry now uses `Update` to preserve copying without including it
twice. All seven rollback tests passed. The full suite also exposed an existing
Facebook-share canonical-key bug: the attached YouTube clip must identify the
shared video instead of the Facebook wrapper post. That behavior is fixed, with
five additional cases covering source precedence and fallback identities.

Validation on November 8, 2026: **551 passed, 0 failed, 21 skipped** (572 total).
All seven rollback tests passed. The 21 skips are existing explicit exclusions
for advert-search behavior after search was changed to discussions only. The
TRX report is available in `review/test-results/backend-tests.trx`.

SQL Server UAT and multi-instance publisher testing remain required before
enabling any later generation feature. Passing SQLite tests does not establish
SQL Server compatibility. NuGet reported dependency security advisories for
ImageSharp and transitive SQLite/PKCS packages; dependency upgrades are not part
of this testing fix.

The frontend source was downloaded from private repository commit
`f4603dbdd81d1a580873a67d24ca15b6e27a6f12`. Its local Git baseline is an archive
snapshot, not the repository's original Git ancestry; apply its patch onto that
real commit rather than pushing the snapshot as a replacement history.
