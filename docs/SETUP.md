# Setup

One-time setup for the services, the home server, and GitHub. After this, every push to `main` deploys.

## 1. API key for the Shortcut

Generate a random secret. It becomes the `API_KEY` GitHub secret and the `X-API-Key` header in the Shortcut.

```bash
openssl rand -hex 32
```

## 2. Google Calendar (service account)

1. In the [Google Cloud console](https://console.cloud.google.com/), create a project (or pick one).
2. **APIs & Services → Library**: enable **Google Calendar API**.
3. **IAM & Admin → Service Accounts → Create service account**. No roles are needed.
4. Open the service account, go to **Keys → Add key → Create new key → JSON**, and download it.
5. In [Google Calendar](https://calendar.google.com/) → **Settings** → select your calendar → **Share with specific people** → add the
   service account's email (`…@….iam.gserviceaccount.com`) with **Make changes to events**.
6. On the same page, under **Integrate calendar**, copy the **Calendar ID**. For your main calendar this is your Gmail address.
7. Base64-encode the key file:

   ```bash
   base64 -w0 key.json                    # Linux
   base64 -i key.json                     # macOS
   ```
   ```powershell
   [Convert]::ToBase64String([IO.File]::ReadAllBytes("key.json"))   # Windows
   ```

   Then delete the downloaded key file.

The service account creates the events, so it shows as the event's creator. It can't send invitations to other attendees.

## 3. Todoist

Go to **Todoist → Settings → Integrations → Developer** and copy the **API token**.

To use a project other than Inbox, open the project in the web app. The ID is the last part of the URL
(`…/project/<name>-<id>`); store it as the `TODOIST_PROJECT_ID` variable.

## 4. Claude (LLM fallback, optional)

Create a key at [platform.claude.com](https://platform.claude.com/). Without one, the API uses rules only.
The rules handle nearly everything, so Claude is rarely called (for example for `oct 1 2025`, a date in the past
with an explicit year). Without a key, those few phrases get the rules' best guess.

The default model is `claude-haiku-5-5`: fast, and a fraction of a cent per call. You can change it with the
`ANTHROPIC_MODEL` repository variable. The Claude Pro plan doesn't cover API usage; it is billed separately through
prepaid credits on platform.claude.com.

## 5. Cloudflare Tunnel

1. In the Cloudflare dashboard, go to **Zero Trust → Networks → Tunnels → Create a tunnel → Cloudflared**, and name it.
2. Copy the **tunnel token** from the install command (the long string after `--token`). It becomes the
   `CLOUDFLARE_TUNNEL_TOKEN` secret, and the deploy workflow then also runs the `cloudflared` container.
3. Add a **Public hostname**, for example `organizer.yourdomain.com`, with:
   - **Service:** `http://api:8080`. Use `http://localhost:8080` if cloudflared runs outside this compose project. In that
     case, leave the secret empty.

The API is protected by the `X-API-Key` header. For extra protection you can put a Cloudflare Access application in
front of it with a **service token**. Then add the `CF-Access-Client-Id` and `CF-Access-Client-Secret` headers in the
Shortcut as well.

## 6. Self-hosted GitHub Actions runner (home server)

Prerequisites on the server: Docker with the compose plugin, and `curl`.

From your PC, copy the setup script over and run it with a fresh registration token (valid for one hour):

```bash
scp scripts/setup-runner.sh user@server:~
ssh -t user@server "bash ~/setup-runner.sh $(gh api -X POST repos/ayushtvora/life-organizer-api/actions/runners/registration-token --jq .token)"
```

It downloads the runner, registers it with the `self-hosted` label, installs it as a service that survives reboots,
and adds your user to the `docker` group. It asks for your sudo password on the server.

> **Security:** a self-hosted runner executes workflow code on your server. Keep this repository **private**. For
> public repos, require approval for outside-contributor workflows. `deploy.yml` only runs on pushes to `main`.

## 7. GitHub secrets and variables

**Settings → Secrets and variables → Actions**

| Secrets | |
|---|---|
| `API_KEY` | From step 1 (required) |
| `GOOGLE_SERVICE_ACCOUNT_JSON_BASE64` | From step 2 |
| `GOOGLE_CALENDAR_ID` | From step 2 |
| `TODOIST_API_TOKEN` | From step 3 |
| `ANTHROPIC_API_KEY` | From step 4 (optional) |
| `CLOUDFLARE_TUNNEL_TOKEN` | From step 5 (optional; runs cloudflared in compose) |

| Variables (optional) | Default |
|---|---|
| `TIME_ZONE` | `America/Toronto` (IANA name) |
| `ANTHROPIC_MODEL` | `claude-haiku-5-5` |
| `TODOIST_PROJECT_ID` | Inbox |
| `API_PORT` | `8080` (host port, bound to 127.0.0.1) |

## 8. Deploy and verify

Push to `main`, or run **Actions → Deploy → Run workflow**. Then check it:

```bash
curl https://organizer.yourdomain.com/health
curl https://organizer.yourdomain.com/api/capture \
  -H "X-API-Key: <key>" -H "Content-Type: application/json" \
  -d '{"text":"test event tomorrow 3pm"}'
```

Then build the Shortcut: [SHORTCUT.md](SHORTCUT.md).
