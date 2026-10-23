using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.MemoryMappedFiles;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PixelCam;

static class Program
{
    public static readonly string Root = AppContext.BaseDirectory;
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
    public static string Tool(string name)
    {
        // Installer downloads keep upstream packages intact, including licenses.
        string? packaged=name switch {"ffmpeg.exe"=>"ffmpeg/bin/ffmpeg.exe","ffprobe.exe"=>"ffmpeg/bin/ffprobe.exe","adb.exe"=>"platform-tools/adb.exe",_=>null};
        string runtime=Path.Combine(Root,"runtime");
        return packaged!=null && File.Exists(Path.Combine(runtime,packaged))?Path.Combine(runtime,packaged):Path.Combine(runtime,name);
    }
    // CPU processing and NvCVImage use limited-range BT.601. Normalize source
    // matrix/range before rawvideo discards metadata (Pixel H.264 is BT.709).
    public const string DecodeColorOptions="in_color_matrix=auto:out_color_matrix=bt601:in_range=auto:out_range=tv";
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if(args.Length>1 && args[0]=="--hardware-report"){File.WriteAllText(args[1],JsonSerializer.Serialize(MaxineHardware.Current,Json));return;}
            if(args.Length>1 && args[0]=="--hardware-summary"){File.WriteAllText(args[1],MaxineHardware.Current.Message);return;}
            if (args.Length > 0 && args[0] == "--register-camera") { RegisterCamera(); return; }
            if (args.Length > 0 && args[0] == "--self-test") { SelfTest(args).GetAwaiter().GetResult(); return; }
            if (args.Length > 2 && args[0] == "--recorded-test") { RecordedVideo.Test(args).GetAwaiter().GetResult(); return; }
            if (args.Length > 2 && args[0] == "--eye-test") { EyeContactTests.Run(args).GetAwaiter().GetResult(); return; }
            if (args.Length > 1 && args[0] == "--processing-test") { ProcessingTests.Run(args[1]); return; }
            if (args.Length > 1 && args[0] == "--control-settings-test") { ControlSettingsTests.Run(args[1]); return; }
            if (args.Length > 1 && args[0] == "--orientation-test") { OrientationTests.Run(args[1]); return; }
            if (args.Length > 2 && args[0] == "--orientation-video-test") { OrientationTests.RunRecorded(args[1],args[2]); return; }
            if (args.Length > 1 && args[0] == "--preset-test") { BackgroundPresets.Test(args[1]); return; }
            if (args.Length > 0 && args[0] == "--unregister-camera") { UnregisterCamera(); return; }
            Application.Run(new MainForm(args));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Root, "PixelCam-error.txt"), ex.ToString());
            if (!args.Any(a => a.StartsWith("--"))) MessageBox.Show(ex.Message, "PixelCam", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }
    public static async Task<string> Run(string executable, IEnumerable<string> args, int timeout = 30000)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + Path.GetFileName(executable));
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { try { process.Kill(true); } catch { } throw new IOException("The device command timed out. Reconnect the USB cable."); }
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new IOException((error + " " + output).Trim());
        return output.Trim();
    }
    public static Task<string> Adb(string serial, params string[] args) => Run(Tool("adb.exe"), new[] { "-s", serial }.Concat(args));
    public static async Task<T> Get<T>(string url) => await Http.GetFromJsonAsync<T>(url, Json) ?? throw new IOException("The phone returned an empty response.");
    public static async Task<HttpResponseMessage> PostConfig(string baseUrl, Config config, CancellationToken cancellation = default)
    {
        // A fixed content length also supports the phone's deliberately small HTTP server.
        using var content = new StringContent(JsonSerializer.Serialize(config, Json), System.Text.Encoding.UTF8, "application/json");
        return await Http.PostAsync(baseUrl + "/api/config", content, cancellation);
    }
    public static async Task Configure(string baseUrl, Config config)
    {
        using var response = await PostConfig(baseUrl, config);
        if (!response.IsSuccessStatusCode) throw new IOException(await response.Content.ReadAsStringAsync());
        for (int attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(150);
            var status = await Get<CameraStatus>(baseUrl + "/api/status");
            if (!status.Permission) throw new IOException("Allow camera access in PixelCam on your phone.");
            if (status.State == "paused") throw new IOException("Open PixelCam on your phone and keep it visible.");
            if (status.State == "error") throw new IOException(status.Error);
            if ((!config.Enabled && status.State == "stopped") || (status.State == "streaming" && status.Lens == config.Lens && status.Width == config.Width && status.Height == config.Height && status.Fps == config.Fps)) return;
        }
        throw new IOException("The camera did not finish switching. Try reconnecting.");
    }
    public static bool CameraRegistered()
    {
        using var category = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance");
        return category?.GetSubKeyNames().Any(key => { using var entry = category.OpenSubKey(key); return (string?)entry?.GetValue("FriendlyName") == "PixelCam"; }) == true;
    }
    public static void RegisterCamera()
    {
        string moduleDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PixelCam", "virtual-camera");
        Directory.CreateDirectory(moduleDirectory);
        foreach (string architecture in new[] { "64", "32" })
        {
            string name = "obs-virtualcam-module" + architecture + ".dll";
            string destination = Path.Combine(moduleDirectory, name);
            if (!File.Exists(destination) || !File.ReadAllBytes(destination).SequenceEqual(File.ReadAllBytes(Tool("virtual-camera/" + name)))) File.Copy(Tool("virtual-camera/" + name), destination, true);
            string report = Path.Combine(moduleDirectory, "registration" + architecture + ".txt");
            Run(Tool("RegisterCamera" + architecture + ".exe"), new[] { destination, report }).GetAwaiter().GetResult();
            if (!File.Exists(report) || !File.ReadAllText(report).StartsWith("OK")) throw new IOException("Webcam registration failed: " + (File.Exists(report) ? File.ReadAllText(report) : "no report"));
        }
        if (!CameraRegistered()) throw new IOException("Windows did not list the PixelCam webcam after registration.");
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-virtualcam.txt");
        if(!File.Exists(fallback)) File.WriteAllText(fallback, "1280x720x333333");
    }
    static void UnregisterCamera()
    {
        string moduleDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PixelCam", "virtual-camera");
        foreach (string architecture in new[] { "64", "32" }) Run(Tool("RegisterCamera" + architecture + ".exe"), new[] { Path.Combine(moduleDirectory, "obs-virtualcam-module" + architecture + ".dll"), Path.Combine(moduleDirectory, "unregister" + architecture + ".txt"), "unregister" }).GetAwaiter().GetResult();
    }
    static async Task SelfTest(string[] args)
    {
        string url = args[1], report = args[2], image = args[3];
        var result = new Dictionary<string, object>();
        try
        {
            var status = await Get<CameraStatus>(url + "/api/status");
            var cameras = await Get<CameraList>(url + "/api/cameras");
            result["lenses"] = cameras.Lenses; result["camera"] = status;
            using var decoder = new Decoder(url, status.Width, status.Height, 0, false);
            if(args.Length>4) decoder.Settings=JsonSerializer.Deserialize<ProcessingSettings>(File.ReadAllText(args[4]),Json)??new();
            long frames = 0; bool saved = false;
            long firstFrame=0,lastFrame=0;
            decoder.Preview += bitmap => { if (!saved) { if(image != "--no-image") bitmap.Save(image, ImageFormat.Png); saved = bitmap.Width > 0 && bitmap.Height > 0; } bitmap.Dispose(); };
            decoder.FullFrame += bitmap=>bitmap.Dispose();
            decoder.Frame += () => { long now=Stopwatch.GetTimestamp();Interlocked.CompareExchange(ref firstFrame,now,0);Interlocked.Exchange(ref lastFrame,now);Interlocked.Increment(ref frames); };
            decoder.WebcamEnabled = true;
            var task = decoder.Start();
            await Task.Delay(6500); decoder.Stop(); await task;
            result["decodedFrames"] = frames; result["webcamFrames"] = decoder.WebcamFrames; result["webcamError"] = decoder.WebcamError;
            result["measuredSourceFps"]=lastFrame>firstFrame?Math.Round((frames-1)/Stopwatch.GetElapsedTime(firstFrame,lastFrame).TotalSeconds,2):0;
            result["decoderLaunches"]=decoder.ProcessLaunches;result["processingMs"]=decoder.ProcessingMilliseconds;result["processingSettings"]=decoder.Settings;
            result["maxineFrames"]=decoder.BackgroundFrames;result["maxineMs"]=decoder.BackgroundMilliseconds;result["maxineError"]=decoder.BackgroundError;
            result["eyeFrames"]=decoder.EyeFrames;result["eyeFaceFrames"]=decoder.EyeFaceFrames;result["eyeError"]=decoder.EyeError;
            result["passed"] = frames > 15 && decoder.WebcamFrames > 15 && saved && decoder.WebcamError=="" && decoder.BackgroundError=="" && (decoder.Settings.Background==BackgroundEffect.Off || decoder.BackgroundFrames>15) && (!decoder.Settings.EyeContact || decoder.EyeFrames>15 && decoder.EyeError=="");
            if (!(bool)result["passed"]) Environment.ExitCode = 2;
        }
        catch (Exception ex) { result["passed"] = false; result["error"] = ex.ToString(); Environment.ExitCode = 1; }
        File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions(Json) { WriteIndented = true }));
    }
}

