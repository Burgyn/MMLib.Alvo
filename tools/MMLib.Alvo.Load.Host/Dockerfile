# syntax=docker/dockerfile:1

# The load harness's hooks arm (HX-10). Built only by scripts/test-load; never published.
#
# The standalone src/MMLib.Alvo.Host/Dockerfile with three differences and nothing else: tools/MMLib.Alvo.Load.Host
# is copied in, restore and publish name it, and the entrypoint runs it. No OCI labels and no examples: this image is
# never pushed, and compose mounts the field-service descriptor. The build context is the repository root, as there;
# Dockerfile.dockerignore beside this file admits tools/MMLib.Alvo.Load.Host, which the root .dockerignore excludes.
# tools/Directory.Build.props stays out: it only sets IsPackable=false, and publish never packs. LoadHostImageParityTests
# (ring0) holds this file and Dockerfile.dockerignore to exactly these differences.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /source

# What the image build says about itself. MinVer derives a version from git tags and the context
# carries no .git, so the version is stated rather than discovered: .github/workflows/image.yml passes
# the real one (minver-cli over the checkout), and a local build says it has none instead of inheriting
# the SDK's 1.0.0 — a version this pre-1.0 project has never released.
ARG VERSION=0.0.0-docker

# The three things the repository's build looks for outside the context and would otherwise warn
# about, told rather than discovered. None of them relaxes a bar: TreatWarningsAsErrors still
# applies to every project here, and source-link metadata serves symbol servers for published
# packages, which this image is not. Set as environment so they reach restore and every referenced
# project, not only the one named on the command line.
ENV MinVerVersionOverride=$VERSION \
    EnableSourceLink=false \
    EnableSourceControlManagerQueries=false \
    HUSKY=0

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig MMLib.Alvo.slnx ./
# schema/ is not documentation here: MMLib.Alvo compiles project.schema.json as an AdditionalFile
# and the descriptor validator's types are generated from it, so a context without it does not build.
COPY schema/ schema/
# plugins/alvo/skills/ is not tooling here: MMLib.Alvo.Ai embeds those skills (D33) and its build fails without
# them. .dockerignore admits only the skills of plugins/.
COPY plugins/alvo/skills/ plugins/alvo/skills/
COPY src/ src/
COPY tools/MMLib.Alvo.Load.Host/ tools/MMLib.Alvo.Load.Host/

RUN dotnet restore tools/MMLib.Alvo.Load.Host/MMLib.Alvo.Load.Host.csproj
RUN dotnet publish tools/MMLib.Alvo.Load.Host/MMLib.Alvo.Load.Host.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

WORKDIR /app
COPY --from=build /app .

# /alvo is where the descriptor is mounted and where the SQLite default would be written, so it has
# to exist and be writable by the non-root user the image runs as.
RUN mkdir -p /alvo/data && chown -R $APP_UID:$APP_UID /alvo

USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "MMLib.Alvo.Load.Host.dll"]
