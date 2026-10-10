# syntax=docker/dockerfile:1

# The build context is the repository ROOT, not this directory: Central Package Management,
# Directory.Build.props and .editorconfig live there, and a project built without them is a
# different build from the one CI gates. docker-compose.yml sets `context: .` for that reason.
#
# The build stage runs on the BUILDER's platform, whatever platform the image targets. The publish
# output is framework-dependent and RID-less — managed assemblies plus every native runtimes/<rid>/
# asset (SQLite's included) — so the same /app runs on amd64 and arm64, and a multi-arch build
# (.github/workflows/image.yml) compiles once natively instead of running the SDK under QEMU, which
# is many times slower and the usual place such a build crashes. UseAppHost=false drops the native
# launcher the image never uses (the ENTRYPOINT is `dotnet MMLib.Alvo.Host.dll`), which would
# otherwise be the builder's architecture inside an image of another.
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
# .claude/skills/alvo-descriptor-* is not tooling here: MMLib.Alvo.Ai embeds those skills (D33) and its build fails
# without them. .dockerignore admits only those directories of .claude/.
COPY .claude/skills/ .claude/skills/
COPY src/ src/

RUN dotnet restore src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj
RUN dotnet publish src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

# The OCI labels. `source` is not decoration: it is what links the package GitHub Container Registry
# creates on the first push to this repository, so the package inherits the repository's README and
# can inherit its access. VERSION and REVISION are stated by .github/workflows/image.yml (the MinVer
# version and the commit); a local build says it has neither rather than inventing them.
ARG VERSION=0.0.0-docker
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/Burgyn/MMLib.Alvo" \
      org.opencontainers.image.url="https://github.com/Burgyn/MMLib.Alvo" \
      org.opencontainers.image.title="Alvo" \
      org.opencontainers.image.description="Alvo standalone host: a .NET Backend-as-a-Service driven by one JSON descriptor." \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"

WORKDIR /app
COPY --from=build /app .

# /alvo is where the descriptor is mounted and where the SQLite default would be written, so it has
# to exist and be writable by the non-root user the image runs as.
RUN mkdir -p /alvo/data && chown -R $APP_UID:$APP_UID /alvo

# The runnable examples, so docker-compose.quickstart.yml can point Alvo__DescriptorPath at one with
# nothing on the reader's disk but that compose file. Copied after the chown on purpose: they stay
# root-owned and read-only to the process, like a mounted `:ro` descriptor. Only the descriptors are
# copied — never a seed, a README or an .env — and only the examples a host can apply: `_negative/`
# exists to be rejected and `complex-crm/` carries NOT-RUNNABLE.md. QuickStartImageTests holds this
# list equal to AlvoExamples.Runnable(), so an example added, removed or marked not runnable fails
# ring0 until this list follows.
COPY examples/bike-workshop/bike-workshop.alvo.json /alvo/examples/bike-workshop/
COPY examples/field-service/field-service.alvo.json /alvo/examples/field-service/
COPY examples/help-desk/help-desk.alvo.json /alvo/examples/help-desk/
COPY examples/simple-tasks/tasks.alvo.json /alvo/examples/simple-tasks/
COPY examples/vehicle-registry/vehicles.alvo.json /alvo/examples/vehicle-registry/

USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "MMLib.Alvo.Host.dll"]
