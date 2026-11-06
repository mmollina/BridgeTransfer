#!/usr/bin/env bash
# Starts an isolated OpenSSH SFTP server for BridgeTransfer integration tests.
# - Listens only on 127.0.0.1 (ports 2222 and 2223), never touches the system sshd or its config.
# - Uses a dedicated account "bttest" with no login shell (SFTP only).
# - Generates throwaway host and client keys plus a random password under tests/fixture/.run (git-ignored).
# Requires root (sshd privilege separation) on a disposable Linux machine or container.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUN="$HERE/.run"
USER_NAME="${BT_FIXTURE_USER:-bttest}"
PORT="${BT_FIXTURE_PORT:-2222}"
PORT_NOREPLACE="${BT_FIXTURE_PORT_NOREPLACE:-2223}"

if [[ $EUID -ne 0 ]]; then echo "Run as root inside a disposable test environment." >&2; exit 1; fi
command -v /usr/sbin/sshd >/dev/null || { echo "openssh-server is not installed." >&2; exit 1; }

"$HERE/stop-sftp-fixture.sh" >/dev/null 2>&1 || true
rm -rf "$RUN"; mkdir -p "$RUN" /run/sshd; chmod 755 "$RUN"

if ! id "$USER_NAME" >/dev/null 2>&1; then
  useradd --create-home --shell /usr/sbin/nologin "$USER_NAME"
fi
PASSWORD="$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 20)"
echo "$USER_NAME:$PASSWORD" | chpasswd
HOME_DIR="$(getent passwd "$USER_NAME" | cut -d: -f6)"
ROOT="$HOME_DIR/it"
rm -rf "$ROOT"; mkdir -p "$ROOT"; chown "$USER_NAME:$USER_NAME" "$ROOT"

ssh-keygen -q -t ed25519 -N '' -f "$RUN/host_ed25519"
ssh-keygen -q -t ed25519 -N '' -f "$RUN/host2_ed25519"
ssh-keygen -q -t ed25519 -N '' -C bt-it-plain -f "$RUN/client_ed25519"
ssh-keygen -q -t ed25519 -N 'correct horse' -C bt-it-encrypted -f "$RUN/client_ed25519_enc"
ssh-keygen -q -t rsa -b 3072 -m PEM -N '' -C bt-it-rsa-pem -f "$RUN/client_rsa_pem"
ssh-keygen -q -t ecdsa -b 256 -N '' -C bt-it-ecdsa -f "$RUN/client_ecdsa"
ssh-keygen -q -t ed25519 -N '' -C bt-it-not-authorized -f "$RUN/client_unauthorized"
if command -v puttygen >/dev/null; then
  puttygen "$RUN/client_ed25519" -O private -o "$RUN/client_ed25519_v3.ppk"
  puttygen "$RUN/client_rsa_pem" -O private --ppk-param version=2 -o "$RUN/client_rsa_v2.ppk"
  puttygen "$RUN/client_ed25519" -O private -o "$RUN/client_ed25519_enc.ppk" --new-passphrase <(echo "ppk pass")
fi
cat "$RUN"/client_ed25519.pub "$RUN"/client_ed25519_enc.pub "$RUN"/client_rsa_pem.pub "$RUN"/client_ecdsa.pub > "$RUN/authorized_keys"
chmod 644 "$RUN/authorized_keys"; chmod 600 "$RUN"/client_* "$RUN"/host*

write_config() {
  local port="$1" hostkey="$2" extra="$3" cfg="$4"
  cat > "$cfg" <<CFG
Port $port
ListenAddress 127.0.0.1
HostKey $hostkey
PidFile $RUN/sshd-$port.pid
AuthorizedKeysFile $RUN/authorized_keys
StrictModes no
PasswordAuthentication yes
KbdInteractiveAuthentication no
PubkeyAuthentication yes
PermitRootLogin no
AllowUsers $USER_NAME
UsePAM no
X11Forwarding no
AllowTcpForwarding no
ClientAliveInterval 0
Subsystem sftp internal-sftp $extra
CFG
}
write_config "$PORT" "$RUN/host_ed25519" "" "$RUN/sshd_config"
write_config "$PORT_NOREPLACE" "$RUN/host2_ed25519" "-P posix-rename" "$RUN/sshd_config_noreplace"

/usr/sbin/sshd -t -f "$RUN/sshd_config"
/usr/sbin/sshd -f "$RUN/sshd_config" -E "$RUN/sshd.log"
/usr/sbin/sshd -f "$RUN/sshd_config_noreplace" -E "$RUN/sshd_noreplace.log"

FP="$(ssh-keygen -lf "$RUN/host_ed25519.pub" | awk '{print $2}')"
cat > "$RUN/fixture.env" <<ENV
BT_IT_HOST=127.0.0.1
BT_IT_PORT=$PORT
BT_IT_PORT_NOREPLACE=$PORT_NOREPLACE
BT_IT_USER=$USER_NAME
BT_IT_PASSWORD=$PASSWORD
BT_IT_ROOT=$ROOT
BT_IT_KEY=$RUN/client_ed25519
BT_IT_KEY_ENC=$RUN/client_ed25519_enc
BT_IT_KEY_ENC_PASSPHRASE=correct horse
BT_IT_KEY_RSA_PEM=$RUN/client_rsa_pem
BT_IT_KEY_ECDSA=$RUN/client_ecdsa
BT_IT_KEY_UNAUTHORIZED=$RUN/client_unauthorized
BT_IT_HOST_FINGERPRINT=$FP
ENV
if [[ -f "$RUN/client_ed25519_v3.ppk" ]]; then
  cat >> "$RUN/fixture.env" <<ENV
BT_IT_KEY_PPK3=$RUN/client_ed25519_v3.ppk
BT_IT_KEY_PPK2_RSA=$RUN/client_rsa_v2.ppk
BT_IT_KEY_PPK_ENC=$RUN/client_ed25519_enc.ppk
BT_IT_KEY_PPK_ENC_PASSPHRASE=ppk pass
ENV
fi
chmod 600 "$RUN/fixture.env"
echo "SFTP fixture running on 127.0.0.1:$PORT (and :$PORT_NOREPLACE without posix-rename). Env: $RUN/fixture.env"
