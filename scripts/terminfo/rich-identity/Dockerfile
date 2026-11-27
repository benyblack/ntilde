FROM node:24-trixie-slim
RUN apt-get update -qq \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        fonts-dejavu-core \
        foot \
        gir1.2-vte-2.91 \
        libfontconfig1 \
        libgl1 \
        libice6 \
        libicu76 \
        libsm6 \
        python3 \
        python3-gi \
        sway \
        x11-utils \
        xwayland \
    && install -d -m 1777 /tmp/.X11-unix \
    && rm -rf /var/lib/apt/lists/*
USER node
