# Build context: the nodered/ folder. Targets: runtime (Node-RED), certs (one-shot certificate), tools (test server and tests).

FROM nodered/node-red:5.0.9-debian AS runtime
USER root
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
# Databases and the certificate cache; mounted as a volume so they survive rebuilds.
VOLUME /data/state
EXPOSE 1880

FROM alpine:3.20 AS certs
RUN apk add --no-cache openssl
COPY docker/make-certs.sh /make-certs.sh
RUN sed -i 's/\r$//' /make-certs.sh
ENTRYPOINT ["sh", "/make-certs.sh"]

FROM nodered/node-red:5.0.9-debian AS tools
USER root
# Same layout as the repository: the unit tests read ../flows.json.
WORKDIR /app/test
COPY test/package.json test/package-lock.json ./
RUN npm ci --no-audit --no-fund && npm cache clean --force
COPY test/ ./
COPY flows.json /app/flows.json
RUN chown -R node-red:node-red /app
USER node-red
ENTRYPOINT []
CMD ["node", "sim.js", "1502"]
