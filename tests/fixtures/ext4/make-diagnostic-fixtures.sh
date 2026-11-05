#!/bin/bash
# Builds the ext4 fixtures of feature 009 (diagnostic tools: SSH check, startup and scheduled tasks).
# Developer tooling only: run in Docker (debian:bookworm, e2fsprogs 1.47); the product never uses
# Linux tools.
#
#   docker run --rm -v "<repo>/tests/fixtures/ext4:/w/scripts:ro" \
#              -v "<repo>/tests/HddBackup.Core.Tests/Fixtures/ext4:/w/out" \
#              debian:bookworm bash /w/scripts/make-diagnostic-fixtures.sh
#
# Outputs (in /w/out), all small Debian-like roots with merged /usr:
#   diag-ssh-ok.img.gz         SSH server enabled; password and key login; drop-in file; user "pi"
#   diag-ssh-keys-only.img.gz  PasswordAuthentication no, PermitRootLogin no; key for "pi"; locked "guest"
#   diag-ssh-disabled.img.gz   SSH server installed, not enabled
#   diag-startup.img.gz        about forty services, timers, sockets, boot scripts, desktop entries, cron tasks
#   diag-pi-like.img.gz        MBR: FAT16 boot + ext4 root of a Raspberry Pi OS-like system, SSH not enabled
#
# Every fixture holds planted secrets (a "$6$PLANTEDSECRET" password hash, "PLANTEDKEYBLOB" inside
# key lines, a private key file) that no result of the tools may contain (SC-004).
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq e2fsprogs dosfstools mtools fdisk >/dev/null

OUT=/w/out
WORK=/tmp/diag
rm -rf "$WORK" && mkdir -p "$WORK" "$OUT"
cd "$WORK"

export E2FSPROGS_FAKE_TIME=1760000000
FEATURES="has_journal,ext_attr,resize_inode,dir_index,filetype,extent,64bit,flex_bg,sparse_super,large_file,huge_file,dir_nlink,extra_isize,metadata_csum"
UUID=11111111-2222-3333-4444-555555555557
SEED=66666666-7777-8888-9999-aaaaaaaaaaac
MIB=$((1024 * 1024))

# shellcheck source=fixture-lib.sh
source "$(dirname "$0")/fixture-lib.sh"

HASH='$6$PLANTEDSECRET$Zm9vYmFyYmF6cXV4cXV1eHF1dXhxdXV4Zm9vYmFyYmF6cXV4cXV1eHF1dXhxdXV4'
BLOB='AAAAC3NzaC1lZDI1NTE5AAAAIPLANTEDKEYBLOBxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'

put() { # file [mode]; content from stdin
    mkdir -p "$(dirname "$1")"
    cat > "$1"
    chmod "${2:-644}" "$1"
}

unit() { # dir name body-from-stdin  (vendor unit under usr/lib/systemd/system)
    put "$1/usr/lib/systemd/system/$2"
}

enable() { # dir unit target [linkname]  (what "systemctl enable" creates)
    local d=$1 u=$2 t=$3 l=${4:-$2}
    mkdir -p "$d/etc/systemd/system/$t.wants"
    ln -s "/lib/systemd/system/$u" "$d/etc/systemd/system/$t.wants/$l"
}

service() { # dir name description command [wantedby]
    local d=$1 n=$2 desc=$3 cmd=$4 wb=${5:-multi-user.target}
    unit "$d" "$n.service" <<EOF
[Unit]
Description=$desc
After=network.target

[Service]
ExecStart=$cmd

[Install]
WantedBy=$wb
EOF
}

timer() { # dir name description calendar
    local d=$1 n=$2 desc=$3 cal=$4
    unit "$d" "$n.timer" <<EOF
[Unit]
Description=$desc

[Timer]
OnCalendar=$cal
Persistent=true

[Install]
WantedBy=timers.target
EOF
    unit "$d" "$n.service" <<EOF
[Unit]
Description=$desc

[Service]
Type=oneshot
ExecStart=/usr/lib/$n/run
EOF
}

