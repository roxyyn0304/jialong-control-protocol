# 🐉 蛟龙16Pro 控制协议

> **JIAOLONG Control Protocol** — 机械革命蛟龙16Pro 2025 官方控制中心逆向协议文档
>
> 覆盖 **风扇曲线 / 性能模式 / Boost / 功耗 / 键盘背光 / 遥测**,基于三重验证:
> **CLR JIT 原生代码反汇编**(NativeCode + Iced x64)+ **EC 寄存器实测** + **MQTT 抓包**

---

## 🖥️ 设备信息

| 参数 | 值 |
|------|-----|
| 机型 | MECHREVO 蛟龙16Pro 2025 |
| 主板 | JIAOLONG Series-X6xR55xK-B2 |
| CPU | Ryzen 9 7845HX |
| 驱动 | UWACPIDriver.sys(绑定 `ACPI\INOU0000`) |
| 驱动接口 | `\\.\ACPIDriver` |
| 控制中心 | 机械革命控制中心(AiStoneService) |

---

## 🏗️ 架构

```
┌──────────┐   MQTT    ┌──────────────┐    ┌──────────────┐
│ 控制台 UI │ ────────→ │  GCUBridge   │ →  │  GCUService  │
│ (可选)    │  13688    │  (MQTT broker)│    │  (执行者)     │
└──────────┘           └──────────────┘    └──────┬───────┘
                                                   │ DeviceIoControl
                                                   ▼
                                          ┌──────────────────┐
                                          │  UWACPIDriver.sys │
                                          └────────┬─────────┘
                                                   │ ACPI EVAL
                                                   ▼
                                          ┌──────────────────┐
                                          │ ACPI 固件方法      │
                                          │  ECRR(读)/ECRW(写)│
                                          └────────┬─────────┘
                                                   ▼
                                                   EC
```

## 🔐 ACPI 固件协议(核心机制)

**官方控制 = ACPI 固件方法**,不是 WMI(`ACPI\PNP0C14\1` WmiAcpi 设备存在但 GCUService 零引用)。

驱动收到用户 IOCTL 后构建 **ACPI EVAL 缓冲区**,经 IOCTL `0xC014000F` 调用固件方法:

| 偏移 | 字段 | 值 |
|------|------|-----|
| +0x00 | Signature | `'AeiC'`(0x43696541) |
| +0x04 | MethodName | `'ECRR'`(读)=0x52524345 / `'ECRW'`(写)=0x57524345 |
| +0x08 | Size | 0x28(40 字节) |
| +0x0C | ArgumentCount | ECRR=1(地址)/ ECRW=2(地址+值) |
| +0x10 | Arguments | {type=0, length=4, data} |

> ⚠️ **BSOD 0xA5 教训**: 40 字节 "AeiC" 结构本身没错,错在**直接当 IOCTL 输入发给错误码**——驱动把输入前 4 字节当 EC 地址 → 垃圾地址 → ACPI 卡死。正确用法见 `docs/使用手册.md`。

## ⚡ EC 通道(IOCTL 24 个全枚举)

| IOCTL | 用途 | 证据 |
|-------|------|------|
| `0x9C40A488` | **EC Read**(4 字节地址, 结果首字节) | ✅ 原生代码 + 实测 |
| `0x9C40A48C` | **EC Write**(8 字节 `{addr, value}`) | ✅ 原生代码 + 实测 |
| `0x9C40A480/484/490/494/498/49C/4A0/4A4/4C0/4C4/4C8/4CC/4D0/4D4/4D8/4DC/4E0/4E4/500/504` | 内存/端口/CM 等通用访问 | ✅ 驱动 dispatch 枚举 |

> ⚠️ 网传 "0x9C40A4C4=EC读 / 0x9C40A4C8=EC写" 是**错的**——真实是 `0x9C40A488`(读)/ `0x9C40A48C`(写)。

### 🛡️ 固件写保护边界(穷尽测试)

