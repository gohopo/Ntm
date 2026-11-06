# 南天门

南天门（Ntm）是一个自托管的 AI 聚合网关：把多个大模型供应商的渠道接进来，对客户端提供统一的 OpenAI 和 Anthropic 兼容接口，自动选路、故障切换和计费。

完整文档见 [文档站](https://gohopo.github.io/Ntm/)，源文件在 [`apps/docs`](apps/docs)。

## 功能

- 多渠道聚合：一个网关接多个供应商和中转，每个渠道可以有多把渠道密钥和自己的代理。
- 协议互转：OpenAI Chat、OpenAI Responses、Anthropic 三种格式，入站和上游任意组合，流式和非流式都转；格式相同时透传。
- 嵌入和图片生成：`/v1/embeddings`、`/v1/images/generations` 在 OpenAI 格式的渠道之间透传。
- 模型同步：定时从渠道拉取上游模型清单，用匹配规则决定哪些下发给客户端。
- 选路与容错：优先级加权重分流，按健康度调整；在第一个真实输出之前失败就无感切换到下一个候选；按密钥、模型、渠道分层冷却。
- 限制与权限：渠道的每日费用、RPM、并发、可用时段；API 密钥的费用上限、模型和渠道范围、并发上限。
- 计费：自动从 models.dev 取原厂单价，支持人工单价、每张图片单价和渠道倍率。
- 可观测：实时请求、请求日志、选路详情、中断、调试原文、统计面板。
- 工作台：直接测渠道和模型，带多轮对话。

## 快速部署

```sh
mkdir ntm && cd ntm
curl -O https://raw.githubusercontent.com/gohopo/Ntm/main/compose.yaml
# 去掉 compose.yaml 里 NTM_ADMIN_PASSWORD 的注释，改成你自己的密码（至少 12 个字符）
docker compose up -d
```

管理页面在 `http://<主机>:8080`。镜像是 Docker Hub 上的 [`gohopo/ntm`](https://hub.docker.com/r/gohopo/ntm)（amd64、arm64），各版本的改动见 [Releases](https://github.com/gohopo/Ntm/releases)；想从源码构建，在仓库根目录取消 `compose.yaml` 里 `build: .` 的注释，执行 `docker compose up -d --build`。数据在命名卷 `ntm-data` 里，用 `docker compose exec ntm dotnet Ntm.Server.dll backup` 备份。接下来的步骤见文档站的「快速开始」和「部署与运维」。

## 技术栈

- 后端 ASP.NET Core、EF Core、SQLite，代码在 `src`，按项目分模块
- 管理前端 SvelteKit（`apps/web`），构建后由后端托管
- 文档站 SvelteKit（`apps/docs`），预渲染成静态页单独发布
- Docker Compose 部署

## 设计决策

领域语言见 [GLOSSARY-MAP.md](GLOSSARY-MAP.md)，架构决策见 [docs/adr/](docs/adr/)。