base_root() { # dir default-target os-id pretty
    local d=$1 dt=$2 id=$3 pretty=$4
    rm -rf "$d"
    mkdir -p "$d"/{etc/ssh/sshd_config.d,etc/systemd/system,usr/lib/systemd/system,usr/sbin,usr/bin,home,root,var,tmp}
    ln -s usr/lib "$d/lib"
    ln -s usr/bin "$d/bin"
    ln -s usr/sbin "$d/sbin"
    chmod 1777 "$d/tmp"
    printf 'PRETTY_NAME="%s"\nNAME="Debian GNU/Linux"\nVERSION_ID="12"\nID=%s\n' "$pretty" "$id" > "$d/etc/os-release"
    printf '#!/bin/sh\n' > "$d/usr/lib/systemd/systemd"; chmod 755 "$d/usr/lib/systemd/systemd"
    put "$d/usr/lib/systemd/system/sysinit.target" < /dev/null
    for t in sockets timers paths slices; do put "$d/usr/lib/systemd/system/$t.target" < /dev/null; done
    put "$d/usr/lib/systemd/system/getty.target" < /dev/null
    put "$d/usr/lib/systemd/system/rescue.target" < /dev/null
    put "$d/usr/lib/systemd/system/basic.target" <<'EOF'
[Unit]
Description=Basic System
Requires=sysinit.target
Wants=sockets.target timers.target paths.target slices.target
EOF
    put "$d/usr/lib/systemd/system/multi-user.target" <<'EOF'
[Unit]
Description=Multi-User System
Requires=basic.target
Wants=getty.target
EOF
    put "$d/usr/lib/systemd/system/graphical.target" <<'EOF'
[Unit]
Description=Graphical Interface
Requires=multi-user.target
EOF
    ln -s graphical.target "$d/usr/lib/systemd/system/default.target"
    ln -s "/lib/systemd/system/$dt" "$d/etc/systemd/system/default.target"
    cat > "$d/etc/shells" <<'EOF'
/bin/sh
/bin/bash
EOF
    printf 'root:x:0:0:root:/root:/bin/bash\ndaemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin\nsync:x:4:65534:sync:/bin:/bin/sync\nnobody:x:65534:65534:nobody:/nonexistent:/usr/sbin/nologin\n' > "$d/etc/passwd"
    printf 'root:*:19000:0:99999:7:::\ndaemon:*:19000:0:99999:7:::\nsync:*:19000:0:99999:7:::\nnobody:*:19000:0:99999:7:::\n' > "$d/etc/shadow"; chmod 640 "$d/etc/shadow"
    printf 'root:x:0:\ndaemon:x:1:\nnogroup:x:65534:\n' > "$d/etc/group"
}

ssh_server() { # dir enabled(yes|no)
    local d=$1 on=$2
    printf '\177ELF' > "$d/usr/sbin/sshd"; chmod 755 "$d/usr/sbin/sshd"
    unit "$d" ssh.service <<'EOF'
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
    unit "$d" ssh.socket <<'EOF'
[Unit]
Description=OpenBSD Secure Shell server socket

[Socket]
ListenStream=22
Accept=no

[Install]
WantedBy=sockets.target
EOF
    if [ "$on" = yes ]; then
        enable "$d" ssh.service multi-user.target
        ln -s /lib/systemd/system/ssh.service "$d/etc/systemd/system/sshd.service"
    fi
}

