# Voip.NET in a container: the Rust engine and one .NET app in the same image.
#
# Build from the VoipNet directory:
#   docker build -f docker/Dockerfile -t voipnet/cli .
#   docker build -f docker/Dockerfile -t voipnet/meeting \
#     --build-arg PROJECT=samples/VoipNet.Meeting --build-arg ENTRY=VoipNet.Meeting.dll .
#
# Media is UDP over a range of ports, which containers do not forward well: run with --network host,
# or publish the RTP range explicitly. See docs/en/tools-and-diagnostics.md.

FROM rust:1-bookworm AS engine
# The opus crate builds libopus from source, which needs CMake.
RUN apt-get update \
    && apt-get install -y --no-install-recommends cmake \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY native/ native/
RUN cargo build --release --manifest-path native/Cargo.toml

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT=tools/VoipNet.Cli
WORKDIR /src
COPY . .
# VoipNet.Core copies the engine from here into the published output.
COPY --from=engine /src/native/target/release/libvoipnet_core.so native/target/release/
RUN dotnet publish "$PROJECT" -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# OpenAL is opened only when a call uses a sound card; everything else works without it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libopenal1 \
    && rm -rf /var/lib/apt/lists/*
ARG ENTRY=voipnet.dll
ENV VOIPNET_ENTRY=$ENTRY
WORKDIR /app
COPY --from=build /app ./
# SIP over UDP and TCP, TLS, the WebSocket port the samples use, and a small RTP range.
EXPOSE 5060/udp 5060/tcp 5061/tcp 5090/tcp 10000-10100/udp
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"/app/$VOIPNET_ENTRY\" \"$@\"", "--"]
