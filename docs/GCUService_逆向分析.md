# GCUService 控制机制逆向分析

> 基于: 内存映像提取 + CLRMD dump 分析 + JIT 原生代码反汇编 + EC 寄存器实测 + MQTT 抓包
> 设备: 机械革命蛟龙16Pro 2025 (Ryzen 9 7845HX, UWACPIDriver.sys / ACPI\INOU0000)
> 日期: 2026-08-05

---

## 1. 逆向对象

| 组件 | 路径 | 说明 |
|------|------|------|
| GCUService.exe | `AiStoneService\MyControlCenter\` | 18MB .NET Framework 4.8, **ConfuserEx 混淆** |
| GCUBridge.exe | 同目录 | MQTT broker / 路由层(不是执行者) |
| ACPIDriverDll.dll | 同目录 | 原生 C++ 用户态驱动封装 |
| UWACPIDriver.sys | 驱动目录 | 内核驱动, 绑定 ACPI\INOU0000 |

## 2. 混淆情况 (为什么常规反编译全部失效)

GCUService 使用 ConfuserEx 风格 JIT 钩子保护:

- **1061/1066** 个方法体 = `throw new Exception("Runtime exception")`(磁盘/模块内存 IL)
- 每个受保护方法标记 `[MethodImpl(MethodImplOptions.NoInlining)]`
- 真实 IL 只在 **JIT 编译瞬间**由钩子解密提供,之后不留存
- 三种读 IL 的路径全部拿到的是桩:
  1. ILSpy/磁盘反编译 → 桩
  2. 内存映像 `PEStreamOptions.IsLoadedImage` 反编译 → 桩
  3. CLRMD `ClrMethod.IL`(CLR 堆)→ 桩(如 SetFanBoost [11B])

## 3. 解锁路径: JIT 原生代码

**原理**: 混淆只保护 IL 层;JIT 编译后的**原生 x64 代码**(真实执行逻辑)保留在进程堆中。procdump 抓取的 `gcu.dmp`(648MB, Boost 验证期间)含已 JIT 方法的原生代码。

**工具链**:
1. `DataTarget.LoadDump(gcu.dmp)` → `CreateRuntime()`
2. `module.EnumerateTypeDefToMethodTableMap()` → `runtime.GetTypeByMethodTable(mt)`
3. `ClrType.Methods` → `ClrMethod.NativeCode`(ulong 地址)
4. `target.DataReader.Read(nativeCode, buffer)` → Iced `Decoder.Create(64, ...)` 反汇编

**结果**: 39 个方法有真实 JIT 原生代码(`CompilationType: Jit`),反汇编输出 475KB
→ `~/Desktop/JLProbe/native_dump.txt`

## 4. 关键证据 (原生代码反汇编摘录)

### 4.1 EC 读写 (MyECIO.AcpiCtrl)

```
### MyECIO.AcpiCtrl.Read
  mov edi, r8d              ; addr
  mov rsi, r9               ; 结果指针
  ...
  mov edx, 9C40A488h        ; ← IOCTL = 0x9C40A488 (EC 读)
  call 00007FFAB4D98CE8h    ; DeviceIoControl 桩

### MyECIO.AcpiCtrl.Write
  mov esi, r8d              ; addr
  mov edi, r9d              ; value
  ...
  mov edx, 9C40A48Ch        ; ← IOCTL = 0x9C40A48C (EC 写)
  call 00007FFAB4D98CF0h    ; DeviceIoControl 桩
```

**结论**: 官方 EC 读 = `0x9C40A488`(4 字节地址,结果首字节);写 = `0x9C40A48C`(8 字节 {addr, value})。
网上流传的 0x9C40A4C4/C8 是**错的**(0x9C40A4C4 实际是 IO Write)。

### 4.2 模式控制 (SetFanMode / GetFanMode / FanBoostUpdate)

```
### MyFanManager_RamFan1p5.SetFanMode
  mov r8d, 751h             ; ← EC 地址 0x751 (模式寄存器)
  call 00007FFAB4DA9FA0h    ; AcpiCtrl.Write(0x751, 计算出的模式值)

### FanBoostUpdate
  mov r8d, 751h
  call 00007FFAB4DA0310h    ; AcpiCtrl.Read(0x751)
  ...
  mov r9d, 4BCh             ; 0x4BC (未知, 疑似电源/PL 相关)
```

### 4.3 风扇表 (SetEcFanTable / SetEcFanControlRespective)

```
### SetEcFanTable — 循环写 CPU 风扇表
  xor ebx, ebx              ; i = 0
  cmp ebx, 0Fh              ; i < 16
  lea ecx, [rbx+0F00h]      ; ← EC 地址 = 0xF00 + i (0xF00..0xF0F)
  movzx r9d, byte [rcx+r9*8+14h]  ; 表数据
  call 00007FFAB4DA9FA0h    ; AcpiCtrl.Write(0xF00+i, data)