public record Resolution(int Width, int Height, string Label) { public override string ToString() => Label; }
public record Lens(string Id, string Label, string CameraId, string PhysicalId, int EquivalentMm, List<Resolution> Resolutions) { public override string ToString() => Label; }
public record CameraList(List<Lens> Lenses);
public class CameraStatus
{
    public string State { get; set; } = ""; public string Error { get; set; } = "";
    public bool Permission { get; set; } public string Lens { get; set; } = "";
    public int Width { get; set; } public int Height { get; set; } public int Fps { get; set; }
    public long Frames { get; set; } public long LastFrameAgeMs { get; set; }
    public bool OrientationSupported { get; set; } public bool OrientationKnown { get; set; }
    public int DeviceOrientation { get; set; } public int SensorOrientation { get; set; } public int RotationDegrees { get; set; }
}
public record Config(string Lens, int Width, int Height, int Fps, bool Enabled = true);
public record Device(string Serial, string Label) { public override string ToString() => Label; }

/// <summary>Writer for the OBS virtual camera's documented NV12 shared-memory protocol.
/// Protocol reference: obsproject/obs-studio, shared/obs-shared-memory-queue (GPL-2.0-or-later).
/// Only one publisher can own this Windows video queue at a time.</summary>
sealed class WebcamWriter : IDisposable
{
    readonly MemoryMappedFile memory;
    readonly MemoryMappedViewAccessor view;
    readonly int[] offsets = new int[3];
    uint sequence;
    public WebcamWriter(int width, int height, int fps)
    {
        int frameBytes = checked(width * height * 3 / 2), position = 96;
        for (int i = 0; i < 3; i++) { offsets[i] = position; position = (position + frameBytes + 32 + 31) & ~31; }
        memory = MemoryMappedFile.CreateNew("OBSVirtualCamVideo", position, MemoryMappedFileAccess.ReadWrite);
        view = memory.CreateViewAccessor();
        view.Write(8, 1u);
        for (int i = 0; i < 3; i++) view.Write(12 + i * 4, offsets[i]);
        view.Write(28, width); view.Write(32, height); view.Write(40, (ulong)(10_000_000 / fps));
        try { File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-virtualcam.txt"), width + "x" + height + "x" + (10_000_000 / fps)); } catch(IOException) { }
    }
    public void Write(byte[] frame)
    {
        uint next = ++sequence; int position = offsets[next % 3];
        view.Write(0, next);
        view.Write(position, (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000d / Stopwatch.Frequency)));
        view.WriteArray(position + 32, frame, 0, frame.Length);
        Thread.MemoryBarrier(); view.Write(4, next); view.Write(8, 2u);
    }
    public void Dispose() { view.Write(8, 3u); view.Dispose(); memory.Dispose(); }
}

