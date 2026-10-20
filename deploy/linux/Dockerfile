# syntax=docker/dockerfile:1
#
# 中控服务版 —— Linux 容器镜像（多阶段构建）
#
# 在仓库根执行：
#   docker build -f deploy/linux/Dockerfile -t controlserver:latest .
#
# 运行：
#   docker run -d --name controlserver \
#     -p 8899:8899 \
#     -p 9999:9999/udp \
#     -v controlserver-data:/var/lib/controlserver \
#     --restart unless-stopped \
#     controlserver:latest
#
# 注意：
#   · appsettings.json 不随发布输出；配置一律「代码默认值 + 环境变量」覆盖，
#     例如 -e Server__Http__Urls=http://0.0.0.0:9090、-e Server__Auth__EnforcePasswordChange=false。
#   · 数据（数据库 / 授权 / 图片 / 配置 / 日志）落在 /var/lib/controlserver，务必挂卷，
#     否则容器一删授权与配置全丢。
#   · 对接码监听默认 UDP 9999 —— 必须显式映射 UDP 端口，否则主机上被控端发的码收不到。
#     若前面还有 NAT/防火墙，同样要放行 udp/9999。

# ── 阶段 1：构建前端 ──────────────────────────────────────────
FROM node:22-bookworm-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build          # 按 vite.config.ts 产出到 /src/src/ControlServer.Server/wwwroot

# ── 阶段 2：发布后端 ──────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
# 用阶段 1 的前端产物覆盖（Vite outDir 指向 Server/wwwroot）
COPY --from=web /src/src/ControlServer.Server/wwwroot/ ./src/ControlServer.Server/wwwroot/
RUN dotnet publish src/ControlServer.Server \
      -c Release -r linux-x64 --self-contained true \
      -o /app --nologo \
 && find /app -name '*.pdb' -delete

# ── 阶段 3：运行 ──────────────────────────────────────────────
# self-contained 发布已自带 .NET 运行时，只需 OS 级依赖（libicu / libssl）→ runtime-deps。
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-bookworm-slim AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    Server__Http__Urls=http://0.0.0.0:8899 \
    Server__Storage__DataDir=/var/lib/controlserver \
    Server__Startup__AutoOpenBrowser=false

RUN mkdir -p /var/lib/controlserver
VOLUME ["/var/lib/controlserver"]

EXPOSE 8899/tcp
EXPOSE 9999/udp

ENTRYPOINT ["./ControlServer.Server"]
