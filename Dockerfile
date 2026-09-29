# The server as a container image. release.yml builds and pushes one per tagged
# release; `docker build -t gg2-server .` builds the same thing locally.
# docs/DEPLOY.md, "Running in Docker".

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
ARG VERSION=0.0.0-dev
# Self-contained, so the runtime stage needs no .NET runtime, only its native
# dependencies. The publish carries maps/, nav/ and checkpoints/ beside the binary.
RUN dotnet publish src/GG2.Host/GG2.Host.csproj \
        -c Release -r linux-x64 --self-contained true \
        -p:Version=$VERSION -o /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
# Owned by the non-root user because the server writes into nav/: a custom map
# gets its graph built at the map change.
COPY --from=build --chown=app:app /out .
USER app

# The same precaution the systemd unit takes. docs/DEPLOY.md, "Garbage collection".
ENV DOTNET_gcServer=0

# 8190 is the game (TCP), 8191 the admin panel.
EXPOSE 8190/tcp 8191/tcp

# The panel is on and listening on every interface by default, because a
# container's loopback is unreachable from outside it. The password is
# generated and printed to the log unless one is set ([Web] Password in a
# --config file, or --web-password). Plain HTTP unless --web-tls is given; the
# server warns about that at every startup. Anything passed to `docker run`
# after the image name is appended to these flags.
# Exec form, so the server is PID 1 and receives docker stop's SIGTERM.
ENTRYPOINT ["/app/gg2-server", "--web", "8191", "--web-bind", "0.0.0.0"]
