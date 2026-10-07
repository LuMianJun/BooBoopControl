# BooBoopControl

BooBoopControl 是独立的 Boo Boop 硬件控制类库，当前实现 FN010-RX 的发现、连接、伸缩、振动、双输出停止及异步释放。它不依赖游戏、Unity 或 BepInEx，也没有插件入口；仅将 DLL 放入游戏插件目录不会启动设备。

产品版本 **1.2.2**，目标 **net6.0 / C# 10 / x64**，程序集 `BooBoopControl.dll`，命名空间沿用 `BooBoopBridge`，AssemblyVersion 为 `1.0.0.0`，API 主版本为 1。游戏状态由独立项目 **SecretFlasherManaka For EveryThing**（`SecretFlasherManaka.ForEveryThing`）提供，整合策略由 `SecretFlasherManaka.BooBoopBridge` 总项目负责。

## 当前能力

| 能力 | 当前实现及边界 |
| --- | --- |
| 设备发现 | 启动原厂 Native Host，发送一次 search，收集约 5 秒的 FN010-RX 发现结果 |
| 设备选择 | 无地址偏好时必须只有一个候选；可通过构造参数指定地址，比较时忽略冒号、连字符及大小写 |
| 动态 UUID | 从当前用户 `%APPDATA%\BooBoop\product_list.json` 的 serialNumber 解析，不使用固定 UUID |
| 连接 | 使用发现结果及缓存元数据连接；初始化总期限 25 秒，连接阶段期限 12 秒 |
| 伸缩 / 振动 | 两套 API 分别接收协议编号 0..9；0 表示停止对应输出 |
| 双停 | StopAsync 分别尝试伸缩零值和振动零值；第一项失败仍尝试第二项 |
| 排队取消 | 单路零值使该路较早的排队正向请求失效；全局停止、断连和释放使两路旧请求失效 |
| 状态与诊断 | 就绪检查、状态事件、错误事件及有界后台通知队列 |
| 资源释放 | 取消本实例任务，尝试双停，关闭并确认本实例启动的原厂进程退出 |

目前只支持 **FN010-RX**。没有自动重连、多设备同时控制、跨平台传输、物理位置/行程控制、动作执行回执或物理停止确认。0..9 是协议编号，不是线性强度、速度或遥控器 0..10 的已验证对应关系。

## 实现方案与源码结构

```text
BooBoopControl/
  BooBoopControl.csproj
  src/
    BooBoopOptions.cs      # 系统默认路径、自定义路径及无 I/O 校验
    IBooBoopController.cs   # 消费者与 mock 共用的异步控制契约
    BooBoopController.cs   # 一次性会话、发现、UUID、连接、动作与清理
    NativeHost.cs          # 原厂进程、标准输入输出分帧、超时与退出
    MotionEpochs.cs        # 全局及每通道的排队请求版本记账
```

`NativeHost` 启动 `BooBoopOptions.HostExecutablePath` 指定的原厂程序；空值使用系统 Program Files 下的 `Boo Boop/Boo Boop.exe`，工作目录从 EXE 所在目录推导，以原厂扩展 Origin 和 `--parent-window=0` 作为参数，通过重定向管道通信。帧格式是 **4 字节小端长度 + UTF-8 JSON**，单帧上限 1 MiB，JSON 深度上限 16；接收端也兼容最多两层 JSON 字符串包装。标准错误被排空但不保存或显示。

发送命令受白名单限制：get_state、search、connect、disconnect，以及 aa01 / bb01 各 0..9 的命令。当前控制器实际使用 search、connect 和模式命令；白名单允许 get_state / disconnect 不代表已有公开查询或主动断开 API。伸缩命令是 `aa01` 加两位十六进制编号，振动命令是 `bb01` 加编号，例如伸缩停止 `aa0100`、振动停止 `bb0100`。

发现按规范化地址去重，最多保存 128 个记录。选择后校验真实 service_data，并从缓存的成功响应结构中寻找唯一匹配产品配置；无效、空、冲突或多义 UUID 不启用动作。缓存上限 1 MiB、读取期限 3 秒；缓存存在其他型号不代表支持这些型号。

控制器用一个操作信号量串行化初始化、控制和清理；Native Host 另有写锁，防止管道帧交错。`MotionEpochs` 在请求进入队列时捕获全局和对应通道版本，在正向发送前再次检查。已经开始写入的命令不能撤回，停止不会抢占正在执行的管道写入。

## 公共接口与调用约定

| 成员 | 作用 |
| --- | --- |
| BooBoopController(string preferredDeviceAddress = "") | 保留原入口，使用系统默认路径；不启动设备 |
| BooBoopController(string preferredDeviceAddress, BooBoopOptions options) | 自定义原厂程序与缓存路径；不启动设备 |
| IsReady | 本实例和原厂进程当前是否可用 |
| InitializeAsync(CancellationToken) | 显式启动一次初始化；重复调用返回同一个 Task |
| SetStretchAsync(int, CancellationToken) | 请求伸缩协议编号 0..9 |
| SetVibrationAsync(int, CancellationToken) | 请求振动协议编号 0..9 |
| StopAsync(CancellationToken) | 使旧动作请求失效并尝试双停 |
| DisposeAsync() | 共享同一次异步清理 |
| Diagnostic / Error | 后台线程分发通知 |

