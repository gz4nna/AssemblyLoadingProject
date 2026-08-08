# AssemblyLoadingProject

基于 **AssemblyLoadContext** 实现 DLL 热插拔的插件调度中心。
目标：编写各种**定时执行的数据库数据传输服务**，把编译好的插件 DLL 放入 `Plugins` 目录，
即可通过 Web 界面配置参数并定时执行，**无需额外部署独立服务**。

## 一、架构总览

```
┌────────────────────────────────────────────────────────────┐
│  纯 HTML 前端（wwwroot，无 Razor/Blazor）                     │
│   - index.html    DLL 列表                                    │
│   - config.html   配置与启动                                  │
│   - status.html   运行状态（实时刷新 + 历史日志）               │
└───────────────┬────────────────────────────────────────────┘
                │  HTTP fetch 调用 Minimal API
┌───────────────▼────────────────────────────────────────────┐
│  后端 Minimal API（Program.cs）                               │
│   - /api/plugins 等 JSON 接口                                 │
│   - 静态文件 + 回退到 index.html                              │
└───────────────┬────────────────────────────────────────────┘
┌───────────────▼────────────────────────────────────────────┐
│  PluginHostService (单例)                                    │
│   - 周期扫描目录   - 定时调度   - 状态跟踪   - 日志落盘          │
└───────────────┬────────────────────────────────────────────┘
                │
┌───────────────▼────────────────────────────────────────────┐
│  PluginAssemblyLoader (AssemblyLoadContext 核心)             │
│   - 每个 DLL 独立 Collectible ALC                            │
│   - 解析 IDataTransferService 契约实例                        │
│   - 卸载(Unload) + GC 回收实现真正热插拔                        │
└───────────────┬────────────────────────────────────────────┘
                │
┌───────────────▼────────────────────────────────────────────┐
│  Plugins/ 目录                                              │
│   SampleSqlSyncPlugin.dll 等（放入即用）                       │
└────────────────────────────────────────────────────────────┘
```

## 二、核心契约

插件 DLL 只需实现宿主中的接口 `IDataTransferService`（位于宿主程序集）：

```csharp
public interface IDataTransferService
{
    string Id { get; }                       // 唯一标识
    string DisplayName { get; }
    string Version { get; }
    string Description { get; }
    void Initialize(PluginContext context);   // 加载时初始化(连接等)
    Task<TransferResult> ExecuteAsync(PluginContext context, CancellationToken ct); // 单次执行
    void Dispose();                            // 卸载时释放
}
```

关键点：**契约接口必须来自宿主（共享）程序集**，插件才能在自己的 ALC 中解析到同一接口类型，
这是热插拔成立的前提。`PluginContext` 传递前端配置的参数与宿主日志回调。

## 三、使用步骤（纯 HTML 前端）

1. **放置插件**：把编译好的插件 DLL 放入 `AssemblyLoadingProject/bin/Debug/net10.0/Plugins/`
   （或 appsettings 里 `Plugins:Directory` 指定的目录）。
2. **运行宿主**：`dotnet run --project AssemblyLoadingProject`。
3. 浏览器打开启动页（默认 `http://localhost:5246/`，以 launchSettings 或启动参数为准）：
   - **`/`（列表页）**：查看扫描到的 DLL 与状态，可"重新扫描"。
   - **`/config.html`（配置页）**：选择 DLL → 编辑参数（键值对）、间隔、备注 → "保存并应用 / 仅加载 / 立即执行 / 卸载"。
   - **`/status.html`（状态页）**：每 2 秒实时刷新运行状态、执行历史、近期日志，并可"加载完整历史"。
4. **配置持久化**：前端保存的**启用状态、间隔、参数、备注**实时写入 `Plugins/plugins.config.json`；
   宿主重启后按 **扫描 → 默认配置 → 读 JSON 覆盖 → `enabled=true` 自动加载调度** 恢复。
5. **更新/热替换**：替换 DLL 文件后，重新扫描检测到更新并自动重载（无需重启宿主）。

