# Yuzu-Frontend 命名规范文档

## 概述

本文档定义了 Yuzu-Frontend 项目中使用的命名规范，旨在明确区分系统的各个组件：后端（Backend）、前端（Frontend）、MC服务端管理器（Manager）、MC服务端（MCServer）以及服务器（Server）。

## 组件定义

| 组件 | 定义 | 说明 |
|-----|------|-----|
| **Backend** | PMCSsE 后端服务 | 运行在远程服务器上的服务端程序，负责管理 MC 服务端 |
| **Frontend** | Yuzu-Frontend 前端应用 | 用户界面应用，通过加密通道连接后端 |
| **Manager** | MC服务端管理器 | 后端中管理单个 MC 服务端实例的逻辑单元 |
| **MCServer** | Minecraft 服务端 | 实际运行的 Minecraft 服务器进程 |
| **Server** | 物理/虚拟服务器 | 运行后端服务的硬件或虚拟机 |

## 命名规则

### 通用规则

1. **使用 PascalCase**：类型名、属性名、方法名使用 PascalCase
2. **使用 camelCase**：私有字段、局部变量使用 camelCase
3. **使用下划线前缀**：私有字段使用 `_` 前缀
4. **避免缩写**：使用完整单词，如 `BackendAddress` 而非 `BkAddr`
5. **保持一致性**：同类组件使用相同的命名模式

### 组件前缀

| 前缀 | 组件 | 示例 |
|-----|------|-----|
| `backend` | 后端相关 | `BackendAddress`, `BackendPort`, `BackendPassword` |
| `mcServer` | MC服务端相关 | `MCServerName`, `MCServerStatus` |
| `manager` | 管理器相关 | `ManagerId`, `ManagerName` |
| `frontend` | 前端相关 | `FrontendConfig`, `FrontendSettings` |

## 调整后的变量和绑定名称列表

### ConnectionViewModel 属性

| 名称 | 数据类型 | 所属模块 | 作用 |
|-----|---------|---------|-----|
| `BackendAddress` | `string?` | Yuzu_Frontend.Models | 向后端发起连接的地址 |
| `BackendPort` | `string` | Yuzu_Frontend.Models | 向后端发起连接的端口 |
| `BackendPassword` | `string?` | Yuzu_Frontend.Models | 目标后端设置的访问密钥 |
| `IsConnected` | `bool` | Yuzu_Frontend.Models | 是否已与后端建立连接 |
| `IsConnecting` | `bool` | Yuzu_Frontend.Models | 是否正在与后端连接 |
| `ConnectionStatus` | `string` | Yuzu_Frontend.Models | 与后端连接的状态 |
| `FingerprintStatus` | `string` | Yuzu_Frontend.Models | RSA 公钥指纹确认状态 |
| `IsBackendPortValid` | `bool` | Yuzu_Frontend.Models | 后端端口是否有效 |
| `ConnectedBackends` | `ObservableCollection<ConnectionBackendEntry>` | Yuzu_Frontend.Models | 已连接的后端条目集合 |
| `LogEntries` | `ObservableCollection<LogEntry>` | Yuzu_Frontend.Models | 连接日志条目集合 |

### ConnectionViewModel 命令

| 名称 | 所属模块 | 作用 |
|-----|---------|-----|
| `ConnectCommand` | Yuzu_Frontend.Models | 发起连接命令 |
| `DisconnectCommand` | Yuzu_Frontend.Models | 断开连接命令 |
| `ClearLogsCommand` | Yuzu_Frontend.Models | 清空日志命令 |

### ConnectionViewModel 事件

| 名称 | 所属模块 | 作用 |
|-----|---------|-----|
| `Connected` | Yuzu_Frontend.Models | 连接成功建立时触发 |
| `Disconnected` | Yuzu_Frontend.Models | 连接断开时触发 |
| `DataPackReceived` | Yuzu_Frontend.Models | 收到后端数据包时触发 |
| `NeedPassword` | Yuzu_Frontend.Models | 需要用户输入密码时触发 |
| `NeedRSAPublicKeyVerification` | Yuzu_Frontend.Models | 需要用户确认 RSA 公钥指纹时触发 |

### ConnectionBackendEntry 属性

