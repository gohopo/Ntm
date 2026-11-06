# syntax=docker/dockerfile:1
# 三段：Node 构建前端 → SDK 发布后端 → chiseled 运行（Q125）。
# 在仓库根目录执行：docker compose up --build

# 前两段只产出和架构无关的文件（静态页、不带 apphost 的 IL），在构建机的架构上跑，
# 多架构镜像不用模拟 arm64（Q336）。只有最后一段按目标架构换基础镜像。

# ---- 前端 ----
FROM --platform=$BUILDPLATFORM node:24-slim AS web
WORKDIR /src
RUN npm install --global pnpm@12.15.0
# 先只拷清单装依赖，源码改了不用重装。apps/docs 的清单也要在：锁文件里有它。
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml .npmrc ./
COPY apps/web/package.json apps/web/
COPY apps/docs/package.json apps/docs/
RUN pnpm install --frozen-lockfile --filter web...
COPY apps/web apps/web
# 产物写到 ../../src/Ntm.Server/wwwroot（apps/web/vite.config.ts）。
RUN pnpm --filter web build

# ---- 后端 ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS server
# 版本号和提交由发布流程传进来（Q335）；本地构建就是 0.0.0-dev。
ARG VERSION=0.0.0-dev
ARG REVISION=
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Ntm.Protocol/Ntm.Protocol.csproj src/Ntm.Protocol/
COPY src/Ntm.Gateway/Ntm.Gateway.csproj src/Ntm.Gateway/
COPY src/Ntm.Server/Ntm.Server.csproj src/Ntm.Server/
RUN dotnet restore src/Ntm.Server/Ntm.Server.csproj
COPY src src
COPY --from=web /src/src/Ntm.Server/wwwroot src/Ntm.Server/wwwroot
# 不带 apphost：运行镜像按目标架构换，入口统一用 dotnet。
RUN dotnet publish src/Ntm.Server/Ntm.Server.csproj -c Release -o /out --no-restore -p:UseAppHost=false \
        -p:Version="$VERSION" -p:SourceRevisionId="$REVISION" \
    && mkdir /out-data

# ---- 运行 ----
# 必须用 -extra：普通 chiseled 镜像不带时区数据，「每日」上限和可用时段会退回 UTC（Q86/Q125）。
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
ARG VERSION=0.0.0-dev
ARG REVISION=
LABEL org.opencontainers.image.title="南天门" \
      org.opencontainers.image.source="https://github.com/gohopo/Ntm" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"
WORKDIR /app
COPY --from=server /out ./
# 数据目录默认就是工作目录下的 data（Ntm:Data:Directory = "data"），即 /app/data（Q127）。
# chiseled 镜像没有 shell，目录要从上一段拷一个空的过来并交给 app 用户（uid 1654）；
# 空的命名卷第一次挂上时会继承这个属主。
COPY --from=server --chown=1654:1654 /out-data /app/data
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
VOLUME /app/data
USER 1654
# 镜像里没有 shell 和 curl，用网关自己的 healthcheck 子命令请求本机 /healthz（Q338）。
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["dotnet", "Ntm.Server.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Ntm.Server.dll"]
