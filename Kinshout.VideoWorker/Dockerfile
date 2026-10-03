# Build from the repository root: docker build -f Kinshout.VideoWorker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Kinshout.VideoWorker/Kinshout.VideoWorker.csproj Kinshout.VideoWorker/
RUN dotnet restore Kinshout.VideoWorker/Kinshout.VideoWorker.csproj
COPY Kinshout.VideoWorker/ Kinshout.VideoWorker/
RUN dotnet publish Kinshout.VideoWorker/Kinshout.VideoWorker.csproj -c Release -o /app -p:IsPublishable=true

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Kinshout.VideoWorker.dll"]
