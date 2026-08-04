// MqttControl — 蛟龙16Pro 官方 MQTT 控制工具 (走官方通道, 无风险)
// 用法:
//   dotnet run --project tools/MqttControl -- boost on|off
//   dotnet run --project tools/MqttControl -- mode turbo|gaming|office|custom
//   dotnet run --project tools/MqttControl -- curve CPU 30,30,35,40,45,50,55,60,65,70,75,80,85,90,95,100
//   dotnet run --project tools/MqttControl -- status
// 注意: Type 必须大写 CPU/GPU; 曲线前先切 custom 模式等 1.5s (见 docs/使用手册.md)
// 凭证从环境变量读取: $env:JL_MQTT_USER / $env:JL_MQTT_PWD (勿写入代码/仓库)
using System.Text;
using MQTTnet;
using MQTTnet.Client;

class MqttControl
{
    const string Broker = "localhost";
    const int Port = 13688;
    const string ClientId = "PluginClient_1";
    const string Topic = "Fan/Control";

    static async Task Main(string[] args)
    {
        var user = Environment.GetEnvironmentVariable("JL_MQTT_USER");
        var pwd = Environment.GetEnvironmentVariable("JL_MQTT_PWD");
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pwd))
        {
            Console.WriteLine("请先设置环境变量 JL_MQTT_USER / JL_MQTT_PWD (本机控制中心 broker 凭证, 勿写入仓库)");
            return;
        }
        var client = new MqttFactory().CreateMqttClient();
        var res = await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(Broker, Port)
            .WithClientId(ClientId)
            .WithCredentials(user, pwd)
            .Build());
        Console.WriteLine($"连接: {res.ResultCode}");
        if (res.ResultCode != MqttClientConnectResultCode.Success) return;

        async Task Send(string json)
        {
            await client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(Topic)
                .WithPayload(Encoding.UTF8.GetBytes(json))
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());
            Console.WriteLine($"> {json}");
        }

        if (args.Length == 0) { Console.WriteLine("用法见文件头注释"); return; }

        switch (args[0].ToLower())
        {
            case "boost":
                await Send($"{{\"Action\":\"FAN_BOOST_{args[1].ToUpperInvariant()}\"}}");  // on | off
                break;

            case "mode":
                var m = args[1].ToUpperInvariant();
                await Send($"{{\"Action\":\"OPERATING_{m}_MODE\"}}");                      // TURBO|GAMING|OFFICE|CUSTOM
                break;

            case "curve":
                var type = args[1].ToUpperInvariant();                                     // CPU | GPU
                var vals = args[2].Split(',');
                vals[0] = "0";  // 首点(最低温度)必须 0%, 否则 EC 拒绝整表 (实测规则)
                var sb = new StringBuilder();
                sb.Append($"{{\"Action\":\"SET_FAN_SPEED_CURVE_SETTING\",\"Name\":\"M4T1\",\"Type\":\"{type}\"");
                for (int i = 0; i < 16 && i < vals.Length; i++)
                    sb.Append($",\"T{i}\":\"{vals[i].Trim()}\"");
                sb.Append("}");
                // 先切自定义模式再发曲线 (官方顺序)
                await Send("{\"Action\":\"OPERATING_CUSTOM_MODE\"}");
                await Task.Delay(1500);
                await Send(sb.ToString());
                break;

            case "status":
                await Send("{\"Action\":\"GET_FAN_STATUS\"}");
                break;

            case "kb":
                // 键盘背光: kb on | off | bright <0-5> | effect <名字> | status
                var ka = args[1].ToLowerInvariant();
                switch (ka)
                {
                    case "on": await Send("{\"function\":\"SetPower\",\"powerstatus\":1}"); break;
                    case "off": await Send("{\"function\":\"SetPower\",\"powerstatus\":0}"); break;
                    case "bright": await Send($"{{\"function\":\"SetLightingLevel\",\"light\":\"{args[2]}\",\"mode\":\"Lighting\"}}"); break;
                    case "effect": await Send($"{{\"function\":\"SetEffectALL\",\"effect\":\"{args[2]}\",\"mode\":\"Lighting\",\"speed\":\"2\"}}"); break;
                    case "status": await Send("{\"Action\":\"GETSTATUS\"}"); break;
                    default: Console.WriteLine("kb: on|off|bright <0-5>|effect <name>|status"); break;
                }
                break;

            default:
                Console.WriteLine("未知命令");
                break;
        }

        await Task.Delay(1000);
        await client.DisconnectAsync();
    }
}
