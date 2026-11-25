# Build context: the nodered/ folder. Targets: runtime (Node-RED), broker (Mosquitto with its configuration),
# certs (one-shot certificate), sim (simulated plant for staging devices), tools (test server and tests).

FROM nodered/node-red:5.0.9-debian AS runtime
USER root
# Debian's security fixes released since the base image was built; CI fails on any critical one left unfixed.
RUN apt-get update \
    && apt-get upgrade -y --no-install-recommends \
    && rm -rf /var/lib/apt/lists/*
# The npm modules the flow's function nodes load, in the Node-RED user directory where Node-RED looks for them.
COPY docker/package.json /data/package.json
RUN cd /data && npm install --omit=dev --no-audit --no-fund && npm cache clean --force
COPY docker/settings.js /data/settings.js
COPY flows.json docker/prepare-flows.js /tmp/build/
RUN node /tmp/build/prepare-flows.js /tmp/build/flows.json /data/flows.json \
    && rm -rf /tmp/build \
    && mkdir -p /data/state \
    && chown -R node-red:node-red /data
USER node-red
# Read by the flow at startup; set them per device. MQTT_* is the broker used until the registry names one;
# SEED_BROKER_HOST is the host of the registry's "local" broker when the registry is first created.
ENV GATEWAY_ID=nodered-gateway-01 \
    BROKER_ID=local \
    MQTT_HOST=mosquitto \
    MQTT_PORT=8883 \
    MQTT_USERNAME="" \
    MQTT_PASSWORD="" \
    SEED_BROKER_HOST=mosquitto
# Databases and the certificate cache; mounted as a volume so they survive rebuilds.
VOLUME /data/state
EXPOSE 1880

FROM eclipse-mosquitto:2.1.2-alpine AS broker
COPY docker/mosquitto.conf /mosquitto/config/mosquitto.conf
COPY docker/broker-start.sh /broker-start.sh
RUN sed -i 's/\r$//' /broker-start.sh
# Set them per device: the gateway's password is required, the viewer's optional. See broker-start.sh.
ENV MQTT_GATEWAY_PASSWORD="" \
    MQTT_VIEWER_PASSWORD=""
CMD ["sh", "/broker-start.sh"]

FROM alpine:3.20 AS certs
RUN apk add --no-cache openssl
COPY docker/make-certs.sh /make-certs.sh
RUN sed -i 's/\r$//' /make-certs.sh
ENTRYPOINT ["sh", "/make-certs.sh"]

# Simulated plant for staging devices (deploy/simulator): the Modbus server, and seed.js to register it. Node only.
FROM node:22-alpine3.22 AS sim
WORKDIR /app
COPY test/helpers/modbus-sim.js /app/test/helpers/
COPY deploy/simulator/ /app/deploy/simulator/
WORKDIR /app/deploy/simulator
USER node
ENV E2E_LISTEN=0.0.0.0
EXPOSE 1502
CMD ["node", "simulator.js", "1502"]

FROM nodered/node-red:5.0.9-debian AS tools
USER root
# Same layout as the repository: the unit tests read ../flows.json.
WORKDIR /app/test
COPY test/package.json test/package-lock.json ./
RUN npm ci --no-audit --no-fund && npm cache clean --force
COPY test/ ./
COPY flows.json /app/flows.json
COPY deploy/ /app/deploy/
RUN chown -R node-red:node-red /app
USER node-red
ENTRYPOINT []
CMD ["node", "sim.js", "1502"]
