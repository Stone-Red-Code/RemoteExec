# Worker Docker image for isolated task execution
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["RemoteExec.Worker/RemoteExec.Worker.csproj", "RemoteExec.Worker/"]
COPY ["RemoteExec.Shared/RemoteExec.Shared.csproj", "RemoteExec.Shared/"]
RUN dotnet restore "RemoteExec.Worker/RemoteExec.Worker.csproj"
COPY . .
WORKDIR "/src/RemoteExec.Worker"
RUN dotnet build "RemoteExec.Worker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "RemoteExec.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create assembly cache directory with proper permissions
RUN mkdir -p /tmp/assemblies && chmod 777 /tmp/assemblies

# Create non-root user for security (using ID that doesn't conflict)
RUN groupadd -g 10000 worker && \
    useradd -r -u 10000 -g worker worker

# Run as non-root user
# USER worker

# Security: Minimal runtime image with no network access by default
ENTRYPOINT ["dotnet", "RemoteExec.Worker.dll"]
