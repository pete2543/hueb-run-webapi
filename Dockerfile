# syntax=docker/dockerfile:1
# API + EF migration bundle — ใช้กับ Railway (railway.json) หรือที่ไหนก็ได้ที่รัน container

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json dotnet-tools.json ./
RUN dotnet tool restore

# restore แยกชั้น ให้ cache ไม่หายเวลาแก้แค่โค้ด
COPY src/HuebRun.Domain/HuebRun.Domain.csproj src/HuebRun.Domain/
COPY src/HuebRun.Infrastructure/HuebRun.Infrastructure.csproj src/HuebRun.Infrastructure/
COPY src/HuebRun.Api/HuebRun.Api.csproj src/HuebRun.Api/
RUN dotnet restore src/HuebRun.Api/HuebRun.Api.csproj

COPY src/ src/
RUN dotnet publish src/HuebRun.Api/HuebRun.Api.csproj -c Release -o /app/publish --no-restore
# ไฟล์ migration ที่รันได้เอง (pre-deploy) ไม่ต้องมี SDK ใน image ที่ใช้จริง
RUN dotnet ef migrations bundle -p src/HuebRun.Infrastructure -s src/HuebRun.Api \
      --configuration Release -o /app/publish/efbundle --self-contained -r linux-x64 --force

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
COPY scripts/migrate.sh ./migrate.sh
RUN chmod +x migrate.sh efbundle
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# Railway ส่งพอร์ตมาทาง $PORT
ENTRYPOINT ["sh", "-c", "ASPNETCORE_HTTP_PORTS=${PORT:-8080} exec dotnet HuebRun.Api.dll"]
