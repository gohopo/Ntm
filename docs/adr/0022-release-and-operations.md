# 用 release-please 发版，运维靠网关自带的子命令

网关以前没有版本号、没有发布的镜像，也没有 CI；备份要停机后借一个 alpine 容器打包数据卷；`/healthz` 只说明进程在跑；停机时还没结束的请求会丢记录（实测：请求比 `ShutdownTimeout` 长时，写记录那一步撞上已经释放的 `IServiceProvider`）。仓库是一个人维护的，推送和 PR 很频繁。

- **版本号由 release-please 定**（Q335，按用户的要求改过）。提交信息写成约定式提交（Conventional Commits），release-please 据此算下一个版本：1.0 之前 `feat` 和 `fix` 都只加修订号，不兼容的改动加次版本号；第一个版本是 `0.1.0`。当前版本记在 `.release-please-manifest.json`。发布流程把版本和提交传给 `dotnet publish`；其他构建都是 `0.0.0-dev`。版本显示在启动日志、`/healthz`、「设置」页底部、`ntm_build_info` 和镜像标签里。
- **推到 main 时 release-please 维护一个发布 PR，合并它才发版**（Q334，按用户的要求改过）。不用手动打标签：合并发布 PR 后 release-please 打 `vX.Y.Z` 标签、建 GitHub Release。它用 `GITHUB_TOKEN` 建的标签不会触发别的流程，所以 `release.yml` 在同一次运行里接着跑三套测试和前端检查，过了才构建 amd64、arm64 两种镜像推到 Docker Hub 的 `gohopo/ntm`（`X.Y.Z`、`X.Y`、`latest`），并在 Release 说明末尾写上镜像。登录 Docker Hub 用仓库 secrets 里的 `DOCKERHUB_USERNAME` 和 `DOCKERHUB_TOKEN`。`compose.yaml` 里的镜像版本由 release-please 在发布 PR 里一起改掉（`x-release-please-version` 标记）。平时的推送和 PR 不跑测试。代价：坏提交要到发版时才被发现；测试没过时 Release 已经建了、镜像没有，要修好再发一个修订版。
- **不维护 CHANGELOG.md**（Q337，按用户的要求改过）。`release-please-config.json` 开了 `skip-changelog`，说明只写在 Release 页面上：按 `feat`（新功能）、`fix`（修复）、`perf`、`revert`、`refactor`、`docs`、`deps` 分组，带 `!` 或 `BREAKING CHANGE` 的单列在「⚠ BREAKING CHANGES」下；`test`、`build`、`ci`、`chore`、`style` 不写进说明，也不单独触发发版。没有约定式前缀的提交会被忽略。Dependabot 的提交用 `fix(deps)` 和 `ci(deps)` 前缀。
- **运维动作做成子命令**，因为运行镜像里没有 shell（Q338、Q340、Q341）：`healthcheck` 请求本机 `/healthz`，给 Docker 的 HEALTHCHECK 用；`backup` 用 SQLite 的 `VACUUM INTO` 在运行中拷一致的快照，连同密钥环和价目缓存，调试库不备份；`restore` 只在网关停着时做，现有文件挪到 `before-restore-时间`，不删。
- **一个数据目录只给一个网关**（Q341）。启动时独占 `ntm.lock`，第二个实例直接退出。这把 ADR-0003 的「只跑一个实例」从约定变成了检查，`restore` 也靠它判断网关是否停了。
- **不能降级**（Q342）。启动时库里有程序不认识的迁移就拒绝启动，提示换回新版本或恢复升级前的备份。迁移照旧只往前走。
- **停机先等再中断**（Q343，数字比提问时多）。收到停止信号后最多等进行中的请求 30 秒，再把剩下的按「网关关闭」中断（没开始回复的回 503 `gateway_shutting_down`，可重试），再留 5 秒写记录；`ShutdownTimeout` 因此是 40 秒，compose 的 `stop_grace_period` 是 50 秒。只把 `ShutdownTimeout` 设成 30 秒，超过它的请求照样丢记录。
- **`/healthz` 查两个库**（Q338），2 秒内 `SELECT 1`，失败回 503 并写出是哪个库。不查渠道：渠道坏了网关本身还是健康的，重启也修不好。
- **指标默认关**（Q339）。`/metrics` 是自己拼的 Prometheus 文本，可以要求令牌。只在内存里计数，不按 API 密钥分，免得标签基数随密钥数增长。

## 后续调整

第 21 轮后部署文件按维护者选择改用 `gohopo/ntm:latest`，不再携带版本更新标记；上文的固定版本描述是当时的决定。第 22 轮（Q361/Q362）将 Actions 固定到完整提交 SHA，并开启 CodeQL 默认设置：推送和 PR 仍不跑这里的测试套件，但会做安全扫描。这是对原来“推送不运行检查”的明确例外，不改变合并发布 PR 后才构建发布镜像的流程。

2026-11-10（Q365–Q369）增加手动测试镜像，补上“手动运行成功却跳过测试和构建”的缺口：

- `workflow_dispatch` 只走测试镜像路径，不运行 release-please，不创建或修改 GitHub Release；main 推送仍沿用原来的正式发布流程，普通推送不构建测试镜像。
- 在 GitHub Actions 的「发版」中选择分支或标签，默认 main。手动测试固定到本次运行的提交，镜像再检出测试阶段记录的同一提交，避免分支在测试期间前进。
- 手动和正式发布使用相同的三套后端测试、前端检查、管理页面构建及文档站构建。检查失败、跳过或运行取消时不发布镜像，没有跳过测试的选项。
- 手动仅发布 `gohopo/ntm:edge`，继续提供 amd64、arm64。`edge` 是共享的可变标签，其他分支的成功手动发布也会覆盖它；不额外发布提交标签，精确固定镜像内容可使用 digest。手动发布共用一个并发组，不中断正在运行的任务，避免并行覆盖；正式发布使用独立组。
- edge 的程序版本为 `0.0.0-edge`，启动日志、设置页、`/healthz` 和指标保留实际提交号；镜像元数据也记录同一版本和提交。正式镜像仍使用 release-please 的版本以及 `X.Y.Z`、`X.Y`、`latest` 标签，本地默认版本仍为 `0.0.0-dev`。
- 参考 tvbox-aggregator 的手动镜像入口，但不沿用它手动也执行 release-please 的行为，避免一次试跑意外进入正式发布。测试镜像仍会应用数据库迁移，建议用独立数据卷；需要退回旧镜像时，仍受现有迁移检查和备份恢复规则约束。

## Consequences

- 发版前要自己在本地跑测试，推送不会提醒你提交坏了。
- 提交信息要守约定式提交，不然 release-please 看不见这次改动，不会出发布 PR。
- 仓库要在 Settings → Actions → General 里勾上「Allow GitHub Actions to create and approve pull requests」，release-please 才能用 `GITHUB_TOKEN` 开发布 PR。
- 文档站发在 GitHub Pages 上，需要仓库公开（Q333）。
- 数据目录锁用的是文件锁；数据目录放在网络文件系统上本来就不支持（WAL），锁在那里也不可靠。
- `backup` 不定时、不清理旧备份；定时备份交给宿主机的 cron 之类。
