// EcTool — 蛟龙16Pro EC 寄存器读写工具 (已验证格式, 安全规则见 docs/使用手册.md)
// 用法:
//   dotnet run --project tools/EcTool -- read 0x751
//   dotnet run --project tools/EcTool -- read 0x464 0x465        (多地址)
//   dotnet run --project tools/EcTool -- write 0x751 0x10
//   dotnet run --project tools/EcTool -- rpm                     (CPU/GPU RPM + 模式 + 占空比)
//   dotnet run --project tools/EcTool -- curve 0,10,20,30,44,55,65,75,90,100  (写CPU曲线, %值10个点)
//   dotnet run --project tools/EcTool -- apply                   (0x7C6 脉冲: 应用曲线)
using System;
using System.Linq;
using System.Runtime.InteropServices;

class EcTool
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] input, uint inputLen, byte[] output, uint outputLen, out uint returned, IntPtr overlapped);

    const uint IOCTL_EC_READ = 0x9C40A488;
    const uint IOCTL_EC_WRITE = 0x9C40A48C;

    static ushort ParseHex(string s) => Convert.ToUInt16(s.Replace("0x", "").Replace("0X", ""), 16);

    static IntPtr OpenDevice()
    {
        // GENERIC_READ|WRITE, share read/write, OPEN_EXISTING
        var h = CreateFile(@"\\.\ACPIDriver", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == (IntPtr)(-1)) throw new Exception($"打开 \\\\.\\ACPIDriver 失败 (需要管理员权限), Win32Err=0x{Marshal.GetLastWin32Error():X}");
        return h;
    }

    public static byte ReadEC(IntPtr h, ushort addr)
    {
        var input = BitConverter.GetBytes((uint)addr);   // 前4字节 = 地址 (小端)
        var output = new byte[4];
        DeviceIoControl(h, IOCTL_EC_READ, input, 4, output, 4, out _, IntPtr.Zero);
        return output[0];
    }

    public static void WriteEC(IntPtr h, ushort addr, byte value)
    {
        var input = new byte[8];
        Array.Copy(BitConverter.GetBytes((uint)addr), input, 4);
        input[4] = value;                                  // 后4字节 = 值
        DeviceIoControl(h, IOCTL_EC_WRITE, input, 8, null, 0, out _, IntPtr.Zero);
    }

    static int Main(string[] args)
    {
        try
        {
            var h = OpenDevice();
            if (args.Length == 0) { Console.WriteLine("用法: EcTool read <addr>... | write <addr> <val> | rpm | curve <pct1,pct2,...> | apply"); return 1; }

            switch (args[0].ToLower())
            {
                case "read":
                    for (int i = 1; i < args.Length; i++)
                    {
                        var a = ParseHex(args[i]);
                        Console.WriteLine($"0x{a:X4} = 0x{ReadEC(h, a):X2}");
                    }
                    break;

                case "write":
                    var wa = ParseHex(args[1]);
                    var wv = Convert.ToByte(ParseHex(args[2]));
                    WriteEC(h, wa, wv);
                    var backVal = ReadEC(h, wa);
                    Console.WriteLine($"写 0x{wa:X4} = 0x{wv:X2}, 读回 0x{backVal:X2} {(backVal == wv ? "✓" : "✗ 不一致!")}");
                    break;

                case "rpm":
                    // RPM = 高字节(0x464)<<8 | 低字节(0x465)
                    int cpu = (ReadEC(h, 0x464) << 8) | ReadEC(h, 0x465);
                    int gpu = (ReadEC(h, 0x46C) << 8) | ReadEC(h, 0x46D);
                    byte mode = ReadEC(h, 0x751);
                    byte duty = ReadEC(h, 0x75B);
                    string modeName = mode switch { 0x10 => "TURBO", 0x40 => "BOOST", 0x80 => "USER", 0 => "?", _ => $"0x{mode:X2}" };
                    Console.WriteLine($"CPU RPM: {cpu}  GPU RPM: {gpu}  模式: {modeName}  占空比: {duty / 2f}%");
                    break;

                case "curve":
                    // CPU 占空比曲线 -> 0xF2+i; 单位 0.5% (0xC8=200=100%); 首点强制 0%
                    var pcts = args[1].Split(',').Select(int.Parse).ToArray();
                    if (pcts.Length > 16) { Console.WriteLine("最多 16 个点"); return 1; }
                    pcts[0] = 0;  // 首点(44°C)强制 0%
                    for (int i = 1; i < pcts.Length; i++)
                        if (pcts[i] < pcts[i - 1]) { Console.WriteLine($"✗ 曲线必须单调递增 (点{i - 1}={pcts[i - 1]}% > 点{i}={pcts[i]}%)"); return 1; }
                    for (int i = 0; i < pcts.Length; i++)
                    {
                        WriteEC(h, (ushort)(0xF2 + i), (byte)(pcts[i] * 2));  // % -> 0.5% 单位
                        var back = ReadEC(h, (ushort)(0xF2 + i));
                        Console.WriteLine($"0xF{(0xF2 + i):X3} = {pcts[i]}% (0x{back:X2}) {(back == pcts[i] * 2 ? "✓" : "✗")}");
                    }
                    // 应用脉冲: 0x7C6 清 bit2 -> 置 bit2
                    WriteEC(h, 0x7C6, 0x00);
                    WriteEC(h, 0x7C6, 0x04);
                    Console.WriteLine("已应用 (0x7C6 脉冲)");
                    break;

                case "apply":
                    WriteEC(h, 0x7C6, 0x00);
                    WriteEC(h, 0x7C6, 0x04);
                    Console.WriteLine("已应用 (0x7C6 脉冲)");
                    break;

                default:
                    Console.WriteLine("未知命令");
                    return 1;
            }
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"错误: {e.Message}");
            return 1;
        }
    }
}
