# Developer stop and rollback

The global switch is `DiscussionSeeding__Enabled` on the API's Azure App Service. Moderator controls and their HTTP endpoints have been removed.

## Stop future activity

Set `DiscussionSeeding__Enabled=false` on every API instance and apply/restart. This stops generation and publication. Existing comments and accounts remain visible; original and organic content is not changed. Disabling also disables the combined-like display once the frontend refreshes its display setting (normally within 30 seconds).

## Preview or hide generated activity

Use the operator console with the developer's existing database credentials supplied securely as `ConnectionStrings__DefaultConnection`. Do not put credentials in source control, reports or command history.

```bash
dotnet run --project tools/SeedingControl -- preview
```

For rollback, first stop every API instance's seeding worker with the Azure global setting above. Set `DiscussionSeeding__Enabled=false` in the console environment too, then run:

```bash
dotnet run --project tools/SeedingControl -- stop-and-hide
```

The console acknowledgement does not remotely check Azure configuration; the operator must stop all workers first. Otherwise an enabled application restart can initialize a new run.

Stop-and-hide atomically stops runs, cancels pending jobs, hides generated comments/profiles and subtracts only newly hidden replies from counters. It preserves organic replies/likes and generated history. The command is idempotent. `Guid.Empty` records infrastructure/developer actions rather than attributing them to a moderator user.

## Application rollback

GitHub rollback branches `rollback/before-discussion-seeding-20261108` preserve backend `9a4f4b9ccf2baa15a23da950774ce4f758d81439` and frontend `f4603dbdd81d1a580873a67d24ca15b6e27a6f12`.

Disable and hide generated activity before reverting to application code that predates the visibility filters. Retain the additive database schema. Do not downgrade/drop schema as an emergency rollback; later organic activity must not be overwritten with an old database snapshot.
