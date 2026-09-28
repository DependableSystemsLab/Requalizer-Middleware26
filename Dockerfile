FROM ubuntu:24.04

# Configure timezone to avoid interactive prompts
ENV TZ=Etc/UTC

# Install system dependencies, Python 3.12, and .NET 10.0 SDK
RUN apt-get update && \
    DEBIAN_FRONTEND=noninteractive apt-get install -y \
    curl wget git vim tmux make build-essential \
    python3 python3-pip python3-venv \
    dotnet-sdk-10.0 \
    && rm -rf /var/lib/apt/lists/*

SHELL ["/bin/bash", "-c"]

# Install Node.js 22.x
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    DEBIAN_FRONTEND=noninteractive apt-get install -y nodejs && \
    rm -rf /var/lib/apt/lists/*

# Copy Requalizer artifacts
WORKDIR /root/requalizer

COPY ./src ./src
COPY ./scripts ./scripts
COPY ./data ./data
COPY ./dist ./dist

# Install Node.js dependencies for experimental scripts
RUN echo "{}" > package.json && \
    npm install --save-dev dotenv js-beautify xml2js express express-session escodegen

ENV REQUALIZER_ROOT=/root/requalizer
ENV PATH="$PATH:/root/requalizer/dist"

# Build OneOS and the DemoRunner (which references OneOS), so the experiment scripts can run right away
RUN dotnet build /root/requalizer/src/OneOS/OneOS/OneOS.csproj && \
    dotnet build /root/requalizer/src/DemoRunner/DemoRunner.csproj

# Install the OneOS JavaScript environment's npm dependencies where the DemoRunner looks for them
# (<temp>/oneos-live-js-cache; the list is JavaScriptEnvironmentInstaller.Dependencies), so that the first
# run doesn't have to install them
RUN mkdir -p /tmp/oneos-live-js-cache && cd /tmp/oneos-live-js-cache && \
    echo '{ "private": true }' > package.json && \
    npm install --no-audit --no-fund --save esprima@4.0.1 escodegen@2.1.0 js-beautify@1.14.11

RUN mkdir -p /root/output
ENV REQUALIZER_OUTPUT_ROOT=/root/output

# Initialize python virtual environment and dependencies
WORKDIR /root/requalizer/scripts/presentation
RUN python3 -m venv .venv && \
    source .venv/bin/activate && \
    if [ -f requirements.txt ]; then pip install -r requirements.txt; fi

# Set working directory
WORKDIR /root