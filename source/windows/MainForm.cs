using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace PixelCam;

sealed record AppPreferences(string Lens,int Width,int Height,bool Webcam,ProcessingSettings Processing,string Appearance="midnight",bool AutoOrient=true);

sealed class MainForm : Form
{
    readonly ComboBox devices=Combo(190),lenses=Combo(205),resolutions=Combo(160),rotation=Combo(95);
    readonly Button connect=Button("Connect phone",160),settingsButton=Button("\uE713",42),resetFrame=Button("Reset",65),resetColor=Button("Reset color",105);
    readonly ToolStripMenuItem openVideo=new("Open video…");
    readonly ContextMenuStrip settingsMenu=new();
    readonly ToolStripMenuItem appearanceMenu=new("Appearance");
    readonly ComboBox backgroundMode=Combo(220),segmentation=Combo(240),backgroundPreset=Combo(260);
    readonly ResettableTrackBar blurStrength=new(){Minimum=0,Maximum=100,Value=50,ResetValue=50,TickStyle=TickStyle.None,AutoSize=false,Height=30};
    readonly Button chooseBackground=Button("Choose image…",150);
    readonly Button setupMaxine=Button("Set up Maxine",190),setupEyes=Button("Set up Eye contact",190),cancelSetup=Button("Cancel setup",120),restartApp=Button("Restart PixelCam",190),retryEffects=Button("Retry effects",160);
    readonly Label runtimeStatus=new(){AutoSize=true,MaximumSize=new Size(260,0)};
    readonly Label gpuStatus=new(){AutoSize=true,MaximumSize=new Size(260,0),Margin=new Padding(0,0,0,8)};
    readonly ProgressBar setupProgress=new(){Width=250,Height=8,Visible=false};
    readonly Label backgroundFile=new(){AutoEllipsis=true,Text="No image selected",Height=26},blurValue=new(){Text="Blur strength · 50%",AutoSize=true},maxineStatus=new(){AutoSize=true,MaximumSize=new Size(260,0)};
    readonly StyledTabControl sideTabs=new(){Dock=DockStyle.Fill};
    readonly CheckBox webcam=new(){Text="Webcam output",AutoSize=true,Checked=true},mirror=new(){Text="Mirror",AutoSize=true},eyeContact=new(){Text="Eye contact",AutoSize=true};
    readonly ColorControl brightness=new("Brightness",-100,100,0,v=>$"{v:+0;-0;0}"),contrast=new("Contrast",25,200,100,v=>$"{v}%"),saturation=new("Saturation",0,200,100,v=>$"{v}%"),gamma=new("Gamma",50,200,100,v=>$"{v/100d:F2}");
    readonly WhiteBalanceControl whiteBalance=new();readonly FramingControl framing=new();
    readonly ResettableTrackBar zoom=new(){Minimum=100,Maximum=500,Value=100,ResetValue=100,TickStyle=TickStyle.None,AutoSize=false,Height=30,SmallChange=1,LargeChange=10};
    readonly Label zoomValue=new(){AutoSize=true,Text="Zoom 1.00×"};
    readonly PictureBox preview=new(){Dock=DockStyle.Fill,BackColor=Color.FromArgb(8,12,18),SizeMode=PictureBoxSizeMode.Zoom};
    readonly Label empty=new(){Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,Text="Connect your Pixel over USB",ForeColor=Color.FromArgb(155,175,198)};
    readonly ToolStripStatusLabel status=new("Connecting…"),metrics=new(){Spring=true,TextAlign=ContentAlignment.MiddleRight};
    readonly System.Windows.Forms.Timer telemetry=new(){Interval=500},cameraDebounce=new(){Interval=180},saveDebounce=new(){Interval=800},orientationPoll=new(){Interval=300};
    readonly ToolTip tips=new();readonly string[] args;readonly bool testMode;
    readonly string settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PixelCam","settings-v2.json");
    AppPreferences? preferences;ProcessingSettings processing=new();Decoder? decoder;Task? decodeTask;
    string baseUrl="",serial="";int port,suppress,pendingPreview,pendingNavigator,cameraRevision;
    readonly SemaphoreSlim sourceGate=new(1,1);
    List<Lens> availableLenses=new();bool connecting,changing,openingVideo,pendingCamera,closing,cleanupComplete,pollingOrientation;string videoPath="",backgroundPath="",customBackgroundPath="",appearanceId="midnight";
    CameraStatus? phoneOrientation;
    CancellationTokenSource? setupCancellation;Task<SetupResult>? setupTask;bool setupBusy,restartRequired,restartRequested;string? setupNotice;
    Config? activeConfig;long received,previousReceived;DateTime lastFrame=DateTime.MinValue;

