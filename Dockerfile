# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/BerdaShared/BerdaShared.csproj src/BerdaShared/
COPY src/BerdaServer/BerdaServer.csproj src/BerdaServer/
RUN dotnet restore src/BerdaServer/BerdaServer.csproj
COPY src/ src/
RUN dotnet publish src/BerdaServer/BerdaServer.csproj -c Release -o /app --no-restore

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
ENTRYPOINT ["dotnet", "BerdaServer.dll"]