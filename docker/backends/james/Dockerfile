# Apache James (memory) for the IMAP + SMTP submission integration leg.
#
# A second, independent (Java) implementation of IMAP + SMTP submission. Stock James disallows
# plaintext IMAP auth and only announces SMTP AUTH after STARTTLS to unauthorized addresses, so
# this image relaxes both; users are provisioned at runtime (see entrypoint.sh -- the memory
# server is ephemeral). No CalDAV/CardDAV, JMAP serves no routes for our client, and ManageSieve
# is left disabled -- all self-skip via the suite's capability gates (DavBackendFact / the JMAP
# session probe / the ManageSieve STARTTLS probe).
FROM apache/james:memory-3.8.2

# 1) Allow the gateway's plaintext IMAP LOGIN (the fixture speaks Security=None).
# 2) SMTP submission (587): announce AUTH always and over plaintext. Stock James only announces
#    AUTH to unauthorized addresses AND after STARTTLS (requireSSL), so a plaintext MailKit
#    submission from 127.0.0.1 (CI, an authorized address) never sees AUTH. Making it
#    announce=always + requireSSL=false lets the gateway authenticate over the plaintext test
#    connection. (The 465 implicit-TLS block, which we don't use, is harmlessly relaxed too.)
# ManageSieve is deliberately LEFT DISABLED (stock default). James's ManageSieve advertises
# STARTTLS in its capability greeting even with startTLS off, which makes the suite's Oof gate
# (SieveBackendFact requires STARTTLS) run the tests -- but the actual STARTTLS negotiation with
# the gateway's Sieve client hangs. With no 4190 listener the probe gets connection-refused and
# the Oof/Sieve tests skip cleanly (as on Cyrus). James therefore covers IMAP + SMTP only.
RUN sed -i 's#<plainAuthDisallowed>true</plainAuthDisallowed>#<plainAuthDisallowed>false</plainAuthDisallowed>#' /root/conf/imapserver.xml \
	&& sed -i 's#<announce>forUnauthorizedAddresses</announce>#<announce>always</announce>#g' /root/conf/smtpserver.xml \
	&& sed -i 's#<requireSSL>true</requireSSL>#<requireSSL>false</requireSSL>#g' /root/conf/smtpserver.xml

COPY entrypoint.sh /root/entrypoint.sh
RUN chmod +x /root/entrypoint.sh

ENTRYPOINT ["/root/entrypoint.sh"]