| 区域 | 直接写 | 说明 |
|------|:---:|------|
| 遥测读取(RPM/温度/占空比) | ✅ | 唯一免中间层的功能 |
| 辅助寄存器 0xD8/0xD9 | ✅ | 可写但无控制效果 |
| 模式 0x751 | ❌ | MQTT 切 TURBO 后直写 0x00 被拒 |
| **风扇表 0xF2/0xF5/0xF00~0xF5F** | ❌ | 24 IOCTL × 格式/解锁/标志全被拒 |
| Boost 触发 0x768 | ❌ | 仅 GCUService/MQTT 可置位 |
| 功耗墙 PL/TCC 0x783~0x786 | ❌ | 写回读原值 |
| 键盘背光(开关/亮度/颜色) | ❌ | 不在 EC RAM 可见区 |

**结论: 所有控制功能必须经 GCUService(MQTT),中间层不可绕过——固件信任模型为调用上下文级。EC 直控只剩遥测读取。**

## 🗺️ EC 寄存器表

### 模式与 Boost

| 地址 | 说明 | 实测 |
|------|------|:---:|
| `0x751` | 模式: 0x10=TURBO 0x40=BOOST 0x80=USER | ✅ |
| `0x768` | Boost 触发(写 0x04) | ✅ |
| `0xD8`/`0xD9` | Boost 辅助 | ✅ |

### 风扇表(自定义曲线)

| 地址 | 说明 | 实测 |
|------|------|:---:|
| `0x7C5` | 表开关(0x80=启用) | ✅ |
| `0x7C6` | 表触发(0x04) | ✅ |
| `0xF40`~`0xF4F` | CPU 温度点: **46,48,52,56,60,64,68,72,76,80°C** | ✅ |
| `0xF00`~`0xF0F` | GPU 温度点: **44,46,50,54,58,62,66,70,74,78°C** | ✅ |
| `0xF2`~`0xF2F` | **CPU 占空比**(0.5%/unit, 首点强制 0%, 单调递增) | ✅ 写此区→转速跟随 |
| `0xF5`~`0xF5F` | **GPU 占空比** | ✅ 写此区→转速跟随 |
| `0xF10`/`0xF30` | 默认曲线参考区 | ✅ |

### 📊 遥测(只读)

| 地址 | 说明 |
|------|------|
| `0x464`/`0x465` | CPU 风扇 RPM(大端: 0x464<<8\|0x465) |
| `0x46C`/`0x46D` | GPU 风扇 RPM(±4%) |
| `0x75B`/`0x75C` | 占空比显示(0.5%/unit) |
| `0x44C` | **CPU 温度**(与官方 CpuInfo 一致) |
| `0x44F` | **GPU 温度**(≈官方 GpuInfo) |
| `0x783`/`0x784`/`0x785`/`0x786` | PL1 / PL2 / PL4 / TCC |

## 🎯 当前生效曲线(定案)

**CPU**: `0,38,38,38,38,38,38,38,55,100` → 38% ≈ **2400 RPM**
**GPU**: `0,32,32,32,32,32,32,32,50,100` → 32% ≈ **2000 RPM**

| 温度点 | CPU | GPU |
|--------|:---:|:---:|
| 46/44°C | 0% | 0% |
| 48~72°C / 46~70°C | **38%** | **32%** |
| 76/74°C | 55% | 50% |
| 80/78°C | 100% | 100% |

> 策略: 固定基准转速(压住温度的最安静值)+ 高温爬升,避免频繁变速。
> 温度源: 0x44C=CPU / 0x44F≈GPU。

### 📈 RPM-占空比校准表(非线性,实测)

| 占空比 | CPU | GPU | 常用目标 |
|:---:|:---:|:---:|:---:|
| 25% | 1617 | 1567 | — |
| 30% | 1940 | 1863 | — |
| 32% | 2065 | 1978 | **2000 RPM** |
| 35% | 2243 | 2147 | 2250 |
| 37% | 2353 | — | — |
| 38% | 2417 | — | **2400** |
| 40% | 2530 | 2400 | 2500 |
| 45% | 2789 | 2662 | — |
| 50% | 3062 | 2921 | 3000 |
| 60% | 3552 | 3252 | — |
| 70% | 4030 | 3763 | 4000 |
| 100% | 5284 | 4867 | **满转** |

> **RPM 与占空比非线性**(中段偏高): 25-40% 近似线性(每 5%≈320 RPM), 40-70% 斜率陡, 70%+ 饱和。
> **测试方法**: 全曲线设同值(首点 0) + 等 30-40 秒稳定 + 采样 2 次;测前灵敏度设 100 可加速(8 秒)。

