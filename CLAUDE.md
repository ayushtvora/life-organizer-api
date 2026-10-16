# CLAUDE.md

Personal API behind an iPhone Shortcut. The user types a plain-text note ("do laundry 5am",
"meeting 4-5pm wednesday"); the API decides whether it is a **Google Calendar event** or a
**Todoist task**, creates it, and returns a human-readable confirmation for the Shortcut to display.

Single user, self-hosted. Optimise for simplicity and correctness over scale.

## Stack

- .NET 10, ASP.NET Core minimal APIs (`src/LifeOrganizer.Api`)
- xUnit tests (`tests/LifeOrganizer.Api.Tests`)
- Parsing: `Microsoft.Recognizers.Text.DateTime` (rules) + official `Anthropic` C# SDK (LLM fallback)
- Google Calendar: `Google.Apis.Calendar.v3`, authenticated as a **service account** the user's calendar is shared with
- Todoist: REST API v1 (`https://api.todoist.com/api/v1/tasks`) via typed `HttpClient`
- Deploy: Docker image, `docker compose` on a home server, GitHub Actions **self-hosted runner** on push to `main`,
  exposed publicly through **Cloudflare Tunnel**

## Commands

```bash
dotnet build
dotnet test
dotnet run --project src/LifeOrganizer.Api          # http://localhost:5080, Development: key "dev-key", dry run
docker compose up -d --build                        # local container on 127.0.0.1:8095 (needs .env, see .env.example)
```

On the user's Windows machine, Smart App Control blocks loading freshly built DLLs, so `dotnet test`/`dotnet run`
fail with "An Application Control policy has blocked this file". `dotnet build` works. Use the Docker scripts instead
(don't suggest disabling Smart App Control — it can't be re-enabled without resetting Windows):

```powershell
.\scripts\test.ps1                        # dotnet test in an SDK container (optional -Filter "FullyQualifiedName~Rule")
.\scripts\dev.ps1                         # run the API in a container on :5080, dry run, key "dev-key"
.\scripts\dev.ps1 -Live                   # same, with real integrations from .env
```

Manual request:

```bash
curl -s localhost:5080/api/capture -H "X-API-Key: dev-key" -H "Content-Type: application/json" \
  -d '{"text":"meeting 4-5pm wednesday"}'
```

Set `Integrations__DryRun=true` to parse and format without calling Google/Todoist.

## Request flow

`POST /api/capture` → `ApiKeyFilter` → `HybridParser` → `CalendarSink` | `TodoistSink` → `ConfirmationFormatter`.

1. **Override words** at the start force the item type: `e`, `event`, `c`, `cal`, `calendar` → event;
   `t`, `task`, `todo`, `todoist` → task. Must be followed by a space (optional colon before it: `e: party`), so
   `eat lunch` / `c:party` are untouched. Stripped before parsing (`CaptureText.SplitOverride`).
2. **`RuleBasedParser`** classifies and builds the title. Date/time expressions come from **`DatePieces`**, which
   normalizes Recognizers.Text output (plus our own patterns for `eow`/`eom`/`eoy`, `after work`, ordinals like
   "the 20th", and bare hour ranges like "9-5") into `Piece`s. Returns a `ParseResult` with a `Confidence`.
   Goal: **rules decide almost everything**; Low is only for input they cannot resolve (explicit past year, no title
   left, unsupported expressions, recurrence + date range).
3. **`HybridParser`**: if rules are High-confidence, use them. Otherwise call **`LlmParser`** (Claude, structured
   JSON output). If the LLM is not configured, errors, or refuses, fall back to the rule result. `ParsedItem.Parser`
   records which one produced the result ("rules" / "llm").
4. The sink creates the item and returns a link; the formatter builds `message`, which the Shortcut shows verbatim.

### Classification rules (keep LLM prompt and rule parser consistent)

These are decisions the user made; don't change them without asking.

- **Event**: a time range ("4-5pm", "9-5"), a start time + duration ("3pm for 2 hours"), an explicit date range, or
  an appointment-like noun (meeting, appointment, dentist, dinner, lunch, call with, interview, flight, class, party,
  "with <Person>").
- **Task**: an action to do, with or without a due date/time ("do laundry 5am", "send email tomorrow"). Deadlines
  ("by", "before", "eod") and durations without a start time are always tasks.
- **No date**: tasks are due today (all-day). Event-like text with no date → task due today. A *forced* event (`e …`)
  with no date → all-day event today.
