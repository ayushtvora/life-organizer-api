FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/LifeOrganizer.Api/LifeOrganizer.Api.csproj src/LifeOrganizer.Api/
RUN dotnet restore src/LifeOrganizer.Api/LifeOrganizer.Api.csproj

COPY src/ src/
RUN dotnet publish src/LifeOrganizer.Api/LifeOrganizer.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "LifeOrganizer.Api.dll"]
