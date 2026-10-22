# Self-hosted GitHub Actions runners

Sets up GitHub Actions runners directly on an Ubuntu or Debian server (6 by default),
without containers or VMs.

- Each runner lives in `/opt/github-runners/runner-N`, runs as the unprivileged
  `ghrunner` user and is a systemd service that starts on boot.
- Runners are not isolated from the host and are not reset between jobs. Only run
  workflows you trust on them, and restrict who can open pull requests.

## Setup

```bash
sudo ./setup-runners.sh install
```

The script asks for a registration token from
**Settings → Actions → Runners → New self-hosted runner** (the value after `--token`,
valid for 1 hour).

Options are environment variables, e.g. `sudo RUNNER_COUNT=4 ./setup-runners.sh install`.
See the header of `setup-runners.sh` for the full list. Two of them widen what jobs
can do on the host:

- `RUNNER_SUDO=1` gives `ghrunner` passwordless sudo.
- `RUNNER_DOCKER=1` adds `ghrunner` to the `docker` group (needed by
  `game-ci/unity-builder`), which is effectively root.

Re-running `install` is safe: registered runners are kept and only missing ones are added.

## Using the runners

```yaml
runs-on: [self-hosted, trusted]
```

Use them only for workflows triggered by `push`, tags or `workflow_dispatch`. Keep
`pull_request` workflows on GitHub-hosted runners.

## Managing

```bash
sudo ./setup-runners.sh status
journalctl -fu 'actions.runner.*'
sudo ./setup-runners.sh uninstall
```
