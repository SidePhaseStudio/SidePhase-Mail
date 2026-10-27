#!/usr/bin/env bash
# Sets up a disposable, loopback-only Dovecot (IMAP) + Postfix (SMTP submission) server for the integration tests.
# Ubuntu only. Uses two throwaway test users on the reserved example.test domain - never real accounts.
#
#   IMAPS  127.0.0.1:1993   IMAP+STARTTLS 127.0.0.1:1143
#   SMTPS  127.0.0.1:1465   submission+STARTTLS 127.0.0.1:1587
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
echo "postfix postfix/main_mailer_type select No configuration" | sudo debconf-set-selections
echo "postfix postfix/mailname string mail.example.test" | sudo debconf-set-selections
sudo apt-get update -qq
sudo apt-get install -y -qq postfix dovecot-imapd dovecot-lmtpd ssl-cert >/dev/null
sudo make-ssl-cert generate-default-snakeoil --force-overwrite

sudo groupadd -f vmail
id vmail >/dev/null 2>&1 || sudo useradd -r -g vmail -d /var/vmail -s /usr/sbin/nologin vmail
sudo mkdir -p /var/vmail && sudo chown vmail:vmail /var/vmail

# Test users (plain-text passwords are fine here: throwaway accounts on a server bound to 127.0.0.1).
sudo tee /etc/dovecot/test-users >/dev/null <<'USERS'
alice@example.test:{PLAIN}alice-secret
bob@example.test:{PLAIN}bob-secret
USERS
sudo chmod 640 /etc/dovecot/test-users && sudo chown root:dovecot /etc/dovecot/test-users

sudo tee /etc/dovecot/local.conf >/dev/null <<'DOVECOT'
protocols = imap lmtp
listen = 127.0.0.1
ssl = required
ssl_cert = </etc/ssl/certs/ssl-cert-snakeoil.pem
ssl_key = </etc/ssl/private/ssl-cert-snakeoil.key
disable_plaintext_auth = yes
auth_mechanisms = plain login
passdb {
  driver = passwd-file
  args = scheme=PLAIN username_format=%u /etc/dovecot/test-users
}
userdb {
  driver = static
  args = uid=vmail gid=vmail home=/var/vmail/%d/%n
}
mail_location = maildir:~/Maildir
namespace inbox {
  inbox = yes
  mailbox Drafts {
    special_use = \Drafts
    auto = subscribe
  }
  mailbox Sent {
    special_use = \Sent
    auto = subscribe
  }
  mailbox Trash {
    special_use = \Trash
    auto = subscribe
  }
  mailbox Junk {
    special_use = \Junk
    auto = subscribe
  }
  mailbox Archive {
    special_use = \Archive
    auto = subscribe
  }
}
service imap-login {
  inet_listener imap {
    port = 1143
  }
  inet_listener imaps {
    port = 1993
    ssl = yes
  }
}
service lmtp {
  unix_listener /var/spool/postfix/private/dovecot-lmtp {
    mode = 0600
    user = postfix
    group = postfix
  }
}
service auth {
  unix_listener /var/spool/postfix/private/auth {
    mode = 0660
    user = postfix
    group = postfix
  }
}
DOVECOT

sudo cp /usr/share/postfix/main.cf.debian /etc/postfix/main.cf 2>/dev/null || sudo touch /etc/postfix/main.cf
echo "mail.example.test" | sudo tee /etc/mailname >/dev/null
sudo postconf -e \
  "myhostname = mail.example.test" \
  "mydestination = localhost" \
  "inet_interfaces = loopback-only" \
  "inet_protocols = ipv4" \
  "mynetworks = 127.0.0.0/8" \
  "relayhost =" \
  "default_transport = error" \
  "relay_transport = error" \
  "virtual_mailbox_domains = example.test" \
  "virtual_transport = lmtp:unix:private/dovecot-lmtp" \
  "smtpd_tls_cert_file = /etc/ssl/certs/ssl-cert-snakeoil.pem" \
  "smtpd_tls_key_file = /etc/ssl/private/ssl-cert-snakeoil.key" \
  "smtpd_tls_security_level = may" \
  "smtpd_relay_restrictions = permit_mynetworks permit_sasl_authenticated defer_unauth_destination" \
  "maillog_file = /var/log/postfix.log"

sudo postconf -M "1587/inet=1587 inet n - n - - smtpd"
sudo postconf -P "1587/inet/syslog_name=postfix/submission" \
  "1587/inet/smtpd_tls_security_level=encrypt" \
  "1587/inet/smtpd_sasl_auth_enable=yes" \
  "1587/inet/smtpd_sasl_type=dovecot" \
  "1587/inet/smtpd_sasl_path=private/auth" \
  "1587/inet/smtpd_tls_auth_only=yes" \
  "1587/inet/smtpd_recipient_restrictions=permit_sasl_authenticated,reject" \
  "1587/inet/smtpd_relay_restrictions=permit_sasl_authenticated,reject"
sudo postconf -M "1465/inet=1465 inet n - n - - smtpd"
sudo postconf -P "1465/inet/syslog_name=postfix/smtps" \
  "1465/inet/smtpd_tls_wrappermode=yes" \
  "1465/inet/smtpd_sasl_auth_enable=yes" \
  "1465/inet/smtpd_sasl_type=dovecot" \
  "1465/inet/smtpd_sasl_path=private/auth" \
  "1465/inet/smtpd_recipient_restrictions=permit_sasl_authenticated,reject" \
  "1465/inet/smtpd_relay_restrictions=permit_sasl_authenticated,reject"

sudo systemctl restart dovecot || sudo dovecot
sudo systemctl restart postfix || sudo postfix start

for port in 1993 1143 1587 1465; do
  for _ in $(seq 1 20); do
    (echo > /dev/tcp/127.0.0.1/$port) 2>/dev/null && break
    sleep 0.5
  done
  (echo > /dev/tcp/127.0.0.1/$port) 2>/dev/null || { echo "port $port is not listening"; exit 1; }
done
echo "Test mail server ready."
