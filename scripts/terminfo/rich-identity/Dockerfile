FROM node:24-trixie-slim
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends sway xwayland foot python3 python3-gi gir1.2-vte-2.91 x11-utils libgl1 libice6 libsm6 libicu76 libfontconfig1 fonts-dejavu-core && install -d -m 1777 /tmp/.X11-unix && rm -rf /var/lib/apt/lists/*
