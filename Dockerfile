FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app

# Copy everything
COPY . ./
# Restore as distinct layers
RUN dotnet workload restore && dotnet restore
# Build and publish a release
RUN dotnet publish -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0
LABEL org.opencontainers.image.source "https://github.com/Freeesia/CacheOGP"
ENV LANG ja_JP.UTF-8
RUN apt-get update &&\
    apt-get install -y libgtk-3.0 libgbm-dev libnss3 libatk-bridge2.0-0 libasound2 locales fonts-ipafont fonts-ipaexfont fonts-noto-cjk &&\
    echo "ja_JP UTF-8" > /etc/locale.gen &&\
    locale-gen
WORKDIR /app
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "CacheOGP.ApiService.dll"]
