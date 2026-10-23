using System.Diagnostics;
using System.Globalization;

namespace PixelCam;

sealed record MaxineGpu(string Name,string Architecture,string ModelCode,string Driver,string Uuid,string Message)
{
    public bool Supported=>Architecture.Length>0;
}

// Detect once, before loading CUDA. A UUID makes CUDA device zero refer to the
// same GPU whose package was selected, including computers with multiple GPUs.
static class MaxineHardware
{
    static readonly Lazy<MaxineGpu> Detected=new(Detect);
    public static MaxineGpu Current=>Detected.Value;
    public static (string Architecture,string ModelCode) Family(string capability)=>capability.Trim() switch
    {
        "7.5"=>("turing","75"),"8.6"=>("ampere","86"),
        "8.9"=>("ada","89"),"12.0"=>("blackwell","100"),_ =>("","")
    };
    public static MaxineGpu Parse(string output)
    {
        foreach(string line in output.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields=line.Split(',').Select(s=>s.Trim()).ToArray();if(fields.Length!=4)continue;
            var family=Family(fields[1]);
            if(family.Architecture.Length==0 || !fields[0].Contains("RTX",StringComparison.OrdinalIgnoreCase))continue;
            if(!Version.TryParse(fields[2],out var driver) || driver<new Version(572,61))
                return new(fields[0],"","",fields[2],fields[3],fields[0]+" · update the NVIDIA driver to 572.61 or newer for Maxine");
            return new(fields[0],family.Architecture,family.ModelCode,fields[2],fields[3],fields[0]+" · "+CultureInfo.InvariantCulture.TextInfo.ToTitleCase(family.Architecture)+" packages");
        }
        return new("","","","","","Maxine needs a supported NVIDIA RTX GPU. Video, color, framing and webcam output remain available.");
    }
    static MaxineGpu Detect()
    {
        try
        {
            string system=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"nvidia-smi.exe");
            string legacy=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NVIDIA Corporation","NVSMI","nvidia-smi.exe");
            var start=new ProcessStartInfo(File.Exists(system)?system:File.Exists(legacy)?legacy:"nvidia-smi.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("--query-gpu=name,compute_cap,driver_version,uuid");start.ArgumentList.Add("--format=csv,noheader");
            using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(5000)){process.Kill(true);return new("","","","","","NVIDIA GPU check timed out. Check the driver; ordinary webcam controls remain available.");}
            var result=process.ExitCode==0?Parse(output.GetAwaiter().GetResult()):Parse("");
            if(result.Supported)
            {
                string? mask=Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
                if(!string.IsNullOrWhiteSpace(mask) && mask!=result.Uuid)
                    return result with{Architecture="",ModelCode="",Message="A custom CUDA_VISIBLE_DEVICES setting is active. Clear it before using automatic Maxine setup."};
                Environment.SetEnvironmentVariable("CUDA_DEVICE_ORDER","PCI_BUS_ID");
                Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES",result.Uuid);
            }
            return result;
        }
        catch{return Parse("");}
    }
}
