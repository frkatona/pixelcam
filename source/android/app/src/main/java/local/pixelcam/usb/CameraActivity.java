package local.pixelcam.usb;

import android.Manifest;
import android.app.Activity;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.hardware.SensorManager;
import android.hardware.camera2.*;
import android.hardware.camera2.params.*;
import android.media.*;
import android.os.*;
import android.util.*;
import android.view.*;
import android.widget.*;
import org.json.*;
import java.io.*;
import java.net.*;
import java.nio.ByteBuffer;
import java.util.*;
import java.util.concurrent.*;

/** USB-only camera sender. All camera lifecycle operations run on cameraHandler. */
public final class CameraActivity extends Activity {
    static final int PORT = 49737;
    final Handler ui = new Handler(Looper.getMainLooper());
    HandlerThread cameraThread;
    Handler cameraHandler;
    CameraManager manager;
    CameraDevice camera;
    CameraCaptureSession session;
    MediaCodec encoder;
    Surface encoderSurface;
    volatile String state = "starting", error = "";
    volatile boolean resumed, enabled = true, alive = true, serverReady;
    volatile int width = 1280, height = 720, fps = 30, generation;
    volatile String selectedLens = "";
    volatile long frames, bytes, lastFrameTime;
    volatile byte[] codecConfig;
    final List<Lens> lenses = new ArrayList<>();
    final CopyOnWriteArrayList<Subscriber> subscribers = new CopyOnWriteArrayList<>();
    final Set<Socket> connections = ConcurrentHashMap.newKeySet();
    final ExecutorService network = Executors.newFixedThreadPool(8);
    volatile ServerSocket server;
    TextView title, facts, connection;
    Button toggle;
    OrientationEventListener orientationListener;
    volatile boolean orientationSupported, orientationKnown;
    volatile int deviceOrientation;
    int pendingOrientation = -1, lastRawOrientation = OrientationEventListener.ORIENTATION_UNKNOWN;
    final Runnable commitOrientation = () -> {
        if (!resumed || pendingOrientation < 0 || lastRawOrientation == OrientationEventListener.ORIENTATION_UNKNOWN) return;
        // A candidate must remain near its quadrant centre for 250 ms. A flat
        // phone cancels the candidate and retains its last useful upright angle.
        if (angleDistance(lastRawOrientation,pendingOrientation) <= 35) {
            deviceOrientation=pendingOrientation; orientationKnown=true;
        }
        pendingOrientation=-1;
    };

