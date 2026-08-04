// EcTool — 蛟龙16Pro EC 寄存器读写工具 (已验证格式, 安全规则见 docs/使用手册.md)
// 用法:
//   dotnet run --project tools/EcTool -- read 0x751
//   dotnet run --project tools/EcTool -- read 0x464 0x465        (多地址)
//   dotnet run --project tools/EcTool -- write 0x751 0x10
//   dotnet run --project tools/EcTool -- rpm                     (CPU/GPU RPM + 模式 + 占空比)
using System;
using System.Runtime.InteropServices;

class EcTool
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] input, uint inputLen, byte[] output, uint outputLen, out uint returned, IntPtr overlapped);

    const uint IOCTL_EC_READ = 0x9C40A488;
    const uint IOCTL_EC_WRITE = 0x9C40A48C;

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
            if (args.Length == 0) { Console.WriteLine("用法: EcTool read <addr>... | write <addr> <val> | rpm"); return 1; }

            switch (args[0].ToLower())
            {
                case "read":
                    for (int i = 1; i < args.Length; i++)
                    {
                        var a = Convert.ToUInt16(args[i].TrimStart('0', 'x'), 16);
                        Console.WriteLine($"0x{a:X4} = 0x{ReadEC(h, a):X2}");
                    }
                    break;

                case "write":
                    var wa = Convert.ToUInt16(args[1].TrimStart('0', 'x'), 16);
                    var wv = Convert.ToByte(args[2].TrimStart('0', 'x'), 16);
                    WriteEC(h, wa, wv);
                    var back = ReadEC(h, wa);
                    Console.WriteLine($"写 0x{wa:X4} = 0x{wv:X2}, 读回 0x{back:X2} {(back == wv ? "✓" : "✗ 不一致!")}");
                    break;

                case "rpm":
                    ushort cpuLo = 0x464, cpuHi = 0x465, gpuLo = 0x46C, gpuHi = 0x46D;
                    int cpu = ReadEC(h, cpuLo) | (ReadEC(h, cpuHi) << 8);
                    int gpu = ReadEC(h, gpuLo) | (ReadEC(h, gpuHi) << 8);
                    byte mode = ReadEC(h, 0x751);
                    byte duty = ReadEC(h, 0x75B);
                    string modeName = mode switch { 0x10 => "TURBO", 0x40 => "BOOST", 0x80 => "USER", 0 => "?", _ => $"0x{mode:X2}" };
                    Console.WriteLine($"CPU RPM: {cpu}  GPU RPM: {gpu}  模式: {modeName}  占空比: {duty / 2f}%");
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
