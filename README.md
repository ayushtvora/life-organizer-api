# life-organizer-api

Type a quick note in an iPhone Shortcut ("meeting 4-5pm wednesday", "do laundry 5am") and it lands in the right app:
**Google Calendar** for events, **Todoist** for tasks. The Shortcut then shows what was created.

- Parsing is hybrid: fast deterministic date rules first, and Claude when the rules aren't confident.
- .NET 10 minimal API, deployed with Docker on a home server through a self-hosted GitHub Actions runner, and exposed
  through Cloudflare Tunnel.

## Docs

- [docs/SETUP.md](docs/SETUP.md): Google, Todoist, Claude, Cloudflare Tunnel, runner, secrets
- [docs/SHORTCUT.md](docs/SHORTCUT.md): building the iPhone Shortcut
- [CLAUDE.md](CLAUDE.md): architecture and conventions

## Local development

```bash
dotnet test
dotnet run --project src/LifeOrganizer.Api     # Development: API key "dev-key", dry run (nothing is created)

curl -s localhost:5080/api/capture -H "X-API-Key: dev-key" -H "Content-Type: application/json" \
  -d '{"text":"lunch with sam friday 12:30 for 1 hour"}'
```