上述接口由 `IBooBoopController : IAsyncDisposable` 提供；具体类另有 `State`、`StateChanged` 和 `ApiMajorVersion`。状态枚举包含 Created、Initializing、Ready、Disconnected、Faulted、Disposing、Disposed。

```csharp
using BooBoopBridge;

await using IBooBoopController control = new BooBoopController();
// 显式调用以下方法会启动原厂程序并尝试连接真实设备。
await control.InitializeAsync();
// 应用需要动作时，await control.SetVibrationAsync(4) 等调用。
await control.StopAsync();
```

所有控制 Task 都需要 await 或处理异常；就绪前请求直接拒绝，不缓存到连接完成后执行。初始化失败或断连后，不会重试、重连或重放；需要再次连接时由应用释放旧实例并新建实例。库本身不注册游戏生命周期。

通知队列上限 64，积压时丢弃最旧通知，不能把事件当作完整审计记录。回调不能直接访问 Unity 对象；以任务结果和 State / IsReady 判断软件状态。诊断不输出原始响应、完整地址或激活信息。

## 构建与验证

在本项目目录运行，不需要游戏 DLL：

```powershell
dotnet build .\BooBoopControl.csproj -c Release
```

输出为 `bin/Release/net6.0/BooBoopControl.dll`。实际通信要求 Windows 和上述原厂安装路径；在其他平台能够编译不等于可以连接设备。

2026-10-07 本库 Release 构建成功；Bridge 的 **47/47 mock 通过**，其中直接验证了每通道及全局排队取消记账、安装路径解析。

## 代码审查结论与限制

- 责任边界清晰，游戏对象没有进入硬件类库；初始化和输出串行，分通道取消与双停路径已实现。
- 原厂 EXE 和缓存路径已可配置；Origin 和协议仍保持既有实现，没有版本协商，更换协议或设备型号仍需实现和验证。
- 发现观察结束不等于取消原厂扫描；厂商进程可能联网或更新缓存。
- DisposeAsync 的双停属于尽力清理。原厂进程退出无法确认时，任务可能失败且 State 保持 Disposing；再次调用仍返回同一清理 Task，不会重新执行清理。
- 软件写入成功只证明数据写入原厂进程，不证明实际动作或停止。崩溃、强杀或链路故障下不能保证物理停止。

本次路径通用化修改了路径解析与传入方式，保留发现、协议、动作和停止逻辑；没有运行原厂 EXE、扫描蓝牙或控制硬件。完整组合用法见总项目的 README。

## 自定义安装路径

旧构造签名保留，消费者可使用新增重载：

```csharp
var options = new BooBoopOptions
{
    HostExecutablePath = @"E:\Apps\Boo Boop\Boo Boop.exe",
    ProductCachePath = @"%APPDATA%\BooBoop\product_list.json"
};
// 创建实例不启动程序；只有 InitializeAsync 会启动并连接。
await using IBooBoopController control = new BooBoopController("", options);
```

空值（含空白）回退到系统默认目录；支持环境变量，要求展开后为绝对文件路径，不接受相对路径。HostExecutablePath 必须指向 .exe，工作目录自动使用该文件所在目录。创建时解析并固定两条路径，随后配置变化不影响已有实例；文件存在性在真正初始化 / 读取时检查。解析不会读取文件或启动设备。只更改安装路径不会改变原厂扩展 Origin 或 FN010-RX 协议。

BooBoopOptions 的 GetHostExecutablePath / GetProductCachePath / GetHostWorkingDirectory 方法可用于无设备的配置预检。通用化测试覆盖默认目录、非 C 盘、中文和空格、环境变量、非法路径及目录推导。

## 仓库入口与型号扩展

本仓库名称保持 BooBoopControl；可运行 build.ps1 或 bash build.sh 独立构建。C# 命名空间 BooBoopBridge 保留以兼容已有消费者。

后续仍使用 BooBoop 软件的新型号，可在本仓库增加独立适配，不需要按每个型号新建仓库。每个型号必须有明确的发现、连接、命令和停止验证；不能只放宽 FN010-RX 名称判断。

| 型号 | 当前实现能力 |
| --- | --- |
| FN010-RX | 振动、伸缩协议编号 0..9、双停 |
| 其他型号 | 尚未实现 |

兼容新型号若公共能力语义不变，游戏信号插件可以复用；新增位置 / 行程能力时再设计能力接口和 Bridge 映射。

## 许可证

[MIT](LICENSE)，Copyright (c) 2026 Lu_Noodles。外部软件与引用范围见 [第三方说明](THIRD_PARTY_NOTICES.md)。
