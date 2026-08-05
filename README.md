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
    → GCUService.exe (执行者, ConfuserEx 混淆) → DeviceIoControl → UWACPIDriver.sys
    → ACPI 固件方法 ECRR/ECRW → EC
```

## ACPI 固件协议 (核心机制) — ✅ 2026-08-05 反汇编破解

**官方控制 = ACPI 固件方法**,不是 WMI(GCUService 零 WMI 引用,`ACPI\PNP0C14\1` WmiAcpi 设备存在但闲置)。

驱动(UWACPIDriver.sys)收到用户 IOCTL 后,构建 **ACPI EVAL 缓冲区**并通过 IOCTL `0xC014000F` 调用固件方法:

| 偏移 | 字段 | 值 |
|------|------|-----|
| +0x00 | Signature | `'AeiC'` (0x43696541) |
| +0x04 | MethodName | `'ECRR'`(读)=0x52524345 / `'ECRW'`(写)=0x57524345 |
| +0x08 | Size | 0x28 (40 字节) |
| +0x0C | ArgumentCount | ECRR=1(地址) / ECRW=2(地址+值) |
| +0x10 | Arguments | {type=0, length=4, data} |

> ⚠️ 这就是之前 BSOD 0xA5 的"40 字节 AeiC 结构"——结构本身没错,错在**直接把结构当输入发给错误的 IOCTL**(驱动解析输入前 4 字节当 EC 地址 → 垃圾地址 → ACPI 卡死)。正确用法见 `docs/使用手册.md`。

## EC 通道 (IOCTL 完整表, 24 个已枚举)

| IOCTL | 名称 | 用途 | 证据 |
|-------|------|------|------|
| `0x9C40A488` | EC Read | 读 EC(4 字节地址输入, 结果首字节) | ✅ 原生代码 + 实测 |
| `0x9C40A48C` | EC Write | 写 EC(8 字节 `{addr, value}`) | ✅ 原生代码 + 实测 |
| `0x9C40A480/484/490/494/498/49C/4A0/4A4/4C0/4C4/4C8/4CC/4D0/4D4/4D8/4DC/4E0/4E4/500/504` | 内存/端口/CM 等 | 通用访问,非表专用 | ✅ 驱动 dispatch 枚举 |

> ⚠️ 网上流传的 "0x9C40A4C4=EC读 / 0x9C40A4C8=EC写" 是**错的**——真实是 `0x9C40A488`(读)/ `0x9C40A48C`(写)。

### ⚠️ 固件写保护边界 (穷尽测试结论)

| 区域 | 直接写 | 说明 |
|------|--------|------|
| **遥测读取**(RPM/温度/占空比) | ✅ 可读 | 全部只读遥测可用 |
| 辅助 0xD8/0xD9 | ✅ 可写 | 但无控制效果 |
| 模式 0x751 | ❌ 写保护 | 实测:MQTT 切 TURBO 后直接写 0x00 被拒(读回 0x10) |
| **风扇表 0xF2/0xF5/0xF00~0xF5F** | ❌ **固件硬保护** | 全部 24 IOCTL × 各种格式/解锁/标志地址均被拒 |
| Boost 触发 0x768 | ❌ 写保护 | 仅 GCUService/MQTT 可置位 |
| 功耗墙 PL/TCC (0x783~0x786) | ❌ 写保护 | 实测:写 0x783/0x786 读回原值 |
| 键盘背光(开关/亮度/颜色) | ❌ 不可控 | 颜色/开关不在 EC RAM 可见区 |

**结论:所有控制功能(模式/Boost/风扇表/功耗墙/背光)全部固件保护,必须经 GCUService(MQTT)。EC 直控只剩遥测读取。**

**F 表区写保护在 ACPI 固件 ECRW 方法内部,信任模型为调用上下文级,无法从外部穿透。**

深挖结论(2026-08-05, CLRMD 实时 attach + 模仿穷尽):
- `MyEcCtrl.Write → 0xD98CC0 → jmp AcpiCtrl.Write`——**同一实现,同一 IOCTL 0x9C40A48C**,无隐藏机制
- `SetEcFanTable` 真身(0x7F0F030)循环写 `0xF00+i`(温度点区);0xF2(占空比)无直接写入函数——**EC 内部镜像**
- 模仿穷尽测试全部被拒:24 IOCTL × 3 格式 / 解锁 0xD8/D9 / 标志地址 / 0x7C5+0x7C6 完整序列 / 8 种句柄参数 / **SYSTEM 计划任务身份**

**结论:风扇表只能经 GCUService(MQTT),中间层不可绕过**——这是固件设计,不是逆向缺口。

## EC 寄存器表

### 模式与 Boost (核心控制)

| 地址 | 名称 | 说明 | 实测 |
|------|------|------|------|
| `0x751` | 模式字节 | 0x10=TURBO, 0x40=BOOST, 0x80=USER(自定义), 0x81-0x85=LEVEL | ✅ SetFanMode 读写此地址 |
| `0x768` | Boost 触发 | 写 0x04 触发 Boost 生效 | ✅ 实测 |
| `0x00D8`/`0x00D9` | Boost 辅助 | 写 0x04(与 0x768 组合) | ✅ 实测 |

### 风扇表 (自定义曲线) — ✅ 2026-08-05 实测双向验证

| 地址 | 名称 | 说明 | 实测 |
|------|------|------|------|
| `0x7C5` | 表开关 | 0x80=启用自定义风扇表 | ✅ SetEcFanControlRespective 读写 |
| `0x7C6` | 表触发 | 写 0x04 应用表 | ✅ 实测 |
| `0xF10`~`0xF1F` | GPU 默认曲线参考 | 46,50,54,...82% | ✅ 实测 |
| `0xF30`~`0xF3F` | CPU 默认曲线参考 | 46,50,...81% | ✅ 实测 |
| `0xF40`~`0xF4F` | CPU 温度点 | **46,48,52,56,60,64,68,72,76,80°C**(用户提供,以此为准) | ✅ |
| `0xF00`~`0xF0F` | GPU 温度点 | **44,46,50,54,58,62,66,70,74,78°C**(用户提供) | ✅ |
| `0xF2`~`0xF2F` | **CPU 占空比(当前生效)** | 单位 0.5%(0xC8=100%);首点强制 0%,须单调递增 | ✅ **写此区→CPU 转速实时跟随** |
| `0xF5`~`0xF5F` | **GPU 占空比(当前生效)** | 单位 0.5% | ✅ **写此区→GPU 转速跟随** |

> ✅ **曲线控制已验证**: CPU 占空比 0xF2、GPU 占空比 0xF5,与控制台 UI 设置 100% 吻合(用户实测对照);
> 全 100% → RPM 5272;调低 → 4922。首点(44°C)强制 0% 不可调。

### 遥测 (只读)

| 地址 | 说明 |
|------|------|
| `0x464`/`0x465` | CPU 风扇 RPM | 大端: 0x464<<8 \| 0x465 |
| `0x46C`/`0x46D` | GPU 风扇 RPM | 大端 |
| `0x75B`/`0x75C` | 占空比显示 | 0.5% 单位(0x91 ≈ 72.5%) |
| `0x449`/`0x44C` | 温度 | °C(比控制台显示低约 10°C,偏移未明) |
| `0x783`/`0x784`/`0x785` | PL1 / PL2 / PL4 | 功耗墙 |
| `0x786` | TCC | 温度墙 |

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
│   ├── GCUService_逆向分析.md        # 完整逆向过程 (ConfuserEx → CLRMD → JIT 原生代码 → ACPI 协议)
│   └── 使用手册.md                   # ⚠️ 安全操作流程 (先读这个再动手)
└── tools/
    ├── EcTool/                      # EC 寄存器读写/遥测/写曲线 (IOCTL 0x9C40A488/A48C)
    ├── MqttControl/                 # 官方 MQTT 控制 (Boost/模式/曲线, 自动首点0%)
    └── MqttWatch/                   # MQTT 监听 (学习控制台操作序列)
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

## License

MIT
