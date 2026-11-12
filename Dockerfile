FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/ApiHost/ApiHost.csproj -c Release -o /app
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
# порт 8080 задаёт базовый образ (ASPNETCORE_HTTP_PORTS); по умолчанию — рабочее окружение:
# без строки подключения и с демо-данными сервер не стартует
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# не от root: пользователь app есть в базовом образе ASP.NET
USER $APP_UID
ENTRYPOINT ["dotnet","ApiHost.dll"]
