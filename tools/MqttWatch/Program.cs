// MqttWatch 鈥?鐩戝惉鎺у埗鍙?MQTT 娑堟伅 (鐢ㄤ簬瀛︿範鎺у埗鍙扮殑瀹屾暣鎿嶄綔搴忓垪)
// 鐢ㄦ硶: dotnet run --project tools/MqttWatch   (闇€瑕?JL_MQTT_USER / JL_MQTT_PWD 鐜鍙橀噺)
using System.Text;
using MQTTnet;
using MQTTnet.Client;

class MqttWatch
{
    static async Task Main()
    {
        var user = Environment.GetEnvironmentVariable("JL_MQTT_USER");
        var pwd = Environment.GetEnvironmentVariable("JL_MQTT_PWD");
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pwd)) { Console.WriteLine("璇疯缃?JL_MQTT_USER / JL_MQTT_PWD"); return; }

        var watcher = new MqttFactory().CreateMqttClient();
        watcher.ApplicationMessageReceivedAsync += e =>
        {
            var t = e.ApplicationMessage.Topic;
            var txt = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray());
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {t}: {txt}");
            return Task.CompletedTask;
        };
        var res = await watcher.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", 13688)
            .WithClientId("PluginClient_1")
            .WithCredentials(user, pwd)
            .Build());
        Console.WriteLine($"connect: {res.ResultCode}");
        await watcher.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("#").Build());
        Console.WriteLine("鐩戝惉涓?.. (鍘绘帶鍒跺彴鎿嶄綔, 杩欓噷浼氭墦鍗版墍鏈夋秷鎭? Ctrl+C 缁撴潫)");
        await Task.Delay(Timeout.Infinite);
    }
}