## 🧩 控制方法全集(JIT 原生代码确认)

### EC 基础
| 方法 | 动作 |
|------|------|
| `AcpiCtrl.Read(addr)` | IOCTL 0x9C40A488 读 EC |
| `AcpiCtrl.Write(addr, val)` | IOCTL 0x9C40A48C 写 EC |
| `MyEcCtrl.Read/Write` | 同上封装(跳板到 AcpiCtrl) |

### 模式控制
| 方法 | 动作 |
|------|------|
| `SetFanMode(mode)` | 读 0x751 → 计算 → 写 0x751 |
| `GetFanMode()` | 读 0x751 |
| `FanBoostUpdate()` | 读 0x751 + 状态机 |
| `UserSet_FanBoost(v)` | 存状态 → SetFanMode |
| `SetFanSwitchSpeed(Enabled)` | 切换风扇策略 |

### 风扇表
| 方法 | 动作 |
|------|------|
| `SetEcFanTable(table)` | 循环写 0xF00+i |
| `SetEcFanControlRespective(en)` | 读改写 0x7C5(0x80) |
| `SetFanTable` / `FanTable_Init` | 表设置/初始化 |
| `SetFanControlByRamFan1p5` | 按 RamFan1p5 控制 |

## 📡 MQTT 控制面

| 项 | 值 |
|----|-----|
| Broker | `localhost:13688`(GCUBridge) |
| 客户端 | `PluginClient_1`(凭证经环境变量 `JL_MQTT_USER`/`JL_MQTT_PWD`) |
| 主题 | `Fan/Control`(写) `Fan/Status`(读) `Fan/Table`(表) `Keyboard/Ctrl`(背光) |
| 动作 | `RamFanMode1p5`: OPERATING_TURBO/GAMING/OFFICE/CUSTOM_MODE, FAN_BOOST_ON, SET_OPERATING_MODE_DETAIL |

> ⚠️ 曲线 `Type` 字段**区分大小写**: 必须 `"CPU"`/`"GPU"`(小写被静默忽略)。

### ⌨️ 键盘背光(已逆向)

```
{"function":"Init"} → 初始化(点灯前提)
{"function":"SetPower","powerstatus":1/0}  → 开关
{"function":"SetLightingLevel","light":"0-4","mode":"Lighting"} → 亮度
{"function":"SetEffectALL","effect":"Breathing","mode":"Lighting"} → 26种效果
{"function":"SetEffectALL","effect":"Single","MonochromeIndex":"1-30"} → 单色
```

## 📂 项目结构

```
jialong-control-protocol/
├── README.md                        # 本文档
├── promat.md                        # 项目说明(不入库)
└── docs/
    ├── GCUService_逆向分析.md        # 完整逆向过程 (ConfuserEx → CLRMD → JIT → ACPI)
    ├── 使用手册.md                   # ⚠️ 安全操作流程 + 完整协议(先读这个)
    └── 最小中间层部署方案.md          # 脱离 UI 控制中心独立运行
```

纯**协议文档库**:本仓库记录蛟龙16Pro 官方控制中心全部逆向成果(ACPI/EC 寄存器/曲线/功耗墙/背光/校准表/部署方案),不含可执行工具。控制实现见 G-Helper 蛟龙特化版。

## 🚀 快速开始

1. 通读 `docs/使用手册.md`(含 BSOD 0xA5 事故教训和全部已验证协议格式)
2. 需要控制时,用官方控制中心(或按 `docs/最小中间层部署方案.md` 独立部署 GCUService + GCUBridge,经 MQTT 发消息控制)
3. MQTT 消息格式:控制用 `{"function":...}`(如背光),查询/设置用 `{"Action":...}`(如功耗墙)——全部格式见 `docs/使用手册.md`

> ⚠️ 动手前必读 `docs/使用手册.md` — 含 BSOD 0xA5 事故教训和已验证的安全操作格式。

## 📚 参考

- `docs/使用手册.md` — 协议全集与安全操作
- `docs/最小中间层部署方案.md` — 脱离 UI 独立部署

## 📄 License

MIT
