FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet restore src/Luthn.Host.Api/Luthn.Host.Api.csproj \
    && dotnet restore src/Luthn.Tools/Luthn.Tools.csproj \
    && dotnet restore src/Luthn.McpServer/Luthn.McpServer.csproj
RUN dotnet publish src/Luthn.Host.Api/Luthn.Host.Api.csproj -c Release -o /out/api --no-restore \
    && dotnet publish src/Luthn.Tools/Luthn.Tools.csproj -c Release -o /out/tools --no-restore \
    && dotnet publish src/Luthn.McpServer/Luthn.McpServer.csproj -c Release -o /out/mcp --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/api ./api/
COPY --from=build /out/tools ./tools/
COPY --from=build /out/mcp ./mcp/
COPY LICENSE /licenses/AGPL-3.0.txt
COPY LICENSES/Apache-2.0.txt /licenses/Apache-2.0.txt
COPY docker/luthn-container /usr/local/bin/luthn-container
RUN chmod 0755 /usr/local/bin/luthn-container \
    && mkdir -p /var/lib/luthn/operator \
    && chown -R app:app /app /var/lib/luthn
USER app
EXPOSE 8080
ENTRYPOINT ["luthn-container"]
CMD ["api"]
