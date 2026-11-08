#!/bin/bash
# Builds the ext4 fixtures of feature 011 (repairing SSH access). Developer tooling only: run in Docker
# (debian:bookworm, e2fsprogs 1.47); the product never uses Linux tools.
#
#   docker run --rm -v "<repo>/tests/fixtures/ext4:/w/scripts:ro" \
#              -v "<repo>/tests/HddBackup.Core.Tests/Fixtures/ext4:/w/out" \
#              debian:bookworm bash /w/scripts/make-ssh-repair-fixtures.sh
#
# Outputs (in /w/out), small Debian-like roots with merged /usr, each with <name>.manifest.tsv:
#   edit-ssh-ok.img.gz          server enabled, pi has a key; reference (nothing to repair)
#   edit-ssh-nokey.img.gz       server enabled, PasswordAuthentication no; pi has NO .ssh folder; bob's key file
#                               has no final newline; eve's key file uses CRLF
#   edit-ssh-disabled.img.gz    server installed but not enabled
#   edit-ssh-masked.img.gz      ssh.service masked in /etc/systemd/system (-> /dev/null)
#   edit-ssh-allowusers.img.gz  AllowUsers in the main file and DenyUsers in a drop-in; pi not listed
#   edit-ssh-allowgroups.img.gz AllowGroups sshusers; pi is not a member
#   edit-ssh-badshell.img.gz    pi has /usr/sbin/nologin as shell
#   edit-ssh-badmodes.img.gz    pi: home 777, .ssh 755, authorized_keys 666 owned by root
#   edit-ssh-dropin.img.gz      Include first, PubkeyAuthentication no, then a Match block
#   edit-ssh-keyscommand.img.gz AuthorizedKeysCommand set
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq e2fsprogs attr acl >/dev/null

OUT=/w/out
WORK=/tmp/sshrep
rm -rf "${WORK:?}" && mkdir -p "$WORK" "$OUT"
cd "$WORK"

export E2FSPROGS_FAKE_TIME=1760000000
FEATURES="has_journal,ext_attr,resize_inode,dir_index,filetype,extent,64bit,flex_bg,sparse_super,large_file,huge_file,dir_nlink,extra_isize,metadata_csum"
UUID=11111111-2222-3333-4444-55555555555a
SEED=66666666-7777-8888-9999-aaaaaaaaaaae
MIB=$((1024 * 1024))

# shellcheck source=fixture-lib.sh
source "$(dirname "$0")/fixture-lib.sh"

HASH='$6$PLANTEDSECRET$Zm9vYmFyYmF6cXV4cXV1eHF1dXhxdXV4Zm9vYmFyYmF6cXV4cXV1eHF1dXhxdXV4'
BLOB='AAAAC3NzaC1lZDI1NTE5AAAAIPLANTEDKEYBLOBxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'

put() { mkdir -p "$(dirname "$1")"; cat > "$1"; chmod "${2:-644}" "$1"; }

base_root() { # dir (a directory below $WORK)
    local d=${1:?}
    rm -rf "${d:?}"
    mkdir -p "$d"/{etc/ssh/sshd_config.d,etc/systemd/system/multi-user.target.wants,usr/lib/systemd/system,usr/sbin,usr/bin,home,root,var,tmp}
    ln -s usr/lib "$d/lib"; ln -s usr/bin "$d/bin"; ln -s usr/sbin "$d/sbin"
    chmod 1777 "$d/tmp"
    printf 'PRETTY_NAME="Debian GNU/Linux 12"\nNAME="Debian GNU/Linux"\nVERSION_ID="12"\nID=debian\n' > "$d/etc/os-release"
    printf '#!/bin/sh\n' > "$d/usr/lib/systemd/systemd"; chmod 755 "$d/usr/lib/systemd/systemd"
    for t in sysinit sockets timers paths slices getty rescue basic; do : > "$d/usr/lib/systemd/system/$t.target"; done
    printf '[Unit]\nDescription=Multi-User System\nRequires=basic.target\nWants=getty.target\n' > "$d/usr/lib/systemd/system/multi-user.target"
    ln -s multi-user.target "$d/usr/lib/systemd/system/default.target"
    ln -s /lib/systemd/system/multi-user.target "$d/etc/systemd/system/default.target"
    printf '/bin/sh\n/bin/bash\n' > "$d/etc/shells"
    printf '#!/bin/sh\n' > "$d/usr/bin/bash"; chmod 755 "$d/usr/bin/bash"
    printf '#!/bin/sh\n' > "$d/usr/bin/sh"; chmod 755 "$d/usr/bin/sh"
    printf 'root:x:0:0:root:/root:/bin/bash\ndaemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin\nnobody:x:65534:65534:nobody:/nonexistent:/usr/sbin/nologin\n' > "$d/etc/passwd"
    printf 'root:*:19000:0:99999:7:::\ndaemon:*:19000:0:99999:7:::\nnobody:*:19000:0:99999:7:::\n' > "$d/etc/shadow"; chmod 640 "$d/etc/shadow"
    printf 'root:x:0:\ndaemon:x:1:\nnogroup:x:65534:\n' > "$d/etc/group"
}

