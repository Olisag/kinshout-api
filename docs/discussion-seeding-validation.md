# Discussion seeding validation

Run backend tests with `KINSHOUT_DOTNET` pointing to the .NET 10 executable when it is not on PATH.

## Recorded local result

- SQL Server 2022 Developer 16.0.4305.5 with its matching full-text component: 25 seeding checks passed, zero skipped or failed.
- Full backend regression: 562 passed, 21 existing skips, zero failures (583 total).
- Live quality: six real gpt-4o-mini generation calls completed with the temporary restricted key. Five automated checks passed; politics abstention failed. Outputs were moderated successfully. See review/comment-quality-live-initial.json. The revised prompt has not yet been rerun against the live API.
- Developer-control revision: moderator setup and mutation endpoints were removed; a single global production setting now initializes the worker automatically. Production enablement is requested by the owner. Local validation used disposable databases; deployment is recorded separately in review/production-deployment.json.

SQL validation found that the EF model used SET NULL for community/participant reviewer references, while the existing SQL Server schema patcher uses NO ACTION. SQL Server rejects these alongside the membership user cascade as multiple cascade paths. The model now uses NO ACTION on SQL Server, matching the deployed schema; SQLite retains SET NULL. Account suppression already clears reviewer references explicitly.

## SQL Server

The seeding fixtures support `KINSHOUT_SQLSERVER_TEST_CONNECTION`. Each test creates and drops its own `KinshoutSeedingValidation_<random GUID>` database; the connection's initial catalog is replaced. The login needs create/drop database permission. Use an isolated nonproduction server. Loopback is accepted by default; remote validation additionally requires `KINSHOUT_SQLSERVER_ALLOW_REMOTE_TEST_SERVER=true`.

```bash
./scripts/test-backend.sh --filter 'FullyQualifiedName~DiscussionSeeding' --logger 'trx;LogFileName=sqlserver-seeding.trx' --results-directory review/test-results
```

Install SQL Server full-text search before running the full schema repair cases, since the existing application schema patcher creates full-text indexes.

These cases exercise fresh schema creation, additive seeding schema repair, source refresh, publication, scheduling, transactional rollback, concurrency and organic counter preservation. They are not a complete historical migration-chain rehearsal or a load test. The SQL Server provider enables retry-on-failure, matching production behavior.

## Live comment quality

Configure `OPENAI_API_KEY` through secure environment configuration, then run:

```bash
./scripts/check-live-comments.sh review/comment-quality-live.json
```

`OpenAI__ApiKey` is also supported; `OpenAI__Model` can override the default `gpt-4o-mini`. The runner uses the production generator, originality filter and moderation service. Six explicitly authored fixtures cover rumba, food, diaspora language learning, humour, prompt injection and politics abstention. At most six generation and six moderation requests are made. It writes no database rows and publishes nothing.

Missing credentials exit with code 2 before making any API call. Failed checks or HTTP/model errors exit 1. A successful automated run still requires review of French/Lingala fluency, relevance, unsupported facts, near paraphrases and variety. Authored fixtures do not establish quality across real feed content; sample real permitted source inputs in UAT before activation. Reports contain fixture text and model output; they never include credentials or raw HTTP headers.

## Live sample review (2026-11-09)

The first live batch is not release-ready. The four cultural comments were relevant and passed the lexical originality check and moderation, but they were excessively long and generic. The food sample used the English word “fascinating”; the diaspora sample added an unsupported claim about children's self-esteem. The injection case abstained correctly. The political fixture produced a civic comment instead of the required null: a release blocker for a culture-only feed. Passing generic moderation does not establish the product's no-politics requirement. No Lingala output was produced, so Lingala fluency has not been validated.

The revised prompt requests one short concrete reaction, correct French without English, and explicit abstention for political source material or instruction-only input. The generator now rejects output longer than 220 characters. These changes still need a second live batch; first-batch results must not be represented as validation of the revised prompt. No comments were published or queued by this tool.

## Political abstention hardening

After the initial live failure, the generator now abstains before inference when explicit French/English political terms appear in the source title, body or reaction samples. It also rejects generated political output, independently of the model's JSON abstention. This conservative vocabulary check can suppress cultural posts mentioning politics; it is not a complete multilingual classifier and does not establish Lingala political detection. The existing pipeline topic exclusion remains in place. Regression cases exercise source and output rejection, reaction text, oversized output, and ordinary nonpolitical uses of “parti”. The revised generator still awaits a live quality rerun with a usable credential.
