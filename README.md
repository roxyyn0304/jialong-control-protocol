# 蛟龙16Pro 控制协议 (JIAOLONG Control Protocol)

机械革命 蛟龙16Pro 2025 官方控制中心(GCUService)控制机制的逆向协议文档,
覆盖风扇 / 性能模式 / Boost / 功耗 / 遥测。基于 **CLR JIT 原生代码反汇编**(`NativeCode` + Iced x64) + EC 寄存器实测 + MQTT 抓包三重验证。

## 设备信息

| 参数 | 值 |
|------|-----|
| 机型 | MECHREVO 蛟龙16Pro 2025 |
| 主板 | JIAOLONG Series-X6xR55xK-B2 |
| CPU | Ryzen 9 7845HX |
| EC | ITE(控制寄存器映射见下) |
| 驱动 | UWACPIDriver.sys (绑定 `ACPI\INOU0000`) |
| 用户态驱动接口 | `\\.\ACPIDriver` (ACPIDriverDll / 直接 P/Invoke) |
| 控制中心 | 机械革命控制中心 (AiStoneService) |

## 架构

```
CCU.WinUI → MQTT(localhost:13688) → GCUBridge.exe (broker/路由)
    → GCUService.exe (执行者, ConfuserEx 混淆) → DeviceIoControl → UWACPIDriver.sys → EC
```

## EC 通道 (IOCTL)

| IOCTL | 名称 | 用途 | 证据 |
|-------|------|------|------|
| `0x9C40A488` | EC Read | 读 EC 寄存器(4 字节地址输入, 结果首字节) | ✅ 原生代码 + 实测 |
| `0x9C40A48C` | EC Write | 写 EC 寄存器(8 字节 `{addr, value}`) | ✅ 原生代码 + 实测 |
| `0x9C40A484` | CM Write | 未知 | ⚠️ 反编译见 |
| `0x9C40A498` | MM Write B | 内存字节写 | ⚠️ 反编译见 |
| `0x9C40A4C4` | IO Write | IO 端口写 | ⚠️ 反编译见 |

> ⚠️ 网上流传的 "0x9C40A4C4=EC读 / 0x9C40A4C8=EC写" 是**错的**——GCUService 真实 JIT 原生代码明确是 `0x9C40A488`(读)/ `0x9C40A48C`(写)。照错误表操作有 BSOD(0xA5)风险。

## EC 寄存器表

### 模式与 Boost (核心控制)

| 地址 | 名称 | 说明 | 实测 |
|------|------|------|------|
| `0x751` | 模式字节 | 0x10=TURBO, 0x40=BOOST, 0x80=USER(自定义), 0x81-0x85=LEVEL | ✅ SetFanMode 读写此地址 |
| `0x768` | Boost 触发 | 写 0x04 触发 Boost 生效 | ✅ 实测 |
| `0x00D8`/`0x00D9` | Boost 辅助 | 写 0x04(与 0x768 组合) | ✅ 实测 |

### 风扇表 (自定义曲线)

| 地址 | 名称 | 说明 | 实测 |
|------|------|------|------|
| `0x7C5` | 表开关 | 0x80=启用自定义风扇表 | ✅ SetEcFanControlRespective 读写 |
| `0x7C6` | 表触发 | 写 0x04 应用表 | ✅ 实测 |
| `0xF00`~`0xF0F` | CPU 风扇表 | 16 点 (温度→占空比) | ✅ SetEcFanTable 循环写 |
| `0xF20`/`0xF50` | GPU 风扇表 | 16 点 | ✅ 实测(MQTT) |
| `0xF10`~`0xF1F` | CPU 第二组 | 未知 | ⚠️ |

### 遥测 (只读)

| 地址 | 说明 |
|------|------|
| `0x464`/`0x465` | CPU 风扇 RPM (LE16) |
| `0x46C`/`0x46D` | GPU 风扇 RPM |
| `0x75B`/`0x75C` | 占空比显示 (0.5% 单位) |
| `0x449`/`0x44C` | 温度 |
| `0x783`/`0x784`/`0x785` | PL1 / PL2 / PL4 |
| `0x786` | TCC |

## 控制方法全集 (JIT 原生代码确认)

### EC 基础

| 方法 | 动作 |
|------|------|
| `MyECIO.AcpiCtrl.Read(addr)` | IOCTL 0x9C40A488 读 EC |
| `MyECIO.AcpiCtrl.Write(addr, val)` | IOCTL 0x9C40A48C 写 EC |
| `MyECIO.MyEcCtrl.Read/Write` | 同上封装 |
| `MyECIO.AcpiCtrl.CheckAcpiDriverDeviceExists` | 检查驱动设备 |