- Time-only input that is already past today resolves to **tomorrow**. A weekday resolves to its **next** occurrence
  (today counts if the time is still ahead); "next friday" = Friday of next week. Dates without a year resolve to
  their **next** occurrence, even next year; an explicit past year is Low confidence.
- **Am/pm** (no am/pm typed): the **next upcoming** reading between **8am and 10pm** ("gym at 9" at 11am → 9pm today).
- **Parts of the day** are fixed times: morning 9am, lunch 12pm, afternoon 2pm, evening / after work 6pm,
  tonight / night 8pm.
- **Periods → their first day**: "next week" → Monday, "this/next weekend" → Saturday, "next month" → the 1st;
  a period already under way ("this week") → today.
- **Deadlines**: "by friday" → Friday, "before friday" → Thursday, "end of week"/"eow" → Friday,
  "end of month"/"eom" → last day, "eod"/"end of day" → today, no time.
- **Durations** with no start time ("workout for 1 hour", "study 2 hours tomorrow") → task due that day (default
  today) with a Todoist duration. If Todoist rejects the duration, `TodoistSink` retries with it in the description.
- **Several dates/times** → the first wins; the rest stays in the title.
- Events with a start but no end get `App:DefaultEventMinutes` (default 60). Date-only events are all-day.
- **Explicit date ranges** ("oct 20-22", "dec 20 to jan 3") → all-day event spanning every day, inclusive.
- **Recurrence** works for both types (`Recurrence.Find`: every day/weekday/weekend/week/month/year, every other X,
  every N X, weekday lists, daily/weekly/monthly/yearly). Start moves to the first occurrence. A date or period
  alongside the rule sets where the first occurrence is ("every monday next week" → Mondays from Oct 19;
  "every sunday starting next month" → Sundays from Nov 1). Tasks send a Todoist
  `due_string` ("every monday at 9:00am"); events send an RRULE ("FREQ=WEEKLY;BYDAY=MO").

## Conventions

- All "now" calculations use `LocalClock` (wraps `TimeProvider` + the configured `App:TimeZone`, IANA e.g.
  `America/Toronto`) — never `DateTime.Now`. Tests use `TestClock` (Friday 2026-10-16 11:00 local).
- `ParsedItem` times are local wall-clock times; conversion to UTC/timezone happens only in the sinks.
- External services sit behind `ICalendarSink` / `ITaskSink` so endpoint tests use fakes. Tests must never hit real APIs.
- Parser behaviour is tested with fixture phrases in `RuleBasedParserTests`; add a case for every new phrasing fixed.
- Errors return JSON `{ "success": false, "message": "..." }` with a real status code (401, 422, 502). The Shortcut
  shows `message` regardless of status.
- Config binds from `appsettings.json` + environment variables (`Section__Key`). Secrets only come from env vars;
  never commit them. `.env` is git-ignored.

## Configuration (env vars)

| Variable | Purpose |
|---|---|
| `App__ApiKey` | Shared secret the Shortcut sends in `X-API-Key` |
| `App__TimeZone` | IANA timezone for resolving dates and creating events |
| `Anthropic__ApiKey` | Claude API key; empty disables the LLM fallback |
| `Anthropic__Model` | Defaults to `claude-haiku-5-5` |
| `Google__ServiceAccountJsonBase64` | Base64 of the service-account key JSON |
| `Google__CalendarId` | Calendar to write to (usually the user's Gmail address) |
| `Todoist__ApiToken` | Todoist personal API token |
| `Todoist__ProjectId` | Optional; default is Inbox |

## Deployment

- `.github/workflows/ci.yml`: build + test on GitHub-hosted runners for PRs and pushes.
- `.github/workflows/deploy.yml`: on push to `main`, runs on the `self-hosted` runner on the home server, writes
  `.env` from GitHub Secrets and runs `docker compose up -d --build`, then curls `/health`.
- `cloudflared` runs as an optional compose service (profile `tunnel`) pointing at `http://api:8080`; host port is `127.0.0.1:8095` (`API_PORT`). The server already has an existing host-networked cloudflared, so the public hostname points at `http://localhost:8095` and the compose `cloudflared` stays off.
- Keep the repo **private**, or never let untrusted PRs run on the self-hosted runner.

User-facing setup docs: `docs/SETUP.md` (Google, Todoist, runner, tunnel) and `docs/SHORTCUT.md` (building the
iPhone Shortcut).
