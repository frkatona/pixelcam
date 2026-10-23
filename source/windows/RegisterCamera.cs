// Registration helper compiled separately for x64 and x86 using .NET Framework.
// Redirecting HKCR is confined to this process and registers only for the current user.
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static class RegisterCamera
{
    [DllImport("advapi32.dll")] static extern int RegOverridePredefKey(IntPtr key,IntPtr replacement);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Registration();
    [STAThread] static int Main(string[] args)
    {
        try {
            // Load the system filter mapper before HKCR is redirected to the user's classes.
            object mapper=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("CDA42200-BD88-11D0-BD4E-00A0C911CE86")));
            using(RegistryKey classes=Registry.CurrentUser.CreateSubKey(@"Software\Classes")) {
                int result=RegOverridePredefKey(new IntPtr(unchecked((int)0x80000000)),classes.Handle.DangerousGetHandle());
                if(result!=0) throw new Exception("Registry redirection failed: "+result);
                try {
                    IntPtr module=LoadLibrary(args[0]);
                    if(module==IntPtr.Zero) throw new Exception("Could not load the webcam module: "+Marshal.GetLastWin32Error());
                    try {
                        IntPtr procedure=GetProcAddress(module,args.Length>2?"DllUnregisterServer":"DllRegisterServer");
                        if(procedure==IntPtr.Zero) throw new Exception("Registration export missing");
                        var register=(Registration)Marshal.GetDelegateForFunctionPointer(procedure,typeof(Registration));
                        int hr=register(); if(hr<0) Marshal.ThrowExceptionForHR(hr);
                    } finally { FreeLibrary(module); }
                } finally { RegOverridePredefKey(new IntPtr(unchecked((int)0x80000000)),IntPtr.Zero); }
            }
            Marshal.FinalReleaseComObject(mapper);
            if(args.Length<=2) {
                using(var category=Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance",true)) {
                    if(category!=null) foreach(string keyName in category.GetSubKeyNames()) {
                        using(var entry=category.OpenSubKey(keyName,true)) {
                            string clsid=entry.GetValue("CLSID") as string;
                            using(var implementation=Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\"+clsid+@"\InprocServer32")) {
                                if(implementation!=null && String.Equals(implementation.GetValue(null) as string,args[0],StringComparison.OrdinalIgnoreCase)) entry.SetValue("FriendlyName","PixelCam");
                            }
                        }
                    }
                }
            }
            File.WriteAllText(args[1],"OK"); return 0;
        } catch(Exception ex) { if(args.Length>1) File.WriteAllText(args[1],ex.ToString()); return 1; }
    }
}
