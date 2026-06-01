#!/usr/bin/env bash
# Issue a Crypto AI Terminal license key (seller-side, offline).
#
# The app embeds the PUBLIC key and validates signatures offline. You sign
# license payloads with the PRIVATE key (keep it secret — never commit it).
#
# Usage:
#   tools/issue-license.sh "Customer Name" [edition] [expiresISO|none] [machineId|none] [privateKeyPath]
#
# Examples:
#   tools/issue-license.sh "Acme Trading"
#   tools/issue-license.sh "Acme Trading" Pro 2027-01-01T00:00:00Z
#   tools/issue-license.sh "Acme Trading" Pro none A1B2C3D4E5F6A7B8   # bind to a machine
#
# The customer's Machine ID is shown in the app's License activation dialog.
set -euo pipefail

NAME="${1:?Customer name required}"
EDITION="${2:-Pro}"
EXPIRES="${3:-none}"
MACHINE="${4:-none}"
PRIV="${5:-.license-signing/private.pem}"

[ -f "$PRIV" ] || { echo "Private key not found: $PRIV" >&2; exit 1; }

json_field() { # name value-or-none -> "name":<null|"value">
  if [ "$2" = "none" ]; then printf '"%s":null' "$1"; else printf '"%s":"%s"' "$1" "$2"; fi
}

ISSUED="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
PAYLOAD="{\"name\":\"$NAME\",\"edition\":\"$EDITION\",$(json_field expires "$EXPIRES"),$(json_field machine "$MACHINE"),\"issued\":\"$ISSUED\"}"

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
printf '%s' "$PAYLOAD" > "$TMP/payload.bin"
openssl dgst -sha256 -sign "$PRIV" -out "$TMP/sig.bin" "$TMP/payload.bin"

SIG_B64="$(base64 -w0 "$TMP/sig.bin")"
PAY_B64URL="$(base64 -w0 "$TMP/payload.bin" | tr '+/' '-_' | tr -d '=')"

echo "$PAY_B64URL.$SIG_B64"
