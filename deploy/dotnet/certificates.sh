#!/bin/sh
set -eu
mkdir -p /private /public
umask 077
if [ -e /private/broker.key ] || [ -e /private/broker.crt ]; then
    test -s /private/broker.key && test -s /private/broker.crt
else
    # Same self-signed broker certificate as the Node-RED deployment.
    openssl req -x509 -newkey rsa:2048 -noenc -days 365 -sha256 \
        -keyout /private/broker.key -out /private/broker.crt -subj '/CN=mosquitto' \
        -addext 'subjectAltName=DNS:localhost,DNS:mosquitto,IP:127.0.0.1' \
        -addext 'basicConstraints=critical,CA:FALSE' \
        -addext 'keyUsage=critical,digitalSignature,keyEncipherment' -addext 'extendedKeyUsage=serverAuth'
fi
chown 1883:1883 /private/broker.key /private/broker.crt
chmod 600 /private/broker.key
chmod 644 /private/broker.crt
cp /private/broker.crt /public/broker.crt
chmod 644 /public/broker.crt
