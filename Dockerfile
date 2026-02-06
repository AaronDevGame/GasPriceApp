FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

COPY . .
EXPOSE 8080

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENTRYPOINT ["dotnet", "BackendServer.dll"]