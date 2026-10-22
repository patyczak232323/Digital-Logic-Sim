# Self-hosted GitHub Actions runners

Sets up ephemeral, isolated runners on an Ubuntu server (6 by default).

- Each runner is a systemd service (`gh-runner@N`) that starts a fresh container,
  runs one job and is then deleted. Nothing a job leaves behind survives.
- Containers run under [Sysbox](https://github.com/nestybox/sysbox), so jobs can use
  Docker (needed by `game-ci/unity-builder`) and passwordless `sudo` inside the
  container without having root on the host.
- Runners cannot reach private networks (LAN) or the host itself (`BLOCK_LAN=1`).
- The image is rebuilt weekly to pick up new runner releases.

## Setup

1. Create a fine-grained token at <https://github.com/settings/personal-access-tokens/new>
   with access to this repository only and **Administration: Read and write**.
2. On the server:

   ```bash
   sudo ./setup-runners.sh install
   ```

Options are environment variables, e.g.
`sudo RUNNER_COUNT=4 PERSIST_DOCKER_CACHE=1 ./setup-runners.sh install`.
See the header of `setup-runners.sh` for the full list.

`PERSIST_DOCKER_CACHE=1` keeps each runner's pulled Docker images (the Unity image is
several GB) between jobs. Builds start much faster, but the cache outlives the job.

## Using the runners

```yaml
runs-on: [self-hosted, trusted]
```

Use them only for workflows triggered by `push`, tags or `workflow_dispatch`. Keep
`pull_request` workflows on GitHub-hosted runners.

## Managing

```bash
sudo ./setup-runners.sh status
journalctl -fu 'gh-runner@*'
sudo ./setup-runners.sh uninstall
```