    static final class Lens {
        String id, cameraId, physicalId, label;
        float equivalent, focal;
        CameraCharacteristics characteristics;
        List<Size> sizes = new ArrayList<>();
        JSONObject json() throws JSONException {
            JSONObject value = new JSONObject().put("id", id).put("label", label)
                .put("cameraId", cameraId).put("physicalId", physicalId == null ? "" : physicalId)
                .put("equivalentMm", Math.round(equivalent)).put("sensorOrientation",sensorOrientation());
            JSONArray dimensions = new JSONArray();
            for (Size size : sizes) dimensions.put(new JSONObject().put("width",size.getWidth()).put("height",size.getHeight())
                .put("label",size.getWidth()+" × "+size.getHeight()));
            return value.put("resolutions", dimensions);
        }
        int sensorOrientation() {
            Integer degrees=characteristics.get(CameraCharacteristics.SENSOR_ORIENTATION);
            return degrees==null?0:degrees;
        }
    }
    static final class Subscriber {
        final Socket socket;
        final ArrayBlockingQueue<byte[]> packets = new ArrayBlockingQueue<>(45);
        volatile boolean closed;
        Subscriber(Socket socket) { this.socket = socket; }
        void close() { closed = true; try { socket.close(); } catch (IOException ignored) {} }
    }

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        layout.setPadding(28,28,28,28);
        layout.setBackgroundColor(Color.rgb(17,23,32));
        title = text("PixelCam",24); layout.addView(title);
        connection = text("USB camera · waiting for the PC",16); layout.addView(connection);
        facts = text("",16); LinearLayout.LayoutParams factsParams = new LinearLayout.LayoutParams(-1,-2); factsParams.topMargin=28; layout.addView(facts,factsParams);
        toggle = new Button(this); toggle.setText("Stop camera"); layout.addView(toggle);
        toggle.setOnClickListener(v -> {
            if (!permission()) { requestPermissions(new String[]{Manifest.permission.CAMERA},1); return; }
            enabled = !enabled;
            cameraHandler.post(() -> { if (enabled) restart(); else stop("stopped"); });
        });
        layout.addView(text("Choose the rear lens and video resolution in PixelCam on your PC. Keep this app open while using the camera.",15));
        setContentView(layout);
        manager = (CameraManager)getSystemService(CAMERA_SERVICE);
        cameraThread = new HandlerThread("PixelCamCamera"); cameraThread.start();
        cameraHandler = new Handler(cameraThread.getLooper());
        try { discover(); } catch(Exception ex) { fail(ex); }
        orientationListener=new OrientationEventListener(this,SensorManager.SENSOR_DELAY_NORMAL) {
            @Override public void onOrientationChanged(int degrees) { updateOrientation(degrees); }
        };
        orientationSupported=orientationListener.canDetectOrientation();
        new Thread(this::serve,"PixelCamUsbServer").start();
        ui.post(tick);
        if (!permission()) requestPermissions(new String[]{Manifest.permission.CAMERA},1);
    }
    TextView text(String value,int sp) {
        TextView view = new TextView(this); view.setText(value); view.setTextSize(sp);
        view.setTextColor(Color.rgb(231,237,245)); view.setPadding(0,8,0,12); return view;
    }
    boolean permission() { return checkSelfPermission(Manifest.permission.CAMERA)==PackageManager.PERMISSION_GRANTED; }
    @Override protected void onResume() {
        super.onResume(); resumed=true;
        if(orientationListener!=null && orientationSupported) orientationListener.enable();
        if(cameraHandler!=null) cameraHandler.post(this::restart);
    }
    @Override protected void onPause() {
        resumed=false;
        if(orientationListener!=null) orientationListener.disable();
        ui.removeCallbacks(commitOrientation);pendingOrientation=-1;lastRawOrientation=OrientationEventListener.ORIENTATION_UNKNOWN;
        if(cameraHandler!=null) cameraHandler.post(() -> stop("paused")); super.onPause();
    }
    static int angleDistance(int a,int b) { int delta=Math.abs(a-b)%360;return Math.min(delta,360-delta); }
    void updateOrientation(int degrees) {
        lastRawOrientation=degrees;
        if(degrees==OrientationEventListener.ORIENTATION_UNKNOWN) {
            ui.removeCallbacks(commitOrientation);pendingOrientation=-1;return;
        }
        // A 10-degree dead band around the halfway point avoids rapid flips.
        if(orientationKnown && angleDistance(degrees,deviceOrientation)<=55) {
            ui.removeCallbacks(commitOrientation);pendingOrientation=-1;return;
        }
        int nearest=((degrees+45)/90*90)%360;
        if(angleDistance(degrees,nearest)>35) {
            ui.removeCallbacks(commitOrientation);pendingOrientation=-1;return;
        }
        if(nearest!=pendingOrientation) {
            ui.removeCallbacks(commitOrientation);pendingOrientation=nearest;ui.postDelayed(commitOrientation,250);
        }
    }
    @Override public void onRequestPermissionsResult(int request,String[] permissions,int[] results) {
        super.onRequestPermissionsResult(request,permissions,results);
        if(permission()) cameraHandler.post(this::restart); else { state="permission"; error="Allow camera access on the phone."; }
    }
    final Runnable tick = new Runnable() { public void run() {
        if(!alive) return;
        Lens lens = findLens(selectedLens);
        connection.setText(state.equals("streaming") ? (subscribers.isEmpty() ? "Camera ready · waiting for the PC" : "● Streaming over USB") : "Camera · "+state);
        facts.setText((lens==null?"Rear camera":lens.label)+"\n"+width+" × "+height+" · "+fps+" fps\n"+(error.isEmpty()?"":error));
        toggle.setText(permission() ? (enabled?"Stop camera":"Start camera") : "Allow camera access");
        ui.postDelayed(this,600);
    }};

    void discover() throws Exception {
        Set<String> children = new HashSet<>();
        for(String id:manager.getCameraIdList()) {
            CameraCharacteristics c=manager.getCameraCharacteristics(id);
            if(Integer.valueOf(CameraCharacteristics.LENS_FACING_BACK).equals(c.get(CameraCharacteristics.LENS_FACING))) children.addAll(c.getPhysicalCameraIds());
        }
        for(String id:manager.getCameraIdList()) {
            CameraCharacteristics c=manager.getCameraCharacteristics(id);
            if(!Integer.valueOf(CameraCharacteristics.LENS_FACING_BACK).equals(c.get(CameraCharacteristics.LENS_FACING))) continue;
            Set<String> physical=c.getPhysicalCameraIds();
            if(!physical.isEmpty()) { for(String p:physical) addLens(id,p,manager.getCameraCharacteristics(p)); }
            else if(!children.contains(id)) addLens(id,null,c);
        }
        Collections.sort(lenses,(a,b)->Float.compare(a.equivalent,b.equivalent));
        // Some Pixels expose cropped variants of the same sensor as extra physical IDs.
        // Keep one full-field option per optical lens.
        Set<Integer> opticalFocals = new HashSet<>();
        Iterator<Lens> opticalIterator = lenses.iterator();
        while(opticalIterator.hasNext()) { Lens candidate=opticalIterator.next(); if(!opticalFocals.add(Math.round(candidate.focal*100))) opticalIterator.remove(); }
        Lens main=null;
        for(Lens lens:lenses) if(main==null || Math.abs(lens.equivalent-24)<Math.abs(main.equivalent-24)) main=lens;
        for(Lens lens:lenses) {
            float ratio=lens.equivalent/(main==null?24:main.equivalent);
            String name=ratio<0.8f?"Ultra-wide":ratio>1.6f?"Telephoto":"Wide";
            lens.label=String.format(Locale.US,"%s · %s×",name,ratio>2 || Math.abs(ratio-Math.round(ratio))<0.12?String.valueOf(Math.round(ratio)):String.format(Locale.US,"%.1f",ratio));
        }
        if(main!=null) { selectedLens=main.id; Size size=main.sizes.get(0); for(Size s:main.sizes) if(s.getWidth()==1280 && s.getHeight()==720) size=s; width=size.getWidth(); height=size.getHeight(); }
        else throw new IOException("No rear cameras with video output are available.");
    }
    void addLens(String cameraId,String physicalId,CameraCharacteristics c) {
        try {
            Lens lens=new Lens(); lens.cameraId=cameraId; lens.physicalId=physicalId; lens.id=cameraId+(physicalId==null?"":":"+physicalId); lens.characteristics=c;
            float[] focals=c.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS);
            lens.focal=(focals!=null && focals.length>0)?focals[0]:0;
            SizeF sensor=c.get(CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE);
            lens.equivalent=(focals!=null && focals.length>0 && sensor!=null)?focals[0]*43.266f/(float)Math.hypot(sensor.getWidth(),sensor.getHeight()):24;
            StreamConfigurationMap map=c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
            Size[] available=map==null?null:map.getOutputSizes(MediaCodec.class);
            if(available==null) return;
            int[][] preferred={{640,480},{1280,720},{1920,1080},{3840,2160}};
            for(int[] dimensions:preferred) for(Size size:available) if(size.getWidth()==dimensions[0] && size.getHeight()==dimensions[1]) { lens.sizes.add(size); break; }
            if(!lens.sizes.isEmpty()) lenses.add(lens);
        } catch(Exception ignored) {}
    }
    Lens findLens(String id) { for(Lens lens:lenses) if(lens.id.equals(id)) return lens; return null; }
    void restart() {
        stop("starting"); error="";
        if(!serverReady) { state="connecting"; return; }
        if(!permission()) { state="permission"; return; }
        if(!resumed) { state="paused"; return; }
        if(!enabled) { state="stopped"; return; }
        final Lens lens=findLens(selectedLens);
        if(lens==null) { state="error"; error="No rear camera selected."; return; }
        final int g=generation;
        try {
            MediaFormat format=MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC,width,height);
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT,MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
            format.setInteger(MediaFormat.KEY_BIT_RATE,Math.max(2500000, Math.min(28000000,width*height*5)));
            format.setInteger(MediaFormat.KEY_FRAME_RATE,fps);
            format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL,1);
            MediaCodec codec=MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC); encoder=codec;
            codec.setCallback(new MediaCodec.Callback() {
                public void onInputBufferAvailable(MediaCodec mc,int index) {}
                public void onOutputFormatChanged(MediaCodec mc,MediaFormat output) {
                    if(g!=generation) return;
                    ByteArrayOutputStream csd=new ByteArrayOutputStream();
                    for(String key:new String[]{"csd-0","csd-1"}) if(output.containsKey(key)) { ByteBuffer buffer=output.getByteBuffer(key).duplicate(); byte[] data=new byte[buffer.remaining()]; buffer.get(data); csd.write(data,0,data.length); }
                    codecConfig=csd.toByteArray();
                }
                public void onError(MediaCodec mc,MediaCodec.CodecException ex) { if(g==generation) fail(ex); }
                public void onOutputBufferAvailable(MediaCodec mc,int index,MediaCodec.BufferInfo info) {
                    if(g!=generation) return;
                    try {
                        ByteBuffer buffer=mc.getOutputBuffer(index);
                        if(buffer!=null && info.size>0) {
                            buffer.position(info.offset); buffer.limit(info.offset+info.size);
                            byte[] data=new byte[info.size]; buffer.get(data);
                            if((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG)!=0) codecConfig=data;
                            else {
                                byte[] config=codecConfig;
                                if((info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME)!=0 && config!=null) {
                                    byte[] joined=new byte[config.length+data.length]; System.arraycopy(config,0,joined,0,config.length); System.arraycopy(data,0,joined,config.length,data.length); data=joined;
                                }
                                frames++; bytes+=data.length; lastFrameTime=SystemClock.elapsedRealtime();
                                for(Subscriber subscriber:subscribers) if(!subscriber.packets.offer(data)) { subscriber.close(); subscribers.remove(subscriber); }
                            }
                        }
                        mc.releaseOutputBuffer(index,false);
                    } catch(Exception ex) { if(g==generation) fail(ex); }
                }
            },cameraHandler);
            codec.configure(format,null,null,MediaCodec.CONFIGURE_FLAG_ENCODE);
            encoderSurface=codec.createInputSurface(); codec.start();
            manager.openCamera(lens.cameraId,new CameraDevice.StateCallback() {
                public void onOpened(CameraDevice device) {
                    if(g!=generation) { device.close(); return; }
                    camera=device;
                    try {
                        OutputConfiguration output=new OutputConfiguration(encoderSurface);
                        if(lens.physicalId!=null) output.setPhysicalCameraId(lens.physicalId);
                        SessionConfiguration config=new SessionConfiguration(SessionConfiguration.SESSION_REGULAR,Collections.singletonList(output),command->cameraHandler.post(command),new CameraCaptureSession.StateCallback() {
                            public void onConfigured(CameraCaptureSession captureSession) {
                                if(g!=generation) { captureSession.close(); return; }
                                session=captureSession;
                                try {
                                    CaptureRequest.Builder request=device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
                                    request.addTarget(encoderSurface);
                                    // PC rotation metadata describes unrotated sensor buffers.
                                    // Do not let display-orientation compatibility crop/rotate them.
                                    if(Build.VERSION.SDK_INT>=31) {
                                        int[] rotateModes=manager.getCameraCharacteristics(lens.cameraId).get(CameraCharacteristics.SCALER_AVAILABLE_ROTATE_AND_CROP_MODES);
                                        if(rotateModes!=null) for(int mode:rotateModes) if(mode==0) request.set(CaptureRequest.SCALER_ROTATE_AND_CROP,0);
                                    }
                                    request.set(CaptureRequest.CONTROL_MODE,CaptureRequest.CONTROL_MODE_AUTO);
                                    int[] modes=lens.characteristics.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES);
                                    if(modes!=null) for(int mode:modes) if(mode==CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO) request.set(CaptureRequest.CONTROL_AF_MODE,mode);
                                    Range<Integer>[] ranges=manager.getCameraCharacteristics(lens.cameraId).get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
                                    Range<Integer> chosen=null;
                                    if(ranges!=null) for(Range<Integer> range:ranges) if(range.getUpper()==fps && (chosen==null || range.getLower()>chosen.getLower())) chosen=range;
                                    if(chosen!=null) request.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,chosen);
                                    captureSession.setRepeatingRequest(request.build(),null,cameraHandler);
                                    state="streaming";
                                } catch(Exception ex) { fail(ex); }
                            }
                            public void onConfigureFailed(CameraCaptureSession captureSession) { if(g==generation) fail(new IOException("The camera rejected this lens/resolution. Try 720p or 1080p.")); }
                        });
                        device.createCaptureSession(config);
                    } catch(Exception ex) { fail(ex); }
                }
                public void onDisconnected(CameraDevice device) { device.close(); if(g==generation) fail(new IOException("Camera disconnected. Reconnect in the PC app.")); }
                public void onError(CameraDevice device,int code) { device.close(); if(g==generation) fail(new IOException("Camera unavailable ("+code+"). Close other camera apps and reconnect.")); }
            },cameraHandler);
        } catch(Exception ex) { fail(ex); }
    }
    void stop(String nextState) {
        generation++; state=nextState; codecConfig=null;
        for(Subscriber subscriber:subscribers) subscriber.close(); subscribers.clear();
        if(session!=null) { try { session.close(); } catch(Exception ignored) {} session=null; }
        if(camera!=null) { camera.close(); camera=null; }
        if(encoder!=null) { try { encoder.stop(); } catch(Exception ignored) {} try { encoder.release(); } catch(Exception ignored) {} encoder=null; }
        if(encoderSurface!=null) { encoderSurface.release(); encoderSurface=null; }
    }
    void fail(Exception ex) { error=ex.getMessage()==null?ex.toString():ex.getMessage(); state="error"; Log.e("PixelCam",error,ex); }
    JSONObject status() throws JSONException {
        Lens lens=findLens(selectedLens);
        int sensor=lens==null?0:lens.sensorOrientation(),orientation=deviceOrientation;
        // Rear-camera captured-image convention, not a display transform:
        // clockwise correction = sensor orientation + clockwise physical device
        // orientation (CaptureRequest.JPEG_ORIENTATION's documented formula).
        int correction=orientationKnown?(sensor+orientation)%360:0;
        return new JSONObject().put("version","0.2.0").put("state",state).put("error",error).put("permission",permission())
            .put("lens",selectedLens).put("width",width).put("height",height).put("fps",fps).put("frames",frames).put("bytes",bytes)
            .put("lastFrameAgeMs",lastFrameTime==0?-1:SystemClock.elapsedRealtime()-lastFrameTime).put("clients",subscribers.size())
            .put("orientationSupported",orientationSupported).put("orientationKnown",orientationKnown)
            .put("deviceOrientation",orientation).put("sensorOrientation",sensor).put("rotationDegrees",correction);
    }
    static void closeSocket(Closeable socket) { if(socket!=null) try { socket.close(); } catch(IOException ignored) {} }
    ServerSocket bindServer() throws IOException,InterruptedException {
        long deadline=SystemClock.elapsedRealtime()+8000;
        while(alive) {
            ServerSocket candidate=new ServerSocket();boolean bound=false;
            // Publish even an unbound socket so onDestroy can cancel startup.
            server=candidate;
            try {
                if(!alive)return null;
                candidate.setReuseAddress(true);
                candidate.bind(new InetSocketAddress(InetAddress.getByName("127.0.0.1"),PORT),8);
                bound=true;return candidate;
            } catch(BindException ex) {
                // An old Activity can briefly own the port during recreation or
                // an APK update. Release each failed socket before retrying.
                if(!alive)return null;
                if(SystemClock.elapsedRealtime()>=deadline)throw ex;
            } finally {
                if(!bound) { closeSocket(candidate);if(server==candidate)server=null; }
            }
            if(alive)Thread.sleep(125);
        }
        return null;
    }
    void serve() {
        ServerSocket listener=null;
        try {
            listener=bindServer();if(listener==null || !alive)return;
            serverReady=true;
            cameraHandler.post(()->{ if(alive)restart(); });
            while(alive) {
                Socket socket=listener.accept();connections.add(socket);
                if(!alive) { closeSocket(socket);connections.remove(socket);break; }
                try {
                    socket.setSoTimeout(10000);socket.setTcpNoDelay(true);network.execute(()->handle(socket));
                } catch(IOException|RuntimeException ex) {
                    closeSocket(socket);connections.remove(socket);throw ex;
                }
            }
        } catch(Exception ex) {
            if(alive)cameraHandler.post(()->{ if(alive) { stop("error");fail(new IOException("USB connection unavailable. Reopen PixelCam. "+ex.getMessage())); } });
        } finally {
            serverReady=false;closeSocket(listener);if(server==listener)server=null;
        }
    }
    String line(InputStream input) throws IOException {
        ByteArrayOutputStream result=new ByteArrayOutputStream(); int b;
        while((b=input.read())!=-1) { if(b=='\n') break; if(b!='\r') result.write(b); if(result.size()>8192) throw new IOException("Header too long"); }
        return result.toString("UTF-8");
    }
    void reply(Socket socket,int code,JSONObject value) throws IOException {
        byte[] body=value.toString().getBytes("UTF-8");
        OutputStream output=socket.getOutputStream(); output.write(("HTTP/1.1 "+code+" OK\r\nContent-Type: application/json\r\nContent-Length: "+body.length+"\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n").getBytes("UTF-8")); output.write(body); output.flush();
    }
    void handle(Socket socket) {
        Subscriber subscriber=null;
        try {
            InputStream input=new BufferedInputStream(socket.getInputStream());
            String[] request=line(input).split(" "); if(request.length<2) return;
            String method=request[0],path=request[1].split("\\?")[0];
            int length=0; String header;
            while(!(header=line(input)).isEmpty()) if(header.toLowerCase(Locale.US).startsWith("content-length:")) length=Integer.parseInt(header.substring(15).trim());
            if(length<0 || length>8192) { reply(socket,413,new JSONObject().put("error","Request too large")); return; }
            byte[] body=new byte[length]; int read=0;
            while(read<length) { int count=input.read(body,read,length-read); if(count<0) throw new EOFException(); read+=count; }
            if(path.equals("/api/status")) reply(socket,200,status());
            else if(path.equals("/api/cameras")) { JSONArray list=new JSONArray(); for(Lens lens:lenses) list.put(lens.json()); reply(socket,200,new JSONObject().put("lenses",list)); }
            else if(path.equals("/api/config") && method.equals("POST")) {
                JSONObject config=new JSONObject(new String(body,"UTF-8"));
                String id=config.optString("lens",selectedLens); Lens lens=findLens(id);
                int w=config.optInt("width",width),h=config.optInt("height",height),rate=config.optInt("fps",fps);
                boolean valid=false; if(lens!=null) for(Size size:lens.sizes) if(size.getWidth()==w && size.getHeight()==h) valid=true;
                if(!valid || (rate!=15 && rate!=24 && rate!=30)) { reply(socket,400,new JSONObject().put("error","Unsupported camera configuration")); return; }
                final boolean requestedEnabled=config.optBoolean("enabled",true);
                cameraHandler.post(()->{ selectedLens=id; width=w; height=h; fps=rate; enabled=requestedEnabled; restart(); });
                reply(socket,202,new JSONObject().put("accepted",true));
            } else if(path.equals("/video.h264")) {
                if(!state.equals("streaming")) { reply(socket,503,status()); return; }
                subscriber=new Subscriber(socket); subscribers.add(subscriber);
                OutputStream output=new BufferedOutputStream(socket.getOutputStream(),65536);
                output.write("HTTP/1.1 200 OK\r\nContent-Type: video/h264\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n".getBytes("UTF-8")); output.flush();
                cameraHandler.post(()->{ if(encoder!=null) try { Bundle params=new Bundle(); params.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME,0); encoder.setParameters(params); } catch(Exception ignored) {} });
                while(alive && !subscriber.closed) { byte[] packet=subscriber.packets.poll(5,TimeUnit.SECONDS); if(packet==null) break; output.write(packet); output.flush(); }
            } else reply(socket,404,new JSONObject().put("error","Not found"));
        } catch(Exception ignored) {} finally { if(subscriber!=null) { subscriber.close(); subscribers.remove(subscriber); } closeSocket(socket);connections.remove(socket); }
    }
    @Override protected void onDestroy() {
        alive=false;serverReady=false; ui.removeCallbacksAndMessages(null);
        if(orientationListener!=null) orientationListener.disable();
        closeSocket(server);
        for(Socket socket:connections)closeSocket(socket);
        network.shutdownNow();
        cameraHandler.post(()->{ stop("stopped"); cameraThread.quitSafely(); });
        super.onDestroy();
    }
}
