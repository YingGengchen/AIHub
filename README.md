# AI Hub 代理（Visual Studio 扩展）

在 Visual Studio 进程内运行的本地 AI 网关：让 Copilot Chat 用上任意云端或本地大模型。

```
Visual Studio Copilot Chat → http://localhost:11434（本地 Ollama 协议）→ AI Hub 代理 → DeepSeek / Kimi / SiliconFlow / Anthropic …（上游协议）
```

## 功能

- 在 Visual Studio 进程内启动一个本地 HTTP 服务，同时支持两套协议：
  - Ollama：`/`、`/api/version`、`/api/tags`、`/api/ps`、`/api/show`、`/api/chat`、`/api/generate`、`/api/embed`、`/api/embeddings`、`/api/pull`、`/api/delete`、`/api/copy`
  - OpenAI：`/v1/models`、`/v1/chat/completions`
  - 健康检查：`/health`、`/health/providers`
- 模型名按 `提供方/模型` 路由，例如 `deepseek/deepseek-v4-flash`、`siliconflow/Qwen/Qwen3-Coder-480B-A35B-Instruct`。
- 两套上游协议，按提供方分别配置：
  - **OpenAI 兼容**（默认）：`{Base URL}/chat/completions`
  - **Anthropic**：`{Base URL}/v1/messages`，消息、工具调用、`system` 提示与流式事件都会自动双向转换
- 流式输出（Ollama NDJSON 与 OpenAI SSE）与 `reasoning_content` / `thinking` 思考内容透传。
- 工具调用（function calling）双向转换：Ollama 的对象形式参数 ↔ OpenAI 的字符串增量，并按 `tool_name` 回填 `tool_call_id`。
- 重试与故障转移：按提供方配置的重试次数做指数退避（1s / 2s / 4s … 封顶 10s，仅对 408、429、5xx 与连接失败重试），耗尽后自动切到「备用模型」。
- Embeddings 真实转发：`/api/embed`、`/api/embeddings` 会把请求转发到上游 `{Base URL}/embeddings` 并转回 Ollama 形状。
- 在 `/api/tags` 与 `/api/show` 中声明 `model_info["llama.context_length"]`，默认 1048576，避免 Copilot Chat 只按 100K 上下文处理。
- API Key 支持 `${ENV_NAME}` 环境变量引用；也可用 `PROVIDERS_<名称>_BASE_URL` + `PROVIDERS_<名称>_API_KEY` 让提供方在启动时自动注册，用 `EXTRA_MODELS` 追加模型。
- 随 Visual Studio 自动启动、退出时自动停止；日志按天写入并自动轮转。

## API Key 存储

API Key 在保存时用 Windows DPAPI（当前用户范围）加密，配置文件中以 `dpapi:` 前缀的 Base64 密文出现，不再有明文。

- 只有同一台机器上的同一个 Windows 用户能解密；换用户或换机器后该字段会解不出来（会被当作空值处理，重新填写即可）。
- 面板里显示的始终是明文，便于复制核对；加密只发生在写入磁盘这一步。

## 构建

```powershell
dotnet build AIHub.slnx -c Release
```

产物：`AIHub\bin\Release\net472\AIHub.vsix`

## 安装

1. 关闭 Visual Studio。
2. 双击 `AIHub.vsix` 即可；命令行安装则用 VS 安装目录下的 `VSIXInstaller.exe`：

   ```powershell
   & "<Visual Studio 安装目录>\Common7\IDE\VSIXInstaller.exe" AIHub.vsix
   ```

   例如 VS 2022 社区版是 `C:\Program Files\Microsoft Visual Studio\2022\Community\`，
   VS 2026 是 `<盘符>\Microsoft Visual Studio\18\`。
   若装在实验实例上调试，追加 `/rootSuffix:Exp`；更新已安装版本后执行
   `devenv.exe /rootSuffix Exp /updateconfiguration` 可让 pkgdef 缓存立刻刷新。
3. 重新启动 Visual Studio。

## 使用

1. 菜单 **工具 → AI Hub 代理设置...**（或视图 → 其它窗口 → AI Hub 代理）打开面板。
2. 选择提供方，填写 Base URL、API Key，按需选择接口协议、重试次数、备用模型。
3. 编辑模型列表（每行一个 `提供方/模型`），或点 **拉取可用模型** 用当前 API Key 自动获取；确认端口（默认 `11434`）。
4. 点 **保存并应用**，状态行显示 `● 代理运行中：http://localhost:11434` 即成功。
5. Copilot Chat → 模型选择器 → **管理模型** → 添加 **Ollama** → 地址填 `http://localhost:11434` → 勾选面板里配置的模型 → 保存。

