# Single image: React UI (served from wwwroot) + ASP.NET Core API.
FROM node:22.12-bookworm-slim AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY backend/MyApp.Api.csproj ./
RUN dotnet restore
COPY backend/ ./
# Embedded into the assembly as the base schema (see MyApp.Api.csproj).
COPY database/init/ /database/init/
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS whisper-build
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential cmake git \
    && rm -rf /var/lib/apt/lists/*

RUN git clone --depth 1 --branch v1.9.5 https://github.com/ggml-org/whisper.cpp.git /src/whisper.cpp \
    && test "$(git -C /src/whisper.cpp rev-parse --short=7 HEAD)" = d1be6fd
WORKDIR /src/whisper.cpp
RUN cmake -S . -B build -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=OFF -DGGML_BACKEND_DL=OFF \
        -DGGML_NATIVE=OFF -DGGML_OPENMP=OFF \
        -DGGML_SSE42=OFF -DGGML_AVX=OFF -DGGML_AVX2=OFF \
        -DGGML_BMI2=OFF -DGGML_FMA=OFF -DGGML_F16C=OFF \
        -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_SERVER=OFF \
    && cmake --build build --config Release --target whisper-cli --parallel 2

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
ARG NODECK_VERSION=dev
ENV NODECK_VERSION=${NODECK_VERSION}
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libstdc++6 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY --from=whisper-build /src/whisper.cpp/build/bin/whisper-cli /usr/local/bin/whisper-cli
COPY --from=frontend /src/frontend/dist ./wwwroot
ENTRYPOINT ["dotnet", "MyApp.Api.dll"]
