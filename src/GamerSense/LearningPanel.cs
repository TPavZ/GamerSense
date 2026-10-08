using GamerSense.Audio;
namespace GamerSense;

public sealed class LearningPanel : FlowLayoutPanel
{
    private readonly PersonalProfiles _profiles;
    private readonly Func<string> _folder;
    private readonly Func<bool> _running;
    private readonly Label _status=new(){AutoSize=true,MaximumSize=new Size(610,0)};
    private readonly Button _train=new(){Text="Retrain from approved clips",AutoSize=true};
    private readonly Button _apply=new(){Text="Use validated candidate",AutoSize=true,Enabled=false};
    private readonly Button _restore=new(){Text="Restore bundled profiles",AutoSize=true};
    private LearningReport? _candidate;
    public LearningPanel(PersonalProfiles profiles,Func<string> folder,Func<bool> running)
    {
        _profiles=profiles;_folder=folder;_running=running;
        Dock=DockStyle.Fill;AutoScroll=true;FlowDirection=FlowDirection.TopDown;WrapContents=false;Padding=new Padding(25);
        Controls.Add(new Label{Text="LEARN FROM APPROVED SOUNDS",Font=new Font("Segoe UI",16,FontStyle.Bold),AutoSize=true});
        Controls.Add(new Label{Text="Review a clip, choose its final category, then Approve & export. Retraining reads that folder and its subfolders. Unreviewed guesses and mixed / uncertain labels are excluded.",AutoSize=true,MaximumSize=new Size(610,0)});
        Controls.Add(new Label{Text="Each updated category needs at least 5 unique clips from 3 recording sessions, including at least 3 training clips and a held-out session. More varied sessions give a more useful check. Duplicate audio counts once; conflicting labels are excluded.",AutoSize=true,MaximumSize=new Size(610,0)});
        Controls.Add(new Label{Text="Retraining runs in the background. It supplements bundled profiles and tests on sessions held out from this update. Some clips may already be in bundled training; scores are a limited regression check. Each updated category must match or beat current profiles and get at least half its test clips right. Stop playback before applying or restoring. Manual review stays available.",AutoSize=true,MaximumSize=new Size(610,0)});
        Controls.Add(_train);Controls.Add(_apply);Controls.Add(_restore);Controls.Add(_status);
        foreach(var b in new[]{_train,_apply,_restore}){b.ForeColor=Color.Black;b.BackColor=Color.WhiteSmoke;}
        _status.Text=profiles.Status+"\nApproved folder: "+folder();
        _train.Click+=async(_,_)=>
        {
            _train.Enabled=_restore.Enabled=_apply.Enabled=false;_candidate=null;_status.Text="Reading approved samples and checking a candidate…";
            try
            {
                string path=_folder();var report=await Task.Run(()=>_profiles.Train(path));
                if(IsDisposed)return;
                _candidate=report;_status.Text=_profiles.Status+"\n"+report.Summary+
                    "\nUpdated: "+string.Join(", ",report.UpdatedCategories.Select(EventCategorizer.LabelFor))+
                    "\nNeed more data: "+string.Join(", ",report.WaitingCategories.Select(EventCategorizer.LabelFor));
                _apply.Enabled=report.Passed;
            }
            catch(Exception ex){if(!IsDisposed)_status.Text="Retraining did not complete: "+ex.Message;}
            finally{if(!IsDisposed)_train.Enabled=_restore.Enabled=true;}
        };
        _apply.Click+=(_,_)=>Change(()=>{if(_candidate is not null)_profiles.Apply(_candidate);_apply.Enabled=false;});
        _restore.Click+=(_,_)=>Change(()=>{_profiles.RestoreBundled();_apply.Enabled=false;_candidate=null;});
    }
    private void Change(Action action)
    {
        if(_running()){_status.Text="Stop playback first, then apply or restore profiles.";return;}
        try{action();_status.Text=_profiles.Status+". The next playback session and new captured-sound suggestions use these profiles. Existing reviewed labels are kept.";}
        catch(Exception ex){_status.Text="Profiles kept: "+ex.Message;}
    }
}
