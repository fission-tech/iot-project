#!/bin/sh
# Creates the broker's self-signed TLS certificate on the first start; later starts keep it.
# Same settings as scripts/New-LocalBrokerCertificate.ps1, plus the "mosquitto" name containers use.
set -eu
dir=/certs
if [ -s "$dir/broker.crt" ] && [ -s "$dir/broker.key" ]; then
    echo "Broker certificate already exists."
    exit 0
fi
openssl req -x509 -newkey rsa:2048 -noenc -keyout "$dir/broker.key" -out "$dir/broker.crt" -days 365 -sha256 \
    -subj "/CN=mosquitto" \
    -addext "subjectAltName=DNS:localhost,DNS:mosquitto,IP:127.0.0.1" \
    -addext "basicConstraints=critical,CA:FALSE" \
    -addext "keyUsage=critical,digitalSignature,keyEncipherment" \
    -addext "extendedKeyUsage=serverAuth"
# Mosquitto runs as its own user and must read the key; this folder holds nothing else.
chmod 644 "$dir/broker.crt" "$dir/broker.key"
echo "Created broker certificate in $dir."