    public MainForm(string[] args)
    {
        this.args=args;testMode=args.Any(a=>a is "--layout-test" or "--ui-self-test" or "--recorded-ui-test" or "--eye-ui-test" or "--options-ui-test");
        Text="PixelCam";ClientSize=new Size(1280,860);MinimumSize=new Size(1060,800);StartPosition=FormStartPosition.CenterScreen;
        using(var iconStream=typeof(MainForm).Assembly.GetManifestResourceStream("PixelCam.AppIcon"))if(iconStream!=null){using var appIcon=new Icon(iconStream);Icon=(Icon)appIcon.Clone();}
        BackColor=Color.FromArgb(19,26,36);ForeColor=Color.FromArgb(231,237,245);Font=new Font("Segoe UI",10);
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(14)};
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,94));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,28));Controls.Add(root);
        var header=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));header.RowStyles.Add(new RowStyle(SizeType.AutoSize));header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var first=Row();first.Dock=DockStyle.Fill;first.Controls.Add(devices);first.Controls.Add(connect);
        webcam.Margin=new Padding(12,8,5,0);first.Controls.Add(webcam);first.Controls.Add(Info("Choose PixelCam as the camera in your video app. Video apps may need reopening after camera installation.","Webcam information"));
        var firstLine=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2,RowCount=1,Margin=Padding.Empty};firstLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));firstLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));firstLine.Controls.Add(first,0,0);
        settingsButton.Font=new Font("Segoe MDL2 Assets",12);settingsButton.AccessibleName="App settings";settingsButton.Margin=Padding.Empty;firstLine.Controls.Add(settingsButton,1,0);tips.SetToolTip(settingsButton,"App settings: open video and appearance");
        var second=Row();second.Margin=new Padding(0,9,0,0);AddLabel(second,"Lens");second.Controls.Add(lenses);AddLabel(second,"Resolution");second.Controls.Add(resolutions);AddLabel(second,"Rotate");
        rotation.Items.AddRange(new object[]{"0°","90°","180°","270°","Auto"});rotation.SelectedIndex=testMode?0:4;second.Controls.Add(rotation);mirror.Margin=new Padding(12,8,0,0);second.Controls.Add(mirror);eyeContact.Margin=new Padding(14,8,0,0);second.Controls.Add(eyeContact);
        header.Controls.Add(firstLine,0,0);header.Controls.Add(second,0,1);root.Controls.Add(header,0,0);
        var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,312));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));root.Controls.Add(body,0,1);
        body.Controls.Add(sideTabs,0,0);
        var video=new Panel{Dock=DockStyle.Fill,Margin=new Padding(12,0,0,0)};video.Controls.Add(preview);video.Controls.Add(empty);body.Controls.Add(video,1,0);
        var colorTab=new TabPage("Color & framing"){BackColor=Color.FromArgb(23,32,44),ForeColor=ForeColor};sideTabs.TabPages.Add(colorTab);
        var sidebar=new Panel{Dock=DockStyle.Fill,AutoScroll=true,BackColor=colorTab.BackColor,Padding=new Padding(10,8,10,8),Margin=Padding.Empty};colorTab.Controls.Add(sidebar);
        var sideContent=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=10,Margin=Padding.Empty,Padding=Padding.Empty};sideContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));sidebar.Controls.Add(sideContent);
        var frameTitle=Row();frameTitle.Controls.Add(new Label{Text="Framing",AutoSize=true,Font=new Font(Font,FontStyle.Bold)});frameTitle.Controls.Add(Info("Drag the cropped-frame box inside the full image to pan. Scroll to zoom. Arrow keys move the frame; Home resets it.","Framing information"));AddSide(sideContent,frameTitle,28);
        AddSide(sideContent,framing,177);
        var zoomPanel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=2,Margin=new Padding(0,6,0,6)};zoomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,60));zoomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));zoomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,70));zoomPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,24));zoomPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,30));
        zoomPanel.Controls.Add(zoomValue,0,0);zoomPanel.SetColumnSpan(zoomValue,2);zoomPanel.Controls.Add(resetFrame,2,0);zoomPanel.SetRowSpan(resetFrame,2);zoom.Dock=DockStyle.Fill;zoomPanel.Controls.Add(zoom,0,1);zoomPanel.SetColumnSpan(zoom,2);AddSide(sideContent,zoomPanel,74);
        var colorTitle=Row();colorTitle.Controls.Add(new Label{Text="Color",AutoSize=true,Font=new Font(Font,FontStyle.Bold)});colorTitle.Controls.Add(Info("Auto adapts gently to the current image. Uncheck an option to adjust it manually. These controls process the PC preview and webcam together; the phone still performs its own camera processing.","Automatic color information"));AddSide(sideContent,colorTitle,28);
        AddSide(sideContent,brightness,58);AddSide(sideContent,contrast,58);AddSide(sideContent,saturation,58);AddSide(sideContent,gamma,58);AddSide(sideContent,whiteBalance,104);AddSide(sideContent,resetColor,35);
        var backgroundTab=new TabPage("Background"){BackColor=colorTab.BackColor,ForeColor=ForeColor};sideTabs.TabPages.Add(backgroundTab);
        var effectsPanel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(10)};backgroundTab.Controls.Add(effectsPanel);
        effectsPanel.Controls.Add(new Label{Text="NVIDIA Maxine",AutoSize=true,Font=new Font(Font,FontStyle.Bold)});
        gpuStatus.Text=MaxineHardware.Current.Message;effectsPanel.Controls.Add(gpuStatus);
        effectsPanel.Controls.Add(Info("Maxine runs inside PixelCam and starts automatically with your selected effects. Setup is needed only once. Best for one person facing the camera; hair, hands and quick motion can show edge errors.","Maxine information"));
        runtimeStatus.Margin=new Padding(0,0,0,8);effectsPanel.Controls.Add(runtimeStatus);effectsPanel.Controls.Add(setupMaxine);effectsPanel.Controls.Add(setupEyes);effectsPanel.Controls.Add(setupProgress);cancelSetup.Visible=restartApp.Visible=retryEffects.Visible=false;effectsPanel.Controls.Add(cancelSetup);effectsPanel.Controls.Add(restartApp);effectsPanel.Controls.Add(retryEffects);
        backgroundMode.Items.AddRange(new object[]{"Off","Blur background","Replace background"});backgroundMode.SelectedIndex=0;effectsPanel.Controls.Add(backgroundMode);
        effectsPanel.Controls.Add(blurValue);blurStrength.Width=250;effectsPanel.Controls.Add(blurStrength);
        effectsPanel.Controls.Add(new Label{Text="Person mask",AutoSize=true,Margin=new Padding(0,12,0,3)});
        segmentation.Items.AddRange(new object[]{"Quality · keep chair","Fast · keep chair","Quality · remove chair","Fast · remove chair"});segmentation.SelectedIndex=0;effectsPanel.Controls.Add(segmentation);
        effectsPanel.Controls.Add(new Label{Text="Replacement",AutoSize=true,Margin=new Padding(0,14,0,3)});backgroundPreset.Items.AddRange(BackgroundPresets.Choices);backgroundPreset.SelectedIndex=1;backgroundPreset.AccessibleName="Replacement background";effectsPanel.Controls.Add(backgroundPreset);
        chooseBackground.Margin=new Padding(0,8,0,4);effectsPanel.Controls.Add(chooseBackground);backgroundFile.Width=260;effectsPanel.Controls.Add(backgroundFile);
        maxineStatus.Text=MaxineEffects.Available?"Maxine runtime available":"Maxine runtime not installed";maxineStatus.Margin=new Padding(0,16,0,0);effectsPanel.Controls.Add(maxineStatus);
        var strip=new StatusStrip{Dock=DockStyle.Fill,BackColor=BackColor,ForeColor=ForeColor,SizingGrip=false};strip.Items.Add(status);strip.Items.Add(metrics);root.Controls.Add(strip,0,2);
        devices.AccessibleName="Connected Android device";lenses.AccessibleName="Rear camera lens";resolutions.AccessibleName="Capture resolution";rotation.AccessibleName="Video rotation";zoom.AccessibleName="Digital zoom";
        tips.SetToolTip(resolutions,"Capture resolution. Lens and resolution selections apply automatically; the hardware can pause briefly while switching.");tips.SetToolTip(zoom,"Digital zoom, from 1× to 5×. Drag the framing box to choose the crop. Double-click the slider handle to reset to 1×.");
        foreach(var control in new[]{brightness,contrast,saturation,gamma})tips.SetToolTip(control.Slider,$"Double-click the slider handle to reset to {(control==brightness?"0":control==gamma?"1.00":"100%")}. Your last manual value is kept while Auto is checked.");
        tips.SetToolTip(whiteBalance.Warmth,"Double-click the slider handle to reset warmth to 0.");tips.SetToolTip(whiteBalance.Tint,"Double-click the slider handle to reset tint to 0.");tips.SetToolTip(blurStrength,"Double-click the slider handle to reset blur strength to 50%.");
        foreach(var control in new[]{brightness,contrast,saturation,gamma})control.Changed+=(_,_)=>PublishProcessing();whiteBalance.Changed+=(_,_)=>PublishProcessing();
        framing.Changed+=(_,_)=>{if(suppress>0)return;suppress++;zoom.Value=Math.Clamp((int)Math.Round(framing.Zoom*100),100,500);suppress--;PublishProcessing();};
        zoom.ValueChanged+=(_,_)=>{if(suppress>0)return;framing.SetFrame(zoom.Value/100d,framing.CenterX,framing.CenterY,false);PublishProcessing();};
        resetFrame.Click+=(_,_)=>framing.SetFrame(1,.5,.5);resetColor.Click+=(_,_)=>{LoadProcessing(processing with{AutoBrightness=false,AutoContrast=false,AutoSaturation=false,AutoGamma=false,AutoWhiteBalance=false,Brightness=0,Contrast=1,Saturation=1,Gamma=1,Warmth=0,Tint=0});PublishProcessing();};
        rotation.SelectedIndexChanged+=(_,_)=>{ApplySourceOrientation();PublishProcessing();};mirror.CheckedChanged+=(_,_)=>PublishProcessing();eyeContact.CheckedChanged+=(_,_)=>PublishProcessing();
        tips.SetToolTip(rotation,"Auto keeps the phone image upright using its orientation sensor, including upside-down landscape. Choose an angle for a manual override. Portrait output changes webcam dimensions.");
        tips.SetToolTip(eyeContact,"NVIDIA Eye Contact redirects a detected person's gaze toward the camera. Runs locally on the GPU. Correction fades outside suitable eye/head angles. Off by default.");eyeContact.AccessibleName="Enable NVIDIA Eye Contact";
        backgroundMode.SelectedIndexChanged+=(_,_)=>{UpdateBackgroundControls();PublishProcessing();};segmentation.SelectedIndexChanged+=(_,_)=>PublishProcessing();blurStrength.ValueChanged+=(_,_)=>{blurValue.Text=$"Blur strength · {blurStrength.Value}%";PublishProcessing();};
        backgroundPreset.SelectedIndexChanged+=(_,_)=>{if(suppress>0)return;if(backgroundPreset.SelectedItem is BackgroundChoice choice){backgroundPath=choice.Id=="custom"?customBackgroundPath:choice.Id;ShowBackgroundChoice();UpdateBackgroundControls();PublishProcessing();}};
        chooseBackground.Click+=(_,_)=>{using var dialog=new OpenFileDialog{Title="Choose a replacement background",Filter="Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*"};if(dialog.ShowDialog(this)==DialogResult.OK){SetBackgroundChoice(dialog.FileName);PublishProcessing();}};
        lenses.SelectedIndexChanged+=(_,_)=>{if(suppress>0)return;PopulateResolutions(null);QueueCamera();};resolutions.SelectedIndexChanged+=(_,_)=>QueueCamera();
        webcam.CheckedChanged+=(_,_)=>{if(decoder!=null)decoder.WebcamEnabled=webcam.Checked && Program.CameraRegistered();ScheduleSave();};
        connect.Click+=async(_,_)=>await Connect();devices.SelectionChangeCommitted+=async(_,_)=>await Connect();cameraDebounce.Tick+=async(_,_)=>{cameraDebounce.Stop();await ChangeCamera();};
        openVideo.Click+=async(_,_)=>{using var dialog=new OpenFileDialog{Title="Open recorded test video",Filter="Video|*.mp4;*.mov;*.mkv;*.avi;*.webm|All files|*.*",InitialDirectory=Path.Combine(Program.Root,"samples")};if(dialog.ShowDialog(this)==DialogResult.OK)await OpenVideo(dialog.FileName);};
        settingsMenu.Items.Add(openVideo);settingsMenu.Items.Add(new ToolStripSeparator());settingsMenu.Items.Add(appearanceMenu);
        foreach(var theme in AppAppearance.Themes){var item=new ToolStripMenuItem(theme.Name){Tag=theme.Id};item.Click+=(_,_)=>SetAppearance(theme.Id);appearanceMenu.DropDownItems.Add(item);}
        settingsButton.Click+=(_,_)=>settingsMenu.Show(settingsButton,new Point(0,settingsButton.Height));connect.Tag=AppAppearance.Primary;
        setupMaxine.Click+=async(_,_)=>await SetUpRuntime(eyeContact.Checked);setupEyes.Click+=async(_,_)=>await SetUpRuntime(true);cancelSetup.Click+=(_,_)=>setupCancellation?.Cancel();restartApp.Click+=(_,_)=>{restartRequested=true;Close();};retryEffects.Click+=(_,_)=>{decoder?.ReloadEffects();RefreshRuntimeControls();};
        saveDebounce.Tick+=(_,_)=>{saveDebounce.Stop();SavePreferences();};telemetry.Tick+=(_,_)=>UpdateTelemetry();telemetry.Start();
        orientationPoll.Tick+=async(_,_)=>await PollOrientation();orientationPoll.Start();
        if(!testMode)LoadPreferences();UpdateBackgroundControls();RefreshRuntimeControls();SetAppearance(appearanceId,false);
        Shown+=OnShown;FormClosing+=OnClosing;
    }
    static FlowLayoutPanel Row()=>new(){AutoSize=true,WrapContents=false,Margin=Padding.Empty};
    static ComboBox Combo(int width)
    {
        var combo=new ComboBox{Width=width,DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(34,45,60),ForeColor=Color.FromArgb(231,237,245),Margin=new Padding(0,4,10,0),DrawMode=DrawMode.OwnerDrawFixed};
        combo.DrawItem+=(_,e)=>{if(e.Index<0)return;var theme=AppAppearance.Resolve((combo.FindForm() as MainForm)?.appearanceId);using var brush=new SolidBrush(e.State.HasFlag(DrawItemState.Selected)?theme.Selection:combo.BackColor);e.Graphics.FillRectangle(brush,e.Bounds);TextRenderer.DrawText(e.Graphics,combo.GetItemText(combo.Items[e.Index]),combo.Font,e.Bounds,combo.Enabled?combo.ForeColor:theme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);e.DrawFocusRectangle();};return combo;
    }
    static Button Button(string text,int width)=>new(){Text=text,Width=width,Height=30,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(46,104,186),ForeColor=Color.White,Margin=new Padding(0,2,8,0),Cursor=Cursors.Hand};
    static void AddLabel(FlowLayoutPanel row,string text)=>row.Controls.Add(new Label{Text=text,AutoSize=true,Margin=new Padding(0,8,7,0)});
    static void AddSide(TableLayoutPanel table,Control control,int height){int row=table.Controls.Count;table.RowStyles.Add(new RowStyle(SizeType.Absolute,height));control.Dock=DockStyle.Fill;table.Controls.Add(control,0,row);}
    Label Info(string text,string name){var label=new Label{Text="ⓘ",AutoSize=true,TabStop=true,AccessibleName=name,Margin=new Padding(8,1,0,0)};tips.SetToolTip(label,text);return label;}
    void SetStatus(string message){if(!closing)status.Text=message;}
    void SetAppearance(string id,bool save=true)
    {
        var theme=AppAppearance.Resolve(id);appearanceId=theme.Id;
        empty.Tag=AppAppearance.Preview;AppAppearance.Apply(this,theme);AppAppearance.Apply(settingsMenu,theme);
        foreach(ToolStripMenuItem item in appearanceMenu.DropDownItems)item.Checked=(string?)item.Tag==appearanceId;
        if(save)ScheduleSave();
    }
    void RefreshRuntimeControls()
    {
        if(setupBusy)return;
        var readiness=MaxineSetup.Inspect();
        bool supported=MaxineHardware.Current.Supported;
        runtimeStatus.Text=!supported?"AI effects unavailable on this configuration":restartRequired?"Setup complete · restart PixelCam to activate":setupNotice??(readiness.VideoEffectsReady?"Ready · effects start automatically":readiness.Message);
        setupMaxine.Visible=supported && !readiness.VideoEffectsReady;setupEyes.Visible=supported && !readiness.EyeContactReady;setupMaxine.Enabled=setupEyes.Enabled=!closing;
        retryEffects.Visible=readiness.VideoEffectsReady && decoder!=null && (!string.IsNullOrEmpty(decoder.BackgroundError) || !string.IsNullOrEmpty(decoder.EyeError)) && !restartRequired;
        restartApp.Visible=restartRequired;setupProgress.Visible=cancelSetup.Visible=false;
    }
    async Task SetUpRuntime(bool includeEyes)
    {
        if(setupBusy || closing)return;setupBusy=true;setupNotice=null;setupCancellation=new CancellationTokenSource();setupMaxine.Enabled=setupEyes.Enabled=false;setupProgress.Visible=cancelSetup.Visible=true;restartApp.Visible=retryEffects.Visible=false;
        var progress=new Progress<SetupProgress>(update=>{if(closing)return;runtimeStatus.Text=update.Message;setupProgress.Style=update.Percent.HasValue?ProgressBarStyle.Continuous:ProgressBarStyle.Marquee;if(update.Percent.HasValue)setupProgress.Value=Math.Clamp(update.Percent.Value,0,100);});
        string? outcome=null;
        try {
            // Hashing cached packages and checking files also stay off the UI thread.
            setupTask=Task.Run(()=>MaxineSetup.EnsureAsync(includeEyes,progress,setupCancellation.Token));var result=await setupTask;
            restartRequired|=result.RestartRecommended;if(!restartRequired)decoder?.ReloadEffects();outcome=result.Message;
        }catch(OperationCanceledException){outcome="Setup cancelled · existing effects kept";}
        catch(Exception ex){outcome=ex.Message;}
        finally{setupBusy=false;setupTask=null;setupCancellation.Dispose();setupCancellation=null;setupNotice=outcome;if(!closing)RefreshRuntimeControls();}
    }
    void SetBackgroundChoice(string path)
    {
        backgroundPath=path;if(!string.IsNullOrEmpty(path) && !BackgroundPresets.IsPreset(path))customBackgroundPath=path;
        suppress++;
        backgroundPreset.SelectedIndex=string.IsNullOrEmpty(path)?1:Math.Max(0,Array.FindIndex(BackgroundPresets.Choices,c=>c.Id==(BackgroundPresets.IsPreset(path)?path:"custom")));
        suppress--;ShowBackgroundChoice();UpdateBackgroundControls();
    }
    void ShowBackgroundChoice()=>backgroundFile.Text=BackgroundPresets.IsPreset(backgroundPath)?"Built-in background":string.IsNullOrEmpty(backgroundPath)?"No image selected":Path.GetFileName(backgroundPath);
    void UpdateBackgroundControls()
    {
        bool replacing=backgroundMode.SelectedIndex==2,custom=(backgroundPreset.SelectedItem as BackgroundChoice)?.Id=="custom";
        if(replacing && !custom && string.IsNullOrEmpty(backgroundPath))backgroundPath=(backgroundPreset.SelectedItem as BackgroundChoice)?.Id??BackgroundPresets.Default;
        blurStrength.Enabled=backgroundMode.SelectedIndex==1;backgroundPreset.Enabled=replacing;chooseBackground.Enabled=replacing && custom;chooseBackground.Visible=backgroundFile.Visible=custom;segmentation.Enabled=backgroundMode.SelectedIndex>0;
    }
    void ApplySourceOrientation()
    {
        if(suppress>0 || decoder==null)return;
        decoder.SourceRotation=videoPath=="" && rotation.SelectedIndex==4 && phoneOrientation is {OrientationSupported:true,OrientationKnown:true} && phoneOrientation.RotationDegrees is 0 or 90 or 180 or 270?phoneOrientation.RotationDegrees:0;
    }
    async Task PollOrientation()
    {
        if(pollingOrientation || closing || connecting || changing || openingVideo || decoder==null || videoPath!="" || string.IsNullOrEmpty(baseUrl) || rotation.SelectedIndex!=4)return;
        var target=decoder;string url=baseUrl;pollingOrientation=true;
        try{var next=await Program.Get<CameraStatus>(url+"/api/status");if(!closing && decoder==target && baseUrl==url && videoPath==""){phoneOrientation=next;ApplySourceOrientation();}}
        catch(HttpRequestException){}catch(TaskCanceledException){}
        finally{pollingOrientation=false;}
    }
    void PublishProcessing()
    {
        if(suppress>0 || closing)return;
        processing=new ProcessingSettings {
            AutoBrightness=brightness.IsAutomatic,AutoContrast=contrast.IsAutomatic,AutoSaturation=saturation.IsAutomatic,AutoGamma=gamma.IsAutomatic,AutoWhiteBalance=whiteBalance.Automatic.Checked,
            Brightness=brightness.Value/100d,Contrast=contrast.Value/100d,Saturation=saturation.Value/100d,Gamma=gamma.Value/100d,Warmth=whiteBalance.ManualWarmth/100d,Tint=whiteBalance.ManualTint/100d,
            Zoom=framing.Zoom,CenterX=framing.CenterX,CenterY=framing.CenterY,Rotation=rotation.SelectedIndex==4?0:rotation.SelectedIndex*90,Mirror=mirror.Checked,
            Background=(BackgroundEffect)Math.Max(0,backgroundMode.SelectedIndex),EyeContact=eyeContact.Checked,BlurStrength=blurStrength.Value/100d,SegmentationMode=Math.Max(0,segmentation.SelectedIndex),BackgroundImage=backgroundPath
        }.Bounded();
        zoomValue.Text=$"Zoom {processing.Zoom:F2}×";if(decoder!=null)decoder.Settings=processing;ScheduleSave();
    }
    void LoadProcessing(ProcessingSettings value)
    {
        suppress++;value=value.Bounded();processing=value;
        brightness.IsAutomatic=value.AutoBrightness;brightness.Value=(int)Math.Round(value.Brightness*100);contrast.IsAutomatic=value.AutoContrast;contrast.Value=(int)Math.Round(value.Contrast*100);
        saturation.IsAutomatic=value.AutoSaturation;saturation.Value=(int)Math.Round(value.Saturation*100);gamma.IsAutomatic=value.AutoGamma;gamma.Value=(int)Math.Round(value.Gamma*100);
        whiteBalance.Automatic.Checked=value.AutoWhiteBalance;whiteBalance.ManualWarmth=Math.Clamp((int)Math.Round(value.Warmth*100),-100,100);whiteBalance.ManualTint=Math.Clamp((int)Math.Round(value.Tint*100),-100,100);
        framing.SetFrame(value.Zoom,value.CenterX,value.CenterY,false);zoom.Value=Math.Clamp((int)Math.Round(value.Zoom*100),100,500);zoomValue.Text=$"Zoom {value.Zoom:F2}×";if(rotation.SelectedIndex!=4)rotation.SelectedIndex=value.Rotation/90;mirror.Checked=value.Mirror;suppress--;
        suppress++;backgroundMode.SelectedIndex=(int)value.Background;eyeContact.Checked=value.EyeContact;segmentation.SelectedIndex=value.SegmentationMode;blurStrength.Value=(int)Math.Round(value.BlurStrength*100);SetBackgroundChoice(value.BackgroundImage);suppress--;UpdateBackgroundControls();
    }
    void LoadPreferences(){try{if(File.Exists(settingsPath)){preferences=JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(settingsPath),Program.Json);if(preferences!=null){LoadProcessing(preferences.Processing);webcam.Checked=preferences.Webcam;appearanceId=AppAppearance.Resolve(preferences.Appearance).Id;suppress++;rotation.SelectedIndex=preferences.AutoOrient?4:processing.Rotation/90;suppress--;processing=processing with{Rotation=rotation.SelectedIndex==4?0:processing.Rotation};}}}catch{} }
    void ScheduleSave(){if(testMode || closing)return;saveDebounce.Stop();saveDebounce.Start();}
    void SavePreferences(){if(testMode)return;try{var lens=lenses.SelectedItem as Lens;var size=resolutions.SelectedItem as Resolution;preferences=new AppPreferences(videoPath==""?lens?.Id??preferences?.Lens??"":preferences?.Lens??"",videoPath==""?size?.Width??preferences?.Width??1280:preferences?.Width??1280,videoPath==""?size?.Height??preferences?.Height??720:preferences?.Height??720,webcam.Checked,processing,appearanceId,rotation.SelectedIndex==4);Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);File.WriteAllText(settingsPath,JsonSerializer.Serialize(preferences,Program.Json));}catch(IOException){} }
    void UpdateTelemetry()
    {
        long count=Interlocked.Read(ref received);long delta=count-previousReceived;previousReceived=count;
        if(decoder==null)return;RefreshRuntimeControls();
        metrics.Text=$"{decoder.OutputWidth} × {decoder.OutputHeight}  ·  {delta*2} fps  ·  {processing.Zoom:F2}×  ·  Webcam {(webcam.Checked?"on":"off")}"+(processing.EyeContact?" · Eyes on":"")+(rotation.SelectedIndex==4 && videoPath==""?" · Auto orient":"");
        var color=decoder.Effective;brightness.ShowAuto((int)Math.Round(color.Brightness*100));contrast.ShowAuto((int)Math.Round(color.Contrast*100));saturation.ShowAuto((int)Math.Round(color.Saturation*100));gamma.ShowAuto((int)Math.Round(color.Gamma*100));whiteBalance.ShowAuto(color.Warmth,color.Tint);
        maxineStatus.Text=!string.IsNullOrEmpty(decoder.BackgroundError)?decoder.BackgroundError:processing.Background==BackgroundEffect.Off && !processing.EyeContact?"Maxine off":$"Maxine active · {decoder.BackgroundMilliseconds:F1} ms/frame";
        if(processing.EyeContact)maxineStatus.Text+="\n"+(!string.IsNullOrEmpty(decoder.EyeError)?decoder.EyeError:decoder.EyeFaceCount>0?"Eye contact · face detected":"Eye contact · waiting for a face");
        if(!string.IsNullOrEmpty(decoder.BackgroundError))SetStatus(decoder.BackgroundError);else if(!string.IsNullOrEmpty(decoder.WebcamError))SetStatus(decoder.WebcamError);else if(!string.IsNullOrEmpty(decoder.EyeError))SetStatus(decoder.EyeError);else if(lastFrame!=DateTime.MinValue && (DateTime.UtcNow-lastFrame).TotalSeconds>4)SetStatus(videoPath!=""?"Recorded video paused.":"Video paused. Keep PixelCam open on your phone, or reconnect.");
    }
    async void OnShown(object? sender,EventArgs e)
    {
        if(args.Length>1 && args[0]=="--layout-test"){await LayoutTest(args[1]);return;}
        try {
            if(!Program.CameraRegistered())await Task.Run(Program.RegisterCamera);
            if(args.Length>1 && (args[0]=="--video-file" || args[0]=="--recorded-ui-test" || args[0]=="--eye-ui-test" || args[0]=="--options-ui-test")) {
                if(args.Contains("--blur")){backgroundMode.SelectedIndex=1;sideTabs.SelectedIndex=1;}await OpenVideo(args[1]);
                if(args[0]=="--recorded-ui-test" && args.Length>2)await TestRecordedControls(args[2]);
                if(args[0]=="--eye-ui-test" && args.Length>2)await TestEyeControls(args[2],true);
                if(args[0]=="--options-ui-test" && args.Length>2)await TestOptions(args[2]);return;
            }
            empty.Text="Open a recorded video, or connect your Pixel";SetStatus("Ready · choose a source");
            if((args.Length>1 && args[0]=="--ui-self-test") || args.Contains("--resume-phone")){await RefreshDevices();if(devices.Items.Count>0)await Connect();}
            if(args.Length>1 && args[0]=="--ui-self-test"){if(args.Contains("--eyes"))await TestEyeControls(args[1],false);else await TestControls(args[1]);}
        }catch(Exception ex){SetStatus(ex.Message);if(args.Length>1 && (args[0]=="--ui-self-test" || args[0]=="--recorded-ui-test" || args[0]=="--eye-ui-test" || args[0]=="--options-ui-test")){string report=args[0]=="--ui-self-test"?args[1]:args[2];File.WriteAllText(report,JsonSerializer.Serialize(new{passed=false,error=ex.ToString()}));Environment.ExitCode=2;Close();}}
    }
    async Task RefreshDevices()
    {
        string? selected=(devices.SelectedItem as Device)?.Serial;string output=await Program.Run(Program.Tool("adb.exe"),new[]{"devices","-l"});devices.Items.Clear();
        foreach(string line in output.Split('\n')){var parts=line.Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);if(parts.Length<2 || parts[1]!="device" || parts[0].StartsWith("emulator-"))continue;string name=parts.FirstOrDefault(p=>p.StartsWith("model:"))?.Substring(6).Replace('_',' ')??parts[0];devices.Items.Add(new Device(parts[0],name));}
        if(devices.Items.Count>0)devices.SelectedIndex=0;foreach(Device device in devices.Items)if(device.Serial==selected)devices.SelectedItem=device;
    }
    async Task StopDecoder(){var current=decoder;current?.Stop();if(decodeTask!=null){try{await decodeTask;}catch{}}current?.Dispose();decoder=null;decodeTask=null;}
    async Task Connect()
    {
        if(connecting || changing || openingVideo || closing)return;connecting=true;connect.Enabled=openVideo.Enabled=devices.Enabled=lenses.Enabled=resolutions.Enabled=false;cameraDebounce.Stop();await sourceGate.WaitAsync();
        try {
            await StopDecoder();videoPath="";phoneOrientation=null;lenses.Enabled=resolutions.Enabled=true;await RefreshDevices();if(devices.SelectedItem is not Device device)throw new IOException("No authorized phone found. Unlock your Pixel and accept USB debugging.");
            if(closing)return;
            if(port>0 && !string.IsNullOrEmpty(serial))await Program.Adb(serial,"forward","--remove","tcp:"+port);
            serial=device.Serial;SetStatus("Connecting to "+device.Label+"…");empty.Text="Opening the camera on your Pixel…";empty.Visible=true;
            string package=await Program.Adb(serial,"shell","pm","path","local.pixelcam.usb");
            bool updatePhone=string.IsNullOrWhiteSpace(package);
            if(!updatePhone){string details=await Program.Adb(serial,"shell","dumpsys","package","local.pixelcam.usb");var match=System.Text.RegularExpressions.Regex.Match(details,@"versionCode=(\d+)");updatePhone=match.Success && int.Parse(match.Groups[1].Value)<2;}
            if(updatePhone){SetStatus("Updating PixelCam on your phone…");await Program.Adb(serial,"install","-r",Path.Combine(Program.Root,"PixelCam-Android.apk"));}
            await Program.Adb(serial,"shell","am","start","-n","local.pixelcam.usb/.CameraActivity");port=int.Parse(await Program.Adb(serial,"forward","tcp:0","tcp:49737"));baseUrl="http://127.0.0.1:"+port;
            CameraStatus? cameraStatus=null;
            for(int attempt=0;attempt<90 && !closing;attempt++){
                try{cameraStatus=await Program.Get<CameraStatus>(baseUrl+"/api/status");if(cameraStatus.Permission && cameraStatus.State!="paused")break;SetStatus("Allow camera access in PixelCam on your phone.");empty.Text="Allow camera access on your Pixel";}
                catch(HttpRequestException){SetStatus("Waiting for PixelCam on your phone…");}await Task.Delay(400);
            }
            if(closing)return;if(cameraStatus==null || !cameraStatus.Permission)throw new IOException("Allow camera access on the phone, then click Connect.");phoneOrientation=cameraStatus;
            availableLenses=(await Program.Get<CameraList>(baseUrl+"/api/cameras")).Lenses;
            suppress++;lenses.Items.Clear();foreach(var lens in availableLenses)lenses.Items.Add(lens);lenses.SelectedItem=availableLenses.FirstOrDefault(l=>l.Id==(preferences?.Lens??cameraStatus.Lens))??availableLenses.FirstOrDefault();suppress--;
            PopulateResolutions(new Resolution(preferences?.Width??cameraStatus.Width,preferences?.Height??cameraStatus.Height,""));
            connecting=false;activeConfig=null;await ChangeCamera(gateHeld:true);connect.Text="Reconnect";
        }catch(Exception ex){SetStatus(ex.Message);empty.Text=ex.Message;empty.Visible=true;}
        finally{connecting=false;connect.Enabled=openVideo.Enabled=devices.Enabled=!closing;lenses.Enabled=resolutions.Enabled=!closing && videoPath=="" && availableLenses.Count>0;sourceGate.Release();}
    }
    async Task OpenVideo(string path)
    {
        if(connecting||changing||openingVideo||closing)return;openingVideo=true;connect.Enabled=openVideo.Enabled=devices.Enabled=lenses.Enabled=resolutions.Enabled=false;await sourceGate.WaitAsync();
        try {
            var video=await RecordedVideo.Probe(path);if(closing)return;cameraDebounce.Stop();pendingCamera=false;await StopDecoder();if(closing)return;
            if(port>0 && !string.IsNullOrEmpty(serial)){
                try{using var response=await Program.PostConfig(baseUrl,SelectedConfig(false));}catch{}
                try{await Program.Adb(serial,"forward","--remove","tcp:"+port);}catch{}
            }
            port=0;serial=baseUrl="";phoneOrientation=null;videoPath=video.Path;
            suppress++;devices.Items.Clear();devices.Items.Add(new Device("recorded","Recorded video"));devices.SelectedIndex=0;lenses.Items.Clear();resolutions.Items.Clear();
            var sizes=new List<Resolution>{new(video.Width,video.Height,$"{video.Width} × {video.Height}")};lenses.Items.Add(new Lens("recorded","File source","","",0,sizes));lenses.SelectedIndex=0;resolutions.Items.Add(sizes[0]);resolutions.SelectedIndex=0;lenses.Enabled=resolutions.Enabled=false;suppress--;
            activeConfig=new("recorded",video.Width,video.Height,Math.Max(1,(int)Math.Round(video.Fps)));connect.Text="Connect phone";
            var next=new Decoder(video.Path,video.Width,video.Height,recorded:true,fps:(int)Math.Round(video.Fps)){Settings=processing,WebcamEnabled=webcam.Checked && Program.CameraRegistered()};decoder=next;
            next.Preview+=bitmap=>QueueImage(bitmap,false,next);next.FullFrame+=bitmap=>QueueImage(bitmap,true,next);next.Frame+=()=>{Interlocked.Increment(ref received);lastFrame=DateTime.UtcNow;};
            next.Error+=message=>{try{if(!closing)BeginInvoke((Action)(()=>SetStatus(message)));}catch{}};decodeTask=next.Start();SetStatus("Recorded video · "+Path.GetFileName(path));
        }catch(Exception ex){SetStatus(ex.Message);empty.Text=ex.Message;empty.Visible=preview.Image==null;}
        finally{openingVideo=false;connect.Enabled=openVideo.Enabled=devices.Enabled=!closing;lenses.Enabled=resolutions.Enabled=!closing && videoPath=="" && availableLenses.Count>0;sourceGate.Release();}
    }
    void PopulateResolutions(Resolution? preferred)
    {
        preferred??=resolutions.SelectedItem as Resolution;suppress++;resolutions.Items.Clear();
        if(lenses.SelectedItem is Lens lens){foreach(var size in lens.Resolutions)resolutions.Items.Add(size);resolutions.SelectedItem=lens.Resolutions.FirstOrDefault(s=>s.Width==preferred?.Width && s.Height==preferred.Height)??lens.Resolutions.FirstOrDefault(s=>s.Width==1280)??lens.Resolutions.FirstOrDefault();}suppress--;
    }
    Config SelectedConfig(bool enabled=true){var lens=lenses.SelectedItem as Lens;var size=resolutions.SelectedItem as Resolution;return new(lens?.Id??"",size?.Width??1280,size?.Height??720,30,enabled);}
    void QueueCamera(){if(suppress>0 || closing || videoPath!="")return;cameraRevision++;pendingCamera=true;cameraDebounce.Stop();cameraDebounce.Start();ScheduleSave();}
    async Task ChangeCamera(bool gateHeld=false)
    {
        if(closing || openingVideo || string.IsNullOrEmpty(baseUrl))return;if(connecting || changing){pendingCamera=true;return;}
        if(!gateHeld){await sourceGate.WaitAsync();if(closing || openingVideo){sourceGate.Release();return;}}
        changing=true;connect.Enabled=false;
        try {
            do {
                pendingCamera=false;Config config=SelectedConfig();if(activeConfig==config && decoder!=null)continue;
                SetStatus("Switching camera…");if(preview.Image==null){empty.Text="Opening camera…";empty.Visible=true;}
                await StopDecoder();await Program.Configure(baseUrl,config);if(closing)return;
                phoneOrientation=await Program.Get<CameraStatus>(baseUrl+"/api/status");activeConfig=config;var next=new Decoder(baseUrl,config.Width,config.Height){Settings=processing,WebcamEnabled=webcam.Checked && Program.CameraRegistered()};decoder=next;ApplySourceOrientation();
                next.Preview+=bitmap=>QueueImage(bitmap,false,next);next.FullFrame+=bitmap=>QueueImage(bitmap,true,next);
                next.Frame+=()=>{Interlocked.Increment(ref received);lastFrame=DateTime.UtcNow;};next.Error+=message=>{try{if(!closing)BeginInvoke((Action)(()=>SetStatus(message)));}catch{}};
                decodeTask=next.Start();
            }while(pendingCamera && !closing);
        }catch(Exception ex){SetStatus(ex.Message);empty.Text=ex.Message;if(preview.Image==null)empty.Visible=true;activeConfig=null;}
        finally{changing=false;connect.Enabled=!closing;if(!gateHeld)sourceGate.Release();}
    }
    void QueueImage(Bitmap bitmap,bool navigator,Decoder source)
    {
        ref int pending=ref(navigator?ref pendingNavigator:ref pendingPreview);
        if(closing || Interlocked.CompareExchange(ref pending,1,0)!=0){bitmap.Dispose();return;}
        try{BeginInvoke((Action)(()=>{
            if(closing || decoder!=source){bitmap.Dispose();}else if(navigator)framing.SetImage(bitmap);else{var old=preview.Image;preview.Image=bitmap;old?.Dispose();empty.Visible=false;if(!changing && string.IsNullOrEmpty(source.WebcamError) && string.IsNullOrEmpty(source.BackgroundError) && string.IsNullOrEmpty(source.EyeError))SetStatus(videoPath!=""?"Recorded video · "+Path.GetFileName(videoPath):$"USB connected  ·  {(lenses.SelectedItem as Lens)?.Label}");}
            if(navigator)Interlocked.Exchange(ref pendingNavigator,0);else Interlocked.Exchange(ref pendingPreview,0);
        }));}catch{bitmap.Dispose();Interlocked.Exchange(ref pending,0);}
    }
    async void OnClosing(object? sender,FormClosingEventArgs e)
    {
        if(cleanupComplete)return;e.Cancel=true;if(closing)return;SavePreferences();closing=true;telemetry.Stop();cameraDebounce.Stop();saveDebounce.Stop();orientationPoll.Stop();settingsMenu.Close();setupCancellation?.Cancel();if(setupTask!=null){try{await setupTask;}catch{}}await sourceGate.WaitAsync();await StopDecoder();
        if(port>0 && !string.IsNullOrEmpty(serial)){
            try{using var deadline=new CancellationTokenSource(2000);using var response=await Program.PostConfig(baseUrl,SelectedConfig(false),deadline.Token);}catch{}
            try{await Program.Adb(serial,"forward","--remove","tcp:"+port);}catch{}
        }sourceGate.Release();cleanupComplete=true;
        if(restartRequested){var start=new ProcessStartInfo(Application.ExecutablePath){UseShellExecute=false};if(videoPath!=""){start.ArgumentList.Add("--video-file");start.ArgumentList.Add(videoPath);}else if(port>0 && !string.IsNullOrEmpty(serial))start.ArgumentList.Add("--resume-phone");Process.Start(start);}
        Close();
    }
    async Task WaitForFrames(long start){for(int i=0;i<140;i++){if(Interlocked.Read(ref received)-start>=20)return;await Task.Delay(100);}throw new IOException("The companion did not receive video frames: "+status.Text);}
    async Task WaitForCamera(){for(int i=0;i<120;i++){if(!changing && !pendingCamera && !cameraDebounce.Enabled)return;await Task.Delay(100);}throw new IOException("Camera change did not finish.");}
    async Task WaitForBackground(long start){for(int i=0;i<180;i++){if(!string.IsNullOrEmpty(decoder?.BackgroundError))throw new IOException(decoder.BackgroundError);if(decoder!=null && decoder.BackgroundFrames-start>=15)return;await Task.Delay(100);}throw new IOException("Maxine did not process frames.");}
    async Task WaitForEyes(long start,bool requireFace=true){for(int i=0;i<200;i++){if(!string.IsNullOrEmpty(decoder?.EyeError))throw new IOException(decoder.EyeError);if(!string.IsNullOrEmpty(decoder?.BackgroundError))throw new IOException(decoder.BackgroundError);if(decoder!=null && decoder.EyeFrames-start>=15 && (!requireFace || decoder.EyeFaceCount>0))return;await Task.Delay(100);}throw new IOException("Eye contact did not process a detected face.");}
    async Task TestEyeControls(string reportPath,bool recorded)
    {
        var checks=new Dictionary<string,object>{{"phoneAccessed",!recorded}};
        try
        {
            if(decoder==null)throw new IOException("No source for Eye Contact test.");
            checks["eyeOffByDefault"]=!eyeContact.Checked && !processing.EyeContact;
            await WaitForFrames(Interlocked.Read(ref received));var original=decoder;
            if(args.Contains("--expect-eye-warning"))
            {
                backgroundMode.SelectedIndex=1;eyeContact.Checked=true;
                await WaitForFrames(Interlocked.Read(ref received));
                checks["missingArShowsWarning"]=original.EyeError!="";checks["backgroundContinues"]=original.BackgroundFrames>15 && original.BackgroundError=="";
                checks["eyeNotFalselyReported"]=original.EyeFrames==0;checks["webcamContinues"]=original.WebcamFrames>15 && original.WebcamError=="";
                eyeContact.Checked=false;await WaitForFrames(Interlocked.Read(ref received));checks["offClearsEyeWarning"]=original.EyeError=="";
            }
            else
            {
                backgroundMode.SelectedIndex=0;eyeContact.Checked=true;await WaitForEyes(original.EyeFrames);checks["eyeOnlyWithoutBackground"]=original.EyeFaceFrames>0 && original.Settings.Background==BackgroundEffect.Off;
                backgroundMode.SelectedIndex=1;await WaitForEyes(original.EyeFrames);checks["eyeWithBlur"]=original.BackgroundError=="";
                string background=reportPath+".background.png";using(var image=new Bitmap(640,360)){using var g=Graphics.FromImage(image);g.Clear(Color.FromArgb(38,78,120));image.Save(background,ImageFormat.Png);}
                backgroundPath=background;backgroundMode.SelectedIndex=2;await WaitForEyes(original.EyeFrames);checks["eyeWithReplacement"]=original.BackgroundError=="";
                brightness.IsAutomatic=false;brightness.Value=10;framing.SetFrame(1.4,.55,.5);rotation.SelectedIndex=1;mirror.Checked=true;await WaitForEyes(original.EyeFrames);checks["eyeWithLiveColorFramingRotation"]=original.Settings.Zoom==1.4 && original.Settings.Rotation==90 && original.Settings.Mirror;
                LoadProcessing(new ProcessingSettings{EyeContact=true,Background=BackgroundEffect.Blur});PublishProcessing();
                long eyeBefore=original.EyeFrames;eyeContact.Checked=false;await WaitForFrames(Interlocked.Read(ref received));checks["eyeOffStopsProcessing"]=original.EyeFrames-eyeBefore<=2 && original.EyeError=="";
                eyeContact.Checked=true;await WaitForEyes(original.EyeFrames);checks["eyeOnResumes"]=original.EyeFaceCount>0;
                checks["noDecoderRestartForEyes"]=decoder==original && original.ProcessLaunches==1;
                if(!recorded)
                {
                    lenses.SelectedItem=availableLenses.First(l=>l.EquivalentMm==24);resolutions.SelectedItem=((Lens)lenses.SelectedItem!).Resolutions.First(r=>r.Width==1280);await WaitForCamera();await WaitForEyes(decoder!.EyeFrames,false);
                    resolutions.SelectedItem=((Lens)lenses.SelectedItem!).Resolutions.First(r=>r.Width==1920);await WaitForCamera();await WaitForEyes(decoder!.EyeFrames);
                    checks["eyeContextRecreatedForResolution"]=decoder!=original && decoder!.EyeFaceCount>0;
                }
                var stable=decoder!;long begin=stable.Frames,eyeStart=stable.EyeFrames,faceStart=stable.EyeFaceFrames;var watch=Stopwatch.StartNew();var times=new List<double>();int launches=stable.ProcessLaunches;
                for(int i=0;i<12;i++){await Task.Delay(2500);if(stable.EyeError!="" || stable.BackgroundError!="")throw new IOException(stable.EyeError+stable.BackgroundError);times.Add(stable.BackgroundMilliseconds);}
                double elapsed=watch.Elapsed.TotalSeconds;long frames=stable.Frames-begin;
                checks["combinedStability"]=new{seconds=Math.Round(elapsed,2),frames,fps=Math.Round(frames/elapsed,2),eyeFrames=stable.EyeFrames-eyeStart,faceFrames=stable.EyeFaceFrames-faceStart,averageEffectsMs=times.Average(),decoderLaunches=stable.ProcessLaunches,eyeError=stable.EyeError,backgroundError=stable.BackgroundError};
                checks["combinedRealtime"]=frames/elapsed>25 && stable.EyeFrames-eyeStart>600 && stable.EyeFaceFrames-faceStart>100 && stable.ProcessLaunches==launches;
                checks["webcamWorking"]=stable.WebcamFrames>30 && stable.WebcamError=="";
                sideTabs.SelectedIndex=1;
            }
            checks["passed"]=checks.Where(x=>x.Value is bool && x.Key!="phoneAccessed").All(x=>(bool)x.Value);
        }
        catch(Exception ex){checks["passed"]=false;checks["error"]=ex.ToString();}
        if(!(bool)checks["passed"])Environment.ExitCode=2;
        File.WriteAllText(reportPath,JsonSerializer.Serialize(checks,new JsonSerializerOptions(Program.Json){WriteIndented=true}));Close();
    }
    async Task TestRecordedControls(string reportPath)
    {
        var checks=new Dictionary<string,object>{{"phoneAccessed",false},{"source",videoPath}};
        try {
            if(decoder==null)throw new IOException("Recorded video did not open: "+status.Text);
            var original=decoder;await WaitForFrames(Interlocked.Read(ref received));
            checks["allFiveManualByDefault"]=!brightness.IsAutomatic && !contrast.IsAutomatic && !saturation.IsAutomatic && !gamma.IsAutomatic && !whiteBalance.Automatic.Checked;
            backgroundMode.SelectedIndex=1;await WaitForBackground(original.BackgroundFrames);checks["blurProcessed"]=original.BackgroundFrames>0;
            blurStrength.Value=80;await WaitForBackground(original.BackgroundFrames);checks["liveBlurStrength"]=original.Settings.BlurStrength==.8;
            string replacement=reportPath+".background.png";using(var image=new Bitmap(640,360)){using var g=Graphics.FromImage(image);g.Clear(Color.FromArgb(38,78,120));image.Save(replacement,ImageFormat.Png);}
            backgroundPath=replacement;backgroundFile.Text=Path.GetFileName(replacement);backgroundMode.SelectedIndex=2;await WaitForBackground(original.BackgroundFrames);checks["replacementProcessed"]=original.Settings.Background==BackgroundEffect.Replace;
            foreach(int mode in new[]{1,2,3,0}){segmentation.SelectedIndex=mode;await WaitForBackground(original.BackgroundFrames);}checks["allFourMaskModes"]=true;
            brightness.IsAutomatic=false;brightness.Value=15;contrast.IsAutomatic=false;contrast.Value=115;saturation.IsAutomatic=false;saturation.Value=110;gamma.IsAutomatic=false;gamma.Value=110;whiteBalance.Automatic.Checked=false;whiteBalance.Warmth.Value=10;
            framing.SetFrame(1.6,.5,.5);framing.TestDrag(.12f,-.08f);await WaitForFrames(Interlocked.Read(ref received));
            checks["colorAndFramingWithMaxine"]=original.Settings.Brightness==.15 && original.Settings.Zoom==1.6 && original.Settings.CenterX>.5;
            rotation.SelectedIndex=1;mirror.Checked=true;await WaitForFrames(Interlocked.Read(ref received));checks["liveRotationAndMirror"]=preview.Image!=null && activeConfig!=null && Math.Abs(preview.Image.Width/(double)preview.Image.Height-activeConfig.Height/(double)activeConfig.Width)<.005;
            rotation.SelectedIndex=0;mirror.Checked=false;backgroundMode.SelectedIndex=0;await WaitForFrames(Interlocked.Read(ref received));checks["effectOffRestoresOriginal"]=original.Settings.Background==BackgroundEffect.Off && original.BackgroundError=="";
            backgroundMode.SelectedIndex=1;await WaitForBackground(original.BackgroundFrames);
            long begin=Interlocked.Read(ref received);var watch=Stopwatch.StartNew();await Task.Delay(3200);checks["playbackFps"]=Math.Round((Interlocked.Read(ref received)-begin)/watch.Elapsed.TotalSeconds,2);
            checks["noDecoderRestart"]=decoder==original && original.ProcessLaunches==1;checks["webcamFrames"]=original.WebcamFrames;checks["maxineFrames"]=original.BackgroundFrames;checks["maxineMs"]=original.BackgroundMilliseconds;
            checks["webcamError"]=original.WebcamError;checks["backgroundError"]=original.BackgroundError;
            string reopen=videoPath;Task firstOpen=OpenVideo(reopen);Task secondOpen=OpenVideo(reopen);await Task.WhenAll(firstOpen,secondOpen);await WaitForFrames(Interlocked.Read(ref received));
            checks["rapidRecordedOpenSerialized"]=decoder!=null && decoder.ProcessLaunches==1 && !openingVideo && !connecting && videoPath==reopen;
            sideTabs.SelectedIndex=1;await Task.Delay(200);using(var capture=new Bitmap(Width,Height)){DrawToBitmap(capture,new Rectangle(0,0,Width,Height));capture.Save(reportPath+".ui.png",ImageFormat.Png);}
            checks["passed"]=checks.Where(x=>x.Value is bool && x.Key!="phoneAccessed").All(x=>(bool)x.Value) && original.WebcamFrames>30 && original.BackgroundError=="" && original.WebcamError=="" && (double)checks["playbackFps"]>25;
        }catch(Exception ex){checks["passed"]=false;checks["error"]=ex.ToString();}
        if(!(bool)checks["passed"])Environment.ExitCode=2;
        File.WriteAllText(reportPath,JsonSerializer.Serialize(checks,new JsonSerializerOptions(Program.Json){WriteIndented=true}));Close();
    }
    async Task TestControls(string reportPath)
    {
        var checks=new Dictionary<string,object>();
        try {
            if(decoder==null)throw new IOException("Connection failed: "+status.Text);
            checks["allFiveManualByDefault"]=!brightness.IsAutomatic && !contrast.IsAutomatic && !saturation.IsAutomatic && !gamma.IsAutomatic && !whiteBalance.Automatic.Checked;
            await WaitForFrames(Interlocked.Read(ref received));checks["initialPreview"]=true;
            var original=decoder;int processCount=original.ProcessLaunches;
            brightness.IsAutomatic=false;brightness.Value=20;contrast.IsAutomatic=false;contrast.Value=120;saturation.IsAutomatic=false;saturation.Value=130;gamma.IsAutomatic=false;gamma.Value=115;whiteBalance.Automatic.Checked=false;whiteBalance.Warmth.Value=15;whiteBalance.Tint.Value=-10;
            framing.SetFrame(2,.5,.5);framing.TestDrag(.16f,-.10f);await WaitForFrames(Interlocked.Read(ref received));
            checks["manualColorLive"]=decoder==original && original.Settings.Brightness==.2 && original.Settings.Contrast==1.2 && original.Settings.Saturation==1.3 && original.Settings.Gamma==1.15 && original.Settings.Warmth==.15;
            checks["framingDragLive"]=original.Settings.Zoom==2 && original.Settings.CenterX>.6 && original.Settings.CenterY<.45;
            checks["noRestartForColorAndFraming"]=original.ProcessLaunches==processCount && decoder==original;
            rotation.SelectedIndex=1;mirror.Checked=true;await WaitForFrames(Interlocked.Read(ref received));checks["liveRotationAndMirror"]=preview.Image!=null && activeConfig!=null && Math.Abs(preview.Image.Width/(double)preview.Image.Height-activeConfig.Height/(double)activeConfig.Width)<.005 && decoder==original;
            lenses.SelectedItem=availableLenses.Last();await WaitForCamera();await WaitForFrames(Interlocked.Read(ref received));
            resolutions.SelectedItem=((Lens)lenses.SelectedItem!).Resolutions.First(s=>s.Width==1920);await WaitForCamera();await WaitForFrames(Interlocked.Read(ref received));
            var cameraStatus=await Program.Get<CameraStatus>(baseUrl+"/api/status");checks["automaticLensAndResolutionApply"]=cameraStatus.Lens==availableLenses.Last().Id && cameraStatus.Width==1920 && cameraStatus.Height==1080;
            if(args.Contains("--maxine"))await TestLiveMaxine(checks,reportPath);
            checks["webcamFrames"]=decoder?.WebcamFrames??0;checks["processingMs"]=decoder?.ProcessingMilliseconds??0;
            checks["passed"]=checks.Where(x=>x.Value is bool).All(x=>(bool)x.Value) && decoder?.WebcamFrames>0;
        }catch(Exception ex){checks["passed"]=false;checks["error"]=ex.ToString();}
        File.WriteAllText(reportPath,JsonSerializer.Serialize(checks,new JsonSerializerOptions(Program.Json){WriteIndented=true}));Close();
    }
    async Task TestLiveMaxine(Dictionary<string,object> checks,string reportPath)
    {
        // Live diagnostics save metrics only, never camera images.
        LoadProcessing(new ProcessingSettings());PublishProcessing();sideTabs.SelectedIndex=1;
        var original=decoder??throw new IOException("No live decoder.");
        backgroundMode.SelectedIndex=1;await WaitForBackground(original.BackgroundFrames);checks["liveMaxineBlur"]=original.BackgroundError=="";
        blurStrength.Value=80;await WaitForBackground(original.BackgroundFrames);checks["liveBlurStrength"]=original.Settings.BlurStrength==.8;
        string replacement=reportPath+".background.png";
        using(var image=new Bitmap(640,360)){using var g=Graphics.FromImage(image);g.Clear(Color.FromArgb(38,78,120));image.Save(replacement,ImageFormat.Png);}
        backgroundPath=replacement;backgroundFile.Text=Path.GetFileName(replacement);backgroundMode.SelectedIndex=2;await WaitForBackground(original.BackgroundFrames);checks["liveMaxineReplacement"]=original.BackgroundError=="";
        foreach(int mode in new[]{1,2,3,0}){segmentation.SelectedIndex=mode;await WaitForBackground(original.BackgroundFrames);}checks["liveAllMaskModes"]=true;
        brightness.IsAutomatic=false;brightness.Value=10;contrast.IsAutomatic=false;contrast.Value=110;framing.SetFrame(1.5,.55,.5);mirror.Checked=true;rotation.SelectedIndex=1;
        await WaitForBackground(original.BackgroundFrames);checks["maxineWithColorFramingRotation"]=original.Settings.Brightness==.1 && original.Settings.Zoom==1.5 && original.Settings.Mirror && preview.Image!=null && preview.Image.Height>preview.Image.Width;
        checks["noDecoderRestartForLiveEffects"]=original==decoder && original.ProcessLaunches==1;
        LoadProcessing(new ProcessingSettings{Background=BackgroundEffect.Blur});PublishProcessing();
        var matrix=new List<object>();
        foreach(var lens in availableLenses){
            lenses.SelectedItem=lens;
            foreach(var size in lens.Resolutions){
                resolutions.SelectedItem=size;await WaitForCamera();var current=decoder??throw new IOException("No decoder after camera change.");
                await WaitForBackground(current.BackgroundFrames);var actual=await Program.Get<CameraStatus>(baseUrl+"/api/status");
                bool matched=actual.Lens==lens.Id && actual.Width==size.Width && actual.Height==size.Height;
                if(!matched || current.BackgroundError!="" || current.WebcamError!="")throw new IOException("Live camera/Maxine mismatch for "+lens.Label+" "+size.Label);
                matrix.Add(new{lens=lens.Label,size.Width,size.Height,maxineFrames=current.BackgroundFrames,maxineMs=current.BackgroundMilliseconds,webcamFrames=current.WebcamFrames,passed=true});
                checks["maxineLensResolutionMatrix"]=matrix;
                File.WriteAllText(reportPath+".progress.json",JsonSerializer.Serialize(checks,new JsonSerializerOptions(Program.Json){WriteIndented=true}));
            }
        }
        checks["allLensesAndResolutionsWithMaxine"]=matrix.Count==availableLenses.Sum(l=>l.Resolutions.Count);
        lenses.SelectedItem=availableLenses.First(l=>l.EquivalentMm==24);
        resolutions.SelectedItem=((Lens)lenses.SelectedItem!).Resolutions.First(s=>s.Width==1920);await WaitForCamera();
        var stable=decoder??throw new IOException("No decoder for stability test.");await WaitForBackground(stable.BackgroundFrames);
        SetStatus("Final live test · 1080p blur and webcam output");
        long first=stable.Frames,backgroundFirst=stable.BackgroundFrames,published=stable.WebcamFrames;var watch=Stopwatch.StartNew();var times=new List<double>();
        int launches=stable.ProcessLaunches;
        for(int i=0;i<12;i++){await Task.Delay(2500);if(stable.BackgroundError!="" || stable.WebcamError!="")throw new IOException(stable.BackgroundError+stable.WebcamError);times.Add(stable.BackgroundMilliseconds);}
        double elapsed=watch.Elapsed.TotalSeconds;long count=stable.Frames-first;
        checks["liveStability"]=new{seconds=Math.Round(elapsed,2),frames=count,fps=Math.Round(count/elapsed,2),maxineFrames=stable.BackgroundFrames-backgroundFirst,webcamFrames=stable.WebcamFrames-published,averageSampledMaxineMs=times.Average(),decoderLaunches=stable.ProcessLaunches,backgroundError=stable.BackgroundError,webcamError=stable.WebcamError};
        checks["stableLiveProcessing"]=count/elapsed>25 && stable.BackgroundFrames-backgroundFirst>600 && stable.WebcamFrames-published>600 && stable.ProcessLaunches==launches;
        backgroundMode.SelectedIndex=0;await WaitForFrames(Interlocked.Read(ref received));checks["liveOffRestoresOriginal"]=stable.BackgroundError=="";
        backgroundMode.SelectedIndex=1;await WaitForBackground(stable.BackgroundFrames);checks["liveEffectCanResume"]=stable.BackgroundError=="";
    }
    async Task TestOptions(string reportPath)
    {
        var checks=new Dictionary<string,object>{{"phoneAccessed",false}};
        try {
            await WaitForFrames(Interlocked.Read(ref received));var original=decoder??throw new IOException("No recorded source.");int launches=original.ProcessLaunches;
            sideTabs.SelectedIndex=1;backgroundMode.SelectedIndex=2;
            foreach(var choice in BackgroundPresets.Choices.Where(c=>c.Id!="custom")) {
                long before=original.BackgroundFrames;backgroundPreset.SelectedIndex=Array.FindIndex(BackgroundPresets.Choices,c=>c.Id==choice.Id);await WaitForBackground(before);
                checks[choice.Id]=original.Settings.BackgroundImage==choice.Id && original.BackgroundError=="";
            }
            brightness.Value=23;brightness.IsAutomatic=true;brightness.ShowAuto(-17);whiteBalance.ManualWarmth=-27;whiteBalance.ManualTint=13;whiteBalance.Automatic.Checked=true;whiteBalance.ShowAuto(.61,-.42);mirror.Checked=!mirror.Checked;
            checks["unrelatedControlKeepsManualColors"]=processing.Brightness==.23 && processing.Warmth==-.27 && processing.Tint==.13;
            brightness.IsAutomatic=false;whiteBalance.Automatic.Checked=false;
            checks["autoOffRestoresLastManual"]=brightness.Value==23 && brightness.Slider.Value==23 && whiteBalance.Warmth.Value==-27 && whiteBalance.Tint.Value==13;
            LoadProcessing(processing with{Brightness=0,Warmth=0,Tint=0,Mirror=false});PublishProcessing();
            long beforeReload=original.BackgroundFrames;await SetUpRuntime(true);await WaitForBackground(beforeReload);
            checks["setupRecognizesReadyInsideApp"]=MaxineSetup.Inspect() is {VideoEffectsReady:true,EyeContactReady:true} && !setupBusy && !restartRequired;
            checks["retryWithoutDecoderRestart"]=original.ProcessLaunches==launches && original.BackgroundError=="";
            checks["openVideoInSettings"]=settingsMenu.Items.Contains(openVideo);
            foreach(var theme in AppAppearance.Themes) {
                SetAppearance(theme.Id);sideTabs.SelectedIndex=0;await Task.Delay(100);
                checks["theme:"+theme.Id]=BackColor==theme.Window && sideTabs.Theme.Id==theme.Id && appearanceMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(i=>(string?)i.Tag==theme.Id).Checked;
                using var capture=new Bitmap(Width,Height);DrawToBitmap(capture,new Rectangle(0,0,Width,Height));capture.Save(reportPath+"."+theme.Id+".png",ImageFormat.Png);
            }
            checks["iconPresent"]=Icon!=null;checks["liveControlsWithoutDecoderRestart"]=decoder==original && original.ProcessLaunches==launches;checks["webcamWorking"]=original.WebcamFrames>15 && original.WebcamError=="";
            checks["passed"]=checks.Where(p=>p.Key!="phoneAccessed").All(p=>p.Value is bool b && b);
        }catch(Exception ex){checks["passed"]=false;checks["error"]=ex.ToString();}
        File.WriteAllText(reportPath,JsonSerializer.Serialize(checks,new JsonSerializerOptions(Program.Json){WriteIndented=true}));if(!(bool)checks["passed"])Environment.ExitCode=2;Close();
    }
    async Task LayoutTest(string path)
    {
        suppress++;devices.Items.Add(new Device("test","Pixel 8 Pro"));devices.SelectedIndex=0;
        var sizes=new List<Resolution>{new(1280,720,"1280 × 720"),new(1920,1080,"1920 × 1080")};lenses.Items.Add(new Lens("test","Wide · 1×","0","2",24,sizes));lenses.SelectedIndex=0;suppress--;PopulateResolutions(null);
        using var image=new Bitmap(1280,720);using(var graphics=Graphics.FromImage(image)){graphics.Clear(Color.FromArgb(28,46,64));for(int x=0;x<1280;x+=160)using(var brush=new SolidBrush(Color.FromArgb(40+x/7,85+x/12,120+x/20)))graphics.FillRectangle(brush,x,180,160,360);}
        preview.Image=(Bitmap)image.Clone();framing.SetImage((Bitmap)image.Clone());framing.SetFrame(1.7,.58,.5);empty.Visible=false;connect.Text="Reconnect";status.Text="USB connected · Wide · 1×";metrics.Text="1920 × 1080 · 30 fps · Webcam on";
        if(args.Contains("--background-tab"))sideTabs.SelectedIndex=1;
        await Task.Delay(250);using var capture=new Bitmap(Width,Height);DrawToBitmap(capture,new Rectangle(0,0,Width,Height));capture.Save(path,ImageFormat.Png);Close();
    }
}
