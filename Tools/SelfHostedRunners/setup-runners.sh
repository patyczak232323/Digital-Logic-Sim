#!/usr/bin/env bash
# Sets up N GitHub Actions runners directly on an Ubuntu or Debian host.
#
# Each runner lives in /opt/github-runners/runner-<n>, runs as the unprivileged
# "ghrunner" user and is installed as a systemd service, so it starts on boot.
#
# Usage:
#   sudo ./setup-runners.sh install     # download, register and start runners
#   sudo ./setup-runners.sh status      # show service state
#   sudo ./setup-runners.sh uninstall   # stop and remove everything
#
# Settings (environment variables):
#   RUNNER_COUNT   number of runners (default 6)
#   REPO_URL       repository the runners register to
#   RUNNER_LABELS  extra labels (default trusted); self-hosted, Linux and X64 are added by GitHub
#   RUNNER_TOKEN   registration token from Settings -> Actions -> Runners -> New self-hosted runner
#                  (asked interactively when not set, valid for 1 hour)
#   RUNNER_SUDO    1 = ghrunner gets passwordless sudo ON THE HOST (default 0)
#   RUNNER_DOCKER  1 = ghrunner joins the docker group, needed by game-ci/unity-builder;
#                  this is effectively root on the host (default 0)
set -euo pipefail

RUNNER_COUNT="${RUNNER_COUNT:-6}"
REPO_URL="${REPO_URL:-https://github.com/patyczak232323/Digital-Logic-Sim}"
RUNNER_LABELS="${RUNNER_LABELS:-trusted}"
RUNNER_SUDO="${RUNNER_SUDO:-0}"
RUNNER_DOCKER="${RUNNER_DOCKER:-0}"

RUNNER_USER=ghrunner
BASE_DIR=/opt/github-runners
SUDOERS_FILE=/etc/sudoers.d/ghrunner

log() { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWARNING:\033[0m %s\n' "$*" >&2; }
die() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

require_root() {
    [[ $EUID -eq 0 ]] || die "Run with sudo."
}

as_runner() {
    runuser -u "$RUNNER_USER" -- "$@"
}

runner_dir() {
    echo "$BASE_DIR/runner-$1"
}

check_system() {
    local distro
    # shellcheck disable=SC1091
    distro="$(. /etc/os-release && echo "$ID")"
    [[ "$distro" == ubuntu || "$distro" == debian ]] || die "This script supports Ubuntu and Debian only."
    [[ "$RUNNER_COUNT" =~ ^[1-9][0-9]*$ ]] || die "RUNNER_COUNT must be a positive number."
}

create_user() {
    if ! id "$RUNNER_USER" >/dev/null 2>&1; then
        log "Creating user $RUNNER_USER..."
        useradd --system --create-home --shell /bin/bash "$RUNNER_USER"
    fi

    if [[ "$RUNNER_SUDO" == 1 ]]; then
        warn "Giving $RUNNER_USER passwordless sudo on this host. Every job can do anything on this server."
        echo "$RUNNER_USER ALL=(ALL) NOPASSWD:ALL" > "$SUDOERS_FILE"
        chmod 0440 "$SUDOERS_FILE"
        visudo -cf "$SUDOERS_FILE" >/dev/null || { rm -f "$SUDOERS_FILE"; die "Invalid sudoers file."; }
    else
        rm -f "$SUDOERS_FILE"
    fi

    if [[ "$RUNNER_DOCKER" == 1 ]]; then
        getent group docker >/dev/null || die "RUNNER_DOCKER=1 but Docker is not installed."
        warn "Adding $RUNNER_USER to the docker group. This is effectively root on this host."
        usermod -aG docker "$RUNNER_USER"
    elif id -nG "$RUNNER_USER" | grep -qw docker; then
        gpasswd -d "$RUNNER_USER" docker >/dev/null
    fi
}

download_runner() {
    local arch release version url sha tarball
    case "$(dpkg --print-architecture)" in
        amd64) arch=x64 ;;
        arm64) arch=arm64 ;;
        armhf) arch=arm ;;
        *) die "Unsupported architecture: $(dpkg --print-architecture)" ;;
    esac

    apt-get install -y curl jq tar >/dev/null
    release="$(curl -fsSL https://api.github.com/repos/actions/runner/releases/latest)"
    version="$(jq -r '.tag_name' <<<"$release")"
    version="${version#v}"
    [[ -n "$version" && "$version" != null ]] || die "Could not determine the latest runner version."
    url="https://github.com/actions/runner/releases/download/v${version}/actions-runner-linux-${arch}-${version}.tar.gz"
    sha="$(jq -r '.body' <<<"$release" | grep -oP "(?<=<!-- BEGIN SHA linux-${arch} -->)[0-9a-f]{64}" | head -n1 || true)"

    tarball="$BASE_DIR/actions-runner-${version}.tar.gz"
    if [[ ! -f "$tarball" ]]; then
        log "Downloading runner ${version} (linux-${arch})..."
        curl -fsSL -o "$tarball.part" "$url"
        mv "$tarball.part" "$tarball"
    fi
    if [[ -n "$sha" ]]; then
        echo "$sha  $tarball" | sha256sum -c --quiet - || { rm -f "$tarball"; die "Checksum mismatch for $tarball."; }
    else
        warn "No checksum found in the release notes; skipping verification."
    fi
    RUNNER_TARBALL="$tarball"
}

