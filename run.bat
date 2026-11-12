@echo off
if "%ASPNETCORE_ENVIRONMENT%"=="" set ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src\ApiHost --no-launch-profile --urls http://localhost:8080