| 名称 | 数据类型 | 所属模块 | 作用 |
|-----|---------|---------|-----|
| `BackendAddress` | `string` | Yuzu_Frontend.Models | 后端地址 |
| `BackendPort` | `string` | Yuzu_Frontend.Models | 后端端口号 |
| `ConnectionStatus` | `string` | Yuzu_Frontend.Models | 当前连接状态 |
| `FingerprintStatus` | `string` | Yuzu_Frontend.Models | RSA 公钥指纹校验状态 |
| `IsSelected` | `bool` | Yuzu_Frontend.Models | UI 表格中的选择状态 |
| `BackendName` | `string` | Yuzu_Frontend.Models | 格式化的后端名称（地址:端口） |

### ConnectionHistoryItemModel 属性

| 名称 | 数据类型 | 所属模块 | 作用 |
|-----|---------|---------|-----|
| `BackendAddress` | `string` | Yuzu_Frontend.Models | 历史连接的后端地址 |
| `BackendPort` | `string` | Yuzu_Frontend.Models | 历史连接的后端端口 |
| `BackendPassword` | `string` | Yuzu_Frontend.Models | 历史连接的访问密钥 |
| `Timestamp` | `DateTime` | Yuzu_Frontend.Models | 最后连接时间 |
| `UseCount` | `int` | Yuzu_Frontend.Models | 使用次数 |
| `IsRememberPassword` | `bool` | Yuzu_Frontend.Models | 是否记住密码 |
| `BackendName` | `string` | Yuzu_Frontend.Models | 格式化的后端名称 |

### ConnectionHistoryItem 属性（StartPage 本地）

| 名称 | 数据类型 | 所属模块 | 作用 |
|-----|---------|---------|-----|
| `Address` | `string` | Yuzu_Frontend.Desktop.Views | 历史连接地址 |
| `Port` | `string` | Yuzu_Frontend.Desktop.Views | 历史连接端口 |
| `Password` | `string` | Yuzu_Frontend.Desktop.Views | 历史连接密码 |
| `Timestamp` | `DateTime` | Yuzu_Frontend.Desktop.Views | 最后连接时间 |
| `UseCount` | `int` | Yuzu_Frontend.Desktop.Views | 使用次数 |
| `BackendName` | `string` | Yuzu_Frontend.Desktop.Views | 格式化的后端名称 |
| `FriendlyTime` | `string` | Yuzu_Frontend.Desktop.Views | 友好的时间显示 |
| `FrequencyLevel` | `string` | Yuzu_Frontend.Desktop.Views | 使用频率级别 |

### LogsViewModel 属性

| 名称 | 数据类型 | 所属模块 | 作用 |
|-----|---------|---------|-----|
| `LogDocument` | `TextDocument?` | Yuzu_Frontend.ViewModels | 日志文档（AvaloniaEdit 绑定） |
| `ActiveManagerId` | `string?` | Yuzu_Frontend.ViewModels | 当前显示的管理器 ID |
| `ActiveManagerName` | `string?` | Yuzu_Frontend.ViewModels | 当前显示的管理器名称 |
| `IsAutoScroll` | `bool` | Yuzu_Frontend.ViewModels | 是否自动滚动到末尾 |
| `LogPollingIntervalMs` | `double` | Yuzu_Frontend.ViewModels | 日志轮询间隔（毫秒） |
| `MaxLineCount` | `int` | Yuzu_Frontend.ViewModels | 文档最大行数 |
| `CommandInput` | `string` | Yuzu_Frontend.ViewModels | 命令输入框内容 |
| `CommandHistory` | `ObservableCollection<string>` | Yuzu_Frontend.ViewModels | 命令历史记录 |
| `CommandsHint` | `ObservableCollection<string>` | Yuzu_Frontend.ViewModels | 常用命令提示 |

### NavigatedPageBase 属性

| 名称 | 数据类型 | 所属模块 | 作用 |
|-----|---------|---------|-----|
| `Connection` | `ConnectionViewModel?` | Yuzu_Frontend.Desktop.Models | 连接视图模型实例 |
| `ToastManager` | `ISukiToastManager?` | Yuzu_Frontend.Desktop.Models | Toast 管理器 |
| `DialogManager` | `ISukiDialogManager?` | Yuzu_Frontend.Desktop.Models | 对话框管理器 |

