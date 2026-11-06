# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

**Layout: multi-context.** Ntm 是一个 .NET 多项目解决方案（`src/`）加两个前端应用（`apps/`）。上下文边界按领域语言划，不是按项目数量划：只有两个上下文各自维护 `GLOSSARY.md`，其余项目沿用 Gateway 的语言。

## Before exploring, read these

- **`GLOSSARY-MAP.md`** at the repo root: it points at one `GLOSSARY.md` per context. Read each one relevant to the topic.
- **`docs/adr/`** at the repo root: system-wide decisions.
- **`<context>/docs/adr/`**: context-scoped decisions for the package you're about to work in.

If any of these files don't exist, **proceed silently**. Don't flag their absence; don't suggest creating them upfront. The `/domain-modeling` skill (reached via `/grill-with-docs` and `/improve-codebase-architecture`) creates them lazily when terms or decisions actually get resolved.

## File structure

```
/
├── GLOSSARY-MAP.md
├── docs/adr/                          ← system-wide decisions
├── src/
│   ├── Ntm.Protocol/
│   │   └── GLOSSARY.md                ← context: API 格式互转
│   ├── Ntm.Gateway/
│   │   └── GLOSSARY.md                ← context: 选路、容错、计费
│   └── Ntm.Server/                    ← 宿主与端点组装，用 Gateway 的语言
└── apps/
    ├── web/                           ← 管理前端，用 Gateway 的语言
    └── docs/                          ← 文档站
```

Contexts: `src/Ntm.Protocol`, `src/Ntm.Gateway`.

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in the relevant `GLOSSARY.md`. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal: either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-0003 (single instance, in-memory runtime state), but worth reopening because…_
