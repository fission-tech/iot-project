#!/usr/bin/env bash
# Turns a fresh Ubuntu VM into a qbee staging device: Docker Engine with Compose, then the qbee agent.
# Run inside the VM as root: setup-device.sh [file holding the qbee bootstrap key]. The key file is deleted once
# read. Without a key, only Docker is installed. Safe to run again: finished steps are skipped.
set -euo pipefail

key=""
if [ -n "${1:-}" ] && [ -f "$1" ]; then
    key="$(head -n 1 "$1" | tr -d '[:space:]')"
    rm -f "$1"
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
