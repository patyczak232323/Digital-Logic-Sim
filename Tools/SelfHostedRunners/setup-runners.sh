#!/usr/bin/env bash
# Sets up N ephemeral, isolated GitHub Actions runners on an Ubuntu host.
#
# Every runner is a systemd service (gh-runner@<n>) that starts a fresh Docker
# container, takes exactly one job, then the container is deleted and a new
# one is started. Containers run under Sysbox, so jobs can use Docker and
# sudo inside the container without getting root on the host.
#
# Usage:
#   sudo ./setup-runners.sh install     # install deps, build image, start runners
#   sudo ./setup-runners.sh status      # show service and container state
#   sudo ./setup-runners.sh uninstall   # stop and remove everything
#
# Settings (environment variables):
#   RUNNER_COUNT          number of runners (default 6)
#   REPO_URL              repository the runners register to
#   RUNNER_LABELS         comma-separated labels (default self-hosted,linux,x64,trusted)
#   GH_PAT                fine-grained token, repo-only, "Administration: read & write"
#                         (asked interactively when not set)
#   BLOCK_LAN             1 = runners cannot reach private networks or the host (default 1)
#   PERSIST_DOCKER_CACHE  1 = keep each runner's inner Docker images between jobs
#                         (much faster Unity builds, less isolation; default 0)
set -euo pipefail

RUNNER_COUNT="${RUNNER_COUNT:-6}"
REPO_URL="${REPO_URL:-https://github.com/patyczak232323/Digital-Logic-Sim}"
RUNNER_LABELS="${RUNNER_LABELS:-self-hosted,linux,x64,trusted}"
BLOCK_LAN="${BLOCK_LAN:-1}"
PERSIST_DOCKER_CACHE="${PERSIST_DOCKER_CACHE:-0}"

INSTALL_DIR=/opt/gh-runners
IMAGE=gh-runner:local
NETWORK=gh-runners
SUBNET=172.31.250.0/24
DOCKER=/usr/bin/docker
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

log() { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
die() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

require_root() {
    [[ $EUID -eq 0 ]] || die "Run with sudo."
}

install_docker() {
    if command -v docker >/dev/null 2>&1; then
        log "Docker already installed."
        return
    fi
    log "Installing Docker Engine..."
    apt-get update
    apt-get install -y ca-certificates curl
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    # shellcheck disable=SC1091
    . /etc/os-release
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
        > /etc/apt/sources.list.d/docker.list
    apt-get update
    apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin
    systemctl enable --now docker
}

install_sysbox() {
    if docker info --format '{{json .Runtimes}}' 2>/dev/null | grep -q sysbox-runc; then
        log "Sysbox already installed."
        return
    fi
    if [[ -n "$(docker ps -q)" ]]; then
        die "Sysbox install restarts Docker. Stop running containers first (docker ps)."
    fi
    log "Installing Sysbox..."
    apt-get install -y curl jq
    local arch url deb
    arch="$(dpkg --print-architecture)"
    url="$(curl -fsSL https://api.github.com/repos/nestybox/sysbox/releases/latest \
        | jq -r --arg a "linux_${arch}.deb" '.assets[] | select(.name | endswith($a)) | .browser_download_url' \
        | head -n1)"
    [[ -n "$url" ]] || die "Could not find a Sysbox package for ${arch}."
    deb="$(mktemp -d)/sysbox.deb"
    curl -fsSL -o "$deb" "$url"
    apt-get install -y "$deb"
    rm -f "$deb"
    systemctl restart docker
}

read_token() {
    if [[ -f "$INSTALL_DIR/runner.env" ]] && grep -q '^ACCESS_TOKEN=.' "$INSTALL_DIR/runner.env" && [[ -z "${GH_PAT:-}" ]]; then
        GH_PAT="$(sed -n 's/^ACCESS_TOKEN=//p' "$INSTALL_DIR/runner.env")"
        log "Reusing token from $INSTALL_DIR/runner.env."
        return
    fi
    if [[ -z "${GH_PAT:-}" ]]; then
        echo "Create a fine-grained token at https://github.com/settings/personal-access-tokens/new"
        echo "  Repository access: only ${REPO_URL#https://github.com/}"
        echo "  Permissions: Administration -> Read and write"
        read -rsp "Paste token: " GH_PAT
        echo
    fi
    [[ -n "$GH_PAT" ]] || die "Token is required."
}

write_config() {
    install -d -m 0700 "$INSTALL_DIR"
    umask 077
    cat > "$INSTALL_DIR/runner.env" <<EOF
ACCESS_TOKEN=${GH_PAT}
REPO_URL=${REPO_URL}
RUNNER_SCOPE=repo
LABELS=${RUNNER_LABELS}
EPHEMERAL=true
RUN_AS_ROOT=false
START_DOCKER_SERVICE=true
DISABLE_AUTO_UPDATE=true
EOF
    chmod 600 "$INSTALL_DIR/runner.env"
    umask 022
}

build_image() {
    log "Building runner image..."
    install -m 0644 "$SCRIPT_DIR/Dockerfile" "$INSTALL_DIR/Dockerfile"
    docker build --pull -t "$IMAGE" "$INSTALL_DIR"
}

create_network() {
    if ! docker network inspect "$NETWORK" >/dev/null 2>&1; then
        log "Creating Docker network $NETWORK ($SUBNET)..."
        docker network create --subnet "$SUBNET" "$NETWORK" >/dev/null
    fi
}

install_firewall() {
    if [[ "$BLOCK_LAN" != 1 ]]; then
        log "BLOCK_LAN=0, skipping firewall."
        rm -f /etc/systemd/system/gh-runners-firewall.service
        return
    fi
    log "Installing firewall rules (runners cannot reach LAN or the host)..."
    cat > "$INSTALL_DIR/firewall.sh" <<EOF
#!/usr/bin/env bash
# Blocks runner containers from private networks and from the host itself.
set -euo pipefail
add() { iptables -C "\$@" 2>/dev/null || iptables -I "\$@"; }
for net in 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 100.64.0.0/10 169.254.0.0/16; do
    add DOCKER-USER -s $SUBNET -d "\$net" -j DROP
done
add DOCKER-USER -s $SUBNET -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
add INPUT -s $SUBNET -j DROP
add INPUT -s $SUBNET -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
EOF
    chmod 700 "$INSTALL_DIR/firewall.sh"
    cat > /etc/systemd/system/gh-runners-firewall.service <<EOF
[Unit]
Description=Firewall rules for GitHub Actions runner containers
After=docker.service
Requires=docker.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=$INSTALL_DIR/firewall.sh

[Install]
WantedBy=multi-user.target
EOF
}

install_units() {
    log "Installing systemd units..."
    local cache_args=""
    if [[ "$PERSIST_DOCKER_CACHE" == 1 ]]; then
        cache_args="-v gh-runner-docker-%i:/var/lib/docker"
    fi
    local fw_dep=""
    if [[ "$BLOCK_LAN" == 1 ]]; then
        fw_dep="gh-runners-firewall.service"
    fi
    cat > /etc/systemd/system/gh-runner@.service <<EOF
[Unit]
Description=Ephemeral GitHub Actions runner %i
After=docker.service network-online.target $fw_dep
Requires=docker.service $fw_dep
Wants=network-online.target

[Service]
Restart=always
RestartSec=5
TimeoutStopSec=90
ExecStartPre=-$DOCKER rm -f gh-runner-%i
ExecStart=$DOCKER run --rm --name gh-runner-%i \\
    --runtime=sysbox-runc \\
    --network $NETWORK --dns 1.1.1.1 --dns 9.9.9.9 \\
    --env-file $INSTALL_DIR/runner.env \\
    -e RUNNER_NAME=%H-%i \\
    $cache_args $IMAGE
ExecStop=$DOCKER stop -t 60 gh-runner-%i

[Install]
WantedBy=multi-user.target
EOF
    # Auto-update is disabled inside ephemeral runners, so rebuild the image
    # weekly to pick up new runner releases. New jobs use the new image.
    cat > /etc/systemd/system/gh-runners-rebuild.service <<EOF
[Unit]
Description=Rebuild GitHub Actions runner image
After=docker.service
Requires=docker.service

[Service]
Type=oneshot
ExecStart=$DOCKER build --pull -t $IMAGE $INSTALL_DIR
EOF
    cat > /etc/systemd/system/gh-runners-rebuild.timer <<EOF
[Unit]
Description=Weekly rebuild of GitHub Actions runner image

[Timer]
OnCalendar=weekly
Persistent=true
RandomizedDelaySec=1h

[Install]
WantedBy=timers.target
EOF
    systemctl daemon-reload
    systemctl enable --now gh-runners-rebuild.timer >/dev/null
}

start_runners() {
    if [[ "$BLOCK_LAN" == 1 ]]; then
        systemctl enable --now gh-runners-firewall.service
        systemctl restart gh-runners-firewall.service
    fi
    # Stop runners beyond the requested count (e.g. after lowering RUNNER_COUNT).
    local unit n
    for unit in $(systemctl list-units --all --plain --no-legend 'gh-runner@*.service' | awk '{print $1}'); do
        n="${unit#gh-runner@}"; n="${n%.service}"
        if (( n > RUNNER_COUNT )); then
            systemctl disable --now "$unit"
        fi
    done
    log "Starting $RUNNER_COUNT runners..."
    for ((n = 1; n <= RUNNER_COUNT; n++)); do
        systemctl enable "gh-runner@$n.service" >/dev/null
        systemctl restart "gh-runner@$n.service"
    done
}

cmd_install() {
    require_root
    [[ "$RUNNER_COUNT" =~ ^[1-9][0-9]*$ ]] || die "RUNNER_COUNT must be a positive number."
    grep -qi ubuntu /etc/os-release || die "This script supports Ubuntu only."
    install_docker
    DOCKER="$(command -v docker)"
    [[ "$DOCKER" != /snap/* ]] || die "Snap Docker is not supported by Sysbox. Remove it (snap remove docker) and rerun."
    install_sysbox
    read_token
    write_config
    build_image
    create_network
    install_firewall
    install_units
    start_runners
    log "Done. Runners should appear as Idle in ${REPO_URL}/settings/actions/runners within a minute."
    log "Logs: journalctl -fu 'gh-runner@*'"
}

cmd_status() {
    systemctl list-units --all --no-pager 'gh-runner@*.service' 'gh-runners-firewall.service' || true
    echo
    docker ps --filter 'name=gh-runner-' --format 'table {{.Names}}\t{{.Status}}' || true
}

cmd_uninstall() {
    require_root
    local unit
    for unit in $(systemctl list-units --all --plain --no-legend 'gh-runner@*.service' | awk '{print $1}'); do
        systemctl disable --now "$unit" || true
    done
    systemctl disable --now gh-runners-firewall.service gh-runners-rebuild.timer 2>/dev/null || true
    rm -f /etc/systemd/system/gh-runner@.service /etc/systemd/system/gh-runners-firewall.service
    rm -f /etc/systemd/system/gh-runners-rebuild.service /etc/systemd/system/gh-runners-rebuild.timer
    systemctl daemon-reload
    if [[ -x "$INSTALL_DIR/firewall.sh" ]]; then
        while iptables -D INPUT -s "$SUBNET" -j DROP 2>/dev/null; do :; done
        while iptables -D INPUT -s "$SUBNET" -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT 2>/dev/null; do :; done
        while iptables -D DOCKER-USER -s "$SUBNET" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN 2>/dev/null; do :; done
        for net in 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 100.64.0.0/10 169.254.0.0/16; do
            while iptables -D DOCKER-USER -s "$SUBNET" -d "$net" -j DROP 2>/dev/null; do :; done
        done
    fi
    docker network rm "$NETWORK" 2>/dev/null || true
    docker volume ls -q --filter 'name=gh-runner-docker-' | xargs -r docker volume rm
    docker image rm "$IMAGE" 2>/dev/null || true
    rm -rf "$INSTALL_DIR"
    log "Removed. Offline runners may still be listed on GitHub; delete them there if so."
}

case "${1:-install}" in
    install) cmd_install ;;
    status) cmd_status ;;
    uninstall) cmd_uninstall ;;
    *) die "Unknown command: $1 (use install | status | uninstall)" ;;
esac