### 日志轻量化（避免长时间运行负担）
- **近期日志**：内存仅保留最近 50 条，状态页直接展示。
- **历史日志**：每条日志同时落盘到 `Plugins/logs/<插件名>.log`（自动轮转，超 2MB 归档），
  状态页可"加载完整历史"按需读取，长期信息不丢失。
- **冗余汇总**：每次执行记录一条"执行历史"（时间/成败/耗时/行数/消息），
  状态页以汇总列表呈现，避免堆积大量细节日志。

## 四、开发插件

参考 `SamplePlugins/SampleSqlSyncPlugin`。注意 csproj 关键配置：

```xml
<EnableDynamicLoading>true</EnableDynamicLoading>

<!-- 引用宿主契约（不复制，回退 Default ALC 保证类型同一性） -->
<ProjectReference Include="..\..\AssemblyLoadingProject\AssemblyLoadingProject.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>

<!-- 引用 TransDataHelper（共享依赖，不复制，宿主统一提供） -->
<ProjectReference Include="..\..\..\DataTableCopyScripts\TransDataHelper\TransDataHelper\TransDataHelper.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>

<OutputPath>..\..\AssemblyLoadingProject\bin\Debug\net10.0\Plugins\</OutputPath>
```

**共享依赖机制（避免重复携带）**：
- 宿主 `AssemblyLoadingProject` 已引用 `TransDataHelper`，因此宿主输出自带 `TransDataHelper.dll` 及其所有驱动依赖（MySqlConnector / Oracle / AseClient / Microsoft.Data.Sqlite 等）。
- 插件同样引用 `TransDataHelper`，但通过 `Private=false` + `ExcludeAssets=runtime`，**不把 TransDataHelper.dll 及驱动复制进插件输出**。插件在独立 ALC 中解析时，经 `Resolving` 事件回退到 `AssemblyLoadContext.Default`（宿主已加载的共享副本）。
- 业务插件只需在自己的 `Plugins/` 目录放置**自身 DLL** 即可（外加个别必需的原生库，如 SQLite 的 `e_sqlite3.dll`）。

### 数据传输插件基类 `DataTransferPluginBase`
宿主提供 `DataTransferPluginBase : IDataTransferService`，封装了基于 `TransDataHelper` 的跨库读写通用流程（构造源/目标适配器 → `ExecuteReader` 读取 → 分批写入）。业务插件继承它，只需实现少量抽象成员即可完成一次真实的数据传输：

```csharp
public sealed class MySyncPlugin : DataTransferPluginBase
{
    public override string Id => "mysync";
    public override string DisplayName => "MySQL→Sybase 同步";
    public override string Version => "1.0.0";
    public override string Description => "...";

    protected override string SourceDbType => "MySql";   // 源库类型
    protected override string TargetDbType => "Sybase";  // 目标库类型
    protected override string BuildSourceSql() => "SELECT ... FROM src WHERE flag=0";
    protected override string GetTargetTable() => "erp_dst";
    protected override string BuildRowSql(DataRow row) => /* 生成 INSERT ... */;
}
```

前端为源/目标连接配置对应参数（`SourceHost/SourceDatabase/SourceUser/...`、`TargetHost/TargetDatabase/...`），
由 `DataTransferPluginBase.CreateAdapter` 按 `dbType` 构建 `TransDataHelper` 适配器。

### 旧式/控制台插件（无需改写即可接入）
如果你有现成的**控制台项目**（如 `testprogram`，含静态 `Main` 方法，且没有实现 `IDataTransferService`），
把它编译出的 DLL 放入插件目录即可被宿主加载并定时调度：
- 加载器找不到 `IDataTransferService` 实现时，会**自动退化为"旧式入口插件"**（`LegacyEntryPointPlugin`），
  在每次执行时通过反射调用其静态入口方法（默认 `Main`）。
- 依赖（如 `TransDataHelper`）同样由宿主共享解析，**插件目录无需重复携带 TransDataHelper.dll**。
- 适用限制：入口方法应为无参或 `string[]` 参数；若其为 `async Task Main`，每次执行会 await 完成。
- 提示：若控制台程序内部是"常驻循环 + 自身定时器"，请改为"单次执行逻辑"，由宿主统一调度，否则会阻塞。

