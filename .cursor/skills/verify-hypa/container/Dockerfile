FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        xvfb xterm xdotool openbox imagemagick x11-utils fonts-dejavu-core procps ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY drive-inside.sh /usr/local/bin/drive-inside.sh
RUN chmod +x /usr/local/bin/drive-inside.sh
ENTRYPOINT ["/usr/local/bin/drive-inside.sh"]
