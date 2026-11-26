#!/bin/sh
# Starts Mosquitto with logins: "gateway" may publish and subscribe under spBv1.0/, "viewer" (MQTTX, dashboards) may
# only read it. The passwords come from MQTT_GATEWAY_PASSWORD and MQTT_VIEWER_PASSWORD (optional; without it there is
# no viewer). Without a gateway password the broker does not start, so a device can't come up open by mistake.
set -eu
# The password and ACL files are created readable by their owner (mosquitto) only.
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
: > "$dir/passwd"
mosquitto_passwd -b "$dir/passwd" gateway "$gateway"
printf 'user gateway\ntopic readwrite spBv1.0/#\n' > "$dir/acl"
if [ -n "$viewer" ]; then
    mosquitto_passwd -b "$dir/passwd" viewer "$viewer"
    printf 'user viewer\ntopic read spBv1.0/#\n' >> "$dir/acl"
fi

exec /usr/sbin/mosquitto -c /mosquitto/config/mosquitto.conf
