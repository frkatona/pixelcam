using System.Text.Json;

namespace PixelCam;

// Exercises the distinction between stored manual values and live Auto
// readings. Native mouse hit testing is checked separately in the visible UI.
static class ControlSettingsTests
{
    public static void Run(string path)
    {
        var checks=new Dictionary<string,object>();
        try {
            var specifications=new[]{
                (Name:"brightness",Minimum:-100,Maximum:100,Neutral:0,Manual:23,Automatic:-17),
                (Name:"contrast",Minimum:25,Maximum:200,Neutral:100,Manual:137,Automatic:88),
                (Name:"saturation",Minimum:0,Maximum:200,Neutral:100,Manual:126,Automatic:94),
                (Name:"gamma",Minimum:50,Maximum:200,Neutral:100,Manual:115,Automatic:91)
            };
            foreach(var spec in specifications) {
                using var control=new ColorControl(spec.Name,spec.Minimum,spec.Maximum,spec.Neutral,value=>value.ToString());
                checks[spec.Name+"StartsManual"]=!control.IsAutomatic && control.Slider.Enabled && control.Value==spec.Neutral;
                control.Value=spec.Manual;control.IsAutomatic=true;
                int notifications=0;control.Changed+=(_,_)=>notifications++;
                control.ShowAuto(spec.Automatic);
                checks[spec.Name+"AutoTelemetryIsQuiet"]=notifications==0;
                checks[spec.Name+"ManualSurvivesAutoReading"]=control.Value==spec.Manual && control.Slider.Value==spec.Automatic;
                // This is the value an unrelated settings publication will read.
                int storedDuringAuto=control.Value;
                control.IsAutomatic=false;
                checks[spec.Name+"AutoOffRestoresManual"]=storedDuringAuto==spec.Manual && control.Slider.Value==spec.Manual && notifications==1;
                control.Slider.ResetToDefault();
                checks[spec.Name+"ResetReturnsToNeutral"]=control.Value==spec.Neutral && control.Slider.Value==spec.Neutral && !control.IsAutomatic;

                // Loading a saved manual value must also work while Auto is on.
                control.IsAutomatic=true;control.Value=spec.Manual;control.ShowAuto(spec.Automatic);control.IsAutomatic=false;
                checks[spec.Name+"SavedValueLoadsWhileAutoOn"]=control.Value==spec.Manual && control.Slider.Value==spec.Manual;
            }

            using var balance=new WhiteBalanceControl();
            checks["whiteBalanceStartsManual"]=!balance.Automatic.Checked && balance.Warmth.Enabled && balance.Tint.Enabled;
            balance.ManualWarmth=-27;balance.ManualTint=13;balance.Automatic.Checked=true;
            int balanceNotifications=0;balance.Changed+=(_,_)=>balanceNotifications++;
            balance.ShowAuto(.61,-.42);
            checks["whiteBalanceAutoTelemetryIsQuiet"]=balanceNotifications==0;
            checks["whiteBalanceManualSurvivesAutoReading"]=balance.ManualWarmth==-27 && balance.ManualTint==13 && balance.Warmth.Value==61 && balance.Tint.Value==-42;
            balance.Automatic.Checked=false;
            checks["whiteBalanceAutoOffRestoresBothValues"]=balance.Warmth.Value==-27 && balance.Tint.Value==13 && balanceNotifications==1;
            balance.Warmth.ResetToDefault();
            checks["whiteBalanceWarmthResetKeepsTint"]=balance.ManualWarmth==0 && balance.ManualTint==13 && !balance.Automatic.Checked;
            balance.Tint.ResetToDefault();
            checks["whiteBalanceTintResetReturnsToNeutral"]=balance.ManualWarmth==0 && balance.ManualTint==0 && !balance.Automatic.Checked;
            balance.Automatic.Checked=true;balance.ManualWarmth=-27;balance.ManualTint=13;balance.ShowAuto(.61,-.42);balance.Automatic.Checked=false;
            checks["whiteBalanceSavedValuesLoadWhileAutoOn"]=balance.Warmth.Value==-27 && balance.Tint.Value==13;

            var saved=new ProcessingSettings{Brightness=.23,Contrast=1.37,Saturation=1.26,Gamma=1.15,Warmth=-.27,Tint=.13,AutoWhiteBalance=true};
            var restored=JsonSerializer.Deserialize<ProcessingSettings>(JsonSerializer.Serialize(saved,Program.Json),Program.Json);
            checks["savedManualValuesAndAutoChoiceRoundTrip"]=restored==saved;
            checks["passed"]=checks.Values.OfType<bool>().All(value=>value);
            if(!(bool)checks["passed"])Environment.ExitCode=2;
        } catch(Exception ex) { checks["passed"]=false;checks["error"]=ex.ToString();Environment.ExitCode=1; }
        File.WriteAllText(path,JsonSerializer.Serialize(checks,new JsonSerializerOptions(Program.Json){WriteIndented=true}));
    }
}
