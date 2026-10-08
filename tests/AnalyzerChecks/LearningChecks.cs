using GamerSense.Audio;
using NAudio.Wave;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class LearningChecks
{
    public static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"GamerSense-learning-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var samples=new float[48000];for(int i=0;i<24000;i++){samples[i*2]=(float)(.25*Math.Sin(i*2*Math.PI*1000/48000));samples[i*2+1]=-samples[i*2];}
        var mean=WardogsModel.ExtractFeatures(samples);string bundled=Path.Combine(root,"bundled.json");
        var model=new{featureVersion="box3-spectrum-v1-short-padding",sampleRate=48000,channels=2,mean,scale=Enumerable.Repeat(1.0,47).ToArray(),prototypes=new Dictionary<string,double[][]>{{"gunfire",[new double[47]]},{"explosions",[Enumerable.Repeat(100.0,47).ToArray()]}},liveRoutingMaxDistance=new{gunfire=1.0}};
        File.WriteAllText(bundled,JsonSerializer.Serialize(model));
        string clips=Path.Combine(root,"clips");Directory.CreateDirectory(clips);
        void Add(string name,int index,string label="gunfire",bool verified=true,string? session=null,int rate=48000)
        {
            string wav=Path.Combine(clips,name+".wav");using(var writer=new WaveFileWriter(wav,WaveFormat.CreateIeeeFloatWaveFormat(rate,2)))
            {var bytes=new byte[48000*4];for(int i=0;i<48000;i++)BitConverter.GetBytes(samples[i]*(1+index*.005f)).CopyTo(bytes,i*4);writer.Write(bytes,0,bytes.Length);}
            File.WriteAllText(Path.ChangeExtension(wav,".json"),JsonSerializer.Serialize(new{audio=name+".wav",label,verified,labelSource="manual event review",sampleUse="useful training sample",sessionId=session??"session-"+index}));
        }
        for(int i=0;i<9;i++)Add("clip"+i,i);
        Add("duplicate",0);Add("conflictA",50);Add("conflictB",50,"explosions / mortars");
        Add("pending",60,verified:false);Add("mixed",61,"mixed / uncertain");Add("wrong-rate",62,rate:44100);
        File.WriteAllText(Path.Combine(clips,"broken.json"),"not json");
        var originals=Directory.GetFiles(clips).ToDictionary(x=>x,ApprovedProfileTrainer.HashFile);
        var profiles=new PersonalProfiles(bundled,Path.Combine(root,"models"));var report=profiles.Train(clips);
        if(!report.Passed||report.AcceptedClips!=9||report.DuplicateClips!=1||report.ConflictingClips!=2||report.SkippedClips!=4||report.TrainingClips<3||report.ValidationClips<1)
            throw new Exception("Reviewed-only learning counts/gates failed: "+report.Summary);
        using(var doc=JsonDocument.Parse(File.ReadAllText(report.ModelFile)))
        {
            var metadata=doc.RootElement.GetProperty("personalLearning");
            var train=metadata.GetProperty("trainingHashes").EnumerateArray().Select(x=>x.GetString()).ToHashSet();
            if(metadata.GetProperty("validationHashes").EnumerateArray().Any(x=>train.Contains(x.GetString())))throw new Exception("Validation audio leaked into training.");
            var sessions=metadata.GetProperty("trainingSessions").EnumerateArray().Select(x=>x.GetString()).ToHashSet();
            if(metadata.GetProperty("validationSessions").EnumerateArray().Any(x=>sessions.Contains(x.GetString())))throw new Exception("Held-out sessions leaked across categories.");
            if(doc.RootElement.GetProperty("prototypes").GetProperty("gunfire").GetArrayLength()>17)throw new Exception("Unbounded personal prototypes.");
            if(doc.RootElement.GetProperty("liveRoutingMaxDistance").GetProperty("gunfire").GetDouble()!=1)throw new Exception("Training widened routing limits.");
        }
        if(originals.Any(x=>ApprovedProfileTrainer.HashFile(x.Key)!=x.Value))throw new Exception("Training changed original approved samples.");
        bool rejected=false;try{profiles.Apply(report with{Passed=false});}catch(InvalidDataException){rejected=true;}if(!rejected)throw new Exception("Failed candidate was activated.");
        profiles.Apply(report);if(profiles.ActivePath==bundled||new PersonalProfiles(bundled,Path.Combine(root,"models")).ActivePath==bundled)throw new Exception("Personal activation did not survive restart.");
        var native=new byte[48000*4];for(int i=0;i<48000;i++)BitConverter.GetBytes(samples[i]).CopyTo(native,i*4);
        var suggestion=profiles.Categorize(new(native,WaveFormat.CreateIeeeFloatWaveFormat(48000,2),0,.5,new(1,.1,"Test",-10)));
        if(suggestion.IsVerified||!suggestion.MachineGenerated||suggestion.WindowsAnalyzed==0)throw new Exception("Personal suggestions became approved or failed to load.");
        File.AppendAllText(report.ModelFile," ");rejected=false;try{profiles.Apply(report);}catch(InvalidDataException){rejected=true;}if(!rejected)throw new Exception("Changed candidate was activated.");
        profiles.RestoreBundled();if(profiles.ActivePath!=bundled||new PersonalProfiles(bundled,Path.Combine(root,"models")).ActivePath!=bundled)throw new Exception("Bundled restore failed.");
        string sparse=Path.Combine(root,"sparse");Directory.CreateDirectory(sparse);
        foreach(string name in new[]{"clip0","clip1","clip2","clip3"})foreach(string ext in new[]{".wav",".json"})File.Copy(Path.Combine(clips,name+ext),Path.Combine(sparse,name+ext));
        if(profiles.Train(sparse).Passed)throw new Exception("Insufficient reviewed clips were accepted.");
        File.WriteAllText(Path.Combine(root,"models","active.json"),"corrupt");
        if(new PersonalProfiles(bundled,Path.Combine(root,"models")).ActivePath!=bundled)throw new Exception("Corrupt personal profiles did not fall back.");
        Console.WriteLine("PASS approved learning: human-only labels, duplicates/conflicts, native integrity, session holdout, minimum data, bounded profiles, unchanged gates, validation/hash activation, restart/restore and unverified suggestions");
    }
}
