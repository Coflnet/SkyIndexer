FROM mcr.microsoft.com/dotnet/sdk:7.0 as build
WORKDIR /build
RUN echo "revision new"
RUN git clone --depth=1 https://github.com/Coflnet/HypixelSkyblock.git dev
WORKDIR /build/sky
COPY SkyIndexer.csproj SkyIndexer.csproj
RUN dotnet restore
COPY . .
RUN dotnet publish -c release -o /artifact

FROM mcr.microsoft.com/dotnet/aspnet:7.0
WORKDIR /app

COPY --from=build /artifact .
RUN mkdir -p ah/files

ENV ASPNETCORE_URLS=http://+:8000


RUN useradd --uid $(shuf -i 2000-65000 -n 1) app
USER app

ENTRYPOINT ["dotnet", "SkyIndexer.dll", "--hostBuilder:reloadConfigOnChange=false"]

VOLUME /data
