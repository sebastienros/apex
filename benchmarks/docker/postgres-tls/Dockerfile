FROM postgres:18-alpine

RUN apk add --no-cache openssl \
    && mkdir -p /etc/postgresql/tls \
    && openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 3650 \
        -subj "/CN=postgres" \
        -keyout /etc/postgresql/tls/server.key \
        -out /etc/postgresql/tls/server.crt \
    && chown -R postgres:postgres /etc/postgresql/tls \
    && chmod 600 /etc/postgresql/tls/server.key

CMD ["postgres", "-c", "ssl=on", "-c", "ssl_cert_file=/etc/postgresql/tls/server.crt", "-c", "ssl_key_file=/etc/postgresql/tls/server.key"]