### SetEcFanControlRespective — 表开关
  mov edi, 80h              ; enable ? 0x80 : 0x00
  ...
  mov r8d, 7C5h
  call 00007FFAB4DA0310h    ; AcpiCtrl.Read(0x7C5)
  movzx edi, cl             ; 保留低字节
  mov r8d, 7C5h
  call 00007FFAB4DA9FA0h    ; AcpiCtrl.Write(0x7C5, 修改后值)
```

### 4.4 Boost 链

```
### UserSet_FanBoost
  mov [rcx+24h], edi        ; 保存 boost 状态
  call 00007FFAB7EFC9F0h    ; 读取状态
  call 00007FFAB7F074D0h    ; → SetFanMode (0x7F074D0 = SetFanMode NativeCode)
```

## 5. 控制方法全集 (39 个真实 JIT 方法)

| 方法 | 作用 | 关键寄存器 |
|------|------|-----------|
| `MyECIO.AcpiCtrl.Read/Write` | EC 读写 | IOCTL 0x9C40A488/A48C |
| `MyECIO.MyEcCtrl.Read/Write` | EC 读写封装 | 同上 |
| `MyFanManager_RamFan1p5.SetFanMode` | 设置模式 | 0x751 |
| `MyFanManager_RamFan1p5.GetFanMode` | 读模式 | 0x751 |
| `MyFanManager_RamFan1p5.FanBoostUpdate` | Boost 状态机 | 0x751, 0x4BC |
| `MyFanManager_RamFan1p5.UserSet_FanBoost` | 用户切 Boost | → SetFanMode |
| `MyFanManager_RamFan1p5.SetFanSwitchSpeed(Enabled)` | 风扇策略切换 | — |
| `MyFanManager_RamFan1p5.SetOperatingModeProfileIndex(Thread)` | 模式配置索引 | — |
| `FanTable_Manager1p5.SetEcFanTable` | 写 CPU 风扇表 | 0xF00+i |
| `FanTable_Manager1p5.SetEcFanControlRespective` | 表开关 | 0x7C5 (0x80) |
| `FanTable_Manager1p5.FanTable_Init` | 表初始化 | — |
| `FanTable_Manager1p5.SetFanTable` | 设置风扇表 | — |
| `MyFanTableCtrl.SetFanControlByRamFan1p5` | 按 RamFan1p5 控制 | — |
| `MyFanCtrl.FanBoostUpdate` | Boost 更新入口 | — |
| `MainOption.set_OperatingMode` | 模式属性 | — |

完整 183 个控制方法签名见 `~/Desktop/JLProbe/il_dump.txt`。

## 6. 实测验证 (与代码证据交叉)

| 操作 | 实测结果 |
|------|---------|
| MQTT FAN_BOOST_ON | RPM 3796 → 5284, 0x751 = 0x40, 0x768 = 0x04 |
| MQTT 模式切换 | 0x751: 0x00 → 0x10 (TURBO), → 0x40 (BOOST) |
| IOCTL 0x9C40A488 读 0x751 | 返回真实模式字节 |
| IOCTL 0x9C40A48C 写 0x751=0x10 | 写回读一致 |
| 风扇表 0xF00 平 100% | 表应用(0x7C5/0x7C6 变化)但**不驱动 RPM**(EC 忽略表,此机型风扇只跟模式/Boost) |

## 7. 已排除的路径

| 路径 | 结论 |
|------|------|
| ACPIDriverDll 包装 | GCUService 从不加载(日志包装零调用) |
| UEFI_Firmware.dll / NVRAM | 与风扇无关(实测零变化) |
| 直接端口 62/66 或 ITE 4E/4F | 无响应(厂商锁定) |
| 内存映像反编译 | 全 stub |
| dnSpy 附加 | 触发反调试,进程被踢 |
| CLR Profiler (ICorProfilerCallback2) | ICorProfilerInfo 交互崩溃(本机环境) |

## 8. 关键资产

- `JLProbe\native_dump.txt` (475KB) — 39 方法 x64 反汇编(本报告证据源)
- `JLProbe\il_dump.txt` (31KB) — 183 方法签名
- `JLProbe\GCUService_mem_raw.bin` (17MB) — 内存映像
- `JLProbe\ProcMon\gcu.dmp` (648MB) — CLR dump(含 JIT 原生代码)
- `%TEMP%\IlDump\` — 反汇编工具源码(CLRMD 2.2 + Iced 1.21)
- `%TEMP%\wrapper\jitprof.cpp` — CLR Profiler 源码(部分可用)
