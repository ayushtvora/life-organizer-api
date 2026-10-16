#!/usr/bin/env bash
# Installs a GitHub Actions self-hosted runner for this repo on a Linux home server, as a service.
#
# Usage (on the server):  ./setup-runner.sh <registration-token>
# Get a token (valid 1 hour) from your PC with:
#   gh api -X POST repos/ayushtvora/life-organizer-api/actions/runners/registration-token --jq .token
set -euo pipefail

TOKEN="${1:?usage: $0 <registration-token>}"
REPO_URL="https://github.com/ayushtvora/life-organizer-api"
DIR="$HOME/actions-runner-life-organizer"

echo "==> Checking prerequisites"
command -v docker >/dev/null || { echo "Docker is not installed: https://docs.docker.com/engine/install/"; exit 1; }
docker compose version >/dev/null || { echo "The docker compose plugin is missing"; exit 1; }
command -v curl >/dev/null || { echo "curl is not installed"; exit 1; }
if ! id -nG "$USER" | grep -qw docker; then
  echo "==> Adding $USER to the docker group"
  sudo usermod -aG docker "$USER"
fi

case "$(uname -m)" in
  x86_64) ARCH=x64 ;;
  aarch64 | arm64) ARCH=arm64 ;;
  armv7l) ARCH=arm ;;
  *) echo "Unsupported architecture: $(uname -m)"; exit 1 ;;
esac

VERSION=$(curl -fsSL https://api.github.com/repos/actions/runner/releases/latest | grep -m1 '"tag_name"' | sed -E 's/.*"v([^"]+)".*/\1/')
echo "==> Installing runner v$VERSION ($ARCH) into $DIR"
mkdir -p "$DIR" && cd "$DIR"
curl -fsSL -o runner.tar.gz "https://github.com/actions/runner/releases/download/v$VERSION/actions-runner-linux-$ARCH-$VERSION.tar.gz"
tar xzf runner.tar.gz && rm runner.tar.gz

./config.sh --unattended --replace \
  --url "$REPO_URL" --token "$TOKEN" \
  --name "$(hostname)-life-organizer" --labels self-hosted

echo "==> Installing as a service"
sudo ./svc.sh install "$USER"
sudo ./svc.sh start

echo "==> Done. The runner should show as Idle under GitHub → Settings → Actions → Runners."