ssh_server() { # dir mode(yes|no|masked)
    local d=$1 on=$2
    printf '\177ELF' > "$d/usr/sbin/sshd"; chmod 755 "$d/usr/sbin/sshd"
    put "$d/usr/lib/systemd/system/ssh.service" <<'EOF'
[Unit]
Description=OpenBSD Secure Shell server
After=network.target auditd.service
ConditionPathExists=!/etc/ssh/sshd_not_to_be_run

[Service]
ExecStartPre=/usr/sbin/sshd -t
ExecStart=/usr/sbin/sshd -D $SSHD_OPTS
Restart=on-failure

[Install]
WantedBy=multi-user.target
Alias=sshd.service
EOF
    if [ "$on" = yes ]; then
        ln -s /lib/systemd/system/ssh.service "$d/etc/systemd/system/multi-user.target.wants/ssh.service"
        ln -s /lib/systemd/system/ssh.service "$d/etc/systemd/system/sshd.service"
    elif [ "$on" = masked ]; then
        ln -s /dev/null "$d/etc/systemd/system/ssh.service"
    fi
}

sshd_config() { # dir lines-before-include lines-after-include [tail]
    cat > "$1/etc/ssh/sshd_config" <<EOF
# This is the sshd server system-wide configuration file.
${2:-}
Include /etc/ssh/sshd_config.d/*.conf
${3:-}
KbdInteractiveAuthentication no
UsePAM yes
X11Forwarding yes
PrintMotd no
AcceptEnv LANG LC_*
Subsystem	sftp	/usr/lib/openssh/sftp-server
${4:-}
EOF
}

add_user() { # dir name uid shadowfield [shell]
    local d=$1 n=$2 uid=$3 sh=$4 shell=${5:-/bin/bash}
    printf '%s:x:%s:%s:%s:/home/%s:%s\n' "$n" "$uid" "$uid" "$n" "$n" "$shell" >> "$d/etc/passwd"
    printf '%s:%s:19000:0:99999:7:::\n' "$n" "$sh" >> "$d/etc/shadow"
    printf '%s:x:%s:\n' "$n" "$uid" >> "$d/etc/group"
    mkdir -p "$d/home/$n"
    chown "$uid:$uid" "$d/home/$n"; chmod 755 "$d/home/$n"
}

give_key() { # dir name uid [final-newline: yes|no|crlf]
    local d=$1 n=$2 uid=$3 mode=${4:-yes}
    mkdir -p "$d/home/$n/.ssh"
    case "$mode" in
        yes)  printf '# keys of %s\nssh-ed25519 %s %s@laptop\n' "$n" "$BLOB" "$n" ;;
        no)   printf 'ssh-ed25519 %s %s@laptop' "$BLOB" "$n" ;;
        crlf) printf '# keys of %s\r\nssh-ed25519 %s %s@laptop\r\n' "$n" "$BLOB" "$n" ;;
    esac > "$d/home/$n/.ssh/authorized_keys"
    chmod 600 "$d/home/$n/.ssh/authorized_keys"; chmod 700 "$d/home/$n/.ssh"
    chown -R "$uid:$uid" "$d/home/$n/.ssh"
}

mkfs_dir() { # name stage
    local name=${1:?} stage=${2:?}
    rm -f "${WORK:?}/${name:?}.ext4"
    mke2fs -q -F -t ext4 -b 4096 -I 256 -O none -O "$FEATURES" -L sshrep \
        -U "$UUID" -E "hash_seed=$SEED,root_owner=0:0" -d "$stage" "$WORK/$name.ext4" 16M
    e2fsck -fn "$WORK/$name.ext4" > "$WORK/$name-fsck.txt" 2>&1 || { cat "$WORK/$name-fsck.txt"; exit 1; }
}

publish() { # name stage
    manifest "$2" > "$OUT/$1.manifest.tsv"
    gzip -9 -n -c "$WORK/$1.ext4" > "$OUT/$1.img.gz"
    echo "built $1: $(stat -c %s "$WORK/$1.ext4") bytes, $(wc -l < "$OUT/$1.manifest.tsv") manifest entries"
}

build() { # name stage-dir
    mkfs_dir "$1" "$2"
    publish "$1" "$2"
}

# --- ok ------------------------------------------------------------------------------------------
S=$WORK/ok; base_root "$S"; ssh_server "$S" yes; sshd_config "$S"
add_user "$S" pi 1000 "$HASH"; give_key "$S" pi 1000
build edit-ssh-ok "$S"

# --- nokey ---------------------------------------------------------------------------------------
S=$WORK/nokey; base_root "$S"; ssh_server "$S" yes
sshd_config "$S" "" "PasswordAuthentication no"
add_user "$S" pi 1000 "$HASH"
add_user "$S" bob 1001 '!'; give_key "$S" bob 1001 no
add_user "$S" eve 1002 '!'; give_key "$S" eve 1002 crlf
build edit-ssh-nokey "$S"

# --- disabled ------------------------------------------------------------------------------------
S=$WORK/disabled; base_root "$S"; ssh_server "$S" no; sshd_config "$S"
add_user "$S" pi 1000 "$HASH"; give_key "$S" pi 1000
build edit-ssh-disabled "$S"

# --- masked --------------------------------------------------------------------------------------
S=$WORK/masked; base_root "$S"; ssh_server "$S" masked; sshd_config "$S"
add_user "$S" pi 1000 "$HASH"; give_key "$S" pi 1000
build edit-ssh-masked "$S"

# --- allowusers ----------------------------------------------------------------------------------
S=$WORK/allowusers; base_root "$S"; ssh_server "$S" yes
sshd_config "$S" "" "AllowUsers alice"
printf 'DenyUsers mallory\n' > "$S/etc/ssh/sshd_config.d/60-deny.conf"
add_user "$S" pi 1000 "$HASH"; give_key "$S" pi 1000
add_user "$S" alice 1003 "$HASH"; give_key "$S" alice 1003
build edit-ssh-allowusers "$S"

# --- allowgroups ---------------------------------------------------------------------------------
S=$WORK/allowgroups; base_root "$S"; ssh_server "$S" yes
sshd_config "$S" "" "AllowGroups sshusers"
add_user "$S" pi 1000 "$HASH"; give_key "$S" pi 1000
printf 'sshusers:x:2000:alice\n' >> "$S/etc/group"
add_user "$S" alice 1003 "$HASH"; give_key "$S" alice 1003
build edit-ssh-allowgroups "$S"

# --- badshell ------------------------------------------------------------------------------------
S=$WORK/badshell; base_root "$S"; ssh_server "$S" yes; sshd_config "$S"
add_user "$S" pi 1000 "$HASH" /usr/sbin/nologin; give_key "$S" pi 1000
build edit-ssh-badshell "$S"

# --- badmodes ------------------------------------------------------------------------------------
S=$WORK/badmodes; base_root "$S"; ssh_server "$S" yes; sshd_config "$S"
add_user "$S" pi 1000 "$HASH"; give_key "$S" pi 1000
chmod 777 "$S/home/pi"; chmod 755 "$S/home/pi/.ssh"; chmod 666 "$S/home/pi/.ssh/authorized_keys"
chown 0:0 "$S/home/pi/.ssh/authorized_keys"
build edit-ssh-badmodes "$S"

# --- dropin (Include first, explicit setting after it, Match block at the end) ----------------------
S=$WORK/dropin; base_root "$S"; ssh_server "$S" yes
sshd_config "$S" "" "PubkeyAuthentication no" $'Match Address 10.0.0.0/8\n    PasswordAuthentication no\n    X11Forwarding no'
printf 'X11Forwarding no\n' > "$S/etc/ssh/sshd_config.d/50-local.conf"
add_user "$S" pi 1000 '!'; give_key "$S" pi 1000
build edit-ssh-dropin "$S"

# --- keyscommand ---------------------------------------------------------------------------------
S=$WORK/keyscmd; base_root "$S"; ssh_server "$S" yes
sshd_config "$S" "" $'AuthorizedKeysCommand /usr/local/bin/fetch-keys %u\nAuthorizedKeysCommandUser nobody'
add_user "$S" pi 1000 '!'
build edit-ssh-keyscommand "$S"
