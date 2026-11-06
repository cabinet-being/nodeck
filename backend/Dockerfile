FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY MyApp.Api.csproj ./
RUN dotnet restore

COPY . ./
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
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libstdc++6 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY --from=whisper-build /src/whisper.cpp/build/bin/whisper-cli /usr/local/bin/whisper-cli
ENTRYPOINT ["dotnet", "MyApp.Api.dll"]
