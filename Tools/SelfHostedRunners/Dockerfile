# Ephemeral GitHub Actions runner image.
#
# Built on top of myoung34/github-runner. The runner process runs as the
# unprivileged "runner" user, which has passwordless sudo *inside the
# container* (same as GitHub-hosted runners). The container itself runs under
# the Sysbox runtime, so root inside it is not root on the host.
ARG BASE_IMAGE=myoung34/github-runner:ubuntu-noble
FROM ${BASE_IMAGE}

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends sudo; \
    rm -rf /var/lib/apt/lists/*; \
    id runner >/dev/null 2>&1 || useradd -m -s /bin/bash runner; \
    getent group docker >/dev/null || groupadd docker; \
    usermod -aG sudo,docker runner; \
    echo 'runner ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/runner; \
    chmod 0440 /etc/sudoers.d/runner; \
    visudo -cf /etc/sudoers.d/runner
