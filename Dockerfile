# ---------- BUILD STAGE ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .

RUN dotnet restore "./BackendServer.csproj"
RUN dotnet publish "./BackendServer.csproj" -c Release -o /out

# ---------- RUN STAGE ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /out ./

ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

ENTRYPOINT ["dotnet", "BackendServer.dll"]