## 自定义提供方

点提供方列表上方的 **新增**，填写名称（作为模型前缀，不含 `/`）、Base URL、接口协议与 API Key。本地服务（如 LM Studio、Ollama）可留空 Key。

## 多个 Visual Studio 实例

同一时间只允许一个实例监听端口，其它实例会自动进入 **其它实例托管** 状态，而不是报「端口被占用」。谁在监听由命名互斥体仲裁，交接完全由事件唤醒驱动，没有任何定时轮询：

- 面板状态显示 `● 其它实例托管`，并提示是哪个进程（PID）在监听；此时启动/重启按钮不可点。
- 想改配置就在任意实例里改：保存后配置文件变化会被其它实例自动重载，面板同步刷新。
- 关闭托管者（或托管者进程崩溃）后，剩下的实例会 **立刻** 接管端口（实测约 10 毫秒），状态自动变回 `● 代理运行中`，不需要重启 Visual Studio。
- 在任意一个实例里点 **停止代理**，所有实例会同步停止并保持停止。
- **重启代理** 不会释放端口所有权，因此重启过程中不会出现被其它实例抢走端口的空档。
- 改了端口并保存/重启后，其它实例会自动跟到新端口上继续待命，旧端口会被释放。
- 若端口被 Ollama 等其它程序占用，仍会按「端口被占用」报错，不会误判成 AI Hub。

## 配置与日志

| 项目 | 路径 |
| --- | --- |
| 配置文件 | `%LOCALAPPDATA%\AIHub\proxy-config.json` |
| 日志（默认） | `%LOCALAPPDATA%\AIHub\logs\proxy-yyyy-MM-dd.log`（10 MB 轮转，保留 14 天） |

配置文件里的 API Key 是 DPAPI 密文，但仍请勿把该文件公开，因公开文件导致的一切损失本人概不负责。

日志面板里的三项设置（修改后点 **保存并应用** 生效）：

- **记录日志**：取消勾选后运行时不再产生任何日志文件，转发功能不受影响。
- **日志目录**：填写绝对路径即可改存到别处（例如 `D:\Logs\AIHub`），支持 `%USERPROFILE%` 与 `${VAR}` 环境变量写法；留空或填相对路径则回到默认目录。旁边 **浏览…** 选目录，**默认** 一键清空。
- **删除日志**：删除当前日志目录下所有 `proxy-*.log*`（含轮转文件），内存里的最近日志一并清空。目录本身会保留。

## 常见问题

- **状态显示“端口 11434 已被占用”**：本机已有 Ollama 或其它程序在运行。关闭它，或在面板中改用其他端口（记得同步修改 Copilot 里填写的地址）。若占用者本身是另一个 Visual Studio 的 AI Hub，则会显示「其它实例托管」而不是报错。
- **返回“未找到提供方”**：模型名必须带提供方前缀，且该提供方需要在面板中存在并填写了 Base URL / API Key。
- **绑定端口被拒绝（错误 5）**：以管理员身份执行
  `netsh http add urlacl url=http://localhost:11434/ user=Everyone`。
- **模型上下文显示很小**：确认 `/api/tags` 返回的 `model_info["llama.context_length"]` 是期望值，并重启 Copilot Chat 会话。
- **Anthropic 提供方报 401**：官方端点只认 `x-api-key`；第三方 Anthropic 兼容网关通常要 Bearer。本扩展对官方域名只发 `x-api-key`，对其它域名两者都发。
- **Embeddings 报“不支持 embeddings”**：Anthropic 没有 embeddings 接口，请把该模型指向 OpenAI 兼容提供方。

## 隐私

- 扩展不收集、不上报任何遥测数据。
- 你的对话内容、代码片段会**原样转发**到你配置的上游服务，是否留存取决于该服务商的条款。
- API Key 只在本机加密保存，转发时也只发往你填写的 Base URL。
- 本地端口仅监听 `localhost` / `127.0.0.1`，且**没有入站认证**，请勿把它暴露到局域网。

安全问题请按 [SECURITY.md](SECURITY.md) 私下报告，不要开公开 Issue。

## 许可证

[MIT](LICENSE)
