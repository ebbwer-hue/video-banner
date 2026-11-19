using System;
using System.IO;
using System.Drawing;
using System.Threading;
using System.Diagnostics;
using System.Windows.Forms;
using System.Globalization;

namespace VideoBanner {
 public class MainForm : Form {
  TextBox source=new TextBox(), banner=new TextBox(), audio=new TextBox(), destination=new TextBox(), ranges=new TextBox();
  NumericUpDown length=new NumericUpDown(), speed=new NumericUpDown(), imageLength=new NumericUpDown(), bannerWidth=new NumericUpDown();
  ComboBox bannerStyle=new ComboBox();
  CheckBox ads=new CheckBox(), image=new CheckBox(), manual=new CheckBox();
  Label status=new Label(); ProgressBar progress=new ProgressBar();
  Button start=new Button(), cancel=new Button(), folder=new Button();
  TableLayoutPanel grid=new TableLayoutPanel();
  Engine engine; bool running; string result;
  string Home { get { return AppDomain.CurrentDomain.BaseDirectory; } }
  public MainForm() {
   Text="ВидеоБаннер · локальная нарезка"; ClientSize=new Size(930,880); MinimumSize=new Size(820,650); AutoScroll=true;
   StartPosition=FormStartPosition.CenterScreen; Font=new Font("Segoe UI",10); AutoScaleMode=AutoScaleMode.Dpi;
   BackColor=Color.FromArgb(19,23,31); ForeColor=Color.FromArgb(235,239,246);
   grid.Dock=DockStyle.Top; grid.AutoSize=true; grid.AutoSizeMode=AutoSizeMode.GrowAndShrink; grid.Padding=new Padding(28,20,28,20); grid.ColumnCount=3; grid.RowCount=20;
   grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,185)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,135));
   Controls.Add(grid);
   Label title=new Label{Text="ВИДЕО / БАННЕР",AutoSize=true,Font=new Font("Segoe UI",22,FontStyle.Bold),Margin=new Padding(0,0,0,8)}; AddWide(title,0);
   Label intro=new Label{Text="Нарезка видео и рекламные паузы со звуком. Всё обрабатывается на вашем ПК.",AutoSize=true,Margin=new Padding(0,0,0,18)}; AddWide(intro,1);
   AddFile("Видео или YouTube",source,2,"Выбрать видео",false);
   source.AccessibleName="Видео или ссылка YouTube";
   ads.Text="Вставлять рекламу"; ads.Checked=true; ads.AutoSize=true; ads.CheckedChanged+=delegate{RefreshEnabled();}; AddWide(ads,3);
   AddFile("Рекламный баннер",banner,4,"Выбрать баннер",false);
   image.Text="Баннер — картинка"; image.AutoSize=true; image.CheckedChanged+=delegate{RefreshEnabled();}; grid.Controls.Add(image,1,5); grid.SetColumnSpan(image,2);
   AddFile("Отдельный звук",audio,6,"Выбрать звук",false);
   Label hint=new Label{Text="Для видеобаннера используется его звук. Отдельный аудиофайл заменяет его.",AutoSize=true,ForeColor=Color.Silver,Margin=new Padding(0,0,0,10)}; AddWide(hint,7);
   imageLength.Minimum=1; imageLength.Maximum=59; imageLength.Value=8; AddValue("Картинка, секунд",imageLength,8);
   length.Minimum=1; length.Maximum=86400; length.Value=180; AddValue("Длина нарезки, секунд",length,9);
   speed.Minimum=1; speed.Maximum=1.10M; speed.DecimalPlaces=2; speed.Increment=0.01M; speed.Value=1; AddValue("Скорость (1–1,10)",speed,10);
   manual.Text="Выбрать отрезки по таймкодам"; manual.AutoSize=true; manual.CheckedChanged+=delegate{RefreshEnabled();}; AddWide(manual,11);
   ranges.Multiline=true; ranges.ScrollBars=ScrollBars.Vertical; ranges.Height=66; ranges.Text="00:00-03:00\r\n03:00-06:00";
   AddValue("Начало-конец, по строке",ranges,12);
   bannerStyle.DropDownStyle=ComboBoxStyle.DropDownList;
   bannerStyle.Items.AddRange(new object[]{"По центру на размытом стоп-кадре (как в примере)","На весь кадр"});
   bannerStyle.SelectedIndex=0; bannerStyle.SelectedIndexChanged+=delegate{RefreshEnabled();}; AddValue("Показ рекламы",bannerStyle,13);
   bannerWidth.Minimum=20; bannerWidth.Maximum=100; bannerWidth.Value=94; AddValue("Ширина баннера, %",bannerWidth,14);
   Label rules=new Label{Text="Реклама ≈ 00:30 / 01:30 / 02:30 готового ролика. Баннер непрозрачный, со звуком.\nВидео и его звук приостановлены до конца рекламы. Длина нарезки — по исходнику.",AutoSize=true,ForeColor=Color.FromArgb(161,194,240),Margin=new Padding(0,8,0,12)}; AddWide(rules,15);
   AddFile("Папка результата",destination,16,"Выбрать папку",true);
   FlowLayoutPanel buttons=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,Margin=new Padding(0,12,0,8)};
   start.Text="Создать ролики"; start.Size=new Size(190,42); start.BackColor=Color.FromArgb(66,117,235); start.ForeColor=Color.White; start.FlatStyle=FlatStyle.Flat; start.Click+=Start;
   cancel.Text="Остановить"; cancel.Size=new Size(135,42); cancel.Enabled=false; cancel.Click+=delegate{cancel.Enabled=false; status.Text="Остановка…"; if(engine!=null) engine.Cancel();};
   folder.Text="Открыть результат"; folder.Size=new Size(180,42); folder.Enabled=false; folder.Click+=delegate{if(result!=null && Directory.Exists(result)) Process.Start("explorer.exe",Engine.Q(result));};
   buttons.Controls.AddRange(new Control[]{start,cancel,folder}); AddWide(buttons,17);
   progress.Dock=DockStyle.Fill; progress.Height=8; AddWide(progress,18);
   status.Text="Готово к работе. Выберите основное видео."; status.AutoSize=true; status.MaximumSize=new Size(800,0); status.Padding=new Padding(0,8,0,0); AddWide(status,19);
   banner.Text=Path.Combine(Home,"RubUP-CS2-original-sound.mp4");
   destination.Text=Path.Combine(Home,"Результаты");
   FormClosing+=delegate(object sender,FormClosingEventArgs e){if(running){e.Cancel=true; MessageBox.Show(this,"Сначала остановите обработку кнопкой «Остановить».","Идёт монтаж");}};
   RefreshEnabled();
  }
  void AddWide(Control c,int row) { grid.Controls.Add(c,0,row); grid.SetColumnSpan(c,3); }
  void AddValue(string caption,Control c,int row) {
   Label label=new Label{Text=caption,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(0,5,8,5)}; grid.Controls.Add(label,0,row);
   c.Dock=DockStyle.Fill; c.Margin=new Padding(0,4,8,4); grid.Controls.Add(c,1,row); grid.SetColumnSpan(c,2);
  }
  void AddFile(string caption,TextBox box,int row,string buttonText,bool directory) {
   AddValue(caption,box,row); grid.SetColumnSpan(box,1);
   Button b=new Button{Text=buttonText,Dock=DockStyle.Fill,AutoSize=true,Margin=new Padding(0,3,0,3)};
   b.Click+=delegate{
    if(directory) { using(var d=new FolderBrowserDialog()){d.Description="Папка для готовых роликов"; if(Directory.Exists(box.Text))d.SelectedPath=box.Text;if(d.ShowDialog(this)==DialogResult.OK)box.Text=d.SelectedPath;} }
    else { using(var d=new OpenFileDialog()){d.Filter=box==source ? "Видео|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts|Все файлы|*.*" : "Медиафайлы|*.mp4;*.mkv;*.mov;*.webm;*.png;*.jpg;*.jpeg;*.webp;*.mp3;*.wav;*.m4a;*.aac;*.ogg|Все файлы|*.*";if(d.ShowDialog(this)==DialogResult.OK){box.Text=d.FileName;if(box==banner)image.Checked=IsImage(d.FileName);}} }
   }; grid.Controls.Add(b,2,row);
  }
  static bool IsImage(string f){string e=Path.GetExtension(f).ToLowerInvariant();return e==".png"||e==".jpg"||e==".jpeg"||e==".webp"||e==".bmp";}
  void RefreshEnabled(){ranges.Enabled=manual.Checked;length.Enabled=!manual.Checked;imageLength.Enabled=ads.Checked&&image.Checked;banner.Enabled=ads.Checked;audio.Enabled=ads.Checked;image.Enabled=ads.Checked;bannerStyle.Enabled=ads.Checked;bannerWidth.Enabled=ads.Checked&&bannerStyle.SelectedIndex==0;}
  void UI(Action a){if(!IsDisposed&&IsHandleCreated)BeginInvoke(a);}
  void Start(object sender,EventArgs e) {
   if(running)return;
   if(string.IsNullOrWhiteSpace(source.Text)){MessageBox.Show(this,"Выберите основное видео или вставьте ссылку YouTube.","Нужен исходник");return;}
   var o=new Options{Source=source.Text.Trim(),Banner=banner.Text.Trim(),BannerAudio=audio.Text.Trim(),Destination=destination.Text.Trim(),Ranges=manual.Checked?ranges.Text:null,ClipSeconds=(double)length.Value,Speed=(double)speed.Value,Advertising=ads.Checked,ImageBanner=image.Checked,ImageSeconds=(double)imageLength.Value,CenterBanner=bannerStyle.SelectedIndex==0,BannerWidth=(double)bannerWidth.Value};
   running=true; result=null; progress.Value=0; start.Enabled=false; cancel.Enabled=true; folder.Enabled=false;
   for(int row=2;row<=16;row++)foreach(Control c in grid.Controls)if(grid.GetRow(c)==row)c.Enabled=false;
   progress.Style=ProgressBarStyle.Marquee;status.Text="Начинаю обработку…";
   engine=new Engine(Path.Combine(Home,"tools")); engine.Status=delegate(string text){UI(delegate{if(!engine.Cancelled)status.Text=text;});};
   Thread worker=new Thread(delegate(){
    string error=null; bool cancelled=false;
    try{result=engine.Execute(o);}catch(OperationCanceledException){cancelled=true;}catch(Exception ex){error=ex.Message;}
    UI(delegate{
     running=false;start.Enabled=true;cancel.Enabled=false;folder.Enabled=result!=null;progress.Style=ProgressBarStyle.Blocks;
     for(int row=2;row<=16;row++)foreach(Control c in grid.Controls)if(grid.GetRow(c)==row)c.Enabled=true;
     RefreshEnabled();
     if(cancelled)status.Text="Обработка остановлена. Уже готовые ролики сохранены.";
     else if(error!=null){status.Text="Не удалось завершить монтаж.";MessageBox.Show(this,error,"Ошибка обработки",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
     else{progress.Value=100;status.Text="Готово. Ролики сохранены: "+result;}
    });
   });worker.IsBackground=true;worker.Start();
  }
  [STAThread] public static void Main(){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new MainForm());}
 }
}