debian_sshd_config() { # dir extra-lines-before-include
    cat > "$1/etc/ssh/sshd_config" <<EOF
# This is the sshd server system-wide configuration file.
Include /etc/ssh/sshd_config.d/*.conf

${2:-}
KbdInteractiveAuthentication no
UsePAM yes
X11Forwarding yes
PrintMotd no
AcceptEnv LANG LC_*
Subsystem	sftp	/usr/lib/openssh/sftp-server
EOF
}

add_user() { # dir name uid shadowfield [keys: 0|1] [shell]
    local d=$1 n=$2 uid=$3 sh=$4 keys=${5:-0} shell=${6:-/bin/bash}
    printf '%s:x:%s:%s:%s:/home/%s:%s\n' "$n" "$uid" "$uid" "$n" "$n" "$shell" >> "$d/etc/passwd"
    printf '%s:%s:19000:0:99999:7:::\n' "$n" "$sh" >> "$d/etc/shadow"
    printf '%s:x:%s:\n' "$n" "$uid" >> "$d/etc/group"
    mkdir -p "$d/home/$n/.ssh"
    if [ "$keys" -gt 0 ]; then
        {
            echo "# keys of $n"
            echo "ssh-ed25519 $BLOB $n@laptop"
            [ "$keys" -gt 1 ] && echo "command=\"/bin/echo hi\",no-pty ssh-rsa $BLOB $n@old-desktop"
        } > "$d/home/$n/.ssh/authorized_keys"
        chmod 600 "$d/home/$n/.ssh/authorized_keys"
    fi
    chown -R "$uid:$uid" "$d/home/$n"
    chmod 700 "$d/home/$n/.ssh"
}

plant_private_key() { # dir user uid
    put "$1/home/$2/.ssh/id_ed25519" 600 <<'EOF'
-----BEGIN OPENSSH PRIVATE KEY-----
PLANTEDPRIVATEKEYBODYPLANTEDPRIVATEKEYBODYPLANTEDPRIVATEKEYBODY
-----END OPENSSH PRIVATE KEY-----
EOF
    chown "$3:$3" "$1/home/$2/.ssh/id_ed25519"
}

mkfs_dir() { # name stage sizeMiB
    local name=$1 stage=$2 size=$3
    rm -f "$WORK/$name.ext4"
    mke2fs -q -F -t ext4 -b 4096 -I 256 -O none -O "$FEATURES" -L diag \
        -U "$UUID" -E "hash_seed=$SEED,root_owner=0:0" -d "$stage" "$WORK/$name.ext4" "${size}M"
    e2fsck -fn "$WORK/$name.ext4" > "$WORK/$name-fsck.txt" 2>&1 || { cat "$WORK/$name-fsck.txt"; exit 1; }
}

publish() { # name
    gzip -9 -n -c "$WORK/$1.ext4" > "$OUT/$1.img.gz"
    echo "built $1: $(stat -c %s "$WORK/$1.ext4") bytes"
}

# --- diag-ssh-ok -------------------------------------------------------------------------------
S=$WORK/ssh-ok
base_root "$S" multi-user.target debian "Debian GNU/Linux 12 (bookworm)"
ssh_server "$S" yes
debian_sshd_config "$S"
put "$S/etc/ssh/sshd_config.d/50-local.conf" <<'EOF'
# Local settings (kept by the administrator)
PasswordAuthentication yes
EOF
add_user "$S" pi 1000 "$HASH" 2
plant_private_key "$S" pi 1000
mkfs_dir diag-ssh-ok "$S" 16
publish diag-ssh-ok

# --- diag-ssh-keys-only ------------------------------------------------------------------------
S=$WORK/ssh-keys
base_root "$S" multi-user.target debian "Debian GNU/Linux 12 (bookworm)"
ssh_server "$S" yes
debian_sshd_config "$S" $'PermitRootLogin no\nPasswordAuthentication no'
add_user "$S" pi 1000 "$HASH" 1
add_user "$S" guest 1001 '!'
plant_private_key "$S" pi 1000
mkfs_dir diag-ssh-keys-only "$S" 16
publish diag-ssh-keys-only

# --- diag-ssh-disabled -------------------------------------------------------------------------
S=$WORK/ssh-off
base_root "$S" multi-user.target debian "Debian GNU/Linux 12 (bookworm)"
ssh_server "$S" no
debian_sshd_config "$S"
add_user "$S" pi 1000 "$HASH" 1
mkfs_dir diag-ssh-disabled "$S" 16
publish diag-ssh-disabled

# --- diag-startup ------------------------------------------------------------------------------
S=$WORK/startup
base_root "$S" graphical.target debian "Debian GNU/Linux 12 (bookworm)"
ssh_server "$S" yes
debian_sshd_config "$S"
add_user "$S" pi 1000 "$HASH" 1

# System services in multi-user.target.
for n in cron rsyslog avahi-daemon bluetooth wpa_supplicant NetworkManager triggerhappy dphys-swapfile rpi-eeprom-update; do
    service "$S" "$n" "The $n service" "/usr/sbin/$n -f"
    enable "$S" "$n.service" multi-user.target
done
printf '\177ELF' > "$S/usr/sbin/cron"; chmod 755 "$S/usr/sbin/cron"
# A template instance, a graphical service, a service enabled only in an unreached target, a masked unit.
unit "$S" getty@.service <<'EOF'
[Unit]
Description=Getty on %I

[Service]
ExecStart=-/sbin/agetty -o '-p -- \\u' --noclear %I $TERM

[Install]
WantedBy=getty.target
DefaultInstance=tty1
EOF
enable "$S" getty@.service getty.target getty@tty1.service
service "$S" lightdm "Light Display Manager" "/usr/sbin/lightdm" graphical.target
enable "$S" lightdm.service graphical.target
service "$S" rescue-helper "Only in rescue mode" "/usr/local/bin/rescue-helper" rescue.target
enable "$S" rescue-helper.service rescue.target
service "$S" plymouth-start "Splash" "/usr/bin/plymouth start"
ln -s /dev/null "$S/etc/systemd/system/plymouth-start.service"
# Timers (each with a service) and sockets.
timer "$S" apt-daily "Daily apt download activities" "*-*-* 6,18:00"
timer "$S" apt-daily-upgrade "Daily apt upgrade and clean activities" "*-*-* 6:00"
timer "$S" man-db "Daily man-db regeneration" "daily"
timer "$S" logrotate "Daily rotation of log files" "daily"
timer "$S" fstrim "Discard unused blocks once a week" "weekly"
timer "$S" e2scrub_all "Periodic ext4 metadata check" "Sun *-*-* 03:10:00"
timer "$S" dpkg-db-backup "Daily dpkg database backup" "daily"
timer "$S" systemd-tmpfiles-clean "Daily Cleanup of Temporary Directories" "*-*-* 00:00:00"
for t in apt-daily apt-daily-upgrade man-db logrotate fstrim e2scrub_all dpkg-db-backup systemd-tmpfiles-clean; do
    enable "$S" "$t.timer" timers.target
done
unit "$S" avahi-daemon.socket <<'EOF'
[Socket]
ListenStream=/run/avahi-daemon/socket

[Install]
WantedBy=sockets.target
EOF
unit "$S" syslog.socket <<'EOF'
[Socket]
ListenDatagram=/run/systemd/journal/syslog

[Install]
WantedBy=sockets.target
EOF
enable "$S" avahi-daemon.socket sockets.target
enable "$S" syslog.socket sockets.target
# A per-user unit.
put "$S/home/pi/.config/systemd/user/sync.service" <<'EOF'
[Unit]
Description=Sync notes

[Service]
ExecStart=/home/pi/bin/sync-notes.sh
EOF
mkdir -p "$S/home/pi/.config/systemd/user/default.target.wants"
ln -s ../sync.service "$S/home/pi/.config/systemd/user/default.target.wants/sync.service"
# Boot scripts and desktop session.
put "$S/etc/rc.local" 755 <<'EOF'
#!/bin/sh -e
# Local boot commands
/usr/local/bin/led-on.sh
/home/pi/bin/announce-ip.sh &
exit 0
EOF
for n in nm-applet lxpolkit pulseaudio-start; do
    put "$S/etc/xdg/autostart/$n.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=$n
Exec=/usr/bin/$n %U
EOF
done
put "$S/etc/xdg/autostart/gnome-keyring.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Keyring
Exec=/usr/bin/gnome-keyring-daemon --start
Hidden=true
EOF
put "$S/home/pi/.config/autostart/pulseaudio-start.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=PulseAudio (disabled by the user)
Exec=/usr/bin/pulseaudio-start
Hidden=true
EOF
put "$S/home/pi/.config/autostart/my-dashboard.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=My dashboard
Exec=/home/pi/bin/dashboard --kiosk
EOF
put "$S/etc/xdg/lxsession/LXDE-pi/autostart" <<'EOF'
@lxpanel --profile LXDE-pi
@pcmanfm --desktop --profile LXDE-pi
# @xscreensaver -no-splash
@/home/pi/bin/wallpaper.sh
EOF
# Cron: system crontab, drop-ins, user crontabs, periodic scripts.
put "$S/etc/crontab" <<'EOF'
# /etc/crontab: system-wide crontab
SHELL=/bin/sh
PATH=/usr/local/sbin:/usr/local/bin:/sbin:/bin:/usr/sbin:/usr/bin

# Example of job definition:
17 *	* * *	root    cd / && run-parts --report /etc/cron.hourly
25 6	* * *	root	test -x /usr/sbin/anacron || { cd / && run-parts --report /etc/cron.daily; }
47 6	* * 7	root	test -x /usr/sbin/anacron || { cd / && run-parts --report /etc/cron.weekly; }
52 6	1 * *	root	test -x /usr/sbin/anacron || { cd / && run-parts --report /etc/cron.monthly; }
EOF
put "$S/etc/cron.d/e2scrub_all" <<'EOF'
30 3 * * 0 root test -e /run/systemd/system || SERVICE_MODE=1 /usr/lib/x86_64-linux-gnu/e2fsprogs/e2scrub_all_cron
EOF
put "$S/etc/cron.d/backup" <<'EOF'
*/15 * * * * pi /home/pi/backup.sh
EOF
put "$S/etc/cron.d/old.bak.conf" <<'EOF'
0 1 * * * root /usr/local/bin/ignored-because-of-the-dot
EOF
mkdir -p "$S/var/spool/cron/crontabs"
put "$S/var/spool/cron/crontabs/pi" 600 <<'EOF'
# DO NOT EDIT THIS FILE
@reboot /home/pi/start-tunnel.sh
0 3 * * mon /home/pi/weekly-report.sh
#30 4 * * * /home/pi/commented-out.sh
EOF
chown 1000:1000 "$S/var/spool/cron/crontabs/pi"
put "$S/var/spool/cron/crontabs/ghost" 600 <<'EOF'
0 5 * * * /home/ghost/job.sh
EOF
for pair in daily:apt-compat daily:dpkg daily:logrotate daily:man-db hourly:fake-hwclock weekly:man-db; do
    put "$S/etc/cron.${pair%%:*}/${pair##*:}" 755 <<'EOF'
#!/bin/sh
exit 0
EOF
done
put "$S/etc/cron.daily/notexec" 644 <<'EOF'
#!/bin/sh
exit 0
EOF
put "$S/etc/cron.daily/with.dot" 755 <<'EOF'
#!/bin/sh
exit 0
EOF
mkfs_dir diag-startup "$S" 16
publish diag-startup

# --- diag-pi-like (MBR: FAT16 boot + ext4 root) ------------------------------------------------
S=$WORK/pi
base_root "$S" multi-user.target raspbian "Raspberry Pi OS (Legacy) GNU/Linux 12"
ssh_server "$S" no
debian_sshd_config "$S"
put "$S/lib/systemd/system/sshswitch.service" <<'EOF'
[Unit]
Description=Turn on SSH if /boot/ssh is present
ConditionPathExistsGlob=/boot/ssh{,.txt}

[Service]
Type=oneshot
ExecStart=/bin/sh -c "systemctl enable ssh && systemctl start ssh"
EOF
put "$S/etc/rpi-issue" <<'EOF'
Raspberry Pi reference 2024-03-15
EOF
mkfs_dir diag-pi-root "$S" 16
make_boot "$WORK/diag-pi-boot.vfat" 32 16
LABEL=dos assemble "$WORK/diag-pi-like.img" "$WORK/diag-pi-boot.vfat" "$WORK/diag-pi-root.ext4"
gzip -9 -n -c "$WORK/diag-pi-like.img" > "$OUT/diag-pi-like.img.gz"
echo "built diag-pi-like: $(stat -c %s "$WORK/diag-pi-like.img") bytes"
