#!/bin/sh
# Starts Mosquitto with logins: "gateway" may publish and subscribe under iot/, "viewer" (MQTTX, dashboards) may only
# read them. The passwords come from MQTT_GATEWAY_PASSWORD and MQTT_VIEWER_PASSWORD (optional; without it there is no
# viewer). Without a gateway password the broker does not start, so a device can't come up open by mistake.
set -eu
# The password and ACL files are created readable by their owner only.
umask 077

refuse() {
    echo "broker: $1" >&2
    exit 1
}
gateway="${MQTT_GATEWAY_PASSWORD:-}"
viewer="${MQTT_VIEWER_PASSWORD:-}"
[ -n "$gateway" ] || refuse "MQTT_GATEWAY_PASSWORD is not set; refusing to start a broker without logins."
# qbee leaves a parameter it doesn't know as the literal text $(NAME).
case "$gateway$viewer" in
    *'$('*) refuse "a broker password is an unset qbee parameter, \$(...); add it to the device or group in qbee." ;;
esac

dir=/mosquitto/auth
mkdir -p "$dir"
: > "$dir/passwd"
mosquitto_passwd -b "$dir/passwd" gateway "$gateway"
cat > "$dir/acl" <<'EOF'
user gateway
topic readwrite iot/#
EOF
if [ -n "$viewer" ]; then
    mosquitto_passwd -b "$dir/passwd" viewer "$viewer"
    printf 'user viewer\ntopic read iot/#\n' >> "$dir/acl"
fi
chown -R mosquitto:mosquitto "$dir"
chmod 700 "$dir"
chmod 600 "$dir/passwd" "$dir/acl"

exec /usr/sbin/mosquitto -c /mosquitto/config/mosquitto.conf
