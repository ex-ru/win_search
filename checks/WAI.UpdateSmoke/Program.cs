using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WAI.App;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;

if (args.Length != 4)
    throw new ArgumentException("Usage: WAI.UpdateSmoke <releaseFolder> <installedVersion> <scratchFolder> <reportJson>");
string feed = Path.GetFullPath(args[0]);
string scratch = Path.GetFullPath(args[2]);
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable("WAI_HOME", Path.Combine(scratch, "settings"));
string cache = Path.Combine(scratch, "packages");
var settings = new Settings { CheckUpdates = true, UpdateSource = feed };
var notifications = new List<string>();
int managerCreations = 0;
Func<string, UpdateManager> factory = source =>
{
    managerCreations++;
    return new UpdateManager(source, options: null,
        locator: new TestVelopackLocator("WinSearch.WAI", args[1], cache, new NullVelopackLogger()));
};
// Exercise the application's service, including its settings and notification path.
var serviceType = typeof(Settings).Assembly.GetType("WAI.App.UpdateService", throwOnError: true)!;
using var service = (IDisposable)Activator.CreateInstance(serviceType,
    settings, (Action<string>)notifications.Add, factory)!;
var check = serviceType.GetMethod("CheckOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
await (Task)check.Invoke(service, null)!;
await (Task)check.Invoke(service, null)!;
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(feed, "releases.win.json")));
var asset = manifest.RootElement.GetProperty("Assets").EnumerateArray()
    .Where(a => a.GetProperty("PackageId").GetString() == "WinSearch.WAI" && a.GetProperty("Type").GetString() == "Full")
    .OrderByDescending(a => Version.Parse(a.GetProperty("Version").GetString()!)).First();
string package = Path.Combine(cache, asset.GetProperty("FileName").GetString()!);
bool checksumMatches = File.Exists(package) &&
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).Equals(asset.GetProperty("SHA256").GetString(), StringComparison.OrdinalIgnoreCase);
bool passed = settings.LastUpdateCheck is not null && notifications.Count == 1 && managerCreations == 1 && checksumMatches;
var report = new
{
    passed, source = feed, installedVersion = args[1], targetVersion = asset.GetProperty("Version").GetString(),
    package, checksumMatches, settings.LastUpdateCheck, managerCreations, notifications,
    applied = false, note = "Local download verified through the real app service; applying waits for the next packaged app launch."
};
File.WriteAllText(args[3], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Local update service: {(passed ? "PASS" : "FAIL")}; {args[1]} -> {asset.GetProperty("Version").GetString()}; checksum {checksumMatches}");
return passed ? 0 : 1;
