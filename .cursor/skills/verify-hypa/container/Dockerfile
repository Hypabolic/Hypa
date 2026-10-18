FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        xvfb xterm xdotool openbox imagemagick x11-utils fonts-dejavu-core procps ca-certificates \
        python3 iproute2 iptables \
    && rm -rf /var/lib/apt/lists/*
COPY drive-inside.sh drive-cube.sh screengrid.py /usr/local/bin/
RUN chmod +x /usr/local/bin/drive-inside.sh /usr/local/bin/drive-cube.sh /usr/local/bin/screengrid.py
ENTRYPOINT ["/usr/local/bin/drive-inside.sh"]
