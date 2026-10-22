# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/playwright:v1.62.0-noble
WORKDIR /opt/suprachat
RUN npm install --prefix /opt/suprachat --ignore-scripts --no-audit --no-fund --save-exact playwright@1.62.0
COPY tools/public-container-browser-smoke.mjs /opt/suprachat/probe.mjs
ENV HOME=/tmp XDG_CACHE_HOME=/tmp/cache
CMD ["node", "/opt/suprachat/probe.mjs"]
