using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace PixelCam;

sealed record SetupProgress(string Message,int? Percent=null);
sealed record SetupReadiness(bool VideoEffectsReady,bool EyeContactReady,string Message);
sealed record SetupResult(bool VideoEffectsReady,bool EyeContactReady,bool InstalledAny,bool RestartRecommended,string Message);

// Overrides support isolated setup verification without touching the installed
// runtime. The app uses the default per-user root and pinned packages below.
sealed record SetupOptions(string RootDirectory,string? VideoEffectsPackage=null,string? EyeContactPackage=null,string? ArchiveTool=null,string Architecture="ampere");

static class MaxineSetup
{
    static readonly SemaphoreSlim SetupGate=new(1,1);
    public static string DefaultRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PixelCam");
    sealed record Package(string Name,string Folder,string Version,string CoreFile,string CacheName,string Url,string Sha256,string[] Required)
    {
        public string Destination(string root)=>Path.Combine(root,Folder,Version);
    }
    static readonly Dictionary<string,Dictionary<string,Package>> Catalog=ReadCatalog();
    static Dictionary<string,Dictionary<string,Package>> ReadCatalog()
    {
        using var stream=typeof(MaxineSetup).Assembly.GetManifestResourceStream("PixelCam.MaxinePackages")!;
        return JsonSerializer.Deserialize<Dictionary<string,Dictionary<string,Package>>>(stream,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
    }
    static string Architecture(SetupOptions? options)=>options?.Architecture??(MaxineHardware.Current.Supported?MaxineHardware.Current.Architecture:"ampere");
    static Package Vfx(SetupOptions? options)=>Catalog[Architecture(options)]["vfx"];
    static Package Ar(SetupOptions? options)=>Catalog[Architecture(options)]["ar"];
    public static string RuntimeDirectory(bool eyes=false)=>ActiveDirectory(eyes?Ar(null):Vfx(null),DefaultRoot,null);
    public static string[] PackageArchitectures=>Catalog.Keys.ToArray();

    public static SetupReadiness Inspect(SetupOptions? options=null)
    {
        string root=Root(options);var VideoEffects=Vfx(options);var EyeContact=Ar(options);
        bool vfx=Complete(VideoEffects,ActiveDirectory(VideoEffects,root,options)),eyes=Complete(EyeContact,ActiveDirectory(EyeContact,root,options));
        return new(vfx,eyes,vfx?(eyes?"Maxine and Eye contact ready":"Maxine ready · Eye contact setup available"):"Maxine setup needed");
    }

    // Setup does not start capture or allocate a GPU model. Maxine itself runs
    // inside PixelCam and starts when a source uses a selected effect.
    public static async Task<SetupResult> EnsureAsync(bool includeEyeContact,IProgress<SetupProgress>? progress=null,CancellationToken cancellationToken=default,SetupOptions? options=null)
    {
        await SetupGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var VideoEffects=Vfx(options);var EyeContact=Ar(options);
            var before=Inspect(options);
            if(before.VideoEffectsReady && (!includeEyeContact || before.EyeContactReady))return new(before.VideoEffectsReady,before.EyeContactReady,false,false,before.Message);
            string root=Root(options);Directory.CreateDirectory(root);
            using var setupLock=AcquireSetupLock(root);
            var requested=new List<Package>();if(!before.VideoEffectsReady)requested.Add(VideoEffects);if(includeEyeContact && !before.EyeContactReady)requested.Add(EyeContact);
            foreach(var package in requested)CheckOverride(package,options);
            progress?.Report(new("Checking the NVIDIA GPU…"));
            if(options==null && !MaxineHardware.Current.Supported)throw new IOException(MaxineHardware.Current.Message);
            string archiveTool=FindArchiveTool(options);
            bool nativeWasLoaded=NativeBridgeLoaded(),installed=false;
            foreach(var package in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Another setup process may have completed before the lock.
                if(Complete(package,ActiveDirectory(package,root,options)))continue;
                string destination=package.Destination(root);
                if(Directory.Exists(destination) && LoadedFrom(destination))throw new IOException("Maxine files are in use. Close and reopen PixelCam, then run setup before enabling effects.");
                string supplied=package.Folder=="maxine"?options?.VideoEffectsPackage??"":options?.EyeContactPackage??"";
                string installer=await GetPackage(package,root,supplied,progress,cancellationToken);
                string stagingRoot=Path.Combine(root,"setup-staging");Directory.CreateDirectory(stagingRoot);
                string staging=Path.Combine(stagingRoot,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
                try
                {
                    progress?.Report(new("Preparing "+package.Name+"…"));
                    await Extract(archiveTool,installer,staging,cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if(!Complete(package,staging))throw new IOException("The NVIDIA package is missing required runtime files, models, or license notices. Setup has kept the previous installation.");
                    progress?.Report(new("Finishing "+package.Name+"…"));
                    Promote(staging,destination,root);installed=true;
                }
                finally { DeleteStaging(staging,stagingRoot); }
            }
            var after=Inspect(options);
            if(!after.VideoEffectsReady || includeEyeContact && !after.EyeContactReady)throw new IOException("Maxine setup did not produce a complete runtime.");
            bool restart=installed && (nativeWasLoaded || NativeBridgeLoaded());
            string message=restart?"Setup complete. Restart PixelCam to reload its NVIDIA libraries.":after.Message+" · starts with your selected effects";
            progress?.Report(new(message,100));return new(after.VideoEffectsReady,after.EyeContactReady,installed,restart,message);
        }
        finally { SetupGate.Release(); }
    }

    static string Root(SetupOptions? options)=>Path.GetFullPath(options?.RootDirectory??DefaultRoot);
    static string ActiveDirectory(Package package,string root,SetupOptions? options)
    {
        if(options!=null)return package.Destination(root);
        string? custom=Environment.GetEnvironmentVariable(package.Folder=="maxine"?"PIXELCAM_MAXINE_PATH":"PIXELCAM_AR_PATH");
        if(!string.IsNullOrWhiteSpace(custom))return custom;
        string destination=package.Destination(root);
        if(File.Exists(Path.Combine(destination,package.CoreFile)))return destination;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NVIDIA Corporation",package.Folder=="maxine"?"NVIDIA Video Effects":"NVIDIA AR SDK");
    }
    static bool Complete(Package package,string directory)
    {
        try{return package.Required.Prepend(package.CoreFile).All(name=>{var file=new FileInfo(Path.Combine(directory,name));return file.Exists && file.Length>0;});}
        catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}
    }
    static void CheckOverride(Package package,SetupOptions? options)
    {
        if(options!=null)return;
        string name=package.Folder=="maxine"?"PIXELCAM_MAXINE_PATH":"PIXELCAM_AR_PATH";
        if(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))throw new IOException($"The custom {name} folder is incomplete. Correct or remove that override before using built-in setup.");
    }
    static FileStream AcquireSetupLock(string root)
    {
        try{return new FileStream(Path.Combine(root,"setup.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException ex){throw new IOException("Maxine setup is already running in another PixelCam window.",ex);}
    }
    static string FindArchiveTool(SetupOptions? options)
    {
        string bundled=Program.Tool("7zip/7z.exe");
        string tool=options?.ArchiveTool??(File.Exists(bundled)?bundled:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"7-Zip","7z.exe"));
        if(!File.Exists(tool))throw new IOException("The extraction helper is missing. Repair PixelCam with its installer, then retry setup.");
        return tool;
    }
    static async Task<string> GetPackage(Package package,string root,string supplied,IProgress<SetupProgress>? progress,CancellationToken cancellationToken)
    {
        if(!string.IsNullOrWhiteSpace(supplied))
        {
            progress?.Report(new("Verifying "+package.Name+" package…"));
            if(!await Verified(supplied,package.Sha256,cancellationToken))throw new IOException("The selected NVIDIA package does not match PixelCam's pinned SHA-256. No runtime files were installed.");
            return Path.GetFullPath(supplied);
        }
        string cache=Path.Combine(root,"downloads");Directory.CreateDirectory(cache);string destination=Path.Combine(cache,package.CacheName);
        if(File.Exists(destination))
        {
            progress?.Report(new("Verifying cached "+package.Name+" package…"));
            if(await Verified(destination,package.Sha256,cancellationToken))return destination;
        }
        string partial=destination+".partial-"+Guid.NewGuid().ToString("N");
        try
        {
            progress?.Report(new("Downloading "+package.Name+"…",0));
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using var client=new HttpClient(new SocketsHttpHandler{ConnectTimeout=TimeSpan.FromSeconds(30)}){Timeout=Timeout.InfiniteTimeSpan};
            using var response=await client.GetAsync(package.Url,HttpCompletionOption.ResponseHeadersRead,timeout.Token);response.EnsureSuccessStatusCode();
            long? total=response.Content.Headers.ContentLength;const long maximum=3L*1024*1024*1024;
            if(total>maximum)throw new IOException("The NVIDIA download is larger than the expected package.");
            await using(var source=await response.Content.ReadAsStreamAsync(timeout.Token))
            await using(var target=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan))
            {
                var buffer=new byte[1024*1024];long received=0;int lastPercent=-1;var update=Stopwatch.StartNew();
                while(true)
                {
                    int count=await source.ReadAsync(buffer,timeout.Token);if(count==0)break;
                    received+=count;if(received>maximum)throw new IOException("The NVIDIA download exceeded its allowed size.");
                    await target.WriteAsync(buffer.AsMemory(0,count),timeout.Token);
                    int? percent=total>0?(int)Math.Clamp(received*100/total.Value,0,100):null;
                    if(percent!=lastPercent || update.ElapsedMilliseconds>500){progress?.Report(new("Downloading "+package.Name+"…",percent));lastPercent=percent??-1;update.Restart();}
                }
            }
            progress?.Report(new("Verifying "+package.Name+" download…"));
            if(!await Verified(partial,package.Sha256,cancellationToken))throw new IOException("The NVIDIA download did not match PixelCam's pinned SHA-256. The incomplete download was discarded.");
            cancellationToken.ThrowIfCancellationRequested();File.Move(partial,destination,true);return destination;
        }
        finally { if(File.Exists(partial))File.Delete(partial); }
    }
    static async Task<bool> Verified(string path,string expected,CancellationToken cancellationToken)
    {
        if(!File.Exists(path))return false;
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream,cancellationToken)).Equals(expected,StringComparison.OrdinalIgnoreCase);
    }
    static async Task Extract(string tool,string package,string destination,CancellationToken cancellationToken)
    {
        await Run(tool,new[]{"x",package,"-o"+destination,"-y","-bso0","-bsp0","*.dll","models\\*","NVIDIA Maxine EULA.pdf","ThirdPartyLicenses.txt","version.h"},cancellationToken);
    }
    static async Task<string> Run(string tool,IEnumerable<string> arguments,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start=new ProcessStartInfo(tool){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("Could not start the setup helper.");
        Task<string> output=process.StandardOutput.ReadToEndAsync(cancellationToken),error=process.StandardError.ReadToEndAsync(cancellationToken);
        try {await process.WaitForExitAsync(cancellationToken);}
        catch {try{process.Kill(true);await process.WaitForExitAsync(CancellationToken.None);}catch{}throw;}
        string text=await output,details=await error;
        if(process.ExitCode!=0)throw new IOException("The setup helper failed: "+(details.Length>500?details[..500]:details));
        return text;
    }
    static bool NativeBridgeLoaded()
    {
        try{using var process=Process.GetCurrentProcess();return process.Modules.Cast<ProcessModule>().Any(module=>module.ModuleName.Equals("PixelCamMaxine.dll",StringComparison.OrdinalIgnoreCase));}
        catch{ return true; } // Conservatively recommend restart if inspection is unavailable.
    }
    static bool LoadedFrom(string directory)
    {
        try{string prefix=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;using var process=Process.GetCurrentProcess();return process.Modules.Cast<ProcessModule>().Any(module=>module.FileName.StartsWith(prefix,StringComparison.OrdinalIgnoreCase));}
        catch{return true;}
    }
    static void Promote(string staging,string destination,string root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);string? backup=null;
        if(Directory.Exists(destination))
        {
            string backupRoot=Path.Combine(root,"setup-backups");Directory.CreateDirectory(backupRoot);backup=Path.Combine(backupRoot,Guid.NewGuid().ToString("N"));Directory.Move(destination,backup);
        }
        try{Directory.Move(staging,destination);}
        catch{if(backup!=null && !Directory.Exists(destination))Directory.Move(backup,destination);throw;}
        // An incomplete prior installation is retained in setup-backups rather
        // than deleting files that might be needed for recovery.
    }
    static void DeleteStaging(string staging,string stagingRoot)
    {
        string prefix=Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string full=Path.GetFullPath(staging);
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(full),"N",out _))throw new IOException("Refused to clean an unexpected setup path.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
