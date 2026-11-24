using System.Management;
using System.Text;
using Microsoft.Win32;

namespace Gui.ChildSession;

// Read-only checks that explain a failure; they never change Windows settings or decide whether to connect.
internal static class ChildSessionEnvironment
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string PasswordLessKey = CurrentVersionKey + @"\PasswordLess\Device";
    private const string TermServiceKey = @"SYSTEM\CurrentControlSet\Services\TermService";

    // The Child Session signs in with the account password, which this Windows setting can refuse.
    internal static bool RequiresWindowsHelloSignIn() =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + PasswordLessKey, "DevicePasswordLessBuildVersion", 0) is 2;

    internal static string ReadSnapshot()
    {
        var text = new StringBuilder();
        void Add(string name, Func<object> read)
        {
            string value;
            try {
                value = read().ToString()!.ReplaceLineEndings(@"\n");
            } catch (Exception exception) {
                value = $"读取失败：{exception.GetType().Name}";
            }
            text.Append(name).Append('=').AppendLine(value);
        }

        foreach (var name in new[] { "EditionID", "CurrentBuild", "DisplayVersion" }) {
            Add(name, () => ReadValue(CurrentVersionKey, name));
        }
        Add("TermService.Start", () => ReadValue(TermServiceKey, "Start"));
        Add("TermService", () => {
            using var service = new ManagementObject("Win32_Service.Name='TermService'");
            return (string)service["State"];
        });
        var serviceDll = new Lazy<string>(() => (string)ReadValue(TermServiceKey + @"\Parameters", "ServiceDll"));
        Add("ServiceDll", () => serviceDll.Value);
        Add("ServiceDll.IsSystemTermSrv", () => string.Equals(Environment.ExpandEnvironmentVariables(serviceDll.Value),
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\termsrv.dll"), StringComparison.OrdinalIgnoreCase));
        Add("ChildSessionsEnabled", () => ChildSessionService.IsChildSessionsEnabled());
        Add("RdpPort", () => ReadValue(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp",
            "PortNumber"));
        Add("fDenyTSConnections", () => ReadValue(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections"));
        Add("DevicePasswordLessBuildVersion", () => ReadValue(PasswordLessKey, "DevicePasswordLessBuildVersion"));
        return text.ToString();
    }

    private static object ReadValue(string path, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            ?? throw new IOException($"注册表值 {name} 缺失。");
    }
}
