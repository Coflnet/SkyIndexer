FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
RUN echo "new revision "
ARG HYPIXEL_CORE_REVISION=d87dbb1a2461deced758dcfc7f4927908b291425
RUN git clone --depth=1 https://github.com/Coflnet/HypixelSkyblock.git dev \
    && git -C dev fetch --depth=1 origin "$HYPIXEL_CORE_REVISION" \
    && git -C dev checkout --detach FETCH_HEAD
WORKDIR /build/sky
COPY SkyIndexer.csproj SkyIndexer.csproj
RUN dotnet restore
COPY . .
RUN dotnet test --no-restore --nologo
RUN dotnet publish -c release -o /artifact

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /artifact .
RUN mkdir -p ah/files

ENV ASPNETCORE_URLS=http://+:8000


# apt in the base image has CVE-2026-109518 (HIGH); upgrade it until the base image ships the fix.
RUN apt-get update \
    && apt-get install -y --only-upgrade --no-install-recommends apt libapt-pkg6.0t64 \
    && rm -rf /var/lib/apt/lists/*
RUN useradd --uid $(shuf -i 2000-65000 -n 1) app-user
USER app-user

ENTRYPOINT ["dotnet", "SkyIndexer.dll", "--hostBuilder:reloadConfigOnChange=false"]

VOLUME /data
