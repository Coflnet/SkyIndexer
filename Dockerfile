FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
RUN echo "new revision "
ARG HYPIXEL_CORE_REVISION=18ea1aaf3c3298d7eff54cd4e342243318141d10
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


RUN useradd --uid $(shuf -i 2000-65000 -n 1) app-user
USER app-user

ENTRYPOINT ["dotnet", "SkyIndexer.dll", "--hostBuilder:reloadConfigOnChange=false"]

VOLUME /data
