FROM mcr.microsoft.com/dotnet/sdk:6.0 as build
WORKDIR /build
RUN echo "revision 37a7e96bdc9cb7d0c6e9a1e280e37297a975a118b"
RUN git clone --depth=1 -b net6 https://github.com/Coflnet/HypixelSkyblock.git dev
WORKDIR /build/sky
COPY SkyIndexer.csproj SkyIndexer.csproj
RUN dotnet restore
COPY . .
RUN dotnet publish -c release

FROM mcr.microsoft.com/dotnet/aspnet:6.0
WORKDIR /app

COPY --from=build /build/sky/bin/release/net6.0/publish/ .
RUN mkdir -p ah/files

ENTRYPOINT ["dotnet", "SkyIndexer.dll"]

VOLUME /data
