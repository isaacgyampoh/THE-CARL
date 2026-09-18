#!/usr/bin/env bash
#
# Prepares a fresh Ubuntu 24.04 LTS server to run Zazi behind Cloudflare.
#
# Run it once, as root, on the VM:
#
#   sudo ZAZI_DOMAIN=example.com bash install.sh
#
# What it does NOT do: publish anything, touch DNS, or start Zazi. It installs the
# dependencies, creates the database, writes the service files and locks the firewall,
# then stops and tells you what is left. Starting happens after you have checked the
# configuration, because a financial service that comes up half-configured is worse
# than one that has not come up.
#
# Secrets are generated here, on this machine, and written to files only root can read.
# They are never printed and never leave the server.

set -euo pipefail

DOMAIN="${ZAZI_DOMAIN:-}"
if [ -z "$DOMAIN" ]; then
    echo "error: set ZAZI_DOMAIN to your domain, e.g. ZAZI_DOMAIN=example.com" >&2
    echo "       The API will be api.\$ZAZI_DOMAIN and the dashboard app.\$ZAZI_DOMAIN." >&2
    exit 1
fi

if [ "$(id -u)" -ne 0 ]; then
    echo "error: run with sudo." >&2
    exit 1
fi

say() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }

# ─── 1. Packages ─────────────────────────────────────────────────────────────
say "Installing .NET 8 runtime, PostgreSQL, Caddy and a firewall"

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq

# ASP.NET Core runtime, not the SDK: the server runs published output and has no
# reason to hold a compiler.
apt-get install -y -qq aspnetcore-runtime-8.0 || {
    # Ubuntu 24.04 carries it; older releases need Microsoft's feed.
    apt-get install -y -qq wget ca-certificates
    wget -q "https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb" -O /tmp/ms.deb
    dpkg -i /tmp/ms.deb && rm -f /tmp/ms.deb
    apt-get update -qq
    apt-get install -y -qq aspnetcore-runtime-8.0
}

apt-get install -y -qq postgresql postgresql-contrib ufw curl gnupg

# Caddy from its own repository; the distribution package lags and this needs >= 2.7
# for trusted_proxies.
if ! command -v caddy >/dev/null 2>&1; then
    curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
        | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
    curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
        | tee /etc/apt/sources.list.d/caddy-stable.list >/dev/null
    apt-get update -qq
    apt-get install -y -qq caddy
fi

# ─── 2. Service user and directories ─────────────────────────────────────────
say "Creating the zazi service account and directories"

# A system account with no shell and no home: it runs two processes and owns nothing
# a person would log in to.
id -u zazi >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin zazi

install -d -o root -g root -m 755 /opt/zazi
install -d -o root -g root -m 755 /opt/zazi/api /opt/zazi/web /opt/zazi/scripts
install -d -o root -g root -m 750 /etc/zazi
install -d -o zazi -g zazi -m 700 /var/lib/zazi /var/lib/zazi/keys
install -d -o root -g root -m 755 /var/log/caddy

# ─── 3. Database ─────────────────────────────────────────────────────────────
say "Creating the PostgreSQL role and database"

systemctl enable --now postgresql

DB_PASSWORD_FILE=/etc/zazi/.db-password
if [ ! -f "$DB_PASSWORD_FILE" ]; then
    # Generated here so no human picks it and it never travels.
    openssl rand -base64 36 | tr -d '\n/+=' | cut -c1-32 > "$DB_PASSWORD_FILE"
    chmod 600 "$DB_PASSWORD_FILE"
fi
DB_PASSWORD="$(cat "$DB_PASSWORD_FILE")"

sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='zazi'" | grep -q 1 \
    || sudo -u postgres psql -qc "CREATE ROLE zazi LOGIN PASSWORD '${DB_PASSWORD}'"
sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='zazi'" | grep -q 1 \
    || sudo -u postgres psql -qc "CREATE DATABASE zazi OWNER zazi"

# PostgreSQL stays on loopback. Nothing outside this machine has any business
# reaching it, and the applications are on the same host.
PG_CONF="$(sudo -u postgres psql -tAc 'SHOW config_file')"
sed -i "s/^#\?listen_addresses.*/listen_addresses = 'localhost'/" "$PG_CONF"
systemctl restart postgresql

# ─── 4. Application configuration ────────────────────────────────────────────
say "Writing /etc/zazi/api.env and /etc/zazi/web.env"