### 模式控制

| 方法 | 动作 |
|------|------|
| `MyFanManager_RamFan1p5.SetFanMode(mode)` | 读 0x751 → 计算 → 写 0x751 |
| `MyFanManager_RamFan1p5.GetFanMode()` | 读 0x751 |
| `MyFanManager_RamFan1p5.FanBoostUpdate()` | 读 0x751 + 状态机 + 0x4BC |
| `MyFanManager_RamFan1p5.UserSet_FanBoost(v)` | 存状态字段 → SetFanMode |
| `MyFanManager_RamFan1p5.SetFanSwitchSpeed(en)` | 切换风扇策略 |
| `MyFanManager_RamFan1p5.SetFanSwitchSpeedEnabled` | 同上 |
| `MyFanManager_RamFan1p5.SetOperatingModeProfileIndex` | 设置模式配置索引 |

### 风扇表

| 方法 | 动作 |
|------|------|
| `FanTable_Manager1p5.SetEcFanTable(table)` | 循环写 0xF00+i (i=0..15) |
| `FanTable_Manager1p5.SetEcFanControlRespective(en)` | 读 0x7C5 → 改低字节(0x80) → 写 0x7C5 |
| `FanTable_Manager1p5.FanTable_Init` | 表初始化 |
| `FanTable_Manager1p5.SetFanTable` | 设置风扇表 |
| `MyFanTableCtrl.SetFanControlByRamFan1p5` | 按 RamFan1p5 控制 |
| `MyFanTableCtrl.SetEcFanControlRespective` | 同上 |

### 其它

| 方法 | 动作 |
|------|------|
| `MainOption.set_OperatingMode / get_OperatingMode` | 模式属性 |
| `MyFanCtrl.FanBoostUpdate` | Boost 更新入口 |
| `CustomizeCtrl.GetFanBoostBtnSupport` | Boost 按钮支持检测 |
| `LiquidHWOC.RamFan1P5SetMode` | 水冷/超频模式 |

## MQTT 控制面 (备选通道)

- Broker: `localhost:13688` (GCUBridge)
- 客户端: `PluginClient_1`(用户名/密码经环境变量 `JL_MQTT_USER` / `JL_MQTT_PWD`,见 `docs/使用手册.md`;勿将本机凭证写入仓库)
- 动作枚举: `RamFanMode1p5` (OPERATING_TURBO_MODE / OPERATING_GAMING_MODE / OPERATING_OFFICE_MODE / FAN_BOOST_ON / ...)
- 主题: `Fan/Control`(写), `Fan/Status`(读), `Fan/Table`(表)

> 注意: `SET_FAN_SPEED_CURVE_SETTING` 的 `Type` 字段**区分大小写**,必须用 `"CPU"`/`"GPU"`(小写被忽略)。

## 项目结构

```
jialong-control-protocol/
├── README.md                        # 本文档
├── promat.md                        # 项目说明
├── docs/
│   ├── GCUService_逆向分析.md        # 完整逆向过程 (ConfuserEx → CLRMD → JIT 原生代码)
│   └── 使用手册.md                   # ⚠️ 安全操作流程 (先读这个再动手)
└── tools/
    ├── EcTool/                      # EC 寄存器读写/遥测 (IOCTL 0x9C40A488/A48C)
    └── MqttControl/                 # 官方 MQTT 控制 (Boost/模式/曲线)
```

## 快速开始

```bash
# 遥测: CPU/GPU RPM + 模式 + 占空比
dotnet run --project tools/EcTool -- rpm

# Boost 开 (官方通道, 无风险)
dotnet run --project tools/MqttControl -- boost on

# 切狂暴模式
dotnet run --project tools/MqttControl -- mode turbo

# 自定义曲线 (自动先切 custom, 1.5s 后发表)
dotnet run --project tools/MqttControl -- curve CPU 30,30,35,40,45,50,55,60,65,70,75,80,85,90,95,100
```

⚠️ 动手前必读 `docs/使用手册.md` — 含 BSOD 0xA5 事故教训和已验证的安全操作格式。

## 参考资料

- `~/Desktop/JLProbe/native_dump.txt` — 39 个 JIT 方法 x64 反汇编 (475KB)
- `~/Desktop/JLProbe/il_dump.txt` — 183 个控制方法签名
- `~/Desktop/JLProbe/decompiled_fan.txt` — IsLoadedImage 反编译 (stub 为主)
- `%TEMP%\wrapper\jitprof.cpp` — CLR Profiler (JIT 捕获, 部分可用)
- `%TEMP%\IlDump\` — CLRMD + Iced 反汇编工具

## License

MIT
