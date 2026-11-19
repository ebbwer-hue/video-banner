using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Web.Script.Serialization;

namespace VideoBanner {
 public class Options {
  public string Source, Banner, BannerAudio, Destination, Ranges;
  public bool Advertising = true, ImageBanner, CenterBanner = true;
  public double ClipSeconds = 180, Speed = 1, ImageSeconds = 8, BannerWidth = 94;
 }
 public class MediaInfo { public double Duration; public int Width, Height; public bool Audio, Video; }
 public class ClipRange { public double Start, End; }
 public class Piece { public bool Ad; public double Start, Duration, OutputStart; }
 public class Engine {
  public readonly string Tools;
  public Action<string> Status = delegate {};
  public volatile bool Cancelled;
  private Process active;
  private readonly object sync = new object();
  private StreamWriter log;
  static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
  public Engine(string tools) { Tools = tools; }
  public static string N(double n) { return n.ToString("0.########", Inv); }
  public static string Q(string text) {
   // CommandLineToArgvW quoting, including terminal backslashes.
   StringBuilder s = new StringBuilder("\""); int slashes = 0;
   foreach (char c in text) { if(c == '\\') { slashes++; continue; }
    if(c == '"') { s.Append('\\', slashes*2+1); s.Append(c); }
    else { s.Append('\\', slashes); s.Append(c); } slashes=0;
   } s.Append('\\', slashes*2); return s.Append('"').ToString();
  }
  public void Cancel() { Cancelled = true; lock(sync) { try { if(active != null && !active.HasExited) active.Kill(); } catch {} } }
  void Check() { if(Cancelled) throw new OperationCanceledException(); }
  public string Run(string exe, params string[] args) {
   Check(); StringBuilder output = new StringBuilder(); object outputLock = new object();
   Process p = new Process();
   p.StartInfo = new ProcessStartInfo(exe, string.Join(" ", args.Select(Q))) {
    UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true,
    StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8
   };
   DataReceivedEventHandler handler = delegate(object sender, DataReceivedEventArgs e) {
    if(e.Data == null) return;
    lock(outputLock) { output.AppendLine(e.Data); if(log != null) { log.WriteLine(e.Data); log.Flush(); } }
   };
   p.OutputDataReceived += handler; p.ErrorDataReceived += handler;
   try {
    lock(sync) { Check(); p.Start(); active=p; }
    p.BeginOutputReadLine(); p.BeginErrorReadLine();
    while(!p.WaitForExit(200)) Check();
    p.WaitForExit(); Check();
    if(p.ExitCode != 0) { string msg=output.ToString(); throw new Exception("Ошибка " + Path.GetFileName(exe) + ":\r\n" + msg.Substring(Math.Max(0,msg.Length-4500))); }
    return output.ToString();
   } finally { lock(sync) { if(active==p) active=null; } p.Dispose(); }
  }
  string Tool(string name) {
   string path=Path.Combine(Tools,name+".exe");
   if(!File.Exists(path)) throw new Exception("Не найден " + name + ".exe в папке tools. Запустите Установить-компоненты.cmd.");
   return path;
  }
  public MediaInfo Probe(string path) {
   string json=Run(Tool("ffprobe"),"-v","error","-show_format","-show_streams","-of","json",path);
   var root=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
   MediaInfo m=new MediaInfo();
   var format=root.ContainsKey("format") ? root["format"] as Dictionary<string,object> : null;
   double duration;
   if(format!=null && format.ContainsKey("duration") && double.TryParse(Convert.ToString(format["duration"],Inv),NumberStyles.Float,Inv,out duration)) m.Duration=duration;
   foreach(var obj in (IEnumerable)root["streams"]) {
    var st=(Dictionary<string,object>)obj; string type=Convert.ToString(st["codec_type"]);
    if(type=="audio") m.Audio=true;
    if(type=="video" && !m.Video) { m.Video=true; m.Width=Convert.ToInt32(st["width"]); m.Height=Convert.ToInt32(st["height"]); }
   }
   return m;
  }
  public static double Time(string value) {
   string[] bits=value.Trim().Split(':'); double t=0;
   if(bits.Length>3 || bits.Length==0) throw new Exception("Неверный таймкод: " + value);
   foreach(string bit in bits) { double n; if(!double.TryParse(bit.Replace(',','.'),NumberStyles.AllowDecimalPoint,Inv,out n) || n<0) throw new Exception("Неверный таймкод: " + value); t=t*60+n; }
   return t;
  }
  public static List<ClipRange> GetRanges(double duration, double length, string manual) {
   if(duration<=0 || double.IsNaN(duration) || double.IsInfinity(duration)) throw new Exception("Не удалось определить длительность исходного видео.");
   var ranges=new List<ClipRange>();
   if(!string.IsNullOrWhiteSpace(manual)) {
    foreach(string line in manual.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)) {
     var pair=line.Trim().Split('-'); if(pair.Length!=2) throw new Exception("Введите каждый отрезок в виде 00:00-03:00.");
     double start=Time(pair[0]), end=Time(pair[1]);
     if(end<=start || end>duration+0.05 || start>=duration) throw new Exception("Отрезок выходит за пределы видео или конец не позже начала: " + line);
     ranges.Add(new ClipRange { Start=start, End=Math.Min(end,duration) });
    }
   } else {
    if(length<1 || length>86400 || double.IsNaN(length)) throw new Exception("Длительность нарезки должна быть от 1 до 86400 секунд.");
    for(double start=0; start<duration-0.01; start+=length) ranges.Add(new ClipRange{Start=start,End=Math.Min(start+length,duration)});
   }
   if(ranges.Count==0 || ranges.Count>1000) throw new Exception("Выберите от 1 до 1000 отрезков.");
   return ranges;
  }
  // Schedule in the finished clip, so inserted ads do not accumulate timing drift.
  public static List<Piece> Plan(double start,double end,double speed,double adDuration,bool ads) {
   if(speed<1 || speed>1.1 || double.IsNaN(speed)) throw new Exception("Скорость должна быть от 1 до 1,1.");
   if(ads && (adDuration<=0 || adDuration>=60 || double.IsNaN(adDuration))) throw new Exception("Рекламная вставка должна длиться больше 0 и меньше 60 секунд.");
   List<Piece> list=new List<Piece>(); double source=start, cursor=0, next=30;
   while(source<end-0.00001) {
    double remaining=(end-source)/speed;
    double content=ads ? Math.Min(remaining,next-cursor) : remaining;
    if(content>0.00001) { list.Add(new Piece{Start=source,Duration=content*speed,OutputStart=cursor}); source+=content*speed; cursor+=content; }
    if(source>=end-0.00001) break;
    list.Add(new Piece{Ad=true,Duration=adDuration,OutputStart=cursor}); cursor+=adDuration; next+=60;
   }
   return list;
  }
  static string VideoFilter(int w,int h) { return "scale="+w+":"+h+":force_original_aspect_ratio=decrease,pad="+w+":"+h+":(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30,format=yuv420p"; }
  string Encode(List<string> args,string output,double duration) {
   args.InsertRange(0,new[]{"-hide_banner","-nostdin","-y"});
   args.AddRange(new[]{"-t",N(duration),"-c:v","libx264","-preset","fast","-crf","20","-pix_fmt","yuv420p","-c:a","aac","-b:a","192k","-ar","48000","-ac","2","-video_track_timescale","15360","-movflags","+faststart",output});
   return Run(Tool("ffmpeg"),args.ToArray());
  }
  public string Execute(Options o) {
   if(o.Speed<1 || o.Speed>1.1 || double.IsNaN(o.Speed)) throw new Exception("Ускорение допускается только от 1 до 1,1.");
   if(o.BannerWidth<20 || o.BannerWidth>100 || double.IsNaN(o.BannerWidth)) throw new Exception("Ширина баннера должна быть от 20 до 100 процентов.");
   if(string.IsNullOrWhiteSpace(o.Destination)) throw new Exception("Выберите папку результата.");
   Directory.CreateDirectory(o.Destination);
   string run=Path.Combine(Path.GetFullPath(o.Destination),"Монтаж-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
   Directory.CreateDirectory(run); string temp=Path.Combine(run,".work"); Directory.CreateDirectory(temp);
   log=new StreamWriter(Path.Combine(run,"processing.log"),false,Encoding.UTF8);
   try {
    Tool("ffmpeg"); Tool("ffprobe"); string input=o.Source==null ? "" : o.Source.Trim();
    Uri uri;
    if(Uri.TryCreate(input,UriKind.Absolute,out uri) && (uri.Scheme=="https" || uri.Scheme=="http")) {
     string host=uri.Host.ToLowerInvariant();
     if(!(host=="youtu.be" || host=="youtube.com" || host.EndsWith(".youtube.com"))) throw new Exception("Для импорта по ссылке укажите адрес YouTube.");
     Status("Загрузка видео с YouTube…");
     string template=Path.Combine(temp,"source.%(ext)s");
     var dl=new List<string>{"--ignore-config","--no-playlist","--no-progress","--newline","--encoding","utf-8","--ffmpeg-location",Tools,"-f","bv*[height<=1080]+ba/b[height<=1080]","--merge-output-format","mp4","-o",template};
     string node=Path.Combine(Tools,"node.exe"); if(File.Exists(node)) dl.AddRange(new[]{"--js-runtimes","node:"+node});
     dl.Add("--"); dl.Add(input); Run(Tool("yt-dlp"),dl.ToArray());
     input=Directory.GetFiles(temp,"source.*").Where(f=>!f.EndsWith(".part") && !f.EndsWith(".ytdl")).OrderByDescending(f=>new FileInfo(f).Length).FirstOrDefault();
    }
    if(string.IsNullOrWhiteSpace(input) || !File.Exists(input)) throw new Exception("Выберите существующий видеофайл или ссылку YouTube.");
    Status("Проверка исходного видео…"); MediaInfo source=Probe(input);
    if(!source.Video) throw new Exception("В исходном файле нет видеодорожки.");
    double factor=Math.Min(1,1920.0/Math.Max(source.Width,source.Height));
    int w=Math.Max(2,(int)(source.Width*factor)/2*2), h=Math.Max(2,(int)(source.Height*factor)/2*2);
    string vf=VideoFilter(w,h); double adDuration=0; string ad=Path.Combine(temp,"ad.mp4");
    if(o.Advertising) {
     if(!File.Exists(o.Banner)) throw new Exception("Выберите рекламный видеобаннер или картинку.");
     MediaInfo banner=Probe(o.Banner);
     if(!banner.Video) throw new Exception("Баннер должен содержать изображение или видео.");
     bool separate=!string.IsNullOrWhiteSpace(o.BannerAudio);
     if(separate && !File.Exists(o.BannerAudio)) throw new Exception("Не найден аудиофайл баннера.");
     if(o.ImageBanner && !separate) throw new Exception("Для картинки обязательно выберите отдельный звук баннера.");
     if(!o.ImageBanner && !separate && !banner.Audio) throw new Exception("У видеобаннера нет звука. Выберите отдельный аудиофайл.");
     adDuration=o.ImageBanner ? o.ImageSeconds : banner.Duration;
     if(separate && !Probe(o.BannerAudio).Audio) throw new Exception("В отдельном файле нет аудиодорожки.");
     if(adDuration<=0 || adDuration>=60) throw new Exception("Рекламная вставка должна длиться больше 0 и меньше 60 секунд.");
     Status("Подготовка непрозрачного баннера со звуком…");
     var a=new List<string>(); if(o.ImageBanner) a.AddRange(new[]{"-loop","1","-framerate","30"});
     a.AddRange(new[]{"-i",o.Banner}); if(separate) a.AddRange(new[]{"-i",o.BannerAudio});
     int adW=w, adH=h;
     if(o.CenterBanner) {
      double cardScale=Math.Min(w*o.BannerWidth/100/banner.Width,h*0.6/banner.Height);
      adW=Math.Max(2,(int)(banner.Width*cardScale)/2*2);
      adH=Math.Max(2,(int)(banner.Height*cardScale)/2*2);
     }
     // Composite any alpha against black. The banner itself is always opaque.
     string graph="color=c=black:s="+adW+"x"+adH+":r=30[bg];[0:v:0]setpts=PTS-STARTPTS,scale="+adW+":"+adH+",setsar=1,fps=30,format=rgba[fg];[bg][fg]overlay=shortest=1:format=auto,format=yuv420p[v]";
     a.AddRange(new[]{"-filter_complex",graph,"-map","[v]","-map",separate ? "1:a:0" : "0:a:0","-af","aresample=48000,apad,atrim=duration="+N(adDuration)+",asetpts=PTS-STARTPTS,volumedetect"});
     string adLog=Encode(a,ad,adDuration);
     var volume=System.Text.RegularExpressions.Regex.Match(adLog,@"max_volume:\s*([-\d.]+|\-inf) dB");
     double peak;
     if(!volume.Success || volume.Groups[1].Value=="-inf" || !double.TryParse(volume.Groups[1].Value,NumberStyles.Float,Inv,out peak) || peak < -80)
      throw new Exception("Звук баннера отсутствует или практически бесшумен. Выберите баннер со звуком либо другой аудиофайл.");
     // Use encoded duration for scheduling; sample/frame quantization is unavoidable.
     adDuration=Probe(ad).Duration;
     if(adDuration>=60) throw new Exception("После подготовки баннер должен быть короче 60 секунд.");
    }
    var ranges=GetRanges(source.Duration,o.ClipSeconds,o.Ranges);
    var report=new List<object>();
    for(int i=0;i<ranges.Count;i++) {
     Check(); var r=ranges[i]; var pieces=Plan(r.Start,r.End,o.Speed,adDuration,o.Advertising);
     var concat=new List<string>(); var timing=new List<object>(); double actualCursor=0; string previousContent=null;
     for(int j=0;j<pieces.Count;j++) {
      Piece piece=pieces[j]; string part;
      if(piece.Ad) {
       part=ad;
       if(o.CenterBanner) {
        Status("Ролик "+(i+1)+" из "+ranges.Count+" · баннер на размытом стоп-кадре…");
        if(previousContent==null) throw new Exception("Не найден кадр для рекламной паузы.");
        string freeze=Path.Combine(temp,"freeze.png");
        // The final displayed frame of the previous content segment is held for
        // the entire ad. No source audio is mapped into this segment.
        Run(Tool("ffmpeg"),"-hide_banner","-nostdin","-loglevel","error","-y","-sseof","-1","-i",previousContent,"-vf","reverse","-frames:v","1",freeze);
        part=Path.Combine(temp,"p"+i+"-"+j+".mp4");
        string sigma=N(Math.Max(2,w/75.0));
        string composite="[0:v:0]setsar=1,gblur=sigma="+sigma+"[bg];[1:v:0]setpts=PTS-STARTPTS[card];[bg][card]overlay=x=(W-w)/2:y=(H-h)/2:shortest=1,format=yuv420p[v]";
        Encode(new List<string>{"-loop","1","-framerate","30","-i",freeze,"-i",ad,"-filter_complex",composite,"-map","[v]","-map","1:a:0"},part,adDuration);
       }
       double renderedAd=Probe(part).Duration;
       timing.Add(new{kind="ad",startSeconds=actualCursor,durationSeconds=renderedAd,style=o.CenterBanner ? "center-blurred-freeze" : "fullscreen"}); actualCursor+=renderedAd;
      }
      else {
       Status("Ролик "+(i+1)+" из "+ranges.Count+" · фрагмент "+(j+1)+" из "+pieces.Count);
       part=Path.Combine(temp,"p"+i+"-"+j+".mp4"); double duration=piece.Duration/o.Speed;
       var a=new List<string>{"-ss",N(piece.Start),"-t",N(piece.Duration),"-i",input};
       if(!source.Audio) a.AddRange(new[]{"-f","lavfi","-i","anullsrc=r=48000:cl=stereo"});
       a.AddRange(new[]{"-map","0:v:0","-map",source.Audio ? "0:a:0" : "1:a:0","-vf","setpts=(PTS-STARTPTS)/"+N(o.Speed)+","+vf,"-af","asetpts=PTS-STARTPTS,atempo="+N(o.Speed)+",aresample=48000,apad"});
       Encode(a,part,duration);
       previousContent=part;
       double rendered=Probe(part).Duration;
       timing.Add(new{kind="source",sourceStartSeconds=piece.Start,sourceEndSeconds=piece.Start+piece.Duration,startSeconds=actualCursor,durationSeconds=rendered});
       actualCursor+=rendered;
      }
      // Generated filenames only; no user path is placed in the concat file.
      concat.Add("file '"+Path.GetFileName(part)+"'");
     }
     string list=Path.Combine(temp,"list.txt"); File.WriteAllLines(list,concat,new UTF8Encoding(false));
     string file="clip-"+(i+1).ToString("000")+".mp4", target=Path.Combine(run,file), partial=Path.Combine(run,"clip-"+(i+1).ToString("000")+".partial.mp4");
     Status("Сохранение ролика "+(i+1)+" из "+ranges.Count+"…");
     Run(Tool("ffmpeg"),"-hide_banner","-nostdin","-y","-f","concat","-safe","1","-i",list,"-map","0:v:0","-map","0:a:0","-c","copy","-movflags","+faststart",partial);
     MediaInfo result=Probe(partial); if(!result.Video || !result.Audio || result.Duration<=0) throw new Exception("Не удалось проверить готовый ролик.");
     File.Move(partial,target);
     report.Add(new{file=file,sourceStartSeconds=r.Start,sourceEndSeconds=r.End,speed=o.Speed,bannerWidthPercent=o.BannerWidth,durationSeconds=result.Duration,timeline=timing});
     File.WriteAllText(Path.Combine(run,"timeline.json"),new JavaScriptSerializer().Serialize(report),Encoding.UTF8);
     foreach(string f in Directory.GetFiles(temp,"p"+i+"-*.mp4")) File.Delete(f);
    }
    Status("Готово · "+ranges.Count+" роликов"); return run;
   } finally {
    log.Dispose(); log=null;
    // Only the private scratch directory created for this run is removed.
    try { Directory.Delete(temp,true); } catch {}
   }
  }
 }
}
