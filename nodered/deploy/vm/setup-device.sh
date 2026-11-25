#!/usr/bin/env bash
# Turns a fresh Ubuntu VM into a qbee staging device: Docker Engine with Compose, then the qbee agent.
# Run inside the VM as root: setup-device.sh [file holding the qbee bootstrap key]. The key file is deleted once
# read. Without a key, only Docker is installed. Safe to run again: finished steps are skipped.
# With ZEROTIER_NETWORK set (a 16-character network ID), it also installs ZeroTier and joins that network; approve the
# device in ZeroTier Central, then give it the qbee parameter ZEROTIER_IP. Staging devices only.
set -euo pipefail

key=""
if [ -n "${1:-}" ] && [ -f "$1" ]; then
    key="$(head -n 1 "$1" | tr -d '[:space:]')"
    rm -f "$1"
fi

# Small machines (such as Oracle's or Google's free 1 GB VMs) get a 2 GB swap file, so one gateway, its broker and the
# simulator fit without the kernel killing a container.
mem_mb=$(awk '/^MemTotal:/ {print int($2 / 1024)}' /proc/meminfo)
if [ "$mem_mb" -lt 2048 ] && ! swapon --show=NAME --noheadings | grep -q .; then
    echo "Only ${mem_mb} MB of RAM and no swap: adding a 2 GB swap file at /swapfile..."
    fallocate -l 2G /swapfile || dd if=/dev/zero of=/swapfile bs=1M count=2048
    chmod 600 /swapfile
    mkswap /swapfile > /dev/null
    swapon /swapfile
    grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
    echo "vm.swappiness = 10" > /etc/sysctl.d/90-swappiness.conf
    sysctl -q -p /etc/sysctl.d/90-swappiness.conf
fi

if docker compose version > /dev/null 2>&1; then
    echo "Docker with Compose is already installed."
else
    echo "Installing Docker Engine and the Compose plugin from Ubuntu's packages..."
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y docker.io docker-compose-v2
    systemctl enable --now docker
fi
docker compose version

if command -v qbee-agent > /dev/null 2>&1; then
    echo "The qbee agent is already installed: $(systemctl is-active qbee-agent 2>/dev/null || echo unknown state)."
elif [ -z "$key" ]; then
    echo "No bootstrap key given: the qbee agent is not installed."
else
    echo "Installing the qbee agent..."
    wget -qO - https://setup.qbee.io | bash -s -- --bootstrap-key "$key"
    echo "qbee agent: $(systemctl is-active qbee-agent 2>/dev/null || echo installed)"
fi

network="${ZEROTIER_NETWORK:-}"
if [ -z "$network" ]; then
    echo "No ZEROTIER_NETWORK given: ZeroTier is not installed."
    exit 0
fi
if ! [[ "$network" =~ ^[0-9a-f]{16}$ ]]; then
    echo "ZEROTIER_NETWORK must be a 16-character network ID, such as 8d1c312afa58e883." >&2
    exit 1
fi
# From ZeroTier's own snap: the apt repository that install.zerotier.com sets up fails on Ubuntu 24.04 (its signing
# key isn't the one the script installs).
zt=/snap/bin/zerotier-cli
if [ -x "$zt" ]; then
    echo "ZeroTier is already installed."
else
    echo "Installing ZeroTier..."
    snap install zerotier
fi
# Lets Docker publish ports on the ZeroTier IP even when ZeroTier connects after Docker starts the gateway at boot.
echo "net.ipv4.ip_nonlocal_bind = 1" > /etc/sysctl.d/90-zerotier-bind.conf
sysctl -q -p /etc/sysctl.d/90-zerotier-bind.conf
# A fresh install's service takes a few seconds to start; until then the CLI fails with "missing port".
for _ in $(seq 30); do
    "$zt" info > /dev/null 2>&1 && break
    sleep 1
done
"$zt" join "$network"
echo "ZeroTier node $("$zt" info | awk '{print $3}') asked to join $network."
echo "Approve it in ZeroTier Central (my.zerotier.com), note its IP, and set the qbee parameter ZEROTIER_IP to it."
