FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Iodine.csproj", "Iodine/"]
RUN dotnet restore "Iodine/Iodine.csproj"
COPY . "Iodine/"
WORKDIR "Iodine/"
RUN dotnet build "Iodine.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Iodine.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Iodine.dll"]
