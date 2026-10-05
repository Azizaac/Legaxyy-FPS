"""
Deploy web-server files to the aaPanel host over SFTP.

Credentials are NEVER hardcoded. Provide them via environment variables or a
local `.env` file (gitignored):

    LEGAXYY_SERVER_HOST   e.g. 203.0.113.10          (required)
    LEGAXYY_SERVER_USER   e.g. your_ssh_user         (required)
    LEGAXYY_SERVER_PASS   SSH/sudo password          (optional; prompted if omitted)
    LEGAXYY_TARGET_DIR    e.g. /www/wwwroot/your-domain.com
    LEGAXYY_TMP_DIR       e.g. /tmp/legaxyy_web_deploy
    LEGAXYY_INSTALLER     installer .exe filename     (optional; newest in
                          web-server/downloads is used if omitted)

If LEGAXYY_SERVER_PASS is empty the script uses SSH key auth and, when a sudo
password is still required, prompts for it interactively.
"""

import os
import sys
import getpass
import shlex
import paramiko

BASE_DIR = os.path.dirname(os.path.abspath(__file__))


def load_env_file(path):
    """Minimal .env loader (KEY=VALUE lines, # comments) without extra deps."""
    if not os.path.exists(path):
        return
    with open(path, "r", encoding="utf-8") as f:
        for raw in f:
            line = raw.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, val = line.partition("=")
            key = key.strip()
            val = val.strip().strip('"').strip("'")
            if key and key not in os.environ:
                os.environ[key] = val


load_env_file(os.path.join(BASE_DIR, ".env"))

SERVER_HOST = os.environ.get("LEGAXYY_SERVER_HOST", "")
SERVER_USER = os.environ.get("LEGAXYY_SERVER_USER", "")
_PASS_ENV   = os.environ.get("LEGAXYY_SERVER_PASS")  # None = not provided at all
SERVER_PASS = _PASS_ENV or ""
TARGET_DIR  = os.environ.get("LEGAXYY_TARGET_DIR", "/www/wwwroot/fps.legaxyy.my.id")
TMP_DIR     = os.environ.get("LEGAXYY_TMP_DIR", "/tmp/legaxyy_web_deploy")
INSTALLER_NAME = os.environ.get("LEGAXYY_INSTALLER", "")


def resolve_installer_name(local_web):
    """Return the installer filename to deploy (env override, else newest .exe)."""
    if INSTALLER_NAME:
        return INSTALLER_NAME
    downloads = os.path.join(local_web, "downloads")
    exes = [f for f in os.listdir(downloads) if f.lower().endswith(".exe")]
    if not exes:
        raise FileNotFoundError(f"No installer .exe found in {downloads}")
    exes.sort(key=lambda f: os.path.getmtime(os.path.join(downloads, f)), reverse=True)
    return exes[0]


def progress_callback(transferred, total):
    percent = (transferred / total) * 100
    mb_done = transferred / (1024 * 1024)
    mb_tot  = total / (1024 * 1024)
    sys.stdout.write(f"\r  >> Progress: {mb_done:.1f} MB / {mb_tot:.1f} MB ({percent:.1f}%)")
    sys.stdout.flush()


def sudo_exec(ssh, cmd, password=None):
    # shlex.quote prevents shell injection from the command string.
    # The password is written to sudo's stdin, never embedded in the command
    # line, so it cannot leak into the server's process list.
    full_cmd = f"sudo -S -p '' bash -c {shlex.quote(cmd)}"
    stdin, stdout, stderr = ssh.exec_command(full_cmd)
    pw = password if password is not None else SERVER_PASS
    if pw:
        try:
            stdin.write(pw + "\n")
            stdin.flush()
        except Exception:
            pass
    out = stdout.read().decode("utf-8")
    err = stderr.read().decode("utf-8")
    return out, err


