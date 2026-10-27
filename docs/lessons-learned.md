# 经验总结（NightWatch 迭代）

这份文档记录做代码审查、Linux 移植、告警改造这几轮里踩过的坑与验证有效的做法，供后续迭代直接复用。
发布操作层面的细节在 [release-checklist.md](release-checklist.md)，这里偏「为什么」和「怎么避免再踩」。

## 1. 流程与协作

- **小步 PR + 双平台 CI**：一个 PR 只做一件事，`win-x64` 与 `linux-x64` 都绿再合并。跨平台改动尤其如此——Windows 绿不代表 Linux 绿。
- **先看清基线再动手**：动手前 `git status` + `git log` 确认当前分支与未提交改动；工作区可能有其他会话的在研改动，`git add -A` 一次就能把无关文件带进提交。
- **合并走 API 比本地 git 稳**：GitHub 的 git 协议在本地经常连不上（`Failed to connect ... :443`），而 REST API 一直可用。合并、建 tag、建 Release 都可以用 API 完成，本地随后 `fetch` + `merge --ff-only` 补齐。
- **网络抖动是常态**：`git fetch/push` 失败用指数退避重试（本次最多重试到第 8 次才成功）；不要因为两次失败就判定「仓库坏了」。
- **发布照清单走**：tag、制品四项校验、双平台 Release、哈希回验，任何一步「凭记忆」都容易漏。

## 2. 代码与设计

- **平台差异集中到 `Platform/`**：服务控制、资源采集、权限审计各一个接口，调用方不写裸 `if (OperatingSystem.IsWindows())`。新增平台时只加实现，不改调用方。
- **Linux 进程名的 15 字符限制**：`/proc/<pid>/stat` 的 `comm` 被内核截断到 15 字符，`Process.ProcessName` 与 `GetProcessesByName` 都受影响。任何按名字匹配的逻辑都必须有「枚举落空 → 回退全量扫描」的兜底，否则长名进程每轮都被判「不存在」而重复拉起。
- **调用外部命令三件套**：`ArgumentList` 传参（不拼命令行）、超时并 kill、stdout/stderr 并发读。`systemctl` 和 `stat` 统一走一个 `ProcessRunner`，避免一份实现修了另一份没修。
- **取消语义不能吞**：`ct.ThrowIfCancellationRequested()` 放在方法开头，`catch (OperationCanceledException) { throw; }` 必须排在降级 `catch (Exception)` 之前。否则停机时取消异常被当成「采集失败」，关闭流程永远等不到结束。
- **别缓存易变的环境事实**：主机 IP 在 DHCP 续租、网卡切换、VPN 上下线后会变。构造时取一次，常驻几天后就会一直推旧地址；正确做法是发送时取 + 短 TTL 缓存（本次 30 秒），既跟得上变化，也不至于每条告警都枚举网卡。
- **`record` + `with` 优于手写拷贝**：给消息对象加字段时，`alert with { Host = …, HostIp = … }` 不会漏拷；手写 `new AlertMessage { ... }` 逐个赋值，下一个字段就会忘。
- **默认值就是兼容性承诺**：新增配置项若默认开启（例如告警默认带主机名与 IP），所有既有部署升级后行为都会变。这类改动必须写进发行说明，必要时给「怎么关掉」。

## 3. 测试

- **把判定抽成纯函数**：`ComputeCpuPercent`、`RulesIgnoringHungRestart`、`HostIpCache`、`SystemdServiceControl.Map` 都是不依赖环境的纯逻辑，直接单测覆盖边界（零间隔、计数回绕、TTL 过期、Linux/Windows 分支）。
- **平台专属用例用 `WindowsFact` / `LinuxFact`**：在错误平台上跳过，而不是假失败；CI 两个 job 合起来才是完整覆盖。
- **失败信息要可诊断**：涉及真实进程/文件的用例，失败时把 `exit code`、`stderr`、文件头、目标目录一起打出来。本次 Linux 用例连续三次红，就是靠 `exit=1 + stderr=coreutils: unknown program` 一次定位到根因。
- **断言锁行为，不锁实现**：排版这类改动的断言要成对——既要「必须分行」，也要禁掉具体症状（`DoesNotContain("PCIP")`）。只断言「包含某两行」放过了「两行粘在一起」的回归。
- **CI 环境假设要显式处理**（本次三个真坑）：
  - Ubuntu 24.04 起 `coreutils` 是**多合一二进制**，`/usr/bin/sleep` 指向 `coreutils` 并按 `argv[0]` 分发；复制改名后再执行会报 `coreutils: unknown program` 并 `exit=1`。需要长名测试进程时，复制**独立**二进制（`bash`/`dash`）。
  - `bash -c "sleep 30"` 会把最后一条外部命令**直接 exec**，bash 自身被替换，`/proc/<pid>/exe` 不再指向复制出来的路径。让 shell 常驻要用复合命令（`while :; do sleep 1; done`）。
  - `/tmp` 可能被挂成 `noexec`。需要执行复制出来的文件时，工作目录放在测试宿主输出目录（同卷、必然可执行）。

