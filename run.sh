#!/bin/sh
# Локальный запуск для разработки: демо-данные и строка подключения из appsettings.Development.json
ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT:-Development} dotnet run --project src/ApiHost --no-launch-profile --urls http://localhost:8080