def main():
    global SERVER_PASS

    if not SERVER_HOST or not SERVER_USER:
        print("[-] Missing LEGAXYY_SERVER_HOST / LEGAXYY_SERVER_USER.")
        print("    Set them as environment variables or in a local .env file.")
        print("    See the docstring at the top of this script.")
        return

    # Only prompt when no password was supplied AT ALL (env var absent) and we
    # have an interactive terminal. Setting the env var to an empty string means
    # "use SSH key + passwordless sudo, never prompt".
    if not SERVER_PASS and _PASS_ENV is None and sys.stdin.isatty():
        SERVER_PASS = getpass.getpass(
            f"[?] SSH/sudo password for {SERVER_USER}@{SERVER_HOST} "
            f"(leave empty to use SSH key): "
        )

    print(f"[*] Connecting to {SERVER_USER}@{SERVER_HOST} via Tailscale...")
    ssh = paramiko.SSHClient()

    # Trust only known host keys to prevent man-in-the-middle attacks.
    # If the server identity is unknown, connect once with the OpenSSH client:
    #     ssh <user>@<host>
    ssh.load_system_host_keys()
    known_hosts = os.path.expanduser(os.path.join("~", ".ssh", "known_hosts"))
    if os.path.exists(known_hosts):
        try:
            ssh.load_host_keys(known_hosts)
        except Exception:
            pass
    ssh.set_missing_host_key_policy(paramiko.RejectPolicy())

    try:
        ssh.connect(SERVER_HOST, username=SERVER_USER, password=SERVER_PASS or None, timeout=10)
        print("[+] SSH connection successful!")
    except paramiko.SSHException as e:
        print(f"[-] SSH handshake failed: {e}")
        print(f"    If the server identity is unknown, first run: ssh {SERVER_USER}@{SERVER_HOST}")
        return
    except Exception as e:
        print(f"[-] SSH Connection failed: {e}")
        return

    # Add Windows local public key to authorized_keys
    pub_key_path = os.path.expanduser("~/.ssh/id_ed25519.pub")
    if os.path.exists(pub_key_path):
        with open(pub_key_path, "r", encoding="utf-8") as f:
            pub_key = f.read().strip()
        add_key_cmd = f'mkdir -p ~/.ssh && chmod 700 ~/.ssh && (grep -qF "{pub_key}" ~/.ssh/authorized_keys 2>/dev/null || echo "{pub_key}" >> ~/.ssh/authorized_keys) && chmod 600 ~/.ssh/authorized_keys'
        ssh.exec_command(add_key_cmd)
        sudo_exec(ssh, f'mkdir -p /root/.ssh && chmod 700 /root/.ssh && (grep -qF "{pub_key}" /root/.ssh/authorized_keys 2>/dev/null || echo "{pub_key}" >> /root/.ssh/authorized_keys) && chmod 600 /root/.ssh/authorized_keys')
        print("[+] Added Windows id_ed25519.pub to authorized_keys (passwordless SSH now enabled)!")

    # Prepare temp staging dir
    ssh.exec_command(f"rm -rf {TMP_DIR} && mkdir -p {TMP_DIR}/downloads")

    print("[*] Opening SFTP channel...")
    sftp = ssh.open_sftp()

    local_web = os.path.join(BASE_DIR, "web-server")

    # 1. Upload index.html
    local_index = os.path.join(local_web, "index.html")
    print(f"[*] Uploading index.html...")
    sftp.put(local_index, f"{TMP_DIR}/index.html")
    print("    -> Done!")

    # 2. Upload version.json
    local_ver = os.path.join(local_web, "version.json")
    print(f"[*] Uploading version.json...")
    sftp.put(local_ver, f"{TMP_DIR}/version.json")
    print("    -> Done!")

    # 3. Upload installer
    installer = resolve_installer_name(local_web)
    local_setup = os.path.join(local_web, "downloads", installer)
    setup_size = os.path.getsize(local_setup) / (1024 * 1024)
    print(f"[*] Uploading {installer} ({setup_size:.1f} MB)...")
    sftp.put(local_setup, f"{TMP_DIR}/downloads/{installer}", callback=progress_callback)
    print("\n    -> Installer upload complete!")

    sftp.close()

    # Move files into aaPanel website directory
    print("[*] Installing files into aaPanel target directory...")
    sudo_exec(ssh, f"mkdir -p {TARGET_DIR}/downloads")
    sudo_exec(ssh, f"cp -f {TMP_DIR}/index.html {TARGET_DIR}/index.html")
    sudo_exec(ssh, f"cp -f {TMP_DIR}/version.json {TARGET_DIR}/version.json")
    sudo_exec(ssh, f"cp -f {TMP_DIR}/downloads/{installer} {TARGET_DIR}/downloads/{installer}")
    sudo_exec(ssh, f"rm -rf {TARGET_DIR}/web-server {TMP_DIR}")
    sudo_exec(ssh, f"chown -R www:www {TARGET_DIR} && chmod -R 755 {TARGET_DIR}")
    print("[+] Files successfully deployed to aaPanel and permissions set!")

    # Verify files
    out, _ = sudo_exec(ssh, f"ls -la {TARGET_DIR} && ls -la {TARGET_DIR}/downloads")
    print("\n[+] Verification of server files:\n" + out)

    # Check Cloudflare Tunnel configuration
    print("[*] Checking Cloudflare Tunnel status on server...")
    out_cf, _ = sudo_exec(ssh, "ps aux | grep cloudflared | grep -v grep")
    print("[+] Cloudflared Process:\n" + (out_cf if out_cf else "cloudflared process not found"))

    # Test local curl on server
    out_curl, _ = sudo_exec(ssh, f"curl -s -H 'Host: fps.legaxyy.my.id' http://127.0.0.1/version.json")
    print("[+] Test local Nginx vhost response:\n" + out_curl)

    ssh.close()
    print("\n[🎉] DEPLOYMENT FINISHED 100%!")


if __name__ == "__main__":
    main()
