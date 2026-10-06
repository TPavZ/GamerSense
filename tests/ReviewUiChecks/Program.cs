using GamerSense;
using GamerSense.Audio;
using NAudio.Wave;
using System.Reflection;
class Program
{
 [STAThread] static void Main()
 {
  string root=Path.GetFullPath("work/review-ui-v0415-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
  Application.SetHighDpiMode(HighDpiMode.DpiUnaware); Application.EnableVisualStyles();
  using var library=new EventLibrary(Path.Combine(root,"library"), categorizer: _ => new EventSuggestion { SuggestedLabel="gunfire", Alternatives=[new("gunfire","gunfire",1)], WindowsAnalyzed=1 });
  var format=WaveFormat.CreateIeeeFloatWaveFormat(48000,2); var bytes=new byte[format.AverageBytesPerSecond*5];
  for(int frame=0;frame<48000*5;frame++){float gain=frame>90000&&frame<115000?.8f:.05f; float sample=gain*MathF.Sin(frame*.065f); BitConverter.GetBytes(sample).CopyTo(bytes,frame*8); BitConverter.GetBytes(sample*.8f).CopyTo(bytes,frame*8+4);}
  var saved=library.SaveNow(new EventClip(bytes,format,10,15,new AudioMarker(1,12,"Loud spike",-8),"test","test-ui"));
  using var form=new Form { ClientSize=new Size(700,900), BackColor=Color.FromArgb(18,18,22),ForeColor=Color.White,Font=new Font("Segoe UI",10),Opacity=0,ShowInTaskbar=false};
  var review=new EventReviewPanel(()=>null,()=>false,()=>null,library,exportDirectory:Path.Combine(root,"exports"));form.Controls.Add(review); form.Show(); Application.DoEvents();
  T Field<T>(string name)=>(T)typeof(EventReviewPanel).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(review)!;
  var list=Field<ListBox>("_saved");list.SelectedIndex=0;Application.DoEvents();
  if(Field<ComboBox>("_label").Text!="gunfire" || !Field<Label>("_suggestionInfo").Text.Contains("unverified")) throw new Exception("Suggested label/review status missing.");
  var waveform=Field<ClipWaveform>("_waveform");
  void Mouse(string name,int x)=>typeof(ClipWaveform).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(waveform,new object[]{new MouseEventArgs(MouseButtons.Left,1,x,40,0)});
  Mouse("OnMouseDown",210); Mouse("OnMouseMove",300); Mouse("OnMouseUp",300); Application.DoEvents();
  var first=Field<NumericUpDown>("_trimStart");var last=Field<NumericUpDown>("_trimEnd");
  if(Math.Abs((double)first.Value-5*210.0/599)>.002||Math.Abs((double)last.Value-5*300.0/599)>.002)throw new Exception("Waveform drag did not update selected range.");
  Mouse("OnMouseDown",300);Mouse("OnMouseMove",330);Mouse("OnMouseUp",330);Application.DoEvents();
  if(Math.Abs((double)last.Value-5*330.0/599)>.002)throw new Exception("End handle did not refine selection.");
  Field<ComboBox>("_label").SelectedItem="explosions / mortars";Field<TextBox>("_notes").Text="Grenade blast: selected onset and tail; movement excluded.";
  Field<CheckBox>("_repeat").Checked=true;
  review.FlushPendingEdits();
  var draft=library.List().Single();
  if(!draft.HasManualEdits || draft.Approved || draft.Label!="explosions / mortars" || draft.AutoSuggestion?.SuggestedLabel!="gunfire")throw new Exception("Review corrections were not stored separately from the guess.");
  review.AutoScrollPosition=new Point(0,10000);Application.DoEvents();
  using(var image=new Bitmap(700,900)){review.DrawToBitmap(image,new Rectangle(0,0,700,900));image.Save(Path.Combine(root,"review-selection.png"));}
  IEnumerable<Button> Buttons(Control c){foreach(Control child in c.Controls){if(child is Button b)yield return b;foreach(var nested in Buttons(child))yield return nested;}}
  double expected=(double)(last.Value-first.Value);Buttons(review).Single(b=>b.Text=="Approve & export").PerformClick();Application.DoEvents();
  var exported=Directory.GetFiles(Path.Combine(root,"exports"),"*.wav").Single();using var reader=new WaveFileReader(exported);
  if(Math.Abs(reader.TotalTime.TotalSeconds-expected)>1.0/48000||library.List().Length!=0)throw new Exception("Approval UI exported the wrong range or kept pending source.");
  Console.WriteLine("PASS waveform drag, handle refinement, numeric synchronization, repeat toggle, native selected-range approval and review removal; screenshot saved.");
 }
}

