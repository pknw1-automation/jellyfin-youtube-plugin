FROM jellyfin/jellyfin:10.11.6

USER root

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip ffmpeg ca-certificates curl unzip \
    && curl -fsSL https://deno.land/install.sh | DENO_INSTALL=/usr/local sh \
    && pip3 install --no-cache-dir --break-system-packages yt-dlp \
    && mv /usr/local/bin/yt-dlp /usr/local/bin/yt-dlp-real \
    && printf '%s\n' '#!/bin/sh' 'exec /usr/local/bin/yt-dlp-real --remote-components ejs:github "$@"' > /usr/local/bin/yt-dlp \
    && chmod +x /usr/local/bin/yt-dlp \
    && rm -rf /var/lib/apt/lists/*