JWT_KEY_FILE=/etc/zazi/.jwt-key
if [ ! -f "$JWT_KEY_FILE" ]; then
    openssl rand -base64 48 > "$JWT_KEY_FILE"
    chmod 600 "$JWT_KEY_FILE"
fi
JWT_KEY="$(cat "$JWT_KEY_FILE")"

CONN="Host=127.0.0.1;Port=5432;Database=zazi;Username=zazi;Password=${DB_PASSWORD}"

# The same key in both files, deliberately: the dashboard validates what the API signs.
for svc in api web; do
    cat > "/etc/zazi/${svc}.env" <<ENVEOF
ConnectionStrings__DefaultConnection=${CONN}
ZAZI_JWT_KEY=${JWT_KEY}
ENVEOF
    chown root:root "/etc/zazi/${svc}.env"
    chmod 600 "/etc/zazi/${svc}.env"
done

# ─── 5. Services and proxy ───────────────────────────────────────────────────
say "Installing systemd units and the Caddy configuration"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

install -m 644 "$HERE/zazi-api.service"          /etc/systemd/system/
install -m 644 "$HERE/zazi-web.service"          /etc/systemd/system/
install -m 644 "$HERE/zazi-healthcheck.service"  /etc/systemd/system/
install -m 644 "$HERE/zazi-healthcheck.timer"    /etc/systemd/system/
install -m 644 "$HERE/Caddyfile"                 /etc/caddy/Caddyfile

# The domain lives here and nowhere else; the Caddyfile reads {$ZAZI_DOMAIN}.
echo "ZAZI_DOMAIN=${DOMAIN}" > /etc/default/caddy
chmod 644 /etc/default/caddy

systemctl daemon-reload

# ─── 6. Firewall ─────────────────────────────────────────────────────────────
say "Locking the firewall to Cloudflare"

# Only Cloudflare may reach the web ports. Two reasons: it stops anyone bypassing the
# WAF by hitting the origin directly, and it is what makes the CF-Connecting-IP header
# trustworthy — a header is only as good as the guarantee about who can set it.
#
# SSH is left open to everywhere here because locking yourself out of a remote VM is a
# worse first-day outcome than an exposed SSH port. Narrow it to your own address once
# you are in: ufw allow from <your-ip> to any port 22 && ufw delete allow 22
ufw --force reset >/dev/null
ufw default deny incoming >/dev/null
ufw default allow outgoing >/dev/null
ufw allow 22/tcp >/dev/null

for cidr in \
    173.245.48.0/20 103.21.244.0/22 103.22.200.0/22 103.31.4.0/22 \
    141.101.64.0/18 108.162.192.0/18 190.93.240.0/20 188.114.96.0/20 \
    197.234.240.0/22 198.41.128.0/17 162.158.0.0/15 104.16.0.0/13 \
    104.24.0.0/14 172.64.0.0/13 131.0.72.0/22 \
    2400:cb00::/32 2606:4700::/32 2803:f800::/32 2405:b500::/32 \
    2405:8100::/32 2a06:98c0::/29 2c0f:f248::/32
do
    ufw allow from "$cidr" to any port 80 proto tcp >/dev/null
    ufw allow from "$cidr" to any port 443 proto tcp >/dev/null
done

ufw --force enable >/dev/null

# Ports 5055 and 5056 are never opened. The services bind to 127.0.0.1 and the firewall
# would refuse anyway; both, because one of them will be wrong eventually.

say "Done. Nothing is running yet — by design."
cat <<'NEXTEOF'

Still to do, in this order:

  1. Publish the application to this server (see docs/PRODUCTION.md), so that
     /opt/zazi/api/Zazi.Api.dll and /opt/zazi/web/Zazi.Web.dll exist.

  2. Point DNS at this server in Cloudflare: A records for api and app, both
     PROXIED (orange cloud), SSL/TLS mode Full (strict).

  3. Start everything:
        sudo systemctl enable --now zazi-api zazi-web zazi-healthcheck.timer
        sudo systemctl reload caddy

  4. Check readiness, which is the one that proves the database is reachable:
        curl -fsS https://api.<your-domain>/ready

Database and signing secrets were generated on this machine and written to
/etc/zazi/*.env with mode 600. They were not printed and are not in the repository.
Back up /etc/zazi and /var/lib/zazi/keys — see docs/PRODUCTION.md.
NEXTEOF
