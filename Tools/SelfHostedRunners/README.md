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

Use them for workflows triggered by `push`, tags or `workflow_dispatch`, and for
`pull_request` workflows only when pull requests from forks are skipped (see
`unity-tests.yml`): a fork's code would otherwise run on this host.

Current users (all need `RUNNER_DOCKER=1`):

- `release.yml` and `unofficial-build.yml`: the Unity build jobs. Publishing stays on
  GitHub-hosted runners, which also turn the uploaded `.tar` builds into release zips.
- `unity-tests.yml`: every run, including pull requests from branches of this repository.
  Pull requests from forks are skipped.

Unity runs in Docker as root and writes into both the checkout and the action's own
folder under `_work/_actions`. These jobs therefore hand the runner's whole `_work`
folder back to `ghrunner` (`chown` through a small `alpine` container) at the start and
end of every job. They need no sudo and do not touch anything outside `_work`.

If a job was killed before that last step, the next job on that runner fails in
"Prepare workflow directory" with "Access to the path ... is denied". Fix it once with
`sudo chown -R ghrunner:ghrunner /mnt/raid/github-runners-work` (or the runner's
`_work` folder).

## Managing

```bash
sudo ./setup-runners.sh status
journalctl -fu 'actions.runner.*'
sudo ./setup-runners.sh uninstall
```