## 4. 工具链（PowerShell 5.1 / git / GitHub / Gitee）

- **提交信息写文件**：`git commit -F <文件>`。PS 5.1 会把提交信息里的 ASCII 双引号、甚至中文弯引号当成字符串定界符，把后半截消息拆成独立参数（报 `pathspec '...' did not match`）。
- **写文件统一无 BOM UTF-8**：`[IO.File]::WriteAllText(path, text, [Text.UTF8Encoding]::new($false))`。`Out-File -Encoding utf8` 会加 BOM，污染提交信息、tag 消息与生成的配置文件。
- **`.ps1` 脚本反而要 UTF-8 with BOM**：PS 5.1 对无 BOM 脚本按 GBK 解析，中文直接导致「字符串缺少终止符」。
- **写 Markdown 用单引号 here-string**：PS 双引号字符串里 `` ` `` 是转义符（`` `n `` 会变换行、`#` 前的反引号会消失）。
- **Gitee API 三个固定套路**：`attach_files` 的 token 必须放 query（放表单返回 401）；响应中文用 `curl -o` 落盘再按 UTF-8 读（PS 直接解码会乱码）；`releases/tags/{tag}` 不存在时返回 `null` 而不是 404，判重改用列表。
- **GitHub 删 tag 会连带删 Release**：Release 绑定在 tag 上。需要把 lightweight tag 换成 annotated 时，删 tag 后必须重建 Release 并重传资产；更省事的做法是一开始就 `git tag -a`。
- **资产下载会偶发超时**：`release-assets.githubusercontent.com` 不通时用 `curl --retry 3 --connect-timeout 30`，别急着怀疑制品损坏。

## 5. 渲染类改动（告警排版）

这几轮里唯一反复返工的地方，教训比较值钱：

- **钉钉遵循标准 markdown**：单个换行折叠成空格。`> 主机：A` 换行 `> IP：B` 会渲染成「主机：AIP：B」。引用块内同样成立。
- **空引用行 `>` 能分段**，但同一块内两行共用一个竖条；**拆成多个引用块**则字段之间留下大段空白、看起来像多条独立消息。最终采用「标题单独一行 + 字段同块 + 空引用行分段」，与常见运维告警版式一致。
- **这类改动必须有真机闭环**：代码对、单测绿，不等于手机上好看。本次排版改了三轮才定稿（粘连 → 断块 → 单块分段），每轮都靠截图确认。
- **先给参考样式再动手**：「像某某服务的格式」比「分行、不要太挤」这类描述有效得多。

## 6. 合并前检查清单

- [ ] 本地 `dotnet build` 0 警告、`dotnet test` 全绿
- [ ] 双平台 CI（`win-x64` + `linux-x64`）都 success
- [ ] 新增/修改的行为有测试，失败信息可诊断
- [ ] 平台差异集中在 `Platform/`，调用方没有裸平台判断
- [ ] 取消令牌会向上抛，没有被降级 catch 吞掉
- [ ] 外部命令调用有超时、`ArgumentList`、并发读流
- [ ] 常驻状态没有缓存易变的环境事实（IP、路径可达性等）
- [ ] 默认值变化已写进发行说明
- [ ] 只 `git add` 本次改动的具体文件

## 7. 发布前检查清单

- [ ] `main` 在两平台是同一个 sha
- [ ] annotated tag（`git tag -a`），两平台都推
- [ ] 制品四项校验：条目数、含可执行文件、不含 `appsettings.json`、记录 SHA256
- [ ] GitHub 与 Gitee 都建 Release 并传资产
- [ ] 四个下载逐个算 SHA256，与本地一致
- [ ] 合并后的分支远端 + 本地都删掉