## 五、设计建议与更合理思路

### 已实现的决策
- **扫描与加载分离**：目录扫描只登记文件元数据，不立即执行；由前端设置参数并启用后才加载调度，符合"指定参数后才启动"的需求。
- **每个 DLL 独立 ALC（`isCollectible: true`）**：卸载后经 GC 真正释放，实现"热替换"。
- **固定间隔轮询调度**：简单可靠，适合大多数数据传输场景；未引入重型 Cron 依赖。
- **TransDataHelper 作为宿主共享依赖**：插件与宿主共用同一份 `TransDataHelper`，避免重复携带驱动文件，同时保证多数据库操作契约统一。

### 可进一步改进（生产化建议）
1. **配置持久化（已实现）**：当前配置已通过 `PluginConfigStore` 持久化为 `Plugins/plugins.config.json`。可选增强：迁移到 SQLite，便于并发读写与查询。
2. **Cron 支持**：对需要"每天 2 点"等精确时刻的任务，引入 `Cronos`/`Quartz.NET` 替换固定间隔。
3. **失败重试与告警**：增加退避重试、执行超时（已内置 30 分钟）、失败邮件/Webhook 通知。
4. **真实数据同步**：示例插件默认演示 SQLite→SQLite；真实库只需在插件中把 `SourceDbType/TargetDbType` 改为 MySql/Oracle/Sybase，并配好连接参数。注意 `TransDataHelper` 的 `OracleAdapter` 禁止写入（主库保护），且 Sybase 走纯文本拼接。
5. **插件依赖管理**：已通过"宿主共享 TransDataHelper"降低重复；如插件有自身私有第三方依赖，可放入插件目录（`Resolving` 事件会先查插件目录）。
6. **安全与隔离**：插件代码可完全访问宿主进程。生产环境建议：签名校验、进程级沙箱（如宿主进程 + 插件子进程通过 IPC 通信），或至少对上传 DLL 做白名单。
7. **托管为 Windows 服务 / Linux systemd**：作为后台常驻服务运行，配合 `Microsoft.Extensions.Hosting` 的 `Host` 实现（当前已内嵌调度循环，可平滑迁移）。

## 六、项目结构

```
AssemblyLoadingProject/
├─ Plugins/                                       插件核心（宿主侧）
│  ├─ Abstractions/
│  │  ├─ IDataTransferService.cs                  契约接口、PluginContext、TransferResult
│  │  ├─ DataTransferPluginBase.cs                基于 TransDataHelper 的跨库传输插件基类
│  │  └─ LegacyEntryPointPlugin.cs                旧式/控制台插件适配器（反射调用入口方法）
│  ├─ PluginAssemblyLoader.cs                     ALC 核心：扫描/加载/卸载/重载
│  ├─ PluginHostService.cs                        单例宿主：调度/状态/日志
│  ├─ PluginHostedService.cs                      标准 IHostedService 生命周期托管
│  ├─ PluginConfig.cs                             插件配置模型
│  ├─ PluginConfigStore.cs                        配置 JSON 持久化存储
│  ├─ PluginRunState.cs                           运行状态与日志条目/执行历史
│  └─ PluginLogStore.cs                           日志落盘（近期内存 + 历史文件轮转）
├─ wwwroot/                                       纯 HTML 前端（无 Razor）
│  ├─ index.html                                  DLL 列表页
│  ├─ config.html                                 配置与启动页
│  ├─ status.html                                 运行状态页（实时刷新 + 历史日志）
│  ├─ app.js                                      前端通用 AJAX/格式化工具
│  └─ app.css                                     基础样式
├─ Program.cs                                     入口：Minimal API + 静态文件
├─ Properties/launchSettings.json                 本地调试启动配置
├─ appsettings.json                               应用配置（插件目录等）
└─ SamplePlugins/SampleSqlSyncPlugin/             示例插件(SQLite→SQLite，基于 TransDataHelper)
```