### 绑定路径（XAML）

| 绑定路径 | 所属文件 | 作用 |
|---------|---------|-----|
| `Connection.BackendAddress` | StartPage.axaml | 后端地址输入框绑定 |
| `Connection.BackendPort` | StartPage.axaml | 后端端口输入框绑定 |
| `Connection.BackendPassword` | StartPage.axaml | 访问密钥输入框绑定 |
| `Connection.ConnectionStatus` | StartPage.axaml | 连接状态显示绑定 |
| `Connection.IsConnecting` | StartPage.axaml | 连接中状态绑定 |
| `Connection.ConnectedBackends` | StartPage.axaml | 已连接后端列表绑定 |
| `Connection.HasSelectedBackends` | StartPage.axaml | 是否有选中后端绑定 |
| `Connection.SelectAllChecked` | StartPage.axaml | 全选复选框绑定 |
| `Connection.LogEntries` | StartPage.axaml | 通信日志列表绑定 |
| `BackendAddress` | StartPage.axaml (DataTemplate) | 后端地址显示绑定 |
| `BackendPort` | StartPage.axaml (DataTemplate) | 后端端口显示绑定 |
| `FingerprintStatus` | StartPage.axaml (DataTemplate) | 指纹状态显示绑定 |
| `IsSelected` | StartPage.axaml (DataTemplate) | 选择状态绑定 |
| `BackendName` | StartPage.axaml (DataTemplate) | 后端名称显示绑定 |
| `FriendlyTime` | StartPage.axaml (DataTemplate) | 友好时间显示绑定 |
| `FrequencyLevel` | StartPage.axaml (DataTemplate) | 使用频率显示绑定 |

## 命名规范总结

### 核心原则

1. **明确性**：名称应清晰表达变量的用途和所属组件
2. **一致性**：同类组件使用相同的命名模式
3. **可区分性**：使用前缀明确区分不同层级的组件

### 层级关系

```
Frontend (前端应用)
    └── Backend (后端服务连接)
            └── Manager (MC服务端管理器)
                    └── MCServer (Minecraft 服务端进程)
                            └── Server (物理服务器)
```

### 前缀使用示例

| 场景 | 正确命名 | 错误命名 |
|-----|---------|---------|
| 后端地址 | `BackendAddress` | `ServerAddress`, `Address` |
| 后端端口 | `BackendPort` | `ServerPort`, `Port` |
| 后端密码 | `BackendPassword` | `Password`, `ServerPassword` |
| 后端名称 | `BackendName` | `ServerName` |
| MC服务端日志 | `MCServerLogs` | `ServerLogs`, `Logs` |
| 管理器ID | `ManagerId` | `Id`, `ServerId` |

## 文件修改记录

| 文件 | 修改内容 |
|-----|---------|
| [ConnectionViewModel.cs](file:///d:/repos/Yuzu-Frontend/Yuzu_Frontend/Models/ConnectionViewModel.cs) | ServerAddress → BackendAddress, ServerPort → BackendPort, Password → BackendPassword, IsPortValid → IsBackendPortValid |
| [ConnectionBackendEntry](file:///d:/repos/Yuzu-Frontend/Yuzu_Frontend/Models/ConnectionViewModel.cs#L920) | ServerAddress → BackendAddress, ServerPort → BackendPort, ServerName → BackendName |
| [StartPage.axaml](file:///d:/repos/Yuzu-Frontend/Yuzu_Frontend.Desktop/Views/StartPage.axaml) | 更新所有绑定路径 |
| [StartPage.axaml.cs](file:///d:/repos/Yuzu-Frontend/Yuzu_Frontend.Desktop/Views/StartPage.axaml.cs) | 更新代码中对 Connection 属性的引用 |
| [CommonModels.cs](file:///d:/repos/Yuzu-Frontend/Yuzu_Frontend/Models/CommonModels.cs) | ConnectionHistoryItemModel 属性重命名 |

## 版本历史

| 版本 | 日期 | 修改内容 |
|-----|------|---------|
| 1.0 | 2026-07-04 | 初始版本，完成命名规范调整 |