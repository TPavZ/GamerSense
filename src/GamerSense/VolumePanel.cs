using GamerSense.Audio;
namespace GamerSense;

public sealed class VolumePanel : FlowLayoutPanel
{
    private readonly VolumeControls _controls;
    private readonly Func<string> _status;
    private readonly Action<VolumeLevels> _save;
    private readonly Label _live = new() { AutoSize = true, MaximumSize = new Size(610,0), ForeColor = Color.Goldenrod };
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 600 };
    private readonly List<TrackBar> _sliders = new();
    private readonly CheckBox _enable = new() { Text = "Apply category volumes (preview)", AutoSize = true };
    private bool _loading, _dirty;
    public VolumePanel(VolumeControls controls, Func<string> status, Action<VolumeLevels> save)
    {
        _controls=controls; _status=status; _save=save;
        Dock=DockStyle.Fill; AutoScroll=true; FlowDirection=FlowDirection.TopDown; WrapContents=false; Padding=new Padding(25);
        Controls.Add(new Label { Text="LIVE VOLUME CONTROLS", Font=new Font("Segoe UI",16,FontStyle.Bold), AutoSize=true });
        Controls.Add(new Label { Text="100% is normal volume. 0% mutes. Boosting above 100% can clip loud audio.", AutoSize=true, MaximumSize=new Size(610,0) });
        Controls.Add(_enable);
        Controls.Add(new Label { Text="Preview adjusts the whole mixed section assigned to one category. Overlapping sounds change together. Brief sounds can finish before the estimate arrives. Uncertain / other audio keeps normal category volume.", AutoSize=true, MaximumSize=new Size(610,0) });
        Controls.Add(new Label { Text="Overall volume always applies. Category routing starts off each time you open the app.", AutoSize=true, MaximumSize=new Size(610,0) });
        Controls.Add(_live);
        var values=_controls.Levels;
        AddSlider("Overall volume",values.Overall,v=>_controls.Levels with { Overall=v });
        AddSlider("Explosions",values.Explosions,v=>_controls.Levels with { Explosions=v });
        AddSlider("Footsteps",values.Footsteps,v=>_controls.Levels with { Footsteps=v });
        AddSlider("Ground vehicles",values.GroundVehicles,v=>_controls.Levels with { GroundVehicles=v });
        AddSlider("Air vehicles",values.AirVehicles,v=>_controls.Levels with { AirVehicles=v });
        _enable.Checked=values.Enabled;
        _enable.CheckedChanged+=(_,_)=>{ _controls.Levels=_controls.Levels with { Enabled=_enable.Checked }; Changed(); };
        var reset=new Button { Text="Reset all volumes to 100%", AutoSize=true, ForeColor=Color.Black, BackColor=Color.WhiteSmoke };
        reset.Click+=(_,_)=>
        {
            _loading=true; foreach(var slider in _sliders) slider.Value=100; _loading=false;
            _controls.Levels=new(Enabled:_enable.Checked); Changed();
        };
        Controls.Add(reset);
        _saveTimer.Tick+=(_,_)=>{ _saveTimer.Stop(); Flush(); };
        UpdateStatus();
    }
    private void AddSlider(string name,double value,Func<double,VolumeLevels> update)
    {
        Controls.Add(new Label { Text=name,AutoSize=true });
        var row=new FlowLayoutPanel { AutoSize=true, WrapContents=false };
        var slider=new TrackBar { Minimum=0,Maximum=150,Value=(int)Math.Round(value),TickFrequency=25,LargeChange=10,Width=480,Height=45,Name=name };
        var percent=new Label { Text=slider.Value+"%",AutoSize=true,Margin=new Padding(5,12,0,0) };
        slider.ValueChanged+=(_,_)=>{percent.Text=slider.Value+"%";if(!_loading){_controls.Levels=update(slider.Value);Changed();}};
        _sliders.Add(slider); row.Controls.Add(slider);row.Controls.Add(percent);Controls.Add(row);
    }
    private void Changed() { _dirty=true;_saveTimer.Stop();_saveTimer.Start(); }
    public void UpdateStatus()=>_live.Text=_status();
    public void Flush() { if(!_dirty)return;_save(_controls.Levels);_dirty=false; }
    protected override void Dispose(bool disposing) { if(disposing){_saveTimer.Stop();Flush();_saveTimer.Dispose();}base.Dispose(disposing); }
}