read_token() {
    if [[ -z "${RUNNER_TOKEN:-}" ]]; then
        echo "Get a registration token at ${REPO_URL}/settings/actions/runners/new"
        echo "(the value after --token in the ./config.sh command, valid for 1 hour)"
        read -rsp "Paste token: " RUNNER_TOKEN
        echo
    fi
    [[ -n "$RUNNER_TOKEN" ]] || die "Token is required."
}

setup_runner() {
    local n="$1" dir name
    dir="$(runner_dir "$n")"
    name="$(hostname)-$n"

    if [[ -f "$dir/.runner" ]]; then
        log "Runner $n already registered, skipping registration."
    else
        log "Registering runner $n ($name)..."
        install -d -o "$RUNNER_USER" -g "$RUNNER_USER" "$dir"
        as_runner tar xzf "$RUNNER_TARBALL" -C "$dir"
        (cd "$dir" && as_runner ./config.sh --unattended --replace \
            --url "$REPO_URL" --token "$RUNNER_TOKEN" \
            --name "$name" --labels "$RUNNER_LABELS" --work _work)
    fi

    if [[ ! -f "$dir/.service" ]]; then
        (cd "$dir" && ./svc.sh install "$RUNNER_USER" >/dev/null)
    fi
    (cd "$dir" && ./svc.sh start >/dev/null)
}

cmd_install() {
    require_root
    check_system
    install -d -m 0755 "$BASE_DIR"
    create_user
    download_runner
    log "Installing tools used by this repository's workflows..."
    apt-get install -y git zip unzip python3 gh >/dev/null

    local need_token=0 n
    for ((n = 1; n <= RUNNER_COUNT; n++)); do
        [[ -f "$(runner_dir "$n")/.runner" ]] || need_token=1
    done
    if [[ "$need_token" == 1 ]]; then
        read_token
    fi

    # Dependencies are the same for every runner; install them once.
    local first
    first="$(runner_dir 1)"
    if [[ ! -x "$first/bin/installdependencies.sh" ]]; then
        install -d -o "$RUNNER_USER" -g "$RUNNER_USER" "$first"
        as_runner tar xzf "$RUNNER_TARBALL" -C "$first"
    fi
    log "Installing runner dependencies..."
    "$first/bin/installdependencies.sh" >/dev/null

    for ((n = 1; n <= RUNNER_COUNT; n++)); do
        setup_runner "$n"
    done

    log "Done. Runners should appear as Idle in ${REPO_URL}/settings/actions/runners."
    log "Use them with: runs-on: [self-hosted, ${RUNNER_LABELS%%,*}]"
}

cmd_status() {
    systemctl list-units --all --no-pager 'actions.runner.*' || true
}

cmd_uninstall() {
    require_root
    local dir token=""
    read -rsp "Removal token (Enter to skip and delete runners on GitHub by hand): " token
    echo
    for dir in "$BASE_DIR"/runner-*; do
        [[ -d "$dir" ]] || continue
        log "Removing $(basename "$dir")..."
        if [[ -f "$dir/.service" ]]; then
            (cd "$dir" || exit 0; ./svc.sh stop >/dev/null || true; ./svc.sh uninstall >/dev/null || true)
        fi
        if [[ -n "$token" && -f "$dir/.runner" ]]; then
            (cd "$dir" && as_runner ./config.sh remove --token "$token") || warn "Could not unregister $dir."
        fi
    done
    rm -rf "${BASE_DIR:?}"
    rm -f "$SUDOERS_FILE"
    log "Removed. If you skipped the token, delete the offline runners at ${REPO_URL}/settings/actions/runners."
    log "The $RUNNER_USER user was kept; remove it with: sudo userdel -r $RUNNER_USER"
}

case "${1:-install}" in
    install) cmd_install ;;
    status) cmd_status ;;
    uninstall) cmd_uninstall ;;
    *) die "Unknown command: $1 (use install | status | uninstall)" ;;
esac
